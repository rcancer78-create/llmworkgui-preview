using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.StarCliProxy;

/// <summary>
/// Account bridge for Codex/AGY routed through the star-cliproxy gateway (ADR-0007, ТЗ §6.4, §6.11a).
/// Codex pinning binds the account's immutable absolute CODEX_HOME; AGY pinning performs a
/// serialized agy-profile switch. Every select → verify → launch sequence runs under the shared
/// account lock of <see cref="IAccountContextManager"/>. OpenCode is never involved.
/// </summary>
public sealed class StarCliProxyAccountBridge : IAccountBridge
{
    public const string StarCliProxyBackendId = "star-cliproxy";

    private readonly IAccountContextManager _contextManager;
    private readonly IStarCliProxyAvailabilityProbe? _availabilityProbe;

    public StarCliProxyAccountBridge(
        IAccountContextManager contextManager,
        IStarCliProxyAvailabilityProbe? availabilityProbe = null)
    {
        ArgumentNullException.ThrowIfNull(contextManager);

        _contextManager = contextManager;
        _availabilityProbe = availabilityProbe;
    }

    public string BackendId => StarCliProxyBackendId;

    /// <summary>Pinning is supported only while at least one valid account context exists (fail-closed).</summary>
    public bool SupportsPinning => _contextManager.HasPinnableContexts;

    /// <summary>Observed route evidence comes from star-cliproxy responses; requires a pinnable context.</summary>
    public bool SupportsObservedRoute => _contextManager.HasPinnableContexts;

    public async Task<IReadOnlyList<DiscoveredAccountInfo>> DiscoverAccountsAsync(
        string providerProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);

        var accounts = new List<DiscoveredAccountInfo>();

        foreach (var context in _contextManager.CodexContexts)
        {
            // A present CODEX_HOME is only a configured context. It does not prove that
            // native credentials are valid or that star-cliproxy served a request for it.
            // Keep discovery fail-closed until an independent native auth/identity probe exists.
            accounts.Add(new DiscoveredAccountInfo(
                Id: FormatCodexAccountId(context.AccountId),
                DisplayName: context.DisplayName,
                ProviderNativeId: context.CodexHomePath,
                AuthState: AuthState.Unknown,
                Email: null,
                IsDefault: false));
        }

        var agyContexts = await _contextManager.ListAgyContextsAsync(cancellationToken).ConfigureAwait(false);

