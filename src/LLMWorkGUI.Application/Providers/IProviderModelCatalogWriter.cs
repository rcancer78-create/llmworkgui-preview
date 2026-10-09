namespace LLMWorkGUI.Application.Providers;

/// <summary>
/// Records the backend model ids a provider profile is configured with, so the adaptation route picker
/// can name a real backend model for an account whose record carries no <c>ProviderNativeId</c>. This
/// is the only writer of that local model list; the reader is <see cref="IProviderModelCatalogSource"/>.
/// </summary>
public interface IProviderModelCatalogWriter
{
    /// <summary>
    /// Replaces the configured model list of the profile. Blank or implausible ids are dropped rather
    /// than stored, so a filesystem path, a profile name or a secret URN can never become a route.
    /// </summary>
    Task SaveModelsAsync(
        ProviderModelCatalogQuery query,
        IReadOnlyList<ProviderModelDescriptor> models,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the configured model list of the profile.</summary>
    Task ClearModelsAsync(ProviderModelCatalogQuery query, CancellationToken cancellationToken = default);
}
