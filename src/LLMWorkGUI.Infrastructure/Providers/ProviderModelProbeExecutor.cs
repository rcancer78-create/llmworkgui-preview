using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>
/// Executes the pinned model probe that verifies a recovery. It performs one minimal OpenAI-compatible
/// chat completion against the provider endpoint so the probe confirms auth, the selected model and a
/// minimal turn — not merely that the endpoint answers (ТЗ §6.10).
///
/// The probe is a separate operation, never a continuation of the user's session, and it is only ever
/// reached behind an explicit operator confirmation with a cost preview.
/// </summary>
public sealed class ProviderModelProbeExecutor : IModelProbeExecutor
{
    public bool CanExecute(ModelProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return !string.IsNullOrWhiteSpace(request.BaseUrl);
    }

    private const string ProbePrompt = "Reply with the single word: ok";
    private const int MaximumResponseBytes = 64 * 1024;

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

    public ProviderModelProbeExecutor(
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

    public async Task<ModelProbeResult> ExecuteAsync(
        ModelProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return ModelProbeResult.Unsupported(
                "The provider has no HTTP base URL, so a pinned model probe cannot be executed for it.");
        }

        var urlValidation = ProviderUrlValidator.Validate(request.BaseUrl);
        if (!urlValidation.IsValid)
        {
            return ModelProbeResult.Failed(
                HealthErrorClass.StartupOrSessionCreation,
                sanitizedEndpoint: null,
                latencyMs: null,
                urlValidation.ErrorMessage ?? "The provider base URL is invalid.");
        }

        string? resolvedApiKey = null;
        if (!string.IsNullOrWhiteSpace(request.ApiKeySecretReference))
        {
            // A configured reference that is Missing or Revoked, or that cannot be resolved, must not
            // turn into an unauthenticated probe: the provider would answer 401 and the audit would
            // record a provider failure instead of a missing credential (ADR-0005 §5.2). Only a
            // provider with no reference at all is a keyless provider.
            var (failure, secret) = await ProviderRequestCredentials.ResolveApiKeyAsync(
                    request.ApiKeySecretReference, request.ProviderProfileId, request.AccountId,
                    _profiles, _accounts, _secretLifecycle, _secretStore, cancellationToken)
                .ConfigureAwait(false);

            if (failure is not null)
            {
                return ModelProbeResult.CredentialUnavailable(failure);
            }

            resolvedApiKey = secret;
        }

        var baseUri = new Uri(urlValidation.NormalizedUrl!, UriKind.Absolute);
        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = path + "/chat/completions";
        }

        var targetUri = builder.Uri;
        var sanitizedUrl = $"{targetUri.Scheme}://{targetUri.Authority}{targetUri.AbsolutePath}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, targetUri)
        {
            Content = new StringContent(BuildBody(request.ModelId), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolvedApiKey);
        }

