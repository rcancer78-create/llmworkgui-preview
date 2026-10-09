using LLMWorkGUI.Domain.Enums;
using System.Text;

namespace LLMWorkGUI.Application.Health;

/// <summary>Identity of a health scope: which kind of entity is being tracked, and which one.</summary>
public sealed record HealthScope(string ScopeType, string ScopeId)
{
    public const string AccountScopeType = "account";

    public const string RouteScopeType = "route";
    public const string ModelRouteScopeType = "model-route";
    private static readonly UTF8Encoding IdentityEncoding = new(false, true);

    public const string BackendScopeType = "backend";

    public static HealthScope ForAccount(string accountId) => new(AccountScopeType, accountId);

    public static HealthScope ForRoute(string routeId) => new(RouteScopeType, routeId);

    /// <summary>
    /// Health scope of one model route inside an account. Auth failures block the account scope, while a
    /// model mismatch blocks only this route, so routing must consult both (ТЗ §6.10).
    /// </summary>
    public static HealthScope ForModelRoute(string accountId, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return new(ModelRouteScopeType,
            $"v1:{Convert.ToHexString(IdentityEncoding.GetBytes(accountId))}:{Convert.ToHexString(IdentityEncoding.GetBytes(modelId))}");
    }

    public bool TryGetModelRoute(out string accountId, out string modelId)
    {
        accountId = modelId = string.Empty;
        if (ScopeType != ModelRouteScopeType) return false;
        var parts = ScopeId.Split(':');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        try
        {
            var account = IdentityEncoding.GetString(Convert.FromHexString(parts[1]));
            var model = IdentityEncoding.GetString(Convert.FromHexString(parts[2]));
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(model) ||
                ForModelRoute(account, model).ScopeId != ScopeId) return false;
            accountId = account;
            modelId = model;
            return true;
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { return false; }
    }

    public static HealthScope ForBackend(string backendId) => new(BackendScopeType, backendId);
}

/// <summary>
/// Observed health of one scope. <see cref="IsRoutable"/> is the single normative answer to
/// "may routing use this scope?", so no caller has to re-derive the rule from the raw state.
/// </summary>
public sealed record HealthSnapshot
{
    public required HealthScope Scope { get; init; }

    public required HealthState State { get; init; }

    /// <summary>Error class of the most recent accounted failure, or <c>null</c> when none is known.</summary>
    public HealthErrorClass? ErrorClass { get; init; }

    /// <summary>Failures counted by the breaker inside the rolling window.</summary>
    public int AccountedFailureCount { get; init; }

    public DateTimeOffset? CooldownUntil { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool AuthenticationFanoutPending { get; init; }

    /// <summary>
    /// A quarantined or manually disabled scope never participates in routing. A scope awaiting a
    /// probe is also excluded: cooldown expiry means "probe required", never "healthy again"
    /// (ROADMAP Phase 7 exit criteria).
    /// </summary>
    public bool IsRoutable => !AuthenticationFanoutPending && State is
        HealthState.Healthy or
        HealthState.Degraded or
        HealthState.ForcedEnabled;

    /// <summary>
    /// True when the scope is usable only because an operator forced it, without a passing probe.
    /// The UI must show this differently from a verified recovery.
    /// </summary>
    public bool IsForcedWithoutVerification => State == HealthState.ForcedEnabled;

    /// <summary>True when a manual probe is required before the scope can recover.</summary>
    public bool RequiresProbe => State is HealthState.ProbeRequired or HealthState.QuarantinedAuto;
}

/// <summary>Outcome of reporting a failure to the breaker.</summary>
public sealed record HealthFailureOutcome
{
    public required HealthSnapshot Snapshot { get; init; }

    /// <summary>
    /// False when the error class is excluded from the breaker by policy (user cancellation and
    /// ambiguous completion). Such an outcome is recorded for explainability but must not push a
    /// route towards quarantine.
    /// </summary>
    public required bool CountedByBreaker { get; init; }

    /// <summary>True when this failure actually changed the health state.</summary>
    public required bool StateChanged { get; init; }

    /// <summary>
    /// True when an ambiguous completion was observed. Such a turn must never be retried
    /// automatically (ROADMAP Phase 7 exit criteria).
    /// </summary>
    public bool IsAmbiguous { get; init; }

