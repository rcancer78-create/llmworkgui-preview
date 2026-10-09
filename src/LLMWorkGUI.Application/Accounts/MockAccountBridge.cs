using System.Collections.Concurrent;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Accounts;

/// <summary>
/// Deterministic test double for testing multi-account discovery, pinning, and auth state.
/// </summary>
public sealed class MockAccountBridge : IAccountBridge
{
    private readonly ConcurrentDictionary<string, DiscoveredAccountInfo[]> _accountsByProfile = new();
    private readonly ConcurrentDictionary<string, AuthState> _authStateByAccount = new();
    private readonly ConcurrentDictionary<string, SessionBinding> _pinnedBindingsByAccount = new();

    public string BackendId => "opencode-mock";

    public bool SupportsPinning { get; set; } = true;

    public bool SupportsObservedRoute { get; set; } = true;

    public Func<string, string, SessionBinding, AccountPinResult>? OnPinAccount { get; set; }

    public void RegisterAccount(string providerProfileId, DiscoveredAccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);
        ArgumentNullException.ThrowIfNull(account);

        _accountsByProfile.AddOrUpdate(
            providerProfileId,
            _ => new[] { account },
            (_, snapshot) => snapshot.Where(a => a.Id != account.Id).Append(account).ToArray());

        _authStateByAccount[account.Id] = account.AuthState;
    }

    public void SetAuthState(string accountId, AuthState state)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        _authStateByAccount[accountId] = state;
    }

    public SessionBinding? GetPinnedBinding(string accountId)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        return _pinnedBindingsByAccount.TryGetValue(accountId, out var b) ? b : null;
    }

    public Task<IReadOnlyList<DiscoveredAccountInfo>> DiscoverAccountsAsync(
        string providerProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);

        if (_accountsByProfile.TryGetValue(providerProfileId, out var accounts))
        {
            var updated = accounts.Select(a =>
            {
                var state = _authStateByAccount.TryGetValue(a.Id, out var s) ? s : a.AuthState;
                return a with { AuthState = state };
            }).ToList();

            return Task.FromResult<IReadOnlyList<DiscoveredAccountInfo>>(updated);
        }

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

        if (!SupportsPinning)
        {
            return Task.FromResult(AccountPinResult.Failure(
                "Account bridge does not support explicit account pinning. Opaque routing must be used."));
        }

        if (OnPinAccount != null)
        {
            return Task.FromResult(OnPinAccount(providerProfileId, accountId, binding));
        }

        _pinnedBindingsByAccount[accountId] = binding;
        return Task.FromResult(AccountPinResult.Success(binding));
    }

    public Task<AccountAuthProbeResult> ProbeAuthAsync(
        string providerProfileId,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);
        ArgumentNullException.ThrowIfNull(accountId);

        var state = _authStateByAccount.TryGetValue(accountId, out var s) ? s : AuthState.Unknown;

        var result = state switch
        {
            AuthState.Valid => AccountAuthProbeResult.Valid(),
            AuthState.Invalid => AccountAuthProbeResult.Invalid("Authentication token is invalid or expired."),
            _ => AccountAuthProbeResult.Unknown("Authentication status could not be verified.")
        };

        return Task.FromResult(result);
    }
}
