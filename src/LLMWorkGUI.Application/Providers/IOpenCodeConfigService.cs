using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Application.Providers;

public interface IOpenCodeConfigService
{
    UrlValidationResult ValidateBaseUrl(string? baseUrl);

    string GenerateProviderConfigJson(CustomProviderSettings settings, string? resolvedApiKey = null, bool redactSecrets = false);

    ConfigPreviewResult GeneratePreview(CustomProviderSettings settings, string? existingConfigContent = null, string? resolvedApiKey = null);

    string MergeConfig(string existingConfigContent, CustomProviderSettings settings, string? resolvedApiKey = null);

    /// <summary>
    /// Creates a secret reference without binding any provider. With a lifecycle, metadata has
    /// ProviderApiKey purpose; otherwise it is unregistered. The caller owns persistence/compensation.
    /// This alone does not authorize HTTP use. Prefer the lifecycle's coherent provider save.
    /// </summary>
    Task<string> CreateUnboundApiKeyReferenceAsync(
        string rawApiKey,
        ISecretStore secretStore,
        ISecretLifecycleService? secretLifecycle = null,
        CancellationToken cancellationToken = default);
}
