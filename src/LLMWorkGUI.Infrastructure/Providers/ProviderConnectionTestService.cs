using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Infrastructure.Http;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>
/// Executes non-destructive connection health checks against LLM provider base URLs,
/// classifying outcomes into distinct, actionable statuses and ensuring secret redaction.
/// </summary>
public sealed class ProviderConnectionTestService : IProviderConnectionTestService
{
    public const int MaxModelsResponseBytes = 4 * 1024 * 1024;
    private static readonly HttpClient s_defaultHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(5)
    });

    private readonly ISecretStore? _secretStore;
    private readonly ISecretLifecycleService? _secretLifecycle;
    private readonly HttpClient _httpClient;
    private readonly IProviderProfileRepository? _profiles;
    private readonly IAccountRepository? _accounts;

    public ProviderConnectionTestService(
        ISecretStore? secretStore = null,
        HttpClient? httpClient = null,
        ISecretLifecycleService? secretLifecycle = null,
        IProviderProfileRepository? profiles = null,
        IAccountRepository? accounts = null)
    {
        _secretStore = secretStore;
        _httpClient = httpClient ?? s_defaultHttpClient;
        _secretLifecycle = secretLifecycle;
        _profiles = profiles;
        _accounts = accounts;
    }

    public async Task<ProviderConnectionTestResult> TestConnectionAsync(
        CustomProviderSettings settings,
        string? explicitApiKey = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 1. URL validation (avoids sending packets to insecure remote HTTP endpoints)
        var urlValidation = ProviderUrlValidator.Validate(settings.BaseUrl);
        if (!urlValidation.IsValid)
        {
            var fallbackUrl = urlValidation.NormalizedUrl ?? settings.BaseUrl ?? string.Empty;
            if (urlValidation.Classification == UrlClassification.InsecureRemoteHttp)
            {
                return ProviderConnectionTestResult.CreateFailure(
                    ProviderConnectionStatus.InsecureRemoteHttp,
                    fallbackUrl,
                    urlValidation.ErrorMessage ?? "Insecure remote HTTP URL is not permitted. Use HTTPS for external providers, or localhost/127.0.0.1 for local servers.");
            }

            return ProviderConnectionTestResult.CreateFailure(
                ProviderConnectionStatus.InvalidUrlFormat,
                fallbackUrl,
                urlValidation.ErrorMessage ?? "Invalid provider URL format.");
        }

        // 2. Construct target URL
        var baseUri = new Uri(urlValidation.NormalizedUrl!, UriKind.Absolute);
        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = path + "/models";
        }

        var targetUri = builder.Uri;
        var sanitizedUrl = $"{targetUri.Scheme}://{targetUri.Authority}{targetUri.AbsolutePath}";

        // 3. Resolve API Key
        string? resolvedApiKey = explicitApiKey;
        if (string.IsNullOrWhiteSpace(resolvedApiKey) && !string.IsNullOrWhiteSpace(settings.ApiKeySecretRef))
        {
            // A configured reference that is Missing or Revoked, or that cannot be resolved, must fail
            // closed: sending the request without credentials would test the wrong thing and hide the
            // reason from the operator (ADR-0005 §5.2). Only a provider that has no reference at all
            // stays a keyless provider.
            var (refusal, secret) = await ProviderRequestCredentials.ResolveApiKeyAsync(
                    settings.ApiKeySecretRef, settings.ProviderId, settings.AccountId,
                    _profiles, _accounts, _secretLifecycle, _secretStore, cancellationToken)
                .ConfigureAwait(false);

            if (refusal is not null)
            {
                return UnusableCredential(sanitizedUrl, refusal);
            }

            resolvedApiKey = secret;
        }

        // 4. Build Request
        using var request = new HttpRequestMessage(HttpMethod.Get, targetUri);
        if (!string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedApiKey);
        }

        var headerFailure = await ProviderRequestHeaders.ApplyAsync(request, settings.ProviderId,
            settings.CustomHeaders, _profiles, _secretLifecycle, _secretStore, cancellationToken).ConfigureAwait(false);
        if (headerFailure is not null) return UnusableCredential(sanitizedUrl, headerFailure);

        // 5. Send with timeout & stopwatch
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(8);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(effectiveTimeout);

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return ProviderConnectionTestResult.CreateFailure(
                ProviderConnectionStatus.TimedOut,
                sanitizedUrl,
                $"Connection timed out after {effectiveTimeout.TotalSeconds:F0} seconds.",
                latencyMs: sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            var status = ClassifyHttpException(ex);
            var rawMsg = FormatHttpErrorMessage(status, ex);
            var sanitizedMsg = SanitizeErrorMessage(rawMsg, resolvedApiKey);

            return ProviderConnectionTestResult.CreateFailure(
                status,
                sanitizedUrl,
                sanitizedMsg,
                statusCode: (int?)ex.StatusCode,
                latencyMs: sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            sw.Stop();
            return ProviderConnectionTestResult.CreateFailure(
                ProviderConnectionStatus.UnknownError,
                sanitizedUrl,
                "An unexpected transport error occurred.",
                latencyMs: sw.ElapsedMilliseconds);
        }

        sw.Stop();
        using var responseLifetime = response;
        var statusCode = (int)response.StatusCode;

        // 6. Handle HTTP Status Codes
        if (response.IsSuccessStatusCode)
        {
            string content;
            sw.Start();
            try
            {
                content = await BoundedHttpContent.ReadTextAsync(response.Content, MaxModelsResponseBytes,
                    linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ProviderConnectionTestResult.CreateFailure(ProviderConnectionStatus.TimedOut, sanitizedUrl,
                    "The provider response body did not complete within the connection deadline.",
                    statusCode: statusCode, latencyMs: sw.ElapsedMilliseconds);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or HttpRequestException)
            {
                return ProviderConnectionTestResult.CreateFailure(ProviderConnectionStatus.UnknownError, sanitizedUrl,
                    "The provider model response could not be read within the permitted byte limit.",
                    statusCode: statusCode, latencyMs: sw.ElapsedMilliseconds);
            }
            finally { sw.Stop(); }
            var models = ParseModelsFromJson(content);

            return ProviderConnectionTestResult.CreateSuccess(
                sanitizedUrl,
                sw.ElapsedMilliseconds,
                models,
                statusCode);
        }

        if (statusCode is 401 or 403)
        {
            return ProviderConnectionTestResult.CreateFailure(
                ProviderConnectionStatus.AuthenticationFailed,
                sanitizedUrl,
                $"Authentication failed (HTTP {statusCode}). The API key or token was rejected by the provider.",
                statusCode: statusCode,
                latencyMs: sw.ElapsedMilliseconds);
        }

        if (statusCode == 404)
        {
            return ProviderConnectionTestResult.CreateFailure(
                ProviderConnectionStatus.EndpointNotFound,
                sanitizedUrl,
                "The models endpoint was not found (HTTP 404). Verify the base URL path prefix (e.g. /v1).",
                statusCode: statusCode,
                latencyMs: sw.ElapsedMilliseconds);
        }

        if (statusCode >= 500)
        {
            return ProviderConnectionTestResult.CreateFailure(
                ProviderConnectionStatus.RemoteServerError,
                sanitizedUrl,
                $"Remote server returned an internal server error (HTTP {statusCode}).",
                statusCode: statusCode,
                latencyMs: sw.ElapsedMilliseconds);
        }

        return ProviderConnectionTestResult.CreateFailure(
            ProviderConnectionStatus.RemoteServerError,
            sanitizedUrl,
            $"Remote server returned HTTP {statusCode}.",
            statusCode: statusCode,
            latencyMs: sw.ElapsedMilliseconds);
    }

    private static ProviderConnectionTestResult UnusableCredential(string sanitizedUrl, string reason)
    {
        return ProviderConnectionTestResult.CreateFailure(
            ProviderConnectionStatus.SecretUnavailable,
            sanitizedUrl,
            reason);
    }

    private static ProviderConnectionStatus ClassifyHttpException(HttpRequestException ex)
    {
        if (ex.InnerException is AuthenticationException ||
            ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderConnectionStatus.SslHandshakeError;
        }

        if (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            return ProviderConnectionStatus.ConnectionRefused;
        }

        if (ex.InnerException is SocketException sockEx)
        {
            if (sockEx.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.HostUnreachable)
            {
                return ProviderConnectionStatus.ConnectionRefused;
            }
        }

        return ProviderConnectionStatus.ConnectionRefused;
    }

    private static string FormatHttpErrorMessage(ProviderConnectionStatus status, HttpRequestException ex)
    {
        return status switch
        {
            ProviderConnectionStatus.SslHandshakeError =>
                "TLS/SSL handshake failed. The remote server certificate is invalid, untrusted, or has expired.",
            ProviderConnectionStatus.ConnectionRefused =>
                "Could not connect to provider. Connection was refused or host was unreachable.",
            _ => ex.Message
        };
    }

    private static IReadOnlyList<DiscoveredModelInfo> ParseModelsFromJson(string json)
    {
        var list = new List<DiscoveredModelInfo>();
        try
        {
            var node = JsonNode.Parse(json);
            if (node == null) return list;

            // 1. OpenAI format: { "data": [ { "id": "...", "name": "..." } ] }
            if (node["data"] is JsonArray dataArray)
            {
                foreach (var item in dataArray)
                {
                    if (item is JsonObject obj)
                    {
                        var id = obj["id"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            var name = obj["name"]?.GetValue<string>() ?? id;
                            var ownedBy = obj["owned_by"]?.GetValue<string>();
                            list.Add(new DiscoveredModelInfo(id, name, ownedBy));
                        }
                    }
                }
                return list;
            }

            // 2. Ollama format: { "models": [ { "name": "...", "model": "..." } ] }
            if (node["models"] is JsonArray modelsArray)
            {
                foreach (var item in modelsArray)
                {
                    if (item is JsonObject obj)
                    {
                        var id = obj["name"]?.GetValue<string>() ?? obj["model"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            list.Add(new DiscoveredModelInfo(id, id, "ollama"));
                        }
                    }
                }
                return list;
            }

            // 3. Root array format: [ { "id": "..." } ]
            if (node is JsonArray rootArray)
            {
                foreach (var item in rootArray)
                {
                    if (item is JsonObject obj)
                    {
                        var id = obj["id"]?.GetValue<string>() ?? obj["name"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            list.Add(new DiscoveredModelInfo(id, id));
                        }
                    }
                }
            }
        }
        catch
        {
            // Non-fatal if body is not valid JSON; return empty list
        }

        return list;
    }

    private static string SanitizeErrorMessage(string message, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 3)
        {
            return message;
        }

        return message.Replace(secret, "***REDACTED***", StringComparison.Ordinal);
    }
}
