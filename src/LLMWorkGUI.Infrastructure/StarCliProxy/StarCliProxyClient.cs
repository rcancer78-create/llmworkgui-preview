using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Infrastructure.Http;

namespace LLMWorkGUI.Infrastructure.StarCliProxy;

/// <summary>
/// OpenAI-compatible HTTP client for a loopback star-cliproxy instance (ТЗ §6.4, §6.11a, ADR-0007).
/// Supports <c>/v1/models</c>, SSE streaming of <c>/v1/chat/completions</c> with normalized events,
/// bounded timeouts, cancellation and requested/observed route evidence. Secrets stay in HTTP
/// headers only and are never logged.
/// </summary>
public sealed class StarCliProxyClient : IStarCliProxyClient
{
    internal const string ModelsPath = "v1/models";

    internal const string ChatCompletionsPath = "v1/chat/completions";

    internal const string SessionHeaderName = "X-Cliproxy-Session-Id";

    private const int MaxErrorBodyExcerptLength = 2048;
    private const int MaximumBodyBytes = 4 * 1024 * 1024;
    private const int MaximumErrorBodyBytes = 64 * 1024;
    private const int MaximumSseLineChars = 1024 * 1024;
    private const int MaximumSseEventChars = 4 * 1024 * 1024;

    private static readonly string[] ProviderHeaderNames = ["X-Cliproxy-Provider", "X-Provider"];

    private static readonly string[] AccountHeaderNames = ["X-Cliproxy-Account", "X-Account"];

    private static readonly string[] SessionHeaderNames = [SessionHeaderName, "X-Session-Id", "X-Request-Id"];

    private readonly HttpClient _httpClient;
    private readonly StarCliProxyOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StarCliProxyClient> _logger;
    private readonly IWorkflowReviewEgressPolicy? _workflowPolicy;

    public StarCliProxyClient(
        HttpClient httpClient,
        IOptions<StarCliProxyOptions>? options = null,
        TimeProvider? timeProvider = null,
        ILogger<StarCliProxyClient>? logger = null,
        IWorkflowReviewEgressPolicy? workflowPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _options = options?.Value ?? new StarCliProxyOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<StarCliProxyClient>.Instance;
        _workflowPolicy = workflowPolicy;
    }

