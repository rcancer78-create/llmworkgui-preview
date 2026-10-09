using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>Explicit Chat support for synthetic workflow fixtures. Production inventory
/// identities alone cannot establish this capability.</summary>
internal sealed class ChatCapabilityFixtureCatalog(ISanitizedCatalogProvider inner) : ISanitizedCatalogProvider
{
    public async Task<SanitizedCapabilityCatalog> GetSanitizedCatalogAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await inner.GetSanitizedCatalogAsync(cancellationToken);
        return new(catalog.Providers,
            catalog.Models.Select(model => model with { Capabilities = ModelCapabilityFlags.Chat }).ToArray(),
            catalog.GeneratedAtUtc);
    }
}
