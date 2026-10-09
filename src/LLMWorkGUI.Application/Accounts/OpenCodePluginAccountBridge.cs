using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Accounts;

/// <summary>
/// Bridge implementation for OpenCode multi-account integration (ТЗ §6.4, §6.11, ADR-0004).
/// If no pinned plugin contract is confirmed, plugins remain opaque and unpinned.
/// OpenCode plugins are never permitted to switch or pin AGY/Codex accounts (ТЗ §6.11).
/// </summary>
public sealed class OpenCodePluginAccountBridge : IAccountBridge
{
    /// <summary>
    /// Exact refusal reason required by ТЗ §6.11 for AGY/Codex account switching through
    /// OpenCode plugins. Codex/AGY routes are owned by the star-cliproxy gateway (ADR-0007).
    /// </summary>
    public const string AgyCodexPluginRefusalReason =
        "OpenCode plugins are not permitted to switch AGY or Codex accounts (ТЗ §6.11). " +
        "Use the star-cliproxy gateway with agy-profile / CODEX_HOME account contexts instead (ADR-0007).";

    private static readonly string[] AgyCodexProviderTokens = ["agy", "antigravity", "codex"];

    private readonly bool _hasMultiAccountPluginContract;

    public string BackendId => "opencode";

    public bool SupportsPinning => false;

    public bool SupportsObservedRoute => false;

    public OpenCodePluginAccountBridge(bool hasMultiAccountPluginContract = false)
    {
        _hasMultiAccountPluginContract = hasMultiAccountPluginContract;
    }

    /// <summary>
    /// Detects provider profile ids that belong to AGY or Codex. Those provider accounts are
    /// never switched or pinned through OpenCode plugins (ТЗ §6.11).
    /// </summary>
    public static bool IsAgyOrCodexProvider(string? providerProfileId)
    {
        if (string.IsNullOrWhiteSpace(providerProfileId))
        {
            return false;
        }

        var tokens = providerProfileId
            .Split(
                new[] { '-', '_', '.', '/', '\\', ':', ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return tokens.Any(token =>
            AgyCodexProviderTokens.Contains(token, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsForbiddenRoute(string providerProfileId, BackendType? backend)
    {
        return backend is BackendType.Agy or BackendType.StarCliProxy || IsAgyOrCodexProvider(providerProfileId);
    }

    public Task<IReadOnlyList<DiscoveredAccountInfo>> DiscoverAccountsAsync(
        string providerProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);

        if (IsForbiddenRoute(providerProfileId, backend: null))
        {
            // Fail closed: the OpenCode plugin bridge must not expose plugin-managed AGY/Codex accounts.
            return Task.FromResult<IReadOnlyList<DiscoveredAccountInfo>>(Array.Empty<DiscoveredAccountInfo>());
        }

        // No discovery API is implemented. A fabricated default row would falsely suggest a
        // native login/context exists even when OpenCode is not installed or authenticated.
        return Task.FromResult<IReadOnlyList<DiscoveredAccountInfo>>(Array.Empty<DiscoveredAccountInfo>());
    }

    public Task<AccountPinResult> PinAccountAsync(
        string providerProfileId,
        string accountId,
        SessionBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);
        ArgumentNullException.ThrowIfNull(accountId);
        ArgumentNullException.ThrowIfNull(binding);

        if (IsForbiddenRoute(providerProfileId, binding.Backend))
        {
            return Task.FromResult(AccountPinResult.Failure(AgyCodexPluginRefusalReason));
        }

        if (!_hasMultiAccountPluginContract)
        {
            return Task.FromResult(AccountPinResult.Failure(
                "OpenCode plugins without pin contract remain opaque routes and do not support automatic account pinning (ADR-0004 §2.3)."));
        }

        // A contract flag is configuration, not an implemented native pin or acknowledgement.
        return Task.FromResult(AccountPinResult.Failure(
            "A plugin contract was declared, but no native account pin adapter is implemented. Account identity remains unconfirmed."));
    }

    public Task<AccountAuthProbeResult> ProbeAuthAsync(
        string providerProfileId,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);
        ArgumentNullException.ThrowIfNull(accountId);

        if (IsForbiddenRoute(providerProfileId, backend: null))
        {
            return Task.FromResult(AccountAuthProbeResult.Unknown(AgyCodexPluginRefusalReason));
        }

        // When no direct auth probe API is confirmed by the plugin, report Unknown rather than false Valid
        if (!_hasMultiAccountPluginContract)
        {
            return Task.FromResult(AccountAuthProbeResult.Unknown(
                "Authentication probe is unsupported without a confirmed plugin bridge contract."));
        }

        return Task.FromResult(AccountAuthProbeResult.Unknown(
            "Authentication probe is unsupported: a declared plugin contract does not implement a native auth probe."));
    }
}
