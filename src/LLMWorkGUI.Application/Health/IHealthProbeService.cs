using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Health;

/// <summary>Which probe was run (or attempted).</summary>
public enum HealthProbeKind
{
    /// <summary>
    /// A cheap connectivity/auth check against the provider's models endpoint. It confirms only that the
    /// endpoint answers; it never verifies a recovery (ТЗ §6.10).
    /// </summary>
    Connection,

    /// <summary>
    /// The pinned probe that confirms auth, the selected model and a minimal turn/operation. Only this
    /// probe may produce a verified recovery.
    /// </summary>
    Model
}

/// <summary>Why a scope could not be probed. Never a silent no-op.</summary>
public enum HealthProbeRefusal
{
    /// <summary>The probe ran; this is not a refusal.</summary>
    None,

    /// <summary>The scope is not awaiting a probe, so running one would fabricate recovery evidence.</summary>
    NoProbePending,

    /// <summary>The scope kind cannot be probed by a provider call (for example a backend process).</summary>
    ScopeKindNotProbeable,

    /// <summary>The scope resolved to no provider profile, so there is nothing to call.</summary>
    ProbeTargetUnknown,

    /// <summary>The provider profile has no base URL, so no endpoint can be reached.</summary>
    ProbeTargetHasNoEndpoint,

    /// <summary>No connection-test service is configured in this composition.</summary>
    ProbeExecutorUnavailable,

    /// <summary>
    /// Another probe is already running for this scope, so this one was not started. Running both would
    /// interleave two observations of the same scope and could record a recovery twice.
    /// </summary>
    ProbeAlreadyRunning,

    /// <summary>
    /// A model probe may spend provider quota, so it needs an explicit confirmation carrying a cost
    /// preview before it may run.
    /// </summary>
    ConfirmationRequired,

    /// <summary>
    /// The backend cannot run a pinned model probe, so a verified recovery cannot be established. The
    /// caller must show this as <c>Unsupported</c> rather than offering a "Probe passed" button.
    /// </summary>
    ProbeModelUnsupported,

    /// <summary>
    /// The secret reference selected for the scope is <c>Missing</c> or <c>Revoked</c>, so the probe
    /// was not sent. Probing it anyway would test an unauthenticated endpoint and report the
    /// provider's rejection as the reason (ADR-0005 §5.2).
    /// </summary>
    SecretReferenceUnavailable,

    /// <summary>A persisted authentication block must finish before another probe can be admitted.</summary>
    AuthenticationFanoutPending
}

/// <summary>
/// Operator confirmation for a model probe. The preview of the possible cost is shown by the caller and
/// acknowledged here, so a probe that may spend quota is never started without an explicit decision.
/// </summary>
public sealed record HealthProbeConfirmation
{
    /// <summary>The model the pinned probe must exercise.</summary>
    public required string ModelId { get; init; }

    /// <summary>True only once the operator has seen the possible cost and confirmed the probe.</summary>
    public required bool CostPreviewAcknowledged { get; init; }

    public static HealthProbeConfirmation ForModel(string modelId, bool costPreviewAcknowledged = false) =>
        new()
        {
            ModelId = modelId,
            CostPreviewAcknowledged = costPreviewAcknowledged
        };
}

/// <summary>
/// Outcome of one executed probe. A refusal is reported explicitly and leaves the health state
/// untouched, because "we could not check" must never be recorded as "the check failed".
/// </summary>
public sealed record HealthProbeOutcome
{
    public required HealthScope Scope { get; init; }

    /// <summary>Which probe this outcome describes.</summary>
    public HealthProbeKind Kind { get; init; } = HealthProbeKind.Connection;

    /// <summary>True only when the probe actually ran and the provider answered successfully.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// True only for a probe whose observed success verifies a recovery. A passing connection probe
    /// stays <c>false</c>: only a pinned model probe may claim a verified recovery (ТЗ §6.10).
    /// </summary>
    public bool ConfirmsVerifiedRecovery { get; init; }

    /// <summary>Why the probe did not run, or <see cref="HealthProbeRefusal.None"/> when it did.</summary>
    public HealthProbeRefusal Refusal { get; init; } = HealthProbeRefusal.None;

    /// <summary>True when the probe was actually executed against a provider endpoint.</summary>
    public bool WasExecuted => Refusal == HealthProbeRefusal.None;

    /// <summary>
    /// True when the backend cannot run this probe at all, so the scope stays out of routing and the UI
    /// must show it as unsupported instead of pretending a verification happened.
    /// </summary>
    public bool IsUnsupported => Refusal == HealthProbeRefusal.ProbeModelUnsupported;

    /// <summary>
    /// Health state after the probe. For a refusal this is the unchanged observed state, so a caller
    /// cannot mistake a refusal for a transition.
    /// </summary>
    public required HealthSnapshot Snapshot { get; init; }

    /// <summary>Error class observed by a failing probe, or <c>null</c> for a pass or a refusal.</summary>
    public HealthErrorClass? ErrorClass { get; init; }

    /// <summary>Observed round-trip latency of an executed probe, or <c>null</c> for a refusal.</summary>
    public long? LatencyMs { get; init; }

    /// <summary>Sanitized endpoint that was called, or <c>null</c> for a refusal.</summary>
    public string? ProbedEndpoint { get; init; }

    /// <summary>Human-readable explanation, always populated.</summary>
    public required string Explanation { get; init; }
}

/// <summary>
/// Executes real probes for health scopes and records the observed result through
/// <see cref="IHealthCenterService"/> (ROADMAP Phase 7 "connection/model probes").
///
/// The caller never supplies the outcome: that is the whole point of this service. A probe that cannot
/// be executed is refused rather than guessed, so the recovery audit only ever contains probe results
/// that were actually observed.
///
/// The two probes are deliberately distinct. <see cref="ProbeConnectionAsync"/> is a cheap check that
/// only proves the endpoint answers; <see cref="ProbeModelAsync"/> is the pinned probe that proves
/// auth, the selected model and a minimal turn, and is the only path to a verified recovery (ТЗ §6.10).
/// </summary>
public interface IHealthProbeService
{
    /// <summary>
    /// True when this composition can actually run the pinned model probe. When false the UI must show
    /// <c>Unsupported</c> for the recovery action instead of substituting an operator judgement.
    /// </summary>
    bool SupportsModelProbe { get; }

    /// <summary>Retries saving retained observations without repeating provider calls. Returns the number still pending.</summary>
    Task<int> RetryPendingCompletionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    /// <summary>
    /// Runs the connection probe: starts it as an auditable step, calls the provider's models endpoint
    /// and records the observed result. A pass is recorded as evidence but never promotes the scope to
    /// <c>Healthy</c>, because a bare <c>GET /models</c> does not confirm a minimal turn.
    /// </summary>
    Task<HealthProbeOutcome> ProbeConnectionAsync(
        HealthScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the pinned model probe that actually verifies a recovery. It requires an explicit
    /// confirmation with a cost preview, and a backend that cannot run it fails closed with
    /// <see cref="HealthProbeRefusal.ProbeModelUnsupported"/> instead of faking a pass.
    /// </summary>
    Task<HealthProbeOutcome> ProbeModelAsync(
        HealthScope scope,
        HealthProbeConfirmation confirmation,
        CancellationToken cancellationToken = default);
}