        if (agyContexts.Count > 0)
        {
            var activeProfile = await _contextManager
                .GetActiveAgyProfileAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var context in agyContexts)
            {
                var isActive = string.Equals(activeProfile, context.ProfileName, StringComparison.OrdinalIgnoreCase);

                accounts.Add(new DiscoveredAccountInfo(
                    Id: FormatAgyAccountId(context.ProfileName),
                    DisplayName: context.ProfileName,
                    ProviderNativeId: context.ProfileName,
                    // The active profile proves selection only; agy-profile does not provide
                    // independent credential validity or response-origin identity here.
                    AuthState: AuthState.Unknown,
                    Email: null,
                    IsDefault: isActive));
            }
        }

        return accounts;
    }

    public async Task<AccountPinResult> PinAccountAsync(
        string providerProfileId,
        string accountId,
        SessionBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);
        ArgumentNullException.ThrowIfNull(accountId);
        ArgumentNullException.ThrowIfNull(binding);

        if (binding.Backend != BackendType.StarCliProxy)
        {
            return AccountPinResult.Failure(
                $"The star-cliproxy account bridge only pins BackendType.StarCliProxy routes; received '{binding.Backend}'.");
        }

        if (!TryParseAccountId(accountId, out var kind, out var nativeAccountId))
        {
            return AccountPinResult.Failure(
                $"Account id '{accountId}' is not a star-cliproxy account context id; expected " +
                $"'{StarCliProxyPolicy.CodexAccountIdPrefix}<account>' or '{StarCliProxyPolicy.AgyAccountIdPrefix}<profile>'.");
        }

        var request = new AccountContextRequest(BackendType.StarCliProxy, kind, nativeAccountId);

        return await _contextManager
            .ExecuteSerializedAsync(
                request,
                (selection, _) =>
                {
                    if (!selection.IsResolved)
                    {
                        return Task.FromResult(AccountPinResult.Failure(
                            selection.FailureReason ?? $"Account context for '{accountId}' could not be resolved."));
                    }

                    // Invariant (ТЗ §6.11a): changing the account context requires a new native
                    // session; the previous binding is never rewritten and no conversation is ported.
                    // selection.RequiresNewSession is surfaced so callers reset any native session id.
                    return Task.FromResult(AccountPinResult.Success(
                        CreateFreshBinding(providerProfileId, accountId, binding),
                        selection.RequiresNewSession
                            || !string.Equals(binding.ProviderProfileId, providerProfileId, StringComparison.Ordinal)
                            || !string.Equals(binding.AccountId, accountId, StringComparison.Ordinal)));
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AccountAuthProbeResult> ProbeAuthAsync(
        string providerProfileId,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);
        ArgumentNullException.ThrowIfNull(accountId);

        if (_availabilityProbe is { IsAvailable: false })
        {
            return AccountAuthProbeResult.Unknown(
                _availabilityProbe.AvailabilityBlocker ?? StarCliProxyPolicy.NotInstalledBlocker);
        }

        if (!TryParseAccountId(accountId, out var kind, out var nativeAccountId))
        {
            return AccountAuthProbeResult.Unknown(
                $"Account id '{accountId}' is not a star-cliproxy account context id.");
        }

        var request = new AccountContextRequest(BackendType.StarCliProxy, kind, nativeAccountId);

        // Read-only verification: never switches an AGY profile during an auth probe.
        var selection = await _contextManager
            .VerifyContextAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!selection.IsResolved)
        {
            return AccountAuthProbeResult.Unknown(selection.FailureReason);
        }

        // Context resolution confirms only that the requested CODEX_HOME/profile can be
        // selected without mutating the native context. It is not an authentication proof.
        return AccountAuthProbeResult.Unknown(
            "The star-cliproxy account context resolved, but native authentication and response-origin identity were not independently verified.");
    }

    public static string FormatCodexAccountId(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return StarCliProxyPolicy.CodexAccountIdPrefix + accountId;
    }

    public static string FormatAgyAccountId(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        return StarCliProxyPolicy.AgyAccountIdPrefix + profileName;
    }

    /// <summary>
    /// Parses a discovered star-cliproxy account id into its context kind and native account id.
    /// Ids without a known prefix are rejected so opaque ids never reach an account context.
    /// </summary>
    public static bool TryParseAccountId(
        string? accountId,
        out AccountContextKind kind,
        out string nativeAccountId)
    {
        kind = AccountContextKind.Codex;
        nativeAccountId = string.Empty;

        if (string.IsNullOrWhiteSpace(accountId))
        {
            return false;
        }

        if (accountId.StartsWith(StarCliProxyPolicy.CodexAccountIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            kind = AccountContextKind.Codex;
            nativeAccountId = accountId[StarCliProxyPolicy.CodexAccountIdPrefix.Length..].Trim();

            return nativeAccountId.Length > 0;
        }

        if (accountId.StartsWith(StarCliProxyPolicy.AgyAccountIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            kind = AccountContextKind.Agy;
            nativeAccountId = accountId[StarCliProxyPolicy.AgyAccountIdPrefix.Length..].Trim();

            return nativeAccountId.Length > 0;
        }

        return false;
    }

    private static SessionBinding CreateFreshBinding(
        string providerProfileId,
        string accountId,
        SessionBinding binding)
    {
        return new SessionBinding(
            backend: BackendType.StarCliProxy,
            providerProfileId: providerProfileId,
            accountId: accountId,
            modelId: binding.ModelId,
            reasoningEffort: binding.ReasoningEffort,
            speedMode: binding.SpeedMode,
            executionMode: binding.ExecutionMode);
    }
}
