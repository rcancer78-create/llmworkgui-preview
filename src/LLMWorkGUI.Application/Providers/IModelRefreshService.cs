namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Service for refreshing the catalog of available models and discovering their capabilities.
/// </summary>
public interface IModelRefreshService
{
    Task<IReadOnlyList<DiscoveredModelDetails>> RefreshModelsAsync(
        CustomProviderSettings settings,
        string? explicitApiKey = null,
        CancellationToken cancellationToken = default);
}
