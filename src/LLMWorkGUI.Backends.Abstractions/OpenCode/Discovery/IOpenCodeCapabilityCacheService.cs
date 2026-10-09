namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;

public interface IOpenCodeCapabilityCacheService
{
    Task<IReadOnlyList<OpenCodeModelInfo>> GetModelsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpenCodeProviderInfo>> GetProvidersAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<OpenCodeModelInfo?> GetModelAsync(
        string modelId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    CapabilityFreshness GetFreshness();

    void Invalidate();
}
