using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Providers;

public sealed class OpenCodeConfigService : IOpenCodeConfigService
{
    private const string RedactedPlaceholder = "***REDACTED***";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public UrlValidationResult ValidateBaseUrl(string? baseUrl) =>
        ProviderUrlValidator.Validate(baseUrl);

    public string GenerateProviderConfigJson(
        CustomProviderSettings settings,
        string? resolvedApiKey = null,
        bool redactSecrets = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var providerNode = BuildProviderNode(settings, resolvedApiKey, redactSecrets);
        return providerNode.ToJsonString(JsonOptions);
    }

    public ConfigPreviewResult GeneratePreview(
        CustomProviderSettings settings,
        string? existingConfigContent = null,
        string? resolvedApiKey = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var fullProviderNode = BuildProviderNode(settings, resolvedApiKey, redactSecrets: false);
        var redactedProviderNode = BuildProviderNode(settings, resolvedApiKey, redactSecrets: true);

        var fullProviderJson = fullProviderNode.ToJsonString(JsonOptions);
        var redactedProviderJson = redactedProviderNode.ToJsonString(JsonOptions);

        if (string.IsNullOrWhiteSpace(existingConfigContent))
        {
            var diffBuilder = new StringBuilder();
            foreach (var line in redactedProviderJson.Split('\n'))
            {
                diffBuilder.AppendLine($"+ {line.TrimEnd('\r')}");
            }

            return new ConfigPreviewResult(
                fullProviderJson,
                redactedProviderJson,
                diffBuilder.ToString().TrimEnd(),
                HasChanges: true,
                HasExistingConfig: false);
        }

        var mergedFull = MergeConfigInternal(existingConfigContent, settings, resolvedApiKey, redactSecrets: false);
        var mergedRedacted = MergeConfigInternal(existingConfigContent, settings, resolvedApiKey, redactSecrets: true);

        var normalizedExisting = NormalizeJson(existingConfigContent);
        var hasChanges = !string.Equals(normalizedExisting, mergedFull, StringComparison.Ordinal);

        var diff = ComputeDiff(RedactExistingConfig(existingConfigContent), mergedRedacted);

        return new ConfigPreviewResult(
            mergedFull,
            mergedRedacted,
            diff,
            hasChanges,
            HasExistingConfig: true);
    }

    public string MergeConfig(
        string existingConfigContent,
        CustomProviderSettings settings,
        string? resolvedApiKey = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return MergeConfigInternal(existingConfigContent, settings, resolvedApiKey, redactSecrets: false);
    }

    public async Task<string> CreateUnboundApiKeyReferenceAsync(
        string rawApiKey,
        ISecretStore secretStore,
        ISecretLifecycleService? secretLifecycle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            throw new ArgumentException("API key cannot be empty.", nameof(rawApiKey));
        }

        // Creation is deliberately unbound. The caller must persist its owner or compensate the
        // value; HTTP probes do not accept an unbound or unregistered reference.
        if (secretLifecycle is not null)
        {
            var status = await secretLifecycle
                .CreateAsync(rawApiKey, SecretReferenceKind.ProviderApiKey, owner: null, cancellationToken)
                .ConfigureAwait(false);

            return status.Reference;
        }

