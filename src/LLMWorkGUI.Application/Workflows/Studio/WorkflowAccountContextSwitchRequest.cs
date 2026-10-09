using LLMWorkGUI.Application.StarCliProxy;

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>A snapshot of the isolated Mirasim state (active account, relay, recording) taken before a
/// studio account-context switch. The studio passes it through by reference and never mutates it.</summary>
public sealed record WorkflowMirasimIsolationState(
    string ActiveAccountId,
    string RelayState,
    bool RecordingEnabled);

/// <summary>
/// Why an account-context switch was refused. Every value names a fact a caller can verify; none of
/// them is a successful switch.
/// </summary>
public enum WorkflowAccountContextSwitchRefusal
{
    /// <summary>
    /// The requested context is not a usable account context at all (a blank account, a profile name
    /// outside the AGY character set, a relative CODEX_HOME). Nothing was started, so this is reported
    /// separately from a missing backend proof.
    /// </summary>
    InvalidContext,

    /// <summary>
    /// The context is well formed, but no installed backend reports the independent account, model,
    /// route and native-session evidence a real switch needs. This is the refusal this host makes.
    /// </summary>
    MissingNativeSwitchProof,

    /// <summary>The caller cancelled before anything was started.</summary>
    Cancelled
}

/// <summary>
/// A request to switch the AGY <c>agy-profile</c> or the Codex <c>CODEX_HOME</c> used by a workflow
/// template.
///
/// The request deliberately carries no observed route, no observed account and no native session id.
/// Those are backend observations, and a caller that supplies them is only echoing its own wish back to
/// itself; a switch is therefore refused rather than confirmed from request data (ADR-0007, ROADMAP
/// Phase 10E).
/// </summary>
public sealed record WorkflowAccountContextSwitchRequest(
    AccountContextKind Kind,
    string AccountId,
    string? PreviousNativeSessionId = null,
    string? CodexHomePath = null,
    string? AgyProfileName = null,
    string? RequestedRouteId = null,
    WorkflowMirasimIsolationState? MirasimState = null);

/// <summary>
/// The outcome of an account-context switch request. On a host whose backend reports no independent
/// identity or session evidence, every outcome is a refusal: <see cref="IsSwitched"/> is false,
/// <see cref="NativeSessionId"/> is null, <see cref="ObservedRouteId"/> is null and no gateway, process,
/// session, execution or role binding was touched. A resolved switch would still never carry the
/// previous session or its credentials (ADR-0007).
/// </summary>
public sealed class WorkflowAccountContextSwitchResult
{
    private WorkflowAccountContextSwitchResult(
        AccountContextKind kind,
        string accountId,
        string? nativeSessionId,
        string? previousNativeSessionId,
        string? requestedRouteId,
        string routeEvidenceSource,
        string provenance,
        WorkflowAccountContextSwitchRefusal refusal,
        string failureReason,
        WorkflowMirasimIsolationState? mirasimState,
        DateTimeOffset createdAtUtc)
    {
        Kind = kind;
        AccountId = accountId;
        NativeSessionId = nativeSessionId;
        PreviousNativeSessionId = previousNativeSessionId;
        RequestedRouteId = requestedRouteId;
        RouteEvidenceSource = routeEvidenceSource;
        Provenance = provenance;
        Refusal = refusal;
        FailureReason = failureReason;
        MirasimState = mirasimState;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Always false. A switch is reported as applied only when a backend independently reported the
    /// account, the actual model, a unique route key and the new native session, and no code path in
    /// this slice can reach that, so the property is a constant rather than a stored flag a caller or
    /// a future factory could set.
    /// </summary>
    public bool IsSwitched => false;

    /// <summary>Always true for the same reason: every outcome of this slice is a refusal.</summary>
    public bool IsBlocked => true;

    public AccountContextKind Kind { get; }

    public string AccountId { get; }

    /// <summary>The backend-reported native session. Null on every refusal; never generated locally.</summary>
    public string? NativeSessionId { get; }

    public string? PreviousNativeSessionId { get; }

    public string? RequestedRouteId { get; }

    /// <summary>
    /// The independently observed route. Always null: no backend reports a unique route key, and a
    /// caller may not supply one. Kept so a future proven observation has an honest place to land.
    /// </summary>
    public string? ObservedRouteId => null;

    /// <summary>Where the route evidence came from; "none" while nothing observed a route.</summary>
    public string RouteEvidenceSource { get; }

    /// <summary>The application-level provenance of the refusal or of a proven switch; never a credential transfer.</summary>
    public string Provenance { get; }

    /// <summary>Why the switch did not happen. Present on every refusal.</summary>
    public WorkflowAccountContextSwitchRefusal? Refusal { get; }

    public string? FailureReason { get; }

    /// <summary>The untouched Mirasim snapshot the caller handed in.</summary>
    public WorkflowMirasimIsolationState? MirasimState { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public bool RequiresNewNativeSession => true;

    public bool CarriesPreviousSession => false;

    public bool CredentialsTransferred => false;

    /// <summary>
    /// The switch was refused because this host has no backend that reports the account, model, route
    /// and native session independently of the request. No gateway, process, session, execution or role
    /// binding was touched and the active run, the pinned role bindings, the account context and the
    /// Mirasim state are unchanged. The previous native session is reported back untouched, on its own
    /// account, and is never carried over.
    /// </summary>
    public static WorkflowAccountContextSwitchResult MissingNativeSwitchProof(
        AccountContextKind kind,
        string accountId,
        string? previousNativeSessionId,
        string? requestedRouteId,
        string failureReason,
        DateTimeOffset createdAtUtc,
        WorkflowMirasimIsolationState? mirasimState) =>
        new(
            kind,
            accountId,
            nativeSessionId: null,
            previousNativeSessionId,
            requestedRouteId,
            routeEvidenceSource: "none",
            provenance: "refused-missing-native-switch-proof",
            WorkflowAccountContextSwitchRefusal.MissingNativeSwitchProof,
            failureReason,
            mirasimState,
            createdAtUtc);

    /// <summary>
    /// The requested context itself is unusable, so the switch never reached the backend. The account
    /// id is echoed as supplied; no path or secret is repeated in the reason.
    /// </summary>
    public static WorkflowAccountContextSwitchResult InvalidContext(
        AccountContextKind kind,
        string accountId,
        string failureReason,
        DateTimeOffset createdAtUtc,
        WorkflowMirasimIsolationState? mirasimState) =>
        new(
            kind,
            accountId,
            nativeSessionId: null,
            previousNativeSessionId: null,
            requestedRouteId: null,
            routeEvidenceSource: "none",
            provenance: "refused-invalid-context",
            WorkflowAccountContextSwitchRefusal.InvalidContext,
            failureReason,
            mirasimState,
            createdAtUtc);

    /// <summary>The caller cancelled before the request started; nothing was touched.</summary>
    public static WorkflowAccountContextSwitchResult Cancelled(
        AccountContextKind kind,
        string accountId,
        string failureReason,
        DateTimeOffset createdAtUtc,
        WorkflowMirasimIsolationState? mirasimState) =>
        new(
            kind,
            accountId,
            nativeSessionId: null,
            previousNativeSessionId: null,
            requestedRouteId: null,
            routeEvidenceSource: "none",
            provenance: "refused-cancelled",
            WorkflowAccountContextSwitchRefusal.Cancelled,
            failureReason,
            mirasimState,
            createdAtUtc);
}
