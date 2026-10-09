using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Providers;

public sealed record GatewayCatalogSnapshot(
    IReadOnlyList<ProviderProfile> Providers,
    IReadOnlyList<Account> Accounts,
    IReadOnlyList<ModelDescriptor> Models,
    IReadOnlyList<Route> Routes,
    IReadOnlyList<QuotaSnapshot> Quotas);

/// <summary>Explicit native discovery and atomic import. Never invoked during application startup.</summary>
public interface IGatewayCatalogImportService
{
    /// <summary>
    /// Queries the configured gateway and inserts missing, disabled catalog entries. Existing user
    /// settings are preserved. Discovery alone grants neither authentication nor response identity.
    /// Returns the discovered snapshot; existing stored settings may differ from its import defaults.
    /// </summary>
    Task<GatewayCatalogSnapshot> ImportAsync(CancellationToken cancellationToken = default);
}
