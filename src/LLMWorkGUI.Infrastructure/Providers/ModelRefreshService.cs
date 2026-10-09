using LLMWorkGUI.Application.Providers;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>
/// Refreshes model inventory from provider endpoints. Identity-only responses
/// do not establish capability support.
/// </summary>
public sealed class ModelRefreshService : IModelRefreshService
{
    private readonly IProviderConnectionTestService _connectionTestService;

    public ModelRefreshService(IProviderConnectionTestService connectionTestService)
    {
        _connectionTestService = connectionTestService ?? throw new ArgumentNullException(nameof(connectionTestService));
    }

    public async Task<IReadOnlyList<DiscoveredModelDetails>> RefreshModelsAsync(
        CustomProviderSettings settings,
        string? explicitApiKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var testResult = await _connectionTestService.TestConnectionAsync(
            settings,
            explicitApiKey,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (testResult.Status == ProviderConnectionStatus.SecretUnavailable)
            throw new ProviderCredentialUnavailableException();

        if (!testResult.IsSuccessful)
        {
            // If dynamic refresh could not reach the provider, return any models pre-configured in settings
            if (settings.Models != null && settings.Models.Count > 0)
            {
                return settings.Models.Select(m => ModelCapabilityDetector.DetectCapabilities(m.ModelId, m.DisplayName)).ToArray();
            }

            // An unavailable inventory is not a confirmed empty inventory. In particular,
            // the editor must not overwrite its saved catalog after a failed HTTP probe.
            throw new ProviderModelDiscoveryUnavailableException(testResult.Status);
        }

        var results = new List<DiscoveredModelDetails>(testResult.DiscoveredModels.Count);
        foreach (var model in testResult.DiscoveredModels)
        {
            var details = ModelCapabilityDetector.DetectCapabilities(model.Id, model.Name, model.OwnedBy);
            results.Add(details);
        }

        return results;
    }
}
