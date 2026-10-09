using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Agy;

/// <summary>
/// Native AGY account bridge over agy-profile (ТЗ §6.4, §6.11a). Account pinning is only
/// advertised when the agy-profile utility is available; the native AGY adapter owns
/// credential handling and no OpenCode plugin participates in AGY account selection.
/// </summary>
public sealed class AgyProfileAccountBridge : IAccountBridge
{
    public const string AgyBackendId = "agy";

    private readonly IAgyProfileService _profileService;

    public AgyProfileAccountBridge(IAgyProfileService profileService)
    {
        ArgumentNullException.ThrowIfNull(profileService);
        _profileService = profileService;
    }

    public string BackendId => AgyBackendId;

    /// <summary>
    /// Pinning is supported only while agy-profile is available (ТЗ §6.11a). When the
    /// utility is missing the route stays opaque and automatic multi-account modes are
    /// forbidden by the fail-closed gate of the routing engine.
    /// </summary>
    public bool SupportsPinning => _profileService.IsAvailable;

    /// <summary>
    /// Profile selection does not provide per-turn backend route evidence.
    /// </summary>
    public bool SupportsObservedRoute => false;

    public async Task<IReadOnlyList<DiscoveredAccountInfo>> DiscoverAccountsAsync(
        string providerProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);

        if (!_profileService.IsAvailable)
        {
            return Array.Empty<DiscoveredAccountInfo>();
        }

        var profiles = await _profileService.ListProfilesAsync(cancellationToken).ConfigureAwait(false);

        return profiles
            .Select(profile => new DiscoveredAccountInfo(
                Id: profile.Name,
                DisplayName: profile.Name,
                ProviderNativeId: profile.Name,
                // A profile name/default marker proves selection, never credential validity.
                AuthState: AuthState.Unknown,
                Email: null,
                IsDefault: profile.IsActive))
            .ToArray();
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

        if (binding.Backend != BackendType.Agy)
        {
            return AccountPinResult.Failure(
                $"The AGY profile bridge only pins BackendType.Agy routes; received '{binding.Backend}'.");
        }

        if (!AgyProfilePolicy.IsValidProfileName(accountId)
            || AgyProfilePolicy.ForbiddenRotationCommands.Contains(accountId, StringComparer.OrdinalIgnoreCase))
            return AccountPinResult.Failure("The requested AGY profile name is invalid or forbidden by policy.");

        if (!_profileService.IsAvailable)
        {
            return AccountPinResult.Failure(
                _profileService.AvailabilityBlocker ?? AgyProfilePolicy.NotInstalledBlocker);
        }

        var activeProfile = await _profileService
            .GetActiveProfileAsync(cancellationToken)
            .ConfigureAwait(false);
        var switched = false;

        if (!string.Equals(activeProfile, accountId, StringComparison.OrdinalIgnoreCase))
        {
            var switchResult = await _profileService
                .SwitchProfileAsync(accountId, cancellationToken)
                .ConfigureAwait(false);

            if (!switchResult.IsSwitched)
            {
                return AccountPinResult.Failure(
                    switchResult.FailureReason ?? $"Switching to AGY profile '{accountId}' failed.");
            }

            if (!string.Equals(switchResult.ActiveProfile, accountId, StringComparison.OrdinalIgnoreCase))
            {
                return AccountPinResult.Failure(
                    $"AGY profile switch was not confirmed: requested '{accountId}', " +
                    $"observed '{switchResult.ActiveProfile ?? "(none)"}'.");
            }

            switched = true;
        }

        // An already-active native profile does not make the caller's previous
        // account/provider binding compatible with that profile's conversation.
        var requiresNewSession = switched
            || !string.Equals(binding.ProviderProfileId, providerProfileId, StringComparison.Ordinal)
            || !string.Equals(binding.AccountId, accountId, StringComparison.Ordinal);

        return AccountPinResult.Success(
            CreateFreshBinding(providerProfileId, accountId, binding),
            requiresNewSession: requiresNewSession);
    }

    public async Task<AccountAuthProbeResult> ProbeAuthAsync(
        string providerProfileId,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);
        ArgumentNullException.ThrowIfNull(accountId);

        if (!_profileService.IsAvailable)
        {
            return AccountAuthProbeResult.Unknown(
                _profileService.AvailabilityBlocker ?? AgyProfilePolicy.NotInstalledBlocker);
        }

        var activeProfile = await _profileService
            .GetActiveProfileAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeProfile is null)
        {
            return AccountAuthProbeResult.Unknown(
                "No valid active AGY profile is reported by agy-profile; run 'agy-profile current' after login.");
        }

        if (!string.Equals(activeProfile, accountId, StringComparison.OrdinalIgnoreCase))
        {
            return AccountAuthProbeResult.Unknown(
                $"AGY profile '{accountId}' is not the active profile ('{activeProfile}'). " +
                "Auth state of a non-active profile cannot be verified without switching accounts.");
        }

        return AccountAuthProbeResult.Unknown(
            "The active AGY profile confirms selection only; native authentication and response-origin identity are not verified.");
    }

    /// <summary>
    /// Builds the confirmed binding for the newly selected account. Invariant (ТЗ §6.11a):
    /// after an account switch a new AGY conversation is mandatory — this binding carries
    /// no native session/conversation id, and any previous conversation must not be resumed
    /// or ported to the new profile. The previous binding is never rewritten in place.
    /// </summary>
    private static SessionBinding CreateFreshBinding(
        string providerProfileId,
        string accountId,
        SessionBinding binding)
    {
        return new SessionBinding(
            backend: BackendType.Agy,
            providerProfileId: providerProfileId,
            accountId: accountId,
            modelId: binding.ModelId,
            reasoningEffort: binding.ReasoningEffort,
            speedMode: binding.SpeedMode,
            executionMode: binding.ExecutionMode);
    }
}
