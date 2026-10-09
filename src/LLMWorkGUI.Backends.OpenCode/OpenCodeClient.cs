using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Events;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.Backends.OpenCode;

public sealed class OpenCodeClient : IOpenCodeClient
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly IOpenCodeEventStreamService? _eventStreamService;
    private readonly Func<CancellationToken, Task<Uri>>? _resolveBaseUrl;
    private readonly Func<string, CancellationToken, Task<Uri>>? _resolveProfileBaseUrl;
    private readonly IAdaptationTransportPolicy? _adaptationTransport;
    private readonly OpenCodeManagedServerCredential? _managedCredential;
    private readonly ConcurrentDictionary<string, string> _sessionDirectories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionProfiles = new(StringComparer.Ordinal);

    public OpenCodeClient(
        HttpClient httpClient,
        Uri baseUrl,
        TimeSpan? requestTimeout = null,
        IOpenCodeEventStreamService? eventStreamService = null,
        Func<CancellationToken, Task<Uri>>? resolveBaseUrl = null,
        IAdaptationTransportPolicy? adaptationTransportPolicy = null,
        OpenCodeManagedServerCredential? managedCredential = null,
        Func<string, CancellationToken, Task<Uri>>? resolveProfileBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUrl);

        if (!baseUrl.IsAbsoluteUri
            || !string.Equals(baseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("BaseUrl must be an absolute http URI.", nameof(baseUrl));
        }

        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;

        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMilliseconds(4294967294L))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                _requestTimeout,
                "Request timeout must be positive and within the timer range (4294967294 milliseconds).");
        }

        _httpClient = httpClient;
        _eventStreamService = eventStreamService;
        _resolveBaseUrl = resolveBaseUrl;
        _resolveProfileBaseUrl = resolveProfileBaseUrl;
        _adaptationTransport = adaptationTransportPolicy;
        _managedCredential = managedCredential;
        BaseUrl = baseUrl;
    }

    public Uri BaseUrl { get; private set; }

    public IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeEventsAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (_eventStreamService is null)
        {
            throw new InvalidOperationException(
                "The OpenCode event stream service is not configured for this client.");
        }

        var directory = sessionId is not null && _sessionDirectories.TryGetValue(sessionId, out var stored) ? stored : null;
        if (sessionId is not null && _sessionProfiles.TryGetValue(sessionId, out var profile))
        {
            if (_resolveProfileBaseUrl is not null)
                return SubscribeProfileEventsAsync(sessionId, directory, profile, cancellationToken);
            if (_resolveBaseUrl is not null)
                throw new OpenCodeClientException("A provider profile cannot use a shared OpenCode server.");
        }
        return _eventStreamService.SubscribeAsync(sessionId, cancellationToken, directory);
    }

    private async IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeProfileEventsAsync(
        string sessionId,
        string? directory,
        string providerProfileId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var baseUrl = await _resolveProfileBaseUrl!(providerProfileId, cancellationToken).ConfigureAwait(false);
        if (_eventStreamService is not OpenCodeEventStreamService stream)
            throw new OpenCodeClientException("A provider profile event stream is not available.");
        await foreach (var envelope in stream.SubscribeAsync(sessionId, cancellationToken, directory, baseUrl))
            yield return envelope;
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var request = CreateRequest(HttpMethod.Get, OpenCodeApiPaths.GlobalHealth);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<OpenCodeDocResponse> GetDocAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var request = CreateRequest(HttpMethod.Get, OpenCodeApiPaths.Doc);

        HttpResponseMessage response;
        string rawJson;

        try
        {
            response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new OpenCodeClientException(
                $"The GET {OpenCodeApiPaths.Doc} request to {BaseUrl} timed out after {_requestTimeout}.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new OpenCodeClientException(
                $"The GET {OpenCodeApiPaths.Doc} request to {BaseUrl} failed.",
                exception);
        }

        using (response)
        {
            try
            {
                rawJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new OpenCodeClientException(
                    $"The GET {OpenCodeApiPaths.Doc} request to {BaseUrl} returned status code " +
                    $"{(int)response.StatusCode}.");
            }
        }

        try
        {
            return OpenCodeDocParser.Parse(rawJson);
        }
        catch (JsonException exception)
        {
            throw new OpenCodeClientException(
                $"The GET {OpenCodeApiPaths.Doc} response from {BaseUrl} is not valid JSON.",
                exception);
        }
    }

    public async Task<IReadOnlyList<OpenCodeProviderInfo>> ListProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rawJson = await GetJsonWithFallbackAsync(
                OpenCodeApiPaths.ApiProviders,
                OpenCodeApiPaths.Providers,
                cancellationToken)
            .ConfigureAwait(false);

        return ParseProviders(rawJson, OpenCodeApiPaths.ApiProviders);
    }

    public async Task<IReadOnlyList<OpenCodeModelInfo>> ListModelsAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        if (providerId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var rawJson = await GetJsonWithFallbackAsync(
                OpenCodeApiPaths.ApiModels,
                OpenCodeApiPaths.Models,
                cancellationToken)
            .ConfigureAwait(false);

        return ParseModels(rawJson, OpenCodeApiPaths.ApiModels, providerId);
    }

    public async Task<OpenCodeConfiguredProvidersResponse> ListConfiguredProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rawJson = await GetJsonAsync(OpenCodeApiPaths.ConfiguredProviders, cancellationToken)
            .ConfigureAwait(false);

        return ParseConfiguredProviders(rawJson, OpenCodeApiPaths.ConfiguredProviders);
    }

    public async Task<OpenCodeSessionResponse> CreateSessionAsync(
        OpenCodeCreateSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.AdaptationAdmissionId is not null && _adaptationTransport is null
            || request.Title?.StartsWith("workflow-adaptation-", StringComparison.Ordinal) == true && request.AdaptationAdmissionId is null)
            throw AdaptationTransportUnavailable();
        Uri? sentUri = null;

        using var content = new StringContent(
            OpenCodeSessionJson.SerializeCreateSessionRequest(request),
            Encoding.UTF8,
            "application/json");
        using var response = await SendAsync(
                HttpMethod.Post,
                WithDirectory(OpenCodeApiPaths.Sessions, request.Directory),
                content,
                cancellationToken,
                request.AdaptationAdmissionId is { } admission ? async (message,token) =>
                {
                    await _adaptationTransport!.AuthorizeCreateAsync(admission,message.RequestUri!,await message.Content!.ReadAsByteArrayAsync(token),token).ConfigureAwait(false);
                    sentUri=message.RequestUri;
                } : null,
                request.ProviderProfileId)
            .ConfigureAwait(false);

        EnsureSuccess(response, HttpMethod.Post, OpenCodeApiPaths.Sessions);

        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var session = ParseSession(rawJson, HttpMethod.Post, OpenCodeApiPaths.Sessions);
        if (request.AdaptationAdmissionId is { } id)
            await _adaptationTransport!.BindCreatedAsync(id,session.Id,session.Directory,sentUri!,cancellationToken).ConfigureAwait(false);
        RememberDirectory(session, request.Directory);
        if (!string.IsNullOrWhiteSpace(request.ProviderProfileId))
            _sessionProfiles[session.Id] = request.ProviderProfileId;
        return session;
    }

    public async Task<OpenCodeSessionResponse?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var path = WithSessionDirectory(OpenCodeApiPaths.Session(sessionId), sessionId);
        using var response = await SendAsync(HttpMethod.Get, path, content: null, cancellationToken,
                providerProfileId: ProfileFor(sessionId))
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, HttpMethod.Get, path);

        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ParseSession(rawJson, HttpMethod.Get, path);
    }

    public async Task<IReadOnlyList<OpenCodeSessionResponse>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var response = await SendAsync(
                HttpMethod.Get,
                OpenCodeApiPaths.Sessions,
                content: null,
                cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccess(response, HttpMethod.Get, OpenCodeApiPaths.Sessions);

        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ParseSessions(rawJson, HttpMethod.Get, OpenCodeApiPaths.Sessions);
    }

    public async Task<OpenCodeSessionResponse> ForkSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var path = WithSessionDirectory(OpenCodeApiPaths.SessionFork(sessionId), sessionId);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, path, content, cancellationToken,
                providerProfileId: ProfileFor(sessionId))
            .ConfigureAwait(false);

        EnsureSuccess(response, HttpMethod.Post, path);

        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var session = ParseSession(rawJson, HttpMethod.Post, path);
        RememberDirectory(session, _sessionDirectories.GetValueOrDefault(sessionId));
        if (_sessionProfiles.TryGetValue(sessionId, out var profile))
            _sessionProfiles[session.Id] = profile;
        return session;
    }

    public async Task<bool> AbortSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var path = WithSessionDirectory(OpenCodeApiPaths.SessionAbort(sessionId), sessionId);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, path, content, cancellationToken,
            _adaptationTransport is null ? null : (message,token)=>_adaptationTransport.AuthorizeAbortAsync(sessionId,message.RequestUri!,token),
            ProfileFor(sessionId))
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            EnsureSuccess(response, HttpMethod.Post, path);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SendPromptAsync(
        string sessionId,
        OpenCodePromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.AdaptationAdmissionId is not null && _adaptationTransport is null) throw AdaptationTransportUnavailable();

        var path = WithSessionDirectory(OpenCodeApiPaths.SessionPromptAsync(sessionId), sessionId);
        using var content = new StringContent(
            OpenCodeSessionJson.SerializePromptRequest(request),
            Encoding.UTF8,
            "application/json");
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(HttpMethod.Post, path, content, cancellationToken, async (message, token) =>
            {
                if (request.DispatchAuthorization is { } authority
                    && !await authority(sessionId, request, message.RequestUri!, token).ConfigureAwait(false))
                    throw new PromptDispatchDeniedException();
                if (_adaptationTransport is not null)
                    await _adaptationTransport.AuthorizePromptAsync(request.AdaptationAdmissionId, sessionId, message.RequestUri!,
                        await message.Content!.ReadAsByteArrayAsync(token), token).ConfigureAwait(false);
            }, ProfileFor(sessionId)).ConfigureAwait(false);
        }
        catch (PromptDispatchDeniedException) { return false; }
        using var responseLifetime = response;

        // Only a local dispatch denial proves that the prompt was not sent. An HTTP
        // failure after dispatch cannot establish whether the server accepted work.
        EnsureSuccess(response, HttpMethod.Post, path);
        return true;
    }

    private sealed class PromptDispatchDeniedException : Exception;

    public async Task<bool> ReplyPermissionAsync(
        string permissionId,
        OpenCodePermissionReply reply,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionId);
        ArgumentNullException.ThrowIfNull(reply);
        cancellationToken.ThrowIfCancellationRequested();

        if (!reply.IsStandard || reply.Remember && reply.Response != OpenCodePermissionReply.Always)
        {
            throw new ArgumentException(
                "The native permission API requires once, always or reject; remembering a once/reject reply is unsupported.",
                nameof(reply));
        }

        var path = OpenCodeApiPaths.PermissionReply(permissionId);
        if (reply.ScopeSessionId is { } sessionId)
        {
            if (!_sessionDirectories.TryGetValue(sessionId, out var directory) || string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Permission reply requires a confirmed native session directory.");
            path = WithDirectory(path, directory);
        }
        using var content = new StringContent(
            OpenCodeSessionJson.SerializePermissionReply(reply),
            Encoding.UTF8,
            "application/json");
        using var response = await SendAsync(HttpMethod.Post, path, content, cancellationToken,
                providerProfileId: reply.ScopeSessionId is { } scope ? ProfileFor(scope) : null)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            EnsureSuccess(response, HttpMethod.Post, path);
        if (!response.IsSuccessStatusCode) return false;
        // Native permission.reply acknowledges with a JSON boolean, not HTTP status alone.
        var acknowledgement = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(acknowledgement.Trim(), "true", StringComparison.Ordinal);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        return new HttpRequestMessage(method, new Uri(BaseUrl, path));
    }

    internal static string WithDirectory(string path, string? directory) => string.IsNullOrWhiteSpace(directory)
        ? path : path + (path.Contains('?') ? "&" : "?") + "directory=" + Uri.EscapeDataString(directory);

    private string WithSessionDirectory(string path, string sessionId) =>
        WithDirectory(path, _sessionDirectories.GetValueOrDefault(sessionId));

    private string? ProfileFor(string sessionId) =>
        _sessionProfiles.TryGetValue(sessionId, out var profile) ? profile : null;

    private void RememberDirectory(OpenCodeSessionResponse session, string? requestedDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(requestedDirectory) ? session.Directory : requestedDirectory;
        if (!string.IsNullOrWhiteSpace(directory)) _sessionDirectories[session.Id] = directory;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        Func<HttpRequestMessage,CancellationToken,Task>? beforeSend = null,
        string? providerProfileId = null)
    {
        if (!string.IsNullOrWhiteSpace(providerProfileId) && _resolveProfileBaseUrl is not null)
        {
            var baseUrl = await _resolveProfileBaseUrl(providerProfileId, cancellationToken).ConfigureAwait(false);
            request.RequestUri = new Uri(baseUrl, request.RequestUri!.PathAndQuery);
            BaseUrl = baseUrl;
        }
        else if (!string.IsNullOrWhiteSpace(providerProfileId) && _resolveBaseUrl is not null)
        {
            throw new OpenCodeClientException("A provider profile cannot use a shared OpenCode server.");
        }
        else if (_resolveBaseUrl is not null)
        {
            var baseUrl = await _resolveBaseUrl(cancellationToken).ConfigureAwait(false);
            request.RequestUri = new Uri(baseUrl, request.RequestUri!.PathAndQuery);
            BaseUrl = baseUrl;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_requestTimeout);
        cancellationToken.ThrowIfCancellationRequested();
        if (beforeSend is not null) await beforeSend(request,timeoutCts.Token).ConfigureAwait(false);
        _managedCredential?.TryApply(request);

        // These endpoints return finite bodies. Keep the request budget alive until the
        // body is buffered; the separate SSE service owns streaming responses.
        return await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken,
        Func<HttpRequestMessage,CancellationToken,Task>? beforeSend = null,
        string? providerProfileId = null)
    {
        using var request = CreateRequest(method, path);

        if (content is not null)
        {
            request.Content = content;
        }

        try
        {
            return await SendAsync(request, cancellationToken, beforeSend, providerProfileId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new OpenCodeClientException(
                $"The {method} {path} request to {BaseUrl} timed out after {_requestTimeout}.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new OpenCodeClientException(
                $"The {method} {path} request to {BaseUrl} failed.",
                exception);
        }
    }

    private static WorkflowValidationException AdaptationTransportUnavailable() => new("Адаптация не передана: проверка на HTTP-границе не настроена или допуск отсутствует.");

    private void EnsureSuccess(HttpResponseMessage response, HttpMethod method, string path)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new OpenCodeClientException(
                $"The {method} {path} request to {BaseUrl} returned status code {(int)response.StatusCode}.",
                response.StatusCode);
        }
    }

    private async Task<string> GetJsonWithFallbackAsync(
        string primaryPath,
        string fallbackPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetJsonAsync(primaryPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenCodeClientException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return await GetJsonAsync(fallbackPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, content: null, cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccess(response, HttpMethod.Get, path);

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<OpenCodeProviderInfo> ParseProviders(string rawJson, string path)
    {
        try
        {
            return OpenCodeDiscoveryJson.ParseProviders(rawJson);
        }
        catch (JsonException exception)
        {
            throw new OpenCodeClientException(
                $"The GET {path} response from {BaseUrl} is not a valid OpenCode provider payload.",
                exception);
        }
    }

    private IReadOnlyList<OpenCodeModelInfo> ParseModels(string rawJson, string path, string? providerId)
    {
        try
        {
            return OpenCodeDiscoveryJson.ParseModels(rawJson, providerId);
        }
        catch (JsonException exception)
        {
            throw new OpenCodeClientException(
                $"The GET {path} response from {BaseUrl} is not a valid OpenCode model payload.",
                exception);
        }
    }

    private OpenCodeConfiguredProvidersResponse ParseConfiguredProviders(string rawJson, string path)
    {
        try
        {
            return OpenCodeDiscoveryJson.ParseConfiguredProviders(rawJson);
        }
        catch (JsonException exception)
        {
            throw new OpenCodeClientException(
                $"The GET {path} response from {BaseUrl} is not a valid OpenCode configured providers payload.",
                exception);
        }
    }

    private OpenCodeSessionResponse ParseSession(string rawJson, HttpMethod method, string path)
    {
        try
        {
            return OpenCodeSessionResponse.Parse(rawJson);
        }
        catch (JsonException exception)
        {
            throw new OpenCodeClientException(
                $"The {method} {path} response from {BaseUrl} is not a valid OpenCode session payload.",
                exception);
        }
    }

    private IReadOnlyList<OpenCodeSessionResponse> ParseSessions(string rawJson, HttpMethod method, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The OpenCode session list payload must be a JSON array.");
            }

            var sessions = new List<OpenCodeSessionResponse>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                sessions.Add(OpenCodeSessionResponse.Parse(element));
            }

            return sessions;
        }
        catch (JsonException exception)
        {
            throw new OpenCodeClientException(
                $"The {method} {path} response from {BaseUrl} is not a valid OpenCode session list payload.",
                exception);
        }
    }
}