    public async Task<IReadOnlyList<StarCliProxyModelInfo>> ListModelsAsync(
        StarCliProxyEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(endpoint, ModelsPath));
        ApplyRequestHeaders(request, endpoint);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new StarCliProxyClientException(
                $"The star-cliproxy model catalog request to {endpoint.BaseUrl} failed with HTTP " +
                $"{FormatStatusCode(response)}.");
        }

        var body = await BoundedHttpContent.ReadTextAsync(response.Content, MaximumBodyBytes, timeoutCts.Token)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);

        return ParseModels(document.RootElement);
    }

    public async Task<StarCliProxyHealthStatus> CheckHealthAsync(
        StarCliProxyEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var checkedAtUtc = _timeProvider.GetUtcNow();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.HealthCheckTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(endpoint, ModelsPath));
            ApplyRequestHeaders(request, endpoint);

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var detail = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? $"star-cliproxy rejected the proxy API key (HTTP {FormatStatusCode(response)})."
                    : $"The star-cliproxy model catalog returned HTTP {FormatStatusCode(response)}.";

                return StarCliProxyHealthStatus.Unhealthy(detail, checkedAtUtc);
            }

            var body = await BoundedHttpContent.ReadTextAsync(response.Content, MaximumBodyBytes, timeoutCts.Token)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            var models = ParseModels(document.RootElement);

            return StarCliProxyHealthStatus.Healthy(models.Count, checkedAtUtc);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StarCliProxyHealthStatus.Unhealthy(
                $"The star-cliproxy health probe timed out after {_options.HealthCheckTimeout}.",
                checkedAtUtc);
        }
        catch (HttpRequestException exception)
        {
            return StarCliProxyHealthStatus.Unhealthy(
                $"The star-cliproxy instance is unreachable on loopback ({exception.Message}).",
                checkedAtUtc);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return StarCliProxyHealthStatus.Unhealthy(
                "The star-cliproxy model catalog is malformed or exceeds the permitted response limit.",
                checkedAtUtc);
        }
    }

    public async IAsyncEnumerable<StarCliProxyStreamEvent> StreamChatCompletionAsync(
        StarCliProxyEndpoint endpoint,
        StarCliProxyChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.RequestTimeout);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(endpoint, ChatCompletionsPath))
        {
            Content = new StringContent(
                SerializeChatRequest(request),
                Encoding.UTF8,
                "application/json")
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyRequestHeaders(httpRequest, endpoint, request.RequestedSessionId);

        if (request.WorkflowReviewContext is { } context)
        {
            if (_workflowPolicy is null || request.RequestedProviderId is not null || request.RequestedAccountId is not null)
                throw new WorkflowReviewEgressException();
            try
            {
                await _workflowPolicy.AuthorizeAsync(context, endpoint.BaseUrl.AbsoluteUri,
                    await httpRequest.Content!.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false),
                    request.RequestedSessionId, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw new WorkflowReviewPreTransportCancellationException(timeoutCts.Token); }
            catch (Exception) { throw new WorkflowReviewEgressException(); }
        }
        if (request.WorkflowReviewContext is not null && timeoutCts.IsCancellationRequested)
            throw new WorkflowReviewPreTransportCancellationException(timeoutCts.Token);
        timeoutCts.Token.ThrowIfCancellationRequested();

        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);

        var headerEvidence = ReadHeaderEvidence(response);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadBoundedBodyAsync(response, timeoutCts.Token).ConfigureAwait(false);

            _logger.LogWarning(
                "The star-cliproxy chat completion request to {BaseUrl} failed with HTTP {StatusCode}.",
                endpoint.BaseUrl,
                (int)response.StatusCode);

            yield return StarCliProxyStreamEvent.Error(
                $"{StarCliProxyStreamEventMarkers.HttpFailurePrefix}{FormatStatusCode(response)}.",
                _timeProvider.GetUtcNow(),
                headerEvidence,
                errorBody);
            yield break;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (mediaType is not null &&
            mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(timeoutCts.Token)
                .ConfigureAwait(false);

            await foreach (var streamEvent in ParseSseAsync(stream, request, headerEvidence, timeoutCts.Token)
                               .ConfigureAwait(false))
            {
                yield return streamEvent;
            }

            yield break;
        }

        string? body = null;
        try
        {
            body = await BoundedHttpContent.ReadTextAsync(response.Content, MaximumBodyBytes, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // A truncated completion is not success and its oversized content is never retained.
        }
        if (body is null)
        {
            yield return StarCliProxyStreamEvent.Error(
                "The star-cliproxy completion exceeds the permitted response limit.",
                _timeProvider.GetUtcNow(), headerEvidence);
            yield break;
        }

        foreach (var streamEvent in ParseChunk(request, body, headerEvidence, _timeProvider.GetUtcNow(), out _, out _))
        {
            yield return streamEvent;
        }
    }

    private async IAsyncEnumerable<StarCliProxyStreamEvent> ParseSseAsync(
        Stream stream,
        StarCliProxyChatRequest request,
        StarCliProxyObservedEvidence headerEvidence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        var lines = new BoundedTextLineReader(reader, MaximumSseLineChars);

        var data = new StringBuilder();
        var hasData = false;
        var terminalEventEmitted = false;
        var currentEvidence = headerEvidence;

        // One accumulated evidence record covers the whole turn: a header that already
        // contradicts the requested route is terminal before any chunk is read.
        if (TryCreateRouteMismatchEvent(
                request,
                currentEvidence,
                _timeProvider.GetUtcNow(),
                payload: null,
                out var headerMismatchEvent))
        {
            yield return headerMismatchEvent;
            yield break;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line = null;
            var lineLimitExceeded = false;

            try
            {
                line = await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                lineLimitExceeded = true;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                _logger.LogWarning(
                    exception,
                    "The star-cliproxy SSE stream read failed; the event enumeration ends without a successful terminal event.");

                yield break;
            }

            if (lineLimitExceeded)
            {
                yield return StarCliProxyStreamEvent.Error(
                    "The star-cliproxy SSE line exceeds the permitted response limit.",
                    _timeProvider.GetUtcNow(), currentEvidence);
                yield break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (!hasData)
                {
                    continue;
                }

                var payload = data.ToString();
                data.Clear();
                hasData = false;

                var receivedAtUtc = _timeProvider.GetUtcNow();

                if (IsDonePayload(payload))
                {
                    // [DONE] is judged on the accumulated evidence, never on the raw headers alone.
                    if (!terminalEventEmitted)
                    {
                        if (TryCreateRouteMismatchEvent(
                                request,
                                currentEvidence,
                                receivedAtUtc,
                                payload: null,
                                out var doneMismatchEvent))
                        {
                            yield return doneMismatchEvent;
                        }
                        else
                        {
                            yield return StarCliProxyStreamEvent.Completed(finishReason: null, receivedAtUtc, currentEvidence);
                        }
                    }

                    yield break;
                }

                var chunkEvents = ParseChunk(
                    request,
                    payload,
                    currentEvidence,
                    receivedAtUtc,
                    out var chunkEvidence,
                    out var isRouteMismatch);

                currentEvidence = chunkEvidence;

                foreach (var streamEvent in chunkEvents)
                {
                    // Exactly one terminal event per turn: Completed and Error/RouteMismatch
                    // both latch the flag, so no later chunk can emit a second terminal event.
                    if (terminalEventEmitted && IsTerminalEvent(streamEvent))
                    {
                        continue;
                    }

                    terminalEventEmitted |= IsTerminalEvent(streamEvent);

                    yield return streamEvent;
                    if (streamEvent.Kind == StarCliProxyStreamEventKind.Error)
                        yield break;
                }

                // A route mismatch is terminal: no further chunks and no normal Completed may follow.
                if (isRouteMismatch)
                {
                    yield break;
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            var field = colonIndex >= 0 ? line[..colonIndex] : line;
            var value = colonIndex >= 0 ? line[(colonIndex + 1)..] : string.Empty;

            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (field == "data")
            {
                var separatorLength = hasData ? 1 : 0;
                if (value.Length > MaximumSseEventChars - data.Length - separatorLength)
                {
                    yield return StarCliProxyStreamEvent.Error(
                        "The star-cliproxy SSE event exceeds the permitted response limit.",
                        _timeProvider.GetUtcNow(), currentEvidence);
                    yield break;
                }
                if (hasData)
                {
                    data.Append('\n');
                }

                data.Append(value);
                hasData = true;
            }
        }

        if (!hasData)
        {
            yield break;
        }

        var trailingPayload = data.ToString();
        var trailingReceivedAtUtc = _timeProvider.GetUtcNow();

        if (IsDonePayload(trailingPayload))
        {
            if (!terminalEventEmitted)
            {
                if (TryCreateRouteMismatchEvent(
                        request,
                        currentEvidence,
                        trailingReceivedAtUtc,
                        payload: null,
                        out var trailingMismatchEvent))
                {
                    yield return trailingMismatchEvent;
                }
                else
                {
                    yield return StarCliProxyStreamEvent.Completed(finishReason: null, trailingReceivedAtUtc, currentEvidence);
                }
            }

            yield break;
        }

        var trailingEvents = ParseChunk(
            request,
            trailingPayload,
            currentEvidence,
            trailingReceivedAtUtc,
            out _,
            out _);

        foreach (var streamEvent in trailingEvents)
        {
            if (terminalEventEmitted && IsTerminalEvent(streamEvent))
            {
                continue;
            }

            yield return streamEvent;
        }
    }

    private static bool IsTerminalEvent(StarCliProxyStreamEvent streamEvent) =>
        streamEvent.Kind is StarCliProxyStreamEventKind.Completed or StarCliProxyStreamEventKind.Error;

    private IReadOnlyList<StarCliProxyStreamEvent> ParseChunk(
        StarCliProxyChatRequest request,
        string payload,
        StarCliProxyObservedEvidence currentEvidence,
        DateTimeOffset receivedAtUtc,
        out StarCliProxyObservedEvidence updatedEvidence,
        out bool isRouteMismatch)
    {
        isRouteMismatch = false;
        updatedEvidence = currentEvidence;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<StarCliProxyStreamEvent>();
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "A star-cliproxy stream chunk was not valid JSON; emitting a structured malformed event.");

            return new[] { StarCliProxyStreamEvent.Malformed(payload, receivedAtUtc, currentEvidence) };
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new[] { StarCliProxyStreamEvent.Malformed(payload, receivedAtUtc, currentEvidence) };
            }

            var bodyEvidence = ExtractEvidence(root);
            var evidence = currentEvidence.Merge(bodyEvidence);

            updatedEvidence = evidence;

            // A body field that contradicts the accumulated header/body evidence is itself a
            // route substitution signal and terminates the turn before any success event.
            if (FindEvidenceConflict(currentEvidence, bodyEvidence) is { } conflictReason)
            {
                isRouteMismatch = true;

                return new[]
                {
                    StarCliProxyStreamEvent.Error(
                        $"{StarCliProxyStreamEventMarkers.RouteMismatchPrefix}{conflictReason}",
                        receivedAtUtc,
                        evidence,
                        payload)
                };
            }

            if (TryCreateRouteMismatchEvent(request, evidence, receivedAtUtc, payload, out var mismatchEvent))
            {
                isRouteMismatch = true;

                return new[] { mismatchEvent };
            }

            if (root.TryGetProperty("error", out var errorElement))
            {
                var message = errorElement.ValueKind == JsonValueKind.Object &&
                              TryGetString(errorElement, "message", out var errorMessage)
                    ? errorMessage
                    : errorElement.ToString();

                return new[]
                {
                    StarCliProxyStreamEvent.Error(
                        string.IsNullOrWhiteSpace(message)
                            ? "star-cliproxy reported an error event."
                            : message,
                        receivedAtUtc,
                        evidence,
                        payload)
                };
            }

            var events = new List<StarCliProxyStreamEvent>();
            var hasTerminalEvent = false;
            string? finishReason = null;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    AppendChoiceEvents(choice, payload, evidence, receivedAtUtc, events, ref hasTerminalEvent, ref finishReason);
                }
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var promptTokens = TryGetInt(usage, "prompt_tokens", out var promptValue) ? promptValue : (int?)null;
                var completionTokens = TryGetInt(usage, "completion_tokens", out var completionValue)
                    ? completionValue
                    : (int?)null;

                events.Add(StarCliProxyStreamEvent.Usage(
                    promptTokens,
                    completionTokens,
                    receivedAtUtc,
                    evidence,
                    payload));
            }

            if (hasTerminalEvent)
            {
                events.Add(StarCliProxyStreamEvent.Completed(finishReason, receivedAtUtc, evidence));
            }

            return events;
        }
    }

    private static void AppendChoiceEvents(
        JsonElement choice,
        string payload,
        StarCliProxyObservedEvidence evidence,
        DateTimeOffset receivedAtUtc,
        List<StarCliProxyStreamEvent> events,
        ref bool hasTerminalEvent,
        ref string? finishReason)
    {
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(delta, "content", out var content) && content.Length > 0)
            {
                events.Add(StarCliProxyStreamEvent.ContentDelta(content, receivedAtUtc, evidence, payload));
            }

            var reasoning = TryGetString(delta, "reasoning_content", out var reasoningContent)
                ? reasoningContent
                : (TryGetString(delta, "reasoning", out var reasoningValue) ? reasoningValue : string.Empty);

            if (reasoning.Length > 0)
            {
                events.Add(StarCliProxyStreamEvent.ReasoningDelta(reasoning, receivedAtUtc, evidence, payload));
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    string? toolName = null;
                    string? toolArguments = null;

                    if (toolCall.TryGetProperty("function", out var function) &&
                        function.ValueKind == JsonValueKind.Object)
                    {
                        toolName = TryGetString(function, "name", out var name) ? name : null;
                        toolArguments = TryGetString(function, "arguments", out var arguments) ? arguments : null;
                    }

                    if (!string.IsNullOrEmpty(toolName) || !string.IsNullOrEmpty(toolArguments))
                    {
                        events.Add(StarCliProxyStreamEvent.ToolCall(
                            string.IsNullOrEmpty(toolName) ? "(unnamed)" : toolName,
                            toolArguments,
                            receivedAtUtc,
                            evidence,
                            payload));
                    }
                }
            }
        }

        if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(message, "content", out var messageContent) && messageContent.Length > 0)
            {
                events.Add(StarCliProxyStreamEvent.ContentDelta(messageContent, receivedAtUtc, evidence, payload));
            }
        }

        if (TryGetString(choice, "finish_reason", out var reason) && reason.Length > 0)
        {
            hasTerminalEvent = true;
            finishReason = reason;
        }
    }

    private static IReadOnlyList<StarCliProxyModelInfo> ParseModels(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<StarCliProxyModelInfo>();
        }

        var models = new List<StarCliProxyModelInfo>();

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetString(item, "id", out var id) ||
                string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var ownedBy = TryGetString(item, "owned_by", out var ownedByValue) ? ownedByValue : null;
            var providerId = TryGetString(item, "provider", out var providerValue) ? providerValue : null;

            DateTimeOffset? createdAtUtc = null;

            if (item.TryGetProperty("created", out var created) &&
                created.ValueKind == JsonValueKind.Number &&
                created.TryGetInt64(out var createdSeconds))
            {
                createdAtUtc = DateTimeOffset.FromUnixTimeSeconds(createdSeconds);
            }

            models.Add(new StarCliProxyModelInfo(id, ownedBy, providerId, createdAtUtc));
        }

        return models;
    }

    /// <summary>
    /// Terminating route-mismatch gate (ТЗ §6.4, ADR-0007 §8): when the gateway reports a
    /// provider/account/model/session that contradicts the requested route, the turn must end
    /// with a terminal error and must never emit a normal <c>Completed</c> event.
    /// </summary>
    private static bool TryCreateRouteMismatchEvent(
        StarCliProxyChatRequest request,
        StarCliProxyObservedEvidence evidence,
        DateTimeOffset receivedAtUtc,
        string? payload,
        out StarCliProxyStreamEvent mismatchEvent)
    {
        var routeEvidence = StarCliProxyRouteEvidence.ForRequest(request, evidence);

        if (!routeEvidence.HasMismatch)
        {
            mismatchEvent = null!;

            return false;
        }

        mismatchEvent = StarCliProxyStreamEvent.Error(
            $"{StarCliProxyStreamEventMarkers.RouteMismatchPrefix}{routeEvidence.MismatchReason}",
            receivedAtUtc,
            evidence,
            payload);

        return true;
    }

    /// <summary>
    /// Reads the generic OpenAI-compatible identity fields off a response body as <b>claims</b>.
    /// The four-argument form leaves every verification flag <c>false</c> on purpose: the completion
    /// <c>model</c> is echoed from <c>body.model</c> of the request we just sent, and nothing here
    /// establishes an independent native source, so no claim may be promoted to observed identity.
    /// </summary>
    private static StarCliProxyObservedEvidence ExtractEvidence(JsonElement root)
    {
        var providerId = TryGetString(root, "provider", out var provider) ? provider : null;
        var accountId = TryGetString(root, "account", out var account) ? account : null;
        var modelId = TryGetString(root, "model", out var model) ? model : null;

        var sessionId = TryGetString(root, "session_id", out var session)
            ? session
            : (TryGetString(root, "conversation_id", out var conversation)
                ? conversation
                : (TryGetString(root, "sessionId", out var camelSession) ? camelSession : null));

        return new StarCliProxyObservedEvidence(providerId, accountId, modelId, sessionId);
    }

    private static string? FindEvidenceConflict(
        StarCliProxyObservedEvidence currentEvidence,
        StarCliProxyObservedEvidence bodyEvidence)
    {
        if (IsConflict(currentEvidence.ProviderId, bodyEvidence.ProviderId))
        {
            return $"Observed provider '{bodyEvidence.ProviderId}' in the response body conflicts with the " +
                   $"previously observed provider '{currentEvidence.ProviderId}'.";
        }

        if (IsConflict(currentEvidence.AccountId, bodyEvidence.AccountId))
        {
            return $"Observed account '{bodyEvidence.AccountId}' in the response body conflicts with the " +
                   $"previously observed account '{currentEvidence.AccountId}'.";
        }

        if (IsConflict(currentEvidence.ModelId, bodyEvidence.ModelId))
        {
            return $"Observed model '{bodyEvidence.ModelId}' in the response body conflicts with the " +
                   $"previously observed model '{currentEvidence.ModelId}'.";
        }

        if (IsConflict(currentEvidence.SessionId, bodyEvidence.SessionId))
        {
            return $"Observed session '{bodyEvidence.SessionId}' in the response body conflicts with the " +
                   $"previously observed session '{currentEvidence.SessionId}'.";
        }

        return null;
    }

    private static bool IsConflict(string? existingValue, string? incomingValue) =>
        existingValue is not null &&
        incomingValue is not null &&
        !string.Equals(existingValue, incomingValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads optional gateway response headers as <b>claims</b>, with every verification flag left
    /// <c>false</c>. These header names have no proven contract behind them and no trusted-header
    /// set is invented here, so a present header is evidence that something was said, never proof of
    /// which provider, account or session actually served the turn. The model is deliberately left
    /// null: no response header is read as a model claim.
    /// </summary>
    private static StarCliProxyObservedEvidence ReadHeaderEvidence(HttpResponseMessage response)
    {
        return new StarCliProxyObservedEvidence(
            FirstHeaderValue(response, ProviderHeaderNames),
            FirstHeaderValue(response, AccountHeaderNames),
            null,
            FirstHeaderValue(response, SessionHeaderNames));
    }

    private static string? FirstHeaderValue(HttpResponseMessage response, IReadOnlyList<string> headerNames)
    {
        foreach (var headerName in headerNames)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                var value = values.FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static bool IsDonePayload(string payload) =>
        string.Equals(payload.Trim(), "[DONE]", StringComparison.Ordinal);

    private static string SerializeChatRequest(StarCliProxyChatRequest request)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.ModelId);
            writer.WriteBoolean("stream", request.Stream);

            if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
            {
                writer.WriteString("reasoning_effort", request.ReasoningEffort);
            }

            writer.WriteStartArray("messages");

            foreach (var message in request.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Uri BuildUri(StarCliProxyEndpoint endpoint, string relativePath) =>
        new(endpoint.BaseUrl, relativePath);

    private static void ApplyRequestHeaders(
        HttpRequestMessage request,
        StarCliProxyEndpoint endpoint,
        string? requestedSessionId = null)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(requestedSessionId))
        {
            request.Headers.TryAddWithoutValidation(SessionHeaderName, requestedSessionId);
        }
    }

    private static async Task<string?> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await BoundedHttpContent.ReadTextAsync(response.Content, MaximumErrorBodyBytes, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            return body.Length <= MaxErrorBodyExcerptLength
                ? body
                : body[..MaxErrorBodyExcerptLength] + "...";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidDataException)
        {
            return null;
        }
    }

    private static string FormatStatusCode(HttpResponseMessage response) =>
        $"{(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"})";

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }

    private static bool TryGetInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;

        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
