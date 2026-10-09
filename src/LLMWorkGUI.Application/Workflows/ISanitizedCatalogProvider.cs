namespace LLMWorkGUI.Application.Workflows;

public interface ISanitizedCatalogProvider
{
    Task<SanitizedCapabilityCatalog> GetSanitizedCatalogAsync(CancellationToken cancellationToken = default);
}