    /// <summary>Safe-retry recommendation, honest about ambiguity.</summary>
    public required bool AutomaticRetryAllowed { get; init; }
}

/// <summary>
/// Normalized health and recovery surface for accounts, routes and backends. Every transition is
/// appended to the recovery audit, and no transition happens outside the normative state table
/// (ТЗ §7.2, ROADMAP Phase 7).
/// </summary>
public interface IHealthCenterService
{
    /// <summary>
    /// Atomically admits and audits a probe before external work. A stale/ineligible scope returns null.
    /// Older implementations fail closed until they support correlated probe ownership.
    /// </summary>
    Task<HealthProbeAttempt?> TryBeginProbeAttemptAsync(HealthScope scope, bool modelProbe, string reason,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Correlated probe admission is not supported.");

    /// <summary>
    /// Records a terminal observation; only the still-current attempt may change health. A late result
    /// is audited without overwriting a manual control or another attempt's recovery state.
    /// </summary>
    Task<HealthProbeAttemptCompletion> CompleteProbeAttemptAsync(HealthProbeAttempt attempt,
        HealthProbeCompletionKind kind, string reason, string? evidenceRedactedJson = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Correlated probe completion is not supported.");

    /// <summary>Records the pinned model's mismatch once, including a lost persistence acknowledgement.</summary>
    Task<HealthFailureOutcome> ReportProbeModelMismatchAsync(HealthProbeAttempt attempt, string modelId,
        string reason, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Correlated model mismatch persistence is not supported.");

    /// <summary>Returns the observed health of a scope, or a Healthy default when nothing is recorded.</summary>
    Task<HealthSnapshot> GetSnapshotAsync(
        HealthScope scope,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HealthSnapshot>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports a normalized failure. Error classes excluded by policy do not move the breaker, and an
    /// ambiguous completion never authorizes an automatic retry.
    /// </summary>
    Task<HealthFailureOutcome> ReportFailureAsync(
        HealthScope scope,
        HealthErrorClass errorClass,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>Durably admits every required auth barrier before projecting any individual failure.</summary>
    Task<IReadOnlyList<HealthFailureOutcome>> ReportAuthenticationFailuresAsync(
        IReadOnlyList<HealthScope> scopes, string? reason = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Durable authentication fan-out is not configured.");

    /// <summary>Retries persisted projections. Returns the number still pending; routing stays blocked.</summary>
    Task<int> RetryAuthenticationFanoutAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    /// <summary>
    /// Records an ordinary success without resetting the rolling failure window or verifying recovery.
    /// Only a successful pinned probe clears accounted failures and restores verified health.
    /// </summary>
    Task<HealthSnapshot> ReportSuccessAsync(
        HealthScope scope,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a scope whose cooldown has elapsed into <see cref="HealthState.ProbeRequired"/>. It never
    /// returns straight to <see cref="HealthState.Healthy"/>.
    /// </summary>
    Task<HealthSnapshot> ExpireCooldownAsync(
        HealthScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>Records the start of a manual probe as its own auditable step.</summary>
    Task<HealthSnapshot> StartProbeAsync(
        HealthScope scope,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compatibility failure observation. Uncorrelated success is refused; verified recovery requires
    /// the current attempt through <see cref="CompleteProbeAttemptAsync"/>.
    /// </summary>
    Task<HealthSnapshot> CompleteProbeAsync(
        HealthScope scope,
        bool succeeded,
        string? evidenceRedactedJson = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an observed probe result that does not by itself verify a recovery. A failing observation
    /// quarantines the scope; a passing one is recorded as evidence and audited but leaves the scope
    /// exactly where it was, so a connection check can never be mistaken for a verified recovery.
    /// </summary>
    Task<HealthSnapshot> RecordProbeObservationAsync(
        HealthScope scope,
        bool observedSuccess,
        string reason,
        string? evidenceRedactedJson = null,
        CancellationToken cancellationToken = default);

    Task<HealthSnapshot> DisableManuallyAsync(
        HealthScope scope,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Re-enables a manually disabled scope into <see cref="HealthState.ProbeRequired"/>.</summary>
    Task<HealthSnapshot> EnableAsync(
        HealthScope scope,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a scope back into routing without a passing probe. This is deliberately distinct from a
    /// verified recovery and is always recorded as such.
    /// </summary>
    Task<HealthSnapshot> ForceEnableAsync(
        HealthScope scope,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a forced scope into <see cref="HealthState.ProbeRequired"/> so the recovery it never earned
    /// through a passing probe must still be verified. A scope that is not ForcedEnabled is refused.
    /// </summary>
    Task<HealthSnapshot> RequireProbeAsync(
        HealthScope scope,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the append-only recovery audit of a scope, newest first.</summary>
    Task<IReadOnlyList<HealthEventView>> GetAuditAsync(
        HealthScope scope,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised for every transition that reaches the append-only recovery audit, after the audit row is
    /// durable. This is the notification seam the Phase 11 Activity Center subscribes to: health is one of
    /// the product's own event sources, and until this event existed there was no way for any other
    /// component to learn that a scope had moved without polling the audit.
    /// </summary>
    event EventHandler<HealthTransitionEventArgs>? TransitionRecorded;
}

/// <summary>One audited health transition, as observed by a subscriber such as the Activity Center.</summary>
public sealed class HealthTransitionEventArgs : EventArgs
{
    public HealthTransitionEventArgs(
        HealthScope scope,
        HealthState? previousState,
        HealthState newState,
        HealthErrorClass? errorClass,
        string? reason,
        DateTimeOffset occurredAt)
    {
        Scope = scope;
        PreviousState = previousState;
        NewState = newState;
        ErrorClass = errorClass;
        Reason = reason;
        OccurredAt = occurredAt;
    }

    public HealthScope Scope { get; }

    public HealthState? PreviousState { get; }

    public HealthState NewState { get; }

    public HealthErrorClass? ErrorClass { get; }

    /// <summary>
    /// Operator- or backend-supplied reason. It is already part of the redacted audit row, so it is
    /// redacted again at the Activity Center ingestion boundary like any other free text.
    /// </summary>
    public string? Reason { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>Stable identity of the audit entry, so a subscriber can keep every observation.</summary>
    public string ScopeId => $"{Scope.ScopeType}:{Scope.ScopeId}";
}

/// <summary>Read model of one audited health transition.</summary>
public sealed record HealthEventView(
    string Id,
    HealthScope Scope,
    HealthState? PreviousState,
    HealthState NewState,
    HealthErrorClass? ErrorClass,
    string? Reason,
    DateTimeOffset OccurredAt)
{
    /// <summary>
    /// True when this transition put the scope into routing without a passing probe, so the audit can
    /// distinguish it from a verified recovery.
    /// </summary>
    public bool IsForcedRecovery => NewState == HealthState.ForcedEnabled;

    /// <summary>True when this transition is a recovery confirmed by an observed probe result.</summary>
    public bool IsVerifiedRecovery =>
        NewState == HealthState.Healthy && PreviousState == HealthState.Recovering;
}
