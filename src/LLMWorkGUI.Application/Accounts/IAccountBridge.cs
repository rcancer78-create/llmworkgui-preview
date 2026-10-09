using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Accounts;

/// <summary>
/// Bridge interface for multi-account discovery, pinning, and authentication probe (ТЗ §6.4, ADR-0004).
/// </summary>
public interface IAccountBridge
{
    string BackendId { get; }

    /// <summary>
    /// Indicates whether this bridge can pin an execution to a specific account before prompts are sent.
    /// If false, plugins remain opaque and cannot participate in automatic multi-account routing.
    /// </summary>
    bool SupportsPinning { get; }

    /// <summary>
    /// Indicates whether this bridge can provide evidence of the observed account route.
    /// </summary>
    bool SupportsObservedRoute { get; }

    Task<IReadOnlyList<DiscoveredAccountInfo>> DiscoverAccountsAsync(
        string providerProfileId,
        CancellationToken cancellationToken = default);

    Task<AccountPinResult> PinAccountAsync(
        string providerProfileId,
        string accountId,
        SessionBinding binding,
        CancellationToken cancellationToken = default);

    Task<AccountAuthProbeResult> ProbeAuthAsync(
        string providerProfileId,
        string accountId,
        CancellationToken cancellationToken = default);
}