        return await secretStore.SaveSecretAsync(rawApiKey.Trim(), cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject BuildProviderNode(
        CustomProviderSettings settings,
        string? resolvedApiKey,
        bool redactSecrets)
    {
        var providerObj = new JsonObject();
        var optionsObj = new JsonObject
        {
            ["baseURL"] = settings.BaseUrl
        };

        if (!string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            optionsObj["apiKey"] = redactSecrets ? RedactedPlaceholder : resolvedApiKey;
        }

        if (settings.CustomHeaders.Count > 0)
        {
            var headersObj = new JsonObject();
            foreach (var header in settings.CustomHeaders)
            {
                headersObj[header.Name] = (header.SecretReference is not null || (redactSecrets && (header.IsSecret || CredentialTextRedactor.IsSensitiveHeaderName(header.Name))))
                    ? RedactedPlaceholder
                    : header.Value;
            }
            optionsObj["headers"] = headersObj;
        }

        providerObj["options"] = optionsObj;

        if (settings.Models.Count > 0)
        {
            var modelsObj = new JsonObject();
            foreach (var model in settings.Models)
            {
                var modelObj = new JsonObject
                {
                    ["name"] = model.DisplayName
                };

                if (model.Variants.Count > 0)
                {
                    var variantsObj = new JsonObject();
                    foreach (var variant in model.Variants)
                    {
                        variantsObj[variant] = true;
                    }
                    modelObj["variants"] = variantsObj;
                }

                modelsObj[model.ModelId] = modelObj;
            }

            providerObj["models"] = modelsObj;
        }

        // These headers have explicit IsSecret metadata; imported JSON does not.
        if (redactSecrets) RedactConfigNode(providerObj, redactOpaqueHeaders: false);
        return providerObj;
    }

    private static string MergeConfigInternal(
        string? existingContent,
        CustomProviderSettings settings,
        string? resolvedApiKey,
        bool redactSecrets)
    {
        JsonObject root;
        if (!string.IsNullOrWhiteSpace(existingContent))
        {
            try
            {
                var docOptions = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                var parsed = JsonNode.Parse(existingContent, null, docOptions);
                root = parsed as JsonObject
                    ?? throw new InvalidDataException("Existing OpenCode configuration must be a JSON object.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Existing OpenCode configuration is malformed; no replacement was generated.", exception);
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (!root.ContainsKey("$schema"))
        {
            root["$schema"] = "https://opencode.ai/config.json";
        }

        JsonObject providersObj;
        if (root.TryGetPropertyValue("provider", out var providerNode) && providerNode is JsonObject existingProviders)
        {
            providersObj = existingProviders;
        }
        else
        {
            providersObj = new JsonObject();
            root["provider"] = providersObj;
        }

        // Imported JSON has no IsSecret metadata for custom headers. Redact the
        // entire existing document before replacing the explicitly configured provider.
        if (redactSecrets) RedactConfigNode(root);
        providersObj[settings.ProviderId] = BuildProviderNode(settings, resolvedApiKey, redactSecrets);

        return root.ToJsonString(JsonOptions);
    }

    private static string NormalizeJson(string rawJson)
    {
        try
        {
            var docOptions = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            var node = JsonNode.Parse(rawJson, null, docOptions);
            return node != null ? node.ToJsonString(JsonOptions) : rawJson.Trim();
        }
        catch
        {
            return rawJson.Trim();
        }
    }

    private static string RedactExistingConfig(string rawJson)
    {
        try
        {
            var node = JsonNode.Parse(rawJson, null, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (node is JsonObject root)
            {
                RedactConfigNode(root);
                return root.ToJsonString(JsonOptions);
            }
        }
        catch (JsonException) { }
        // Never copy malformed input (or scalar/array documents) into a visible diff.
        return "[Existing configuration is invalid; contents hidden]";
    }

    private static void RedactConfigNode(JsonNode? node, bool redactOpaqueHeaders = true)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (CredentialTextRedactor.IsSensitiveHeaderName(property.Key)
                    || (redactOpaqueHeaders && property.Key.Equals("headers", StringComparison.OrdinalIgnoreCase))
                    || property.Key.Equals("command", StringComparison.OrdinalIgnoreCase))
                    obj[property.Key] = RedactedPlaceholder;
                else if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[property.Key] = RedactConfigText(text);
                else
                    RedactConfigNode(property.Value, redactOpaqueHeaders);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                    array[index] = RedactConfigText(text);
                else RedactConfigNode(array[index], redactOpaqueHeaders);
            }
        }
    }

    private static string RedactConfigText(string text) => new CredentialTextRedactor()
        .RedactDiagnostic(text).Replace(CredentialTextRedactor.Placeholder, RedactedPlaceholder, StringComparison.Ordinal);

    private static string ComputeDiff(string original, string updated)
    {
        var originalLines = original.Split('\n');
        var updatedLines = updated.Split('\n');

        var sb = new StringBuilder();
        var maxLines = Math.Max(originalLines.Length, updatedLines.Length);

        for (var i = 0; i < maxLines; i++)
        {
            var orig = i < originalLines.Length ? originalLines[i].TrimEnd('\r') : null;
            var mod = i < updatedLines.Length ? updatedLines[i].TrimEnd('\r') : null;

            if (orig == mod)
            {
                sb.AppendLine($"  {orig}");
            }
            else
            {
                if (orig != null)
                {
                    sb.AppendLine($"- {orig}");
                }
                if (mod != null)
                {
                    sb.AppendLine($"+ {mod}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }
}
