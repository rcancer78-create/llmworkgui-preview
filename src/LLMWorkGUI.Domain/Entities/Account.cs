using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Domain.Entities;

public sealed class Account
{
    private const string SecretReferencePrefix = "urn:llmworkgui:secret:";

    public Account(
        string id,
        string providerProfileId,
        string displayName,
        string? providerNativeId,
        AuthState authState,
        int manualPriority,
        bool isEnabled,
        HealthState health,
        DateTimeOffset? cooldownUntil,
        DateTimeOffset? disabledUntil,
        int maxConcurrentExecutions,
        double? reserveThreshold,
        IReadOnlyList<SessionBinding>? sessionBindings = null,
        string? secretReference = null,
        string? gatewayNativeId = null)
    {
        if (manualPriority < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(manualPriority), "Manual priority must not be negative.");
        }

        if (maxConcurrentExecutions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentExecutions), "At least one concurrent execution slot is required.");
        }

        if (reserveThreshold is { } reserve && (!double.IsFinite(reserve) || reserve < 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(reserveThreshold), "Reserve threshold must be finite and nonnegative.");
        }

        if (secretReference is not null && !secretReference.StartsWith(SecretReferencePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Account secret reference must conform to format '{SecretReferencePrefix}*'.",
                nameof(secretReference));
        }

        Id = DomainGuard.NotBlank(id, nameof(id));
        ProviderProfileId = DomainGuard.NotBlank(providerProfileId, nameof(providerProfileId));
        DisplayName = DomainGuard.NotBlank(displayName, nameof(displayName));
        ProviderNativeId = DomainGuard.OptionalNotBlank(providerNativeId, nameof(providerNativeId));
        AuthState = authState;
        ManualPriority = manualPriority;
        IsEnabled = isEnabled;
        Health = health;
        CooldownUntil = cooldownUntil;
        DisabledUntil = disabledUntil;
        MaxConcurrentExecutions = maxConcurrentExecutions;
        ReserveThreshold = reserveThreshold;
        SessionBindings = sessionBindings is not null
            ? DomainGuard.NotNullList(sessionBindings, nameof(sessionBindings))
            : Array.Empty<SessionBinding>();
        SecretReference = secretReference;
        GatewayNativeId = DomainGuard.OptionalNotBlank(gatewayNativeId, nameof(gatewayNativeId));
    }

    public string Id { get; }

    public string ProviderProfileId { get; }

    public string DisplayName { get; }

    public string? ProviderNativeId { get; }

    public AuthState AuthState { get; }

    public int ManualPriority { get; }

    public bool IsEnabled { get; }

    public HealthState Health { get; }

    public DateTimeOffset? CooldownUntil { get; }

    public DateTimeOffset? DisabledUntil { get; }

    public int MaxConcurrentExecutions { get; }

    public double? ReserveThreshold { get; }

    public string? SecretReference { get; }

    public IReadOnlyList<SessionBinding> SessionBindings { get; }

    /// <summary>
    /// The account identifier a gateway reports, when one has been observed and stored.
    /// <para>
    /// This is NOT <see cref="ProviderNativeId"/>, and reusing that column was rejected rather than
    /// avoided. For the Codex backend it holds a filesystem path and for AGY a profile label, which is
    /// where an account happens to be kept rather than which account answered: two saved profiles can name
    /// one path and a path can be re-pointed, so a credential's storage location cannot prove that it
    /// served a turn. Null means no gateway account identifier is recorded, which is the state of every row
    /// written before this identity existed, and it is never a reason to fall back to
    /// <see cref="ProviderNativeId"/>.
    /// </para>
    /// </summary>
    public string? GatewayNativeId { get; }

    public bool IsEligibleForRouting(DateTimeOffset now)
    {
        if (!IsEnabled) return false;
        if (AuthState != AuthState.Valid) return false;
        if (Health != HealthState.Healthy && Health != HealthState.Degraded && Health != HealthState.ForcedEnabled) return false;
        if (CooldownUntil.HasValue && CooldownUntil.Value > now) return false;
        if (DisabledUntil.HasValue && DisabledUntil.Value > now) return false;
        return true;
    }

    public Account WithAuthState(AuthState newAuthState) =>
        new(
            Id,
            ProviderProfileId,
            DisplayName,
            ProviderNativeId,
            newAuthState,
            ManualPriority,
            IsEnabled,
            Health,
            CooldownUntil,
            DisabledUntil,
            MaxConcurrentExecutions,
            ReserveThreshold,
            SessionBindings,
            SecretReference,
            GatewayNativeId);

    public Account WithHealth(HealthState newHealth) =>
        new(
            Id,
            ProviderProfileId,
            DisplayName,
            ProviderNativeId,
            AuthState,
            ManualPriority,
            IsEnabled,
            newHealth,
            CooldownUntil,
            DisabledUntil,
            MaxConcurrentExecutions,
            ReserveThreshold,
            SessionBindings,
            SecretReference,
            GatewayNativeId);

    public Account WithCooldown(DateTimeOffset? cooldownUntil) =>
        new(
            Id,
            ProviderProfileId,
            DisplayName,
            ProviderNativeId,
            AuthState,
            ManualPriority,
            IsEnabled,
            Health,
            cooldownUntil,
            DisabledUntil,
            MaxConcurrentExecutions,
            ReserveThreshold,
            SessionBindings,
            SecretReference,
            GatewayNativeId);

    public Account WithDisabledUntil(DateTimeOffset? disabledUntil) =>
        new(
            Id,
            ProviderProfileId,
            DisplayName,
            ProviderNativeId,
            AuthState,
            ManualPriority,
            IsEnabled,
            Health,
            CooldownUntil,
            disabledUntil,
            MaxConcurrentExecutions,
            ReserveThreshold,
            SessionBindings,
            SecretReference,
            GatewayNativeId);

    public Account WithManualPriority(int newPriority) =>
        new(
            Id,
            ProviderProfileId,
            DisplayName,
            ProviderNativeId,
            AuthState,
            newPriority,
            IsEnabled,
            Health,
            CooldownUntil,
            DisabledUntil,
            MaxConcurrentExecutions,
            ReserveThreshold,
            SessionBindings,
            SecretReference,
            GatewayNativeId);

    public Account WithIsEnabled(bool isEnabled) =>
        new(
            Id,
            ProviderProfileId,
            DisplayName,
            ProviderNativeId,
            AuthState,
            ManualPriority,
            isEnabled,
            Health,
            CooldownUntil,
            DisabledUntil,
            MaxConcurrentExecutions,
            ReserveThreshold,
            SessionBindings,
            SecretReference,
            GatewayNativeId);

    /// <summary>
    /// Returns a copy bound to another credential reference. The reference is a URN, never a value,
    /// so switching it does not expose the secret it points to.
    /// </summary>
    public Account WithSecretReference(string? secretReference) =>
        new(
            Id,
            ProviderProfileId,
            DisplayName,
            ProviderNativeId,
            AuthState,
            ManualPriority,
            IsEnabled,
            Health,
            CooldownUntil,
            DisabledUntil,
            MaxConcurrentExecutions,
            ReserveThreshold,
            SessionBindings,
            secretReference,
            GatewayNativeId);
}