        var headerFailure = await ProviderRequestHeaders.ApplyAsync(httpRequest, request.ProviderProfileId,
            request.CustomHeaders, _profiles, _secretLifecycle, _secretStore, cancellationToken).ConfigureAwait(false);
        if (headerFailure is not null)
            return ModelProbeResult.CredentialUnavailable(headerFailure);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(15));

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;

        try
        {
            response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            return ModelProbeResult.Failed(
                HealthErrorClass.NetworkOrTimeout,
                sanitizedUrl,
                stopwatch.ElapsedMilliseconds,
                "The pinned model probe timed out.");
        }
        catch (HttpRequestException)
        {
            stopwatch.Stop();

            return ModelProbeResult.Failed(
                HealthErrorClass.NetworkOrTimeout,
                sanitizedUrl,
                stopwatch.ElapsedMilliseconds,
                "The pinned model probe could not reach the provider.");
        }

        using var responseLifetime = response;
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            return ModelProbeResult.Failed(
                ClassifyStatus(statusCode),
                sanitizedUrl,
                stopwatch.ElapsedMilliseconds,
                $"The pinned model probe was rejected with HTTP {statusCode}.");
        }

        // A 2xx only proves that the endpoint answered. ТЗ §6.10 requires the probe to confirm auth, the
        // selected model and a completed minimal turn, so the response body is inspected rather than
        // trusted: a healthy-looking status from a mismatched or broken model must not verify a recovery.
        byte[] content;
        try
        {
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return OversizedResponse();

            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, linkedCts.Token).ConfigureAwait(false);
            content = await response.Content.ReadAsByteArrayAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ModelProbeResult.Failed(HealthErrorClass.NetworkOrTimeout, sanitizedUrl,
                stopwatch.ElapsedMilliseconds, "The pinned model probe timed out while reading the response.");
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            return OversizedResponse();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return ModelProbeResult.Failed(HealthErrorClass.NetworkOrTimeout, sanitizedUrl,
                stopwatch.ElapsedMilliseconds, "The pinned model probe response could not be read.");
        }
        stopwatch.Stop();

        ModelProbeResult OversizedResponse() => ModelProbeResult.Failed(
            HealthErrorClass.MalformedProtocolEvent, sanitizedUrl, stopwatch.ElapsedMilliseconds,
            "The pinned model probe response exceeded the 64 KiB limit.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return ModelProbeResult.Failed(
                HealthErrorClass.MalformedProtocolEvent,
                sanitizedUrl,
                stopwatch.ElapsedMilliseconds,
                "The pinned model probe returned malformed JSON.");
        }

        using (document)
        {
            var root = document.RootElement;

            if (!TryReadReturnedModel(root, out var returnedModel) ||
                !ModelMatches(request.ModelId, returnedModel))
            {
                return ModelProbeResult.Failed(
                    HealthErrorClass.ModelUnavailableOrMismatch,
                    sanitizedUrl,
                    stopwatch.ElapsedMilliseconds,
                    "Model mismatch: the provider did not return the requested model.");
            }

            if (!TryReadCompletedTurn(root, out var turnDetail))
            {
                return ModelProbeResult.Failed(
                    HealthErrorClass.MalformedProtocolEvent,
                    sanitizedUrl,
                    stopwatch.ElapsedMilliseconds,
                    turnDetail);
            }
        }

        return ModelProbeResult.Succeeded(sanitizedUrl, stopwatch.ElapsedMilliseconds);
    }

    private static bool TryReadReturnedModel(JsonElement root, out string? returnedModel)
    {
        returnedModel = null;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("model", out var modelElement) &&
            modelElement.ValueKind == JsonValueKind.String)
        {
            returnedModel = modelElement.GetString();
        }

        return !string.IsNullOrWhiteSpace(returnedModel);
    }

    /// <summary>Generic endpoints provide no authoritative alias map: verify the exact requested id.</summary>
    private static bool ModelMatches(string requestedModelId, string? returnedModel) =>
        !string.IsNullOrWhiteSpace(requestedModelId) &&
        string.Equals(requestedModelId, returnedModel, StringComparison.Ordinal);

    // Only a terminal, well-formed assistant choice verifies a minimal model turn.
    // Tool calls prove model output only; this probe never executes them.
    private static bool TryReadCompletedTurn(JsonElement root, out string detail)
    {
        detail = "The pinned model probe response contained no completed assistant message.";
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return false;

        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object ||
            !choice.TryGetProperty("finish_reason", out var finish) ||
            finish.ValueKind != JsonValueKind.String ||
            !choice.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !HasText(message, "role", "assistant"))
            return false;

        var valid = finish.GetString() switch
        {
            "stop" => HasCompletedText(message) &&
                (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind == JsonValueKind.Null ||
                    (calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() == 0)),
            "tool_calls" => HasCompletedToolCalls(message),
            _ => false
        };
        if (valid) detail = string.Empty;
        return valid;
    }

    private static bool HasText(JsonElement element, string property, string? expected = null) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) &&
        (expected is null || string.Equals(value.GetString(), expected, StringComparison.Ordinal));

    private static bool HasCompletedText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return false;
        if (content.ValueKind == JsonValueKind.String) return !string.IsNullOrWhiteSpace(content.GetString());
        if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0) return false;
        foreach (var part in content.EnumerateArray())
            if (!HasText(part, "type", "text") || !HasText(part, "text")) return false;
        return true;
    }

    private static bool HasCompletedToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) ||
            calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0) return false;
        foreach (var call in calls.EnumerateArray())
        {
            if (!HasText(call, "id") || !HasText(call, "type", "function") ||
                !call.TryGetProperty("function", out var function) || !HasText(function, "name") ||
                !HasText(function, "arguments")) return false;
            try
            {
                using var arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!);
                if (arguments.RootElement.ValueKind != JsonValueKind.Object) return false;
            }
            catch (JsonException) { return false; }
        }
        return true;
    }

    private static string BuildBody(string modelId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = modelId,
            ["messages"] = new[] { new Dictionary<string, string>(StringComparer.Ordinal) { ["role"] = "user", ["content"] = ProbePrompt } },
            ["max_tokens"] = 16,
            ["stream"] = false
        });

    /// <summary>
    /// Maps an HTTP outcome onto the normative taxonomy. A model mismatch is a distinct class, because
    /// it blocks only the specific route rather than the whole account (ТЗ §6.10).
    /// </summary>
    private static HealthErrorClass ClassifyStatus(int statusCode) => statusCode switch
    {
        401 or 403 => HealthErrorClass.AuthenticationOrRefresh,
        404 or 400 or 422 => HealthErrorClass.ModelUnavailableOrMismatch,
        >= 500 => HealthErrorClass.Provider4xx5xx,
        _ => HealthErrorClass.Provider4xx5xx
    };

}
