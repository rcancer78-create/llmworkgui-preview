using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Read-only, local and credential-free view of the backend-native model ids that are configured for
/// one provider profile on one backend. The source exists because a normal OpenCode account carries
/// no <c>ProviderNativeId</c>: the only real model ids the machine knows are the ones the local
/// provider configuration declares, and they have to be reachable without network discovery.
/// </summary>
public interface IProviderModelCatalogSource
{
    /// <summary>
    /// Returns the configured backend model ids for the queried profile, or an empty list when the
    /// profile has none. Implementations never fall back to the account id, a profile name, a home
    /// path or any secret material.
    /// </summary>
    Task<IReadOnlyList<ProviderModelDescriptor>> ListModelsAsync(
        ProviderModelCatalogQuery query,
        CancellationToken cancellationToken = default);
}
