using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LLMWorkGUI.Infrastructure.Http;

namespace LLMWorkGUI.Infrastructure.Mirasim;

public sealed class MirasimClient : IMirasimClient, IDisposable
{
    public const int MaxHealthResponseBytes = 1024 * 1024;
    public const int MaxErrorResponseBytes = 64 * 1024;
    private const string HealthPath = "api/health";

    private const string StatePath = "api/state";

    private readonly HttpClient _httpClient;
    private readonly MirasimOptions _options;
    private readonly ILogger<MirasimClient> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsHttpClient;
    private int _disposed;

    public MirasimClient(
        HttpClient httpClient,
        IOptions<MirasimOptions> options,
        ILogger<MirasimClient>? logger = null,
        TimeProvider? timeProvider = null,
        bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _options.Validate();

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _logger = logger ?? NullLogger<MirasimClient>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;

        BaseUrl = new Uri($"http://{_options.Hostname}:{_options.Port}", UriKind.Absolute);
    }

    public Uri BaseUrl { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsHttpClient) _httpClient.Dispose();
    }

    public async Task<MirasimHealthStatus> ProbeHealthAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(HealthPath));
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "The Mirasim health probe to {BaseUrl} failed with HTTP {StatusCode}.",
                    BaseUrl,
                    (int)response.StatusCode);

                return MirasimHealthStatus.Unavailable(
                    $"The Mirasim health endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"}).");
            }

            var body = await BoundedHttpContent.ReadTextAsync(response.Content, MaxHealthResponseBytes,
                requestCts.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            return ParseHealthStatus(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return MirasimHealthStatus.Unavailable(
                $"The Mirasim health probe timed out after {_options.RequestTimeout}.");
        }
        catch (HttpRequestException exception)
        {
            return MirasimHealthStatus.Unavailable(
                $"The Mirasim instance is unreachable on loopback ({exception.Message}).");
        }
        catch (JsonException)
        {
            return MirasimHealthStatus.Unavailable(
                "The Mirasim health endpoint returned a malformed JSON payload.");
        }
        catch (InvalidDataException)
        {
            return MirasimHealthStatus.Unavailable("The Mirasim health response exceeded the permitted resource bounds.");
        }
        catch (IOException)
        {
            return MirasimHealthStatus.Unavailable(
                "The Mirasim health response could not be read to completion.");
        }
    }

    public async Task<bool> ProbeAuthenticatedEndpointAsync(
        string? testToken = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(StatePath));

            if (!string.IsNullOrWhiteSpace(testToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", testToken);
            }

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCts.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning(
                    "The Mirasim authenticated probe to {BaseUrl} was rejected with HTTP 401; a local access token is required.",
                    BaseUrl);

                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorCode = await ReadErrorCodeAsync(response, requestCts.Token).ConfigureAwait(false);

                _logger.LogWarning(
                    "The Mirasim authenticated probe to {BaseUrl} failed with HTTP {StatusCode}.",
                    BaseUrl,
                    (int)response.StatusCode);

                throw new MirasimClientException(
                    $"The Mirasim authenticated probe to {BaseUrl} failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"}).",
                    response.StatusCode,
                    errorCode);
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MirasimClientException(
                $"The Mirasim authenticated probe to {BaseUrl} timed out after {_options.RequestTimeout}.");
        }
        catch (HttpRequestException exception)
        {
            throw new MirasimClientException(
                $"The Mirasim authenticated probe to {BaseUrl} is unreachable on loopback ({exception.Message}).",
                exception);
        }
    }

    private static MirasimHealthStatus ParseHealthStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return MirasimHealthStatus.Unavailable(
                "The Mirasim health endpoint returned a payload that is not a JSON object.");
        }

        var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;

        return new MirasimHealthStatus
        {
            Ok = ok,
            Name = TryGetString(root, "name"),
            Version = TryGetString(root, "version"),
            Pid = TryGetInt(root, "pid"),
            InstanceId = TryGetString(root, "instance"),
            Uptime = TryGetLong(root, "uptime"),
            ErrorMessage = ok ? null : "The Mirasim health endpoint reported that the instance is not ok."
        };
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await BoundedHttpContent.ReadTextAsync(response.Content, MaxErrorResponseBytes,
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException) { return null; }
        catch (IOException)
        {
            return null;
        }
    }

    private Uri BuildUri(string relativePath) => new(BaseUrl, relativePath);

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static long? TryGetLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out var value)
            ? value
            : null;
    }
}
