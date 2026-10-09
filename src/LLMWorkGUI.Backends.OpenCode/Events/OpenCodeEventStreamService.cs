using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Backends.OpenCode.Events;

public sealed class OpenCodeEventStreamService : IOpenCodeEventStreamService
{
    private static readonly string[] SessionContainers = { "part", "info" };
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUrl;
    private readonly Func<CancellationToken, Task<Uri>>? _resolveBaseUrl;
    private readonly IOpenCodeSseParser _parser;
    private readonly OpenCodeStreamOptions _options;
    private readonly ILogger<OpenCodeEventStreamService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<OpenCodeEventSpool>? _spoolFactory;
    private readonly OpenCodeManagedServerCredential? _managedCredential;
    private readonly object _stateGate = new();
    private BoundedEventBuffer? _buffer;
    private OpenCodeEventSpool? _spool;

    public OpenCodeEventStreamService(
        HttpClient httpClient,
        Uri baseUrl,
        IOpenCodeSseParser? parser = null,
        OpenCodeStreamOptions? options = null,
        ILogger<OpenCodeEventStreamService>? logger = null,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, Task<Uri>>? resolveBaseUrl = null,
        OpenCodeManagedServerCredential? managedCredential = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUrl);

        if (!baseUrl.IsAbsoluteUri
            || !string.Equals(baseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("BaseUrl must be an absolute http URI.", nameof(baseUrl));
        }

        _options = options ?? new OpenCodeStreamOptions();
        _options.Validate();

        _httpClient = httpClient;
        _baseUrl = baseUrl;
        _resolveBaseUrl = resolveBaseUrl;
        _managedCredential = managedCredential;
        _parser = parser ?? new OpenCodeSseParser();
        _logger = logger ?? NullLogger<OpenCodeEventStreamService>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal OpenCodeEventStreamService(
        HttpClient httpClient,
        Uri baseUrl,
        OpenCodeStreamOptions options,
        Func<OpenCodeEventSpool> spoolFactory)
        : this(httpClient, baseUrl, options: options)
    {
        ArgumentNullException.ThrowIfNull(spoolFactory);
        _spoolFactory = spoolFactory;
    }

    public bool IsOverflowed
    {
        get
        {
            lock (_stateGate)
            {
                return (_buffer?.IsOverflowed ?? false) || (_spool?.IsOverflowed ?? false);
            }
        }
    }

    public string? SpoolFilePath
    {
        get
        {
            lock (_stateGate)
            {
                return _spool?.FilePath;
            }
        }
    }

    public IReadOnlyList<OpenCodeEventEnvelope> GetRecentEvents(int? maxCount = null)
    {
        lock (_stateGate)
        {
            return _buffer?.GetSnapshot(maxCount) ?? Array.Empty<OpenCodeEventEnvelope>();
        }
    }

    public IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default) => SubscribeAsync(sessionId, cancellationToken, null);

    public IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeAsync(
        string? sessionId,
        CancellationToken cancellationToken,
        string? directory) => SubscribeAsync(sessionId, cancellationToken, directory, null);

    public async IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeAsync(
        string? sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string? directory,
        Uri? profileBaseUrl)
    {
        var buffer = new BoundedEventBuffer(_options.MaxInMemoryEvents);
        OpenCodeEventSpool? spool = null;

        if (!string.IsNullOrWhiteSpace(_options.SpoolDirectory))
        {
            spool = _spoolFactory?.Invoke() ?? new OpenCodeEventSpool(
                _options.SpoolDirectory,
                _options.MaxInMemoryEvents,
                fileName: $"opencode-events-{Guid.NewGuid():N}.jsonl",
                logger: _logger);
        }

        lock (_stateGate)
        {
            _buffer = buffer;
            _spool = spool;
        }

        try
        {
            var attempt = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var receivedAny = false;
                var failed = false;
                Exception? failure = null;

                await using (var enumerator = ReadOnceAsync(sessionId, directory, spool, buffer, cancellationToken, profileBaseUrl)
                    .GetAsyncEnumerator(cancellationToken))
                {
                    while (true)
                    {
                        bool hasNext;

                        try
                        {
                            hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            failed = true;
                            failure = exception;
                            break;
                        }

                        if (!hasNext)
                        {
                            break;
                        }

                        receivedAny = true;
                        yield return enumerator.Current;
                    }
                }

                if (failed)
                {
                    _logger.LogWarning(
                        failure,
                        "OpenCode event stream connection failed; reconnecting with exponential backoff.");
                }
                else
                {
                    _logger.LogInformation(
                        "OpenCode event stream ended unexpectedly; reconnecting with exponential backoff.");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                attempt = receivedAny ? 0 : attempt + 1;

                var delay = CalculateReconnectDelay(
                    attempt,
                    _options.ReconnectInitialDelay,
                    _options.ReconnectMaxDelay);

                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (spool is not null)
            {
                await spool.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public static TimeSpan CalculateReconnectDelay(int attempt, TimeSpan initialDelay, TimeSpan maxDelay)
    {
        if (initialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay), initialDelay, "Initial delay must be positive.");
        }

        if (maxDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay), maxDelay, "Max delay must not be smaller than the initial delay.");
        }

        var exponent = Math.Clamp(attempt, 0, 16);
        var exponentialTicks = (long)Math.Min(initialDelay.Ticks * Math.Pow(2, exponent), maxDelay.Ticks);

        var halfTicks = exponentialTicks / 2;
        var jitteredTicks = halfTicks + (long)(Random.Shared.NextDouble() * halfTicks);

        return TimeSpan.FromTicks(Math.Clamp(jitteredTicks, 1L, maxDelay.Ticks));
    }

    private async IAsyncEnumerable<OpenCodeEventEnvelope> ReadOnceAsync(
        string? sessionId,
        string? directory,
        OpenCodeEventSpool? spool,
        BoundedEventBuffer buffer,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Uri? profileBaseUrl = null)
    {
        var baseUrl = profileBaseUrl
            ?? (_resolveBaseUrl is null ? _baseUrl : await _resolveBaseUrl(cancellationToken).ConfigureAwait(false));
        var path = OpenCodeClient.WithDirectory(OpenCodeApiPaths.Event, directory);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        _managedCredential?.TryApply(request);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        await foreach (var envelope in _parser.ParseAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (!BelongsToSubscription(envelope, sessionId))
            {
                continue;
            }

            buffer.Add(envelope);

            if (spool is not null)
            {
                await spool
                    .EnqueueAsync(OpenCodeEventSpool.ToJsonLine(envelope.RawJson), cancellationToken)
                    .ConfigureAwait(false);
            }

            yield return envelope;
        }
    }

    private static bool BelongsToSubscription(OpenCodeEventEnvelope envelope, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return true;
        }

        var properties = envelope.Properties;
        var found = false;
        if (!MatchesSession(properties, sessionId, ref found))
        {
            return false;
        }

        if (properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in SessionContainers)
            {
                if (properties.TryGetProperty(name, out var nested)
                    && !MatchesSession(nested, sessionId, ref found))
                {
                    return false;
                }
            }
        }

        // Only connection notifications are sessionless on a scoped subscription.
        return found || envelope.Type == "server.connected";
    }

    private static bool MatchesSession(JsonElement properties, string expectedSession, ref bool found)
    {
        if (properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty("sessionID", out var value))
        {
            return true;
        }

        found = true;
        return value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), expectedSession, StringComparison.Ordinal);
    }
}
