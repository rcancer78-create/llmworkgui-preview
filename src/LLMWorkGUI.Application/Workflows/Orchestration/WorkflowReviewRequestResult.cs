using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// What a request for an assigned model review actually did. The two values are deliberately not a
/// success/failure pair: "refused" means nothing was written and there is nothing to wait for, while
/// "dispatched" means a run-scoped session and execution were persisted and their terminal state is
/// reported - which is not the same as a verdict, and never implies one. Dispatched requests may also
/// contain individually refused roles; admission is not an all-or-nothing operation across roles.
/// </summary>
public enum WorkflowReviewRequestOutcome
{
    /// <summary>
    /// The request was refused by name and nothing was persisted. No session, no execution and no
    /// reviewer-execution binding exist for it, so there is nothing pending to observe and nothing that
    /// could later be mistaken for a review in progress.
    /// </summary>
    Refused,

    /// <summary>
    /// The reviewer turn reached a real dispatch boundary. A run-scoped session and execution exist, their
    /// requested route was persisted before the turn and their observed route is whatever a backend
    /// actually reported. A cancelled, failed, mismatched or ambiguous turn is reported here exactly as it
    /// happened and satisfies nothing.
    /// </summary>
    Dispatched
}

/// <summary>
/// What one required reviewer role of the current stage did with the request, described entirely from
/// persisted rows. Everything is read or written here; none of it is typed by an operator.
/// </summary>
public sealed record WorkflowReviewRequestRoleOutcome
{
    public WorkflowReviewRequestRoleOutcome(
        string role,
        string? assignedRouteId,
        string? assignedModelId,
        string? refusal,
        string? sessionId,
        string? executionId,
        ExecutionState? executionState,
        string? observedRouteId,
        bool readOnly,
        bool boundToArtifact)
    {
        Role = role;
        AssignedRouteId = assignedRouteId;
        AssignedModelId = assignedModelId;
        Refusal = refusal;
        SessionId = sessionId;
        ExecutionId = executionId;
        ExecutionState = executionState;
        ObservedRouteId = observedRouteId;
        ReadOnly = readOnly;
        BoundToArtifact = boundToArtifact;
    }

    /// <summary>The role name exactly as the run's own pinned stage declared it.</summary>
    public string Role { get; }

    /// <summary>
    /// The route the pinned template binds to that role, or null when the assignment could not be resolved
    /// at all. It is a stored <c>Routes</c> id or a refusal, never a free-text label.
    /// </summary>
    public string? AssignedRouteId { get; }

    /// <summary>The model the pinned role binding names, when it names one.</summary>
    public string? AssignedModelId { get; }

    /// <summary>
    /// The named reason this role's review was not dispatched, or null when it was. Every value names what
    /// could not be established; none of them repeats artifact content, a local path or a secret.
    /// </summary>
    public string? Refusal { get; }

    public string? SessionId { get; }

    public string? ExecutionId { get; }

    /// <summary>The persisted execution state, which is also what the transition gate will read.</summary>
    public ExecutionState? ExecutionState { get; }

    /// <summary>
    /// The route a backend reported for this turn, or null when nothing reported one. It is never filled
    /// from <see cref="AssignedRouteId"/>.
    /// </summary>
    public string? ObservedRouteId { get; }

    public bool ReadOnly { get; }

    /// <summary>Whether the execution is durably bound to the exact artifact row the gate will check.</summary>
    public bool BoundToArtifact { get; }

    /// <summary>
    /// True only when a backend reported the assigned route back and the turn ended in
    /// <c>ExecutionState.Succeeded</c>. This is a statement about the dispatch, not a verdict: the model has
    /// not been asked for an Approve or a Reject here, and none is recorded by this slice.
    /// </summary>
    public bool ObservedAssignedRoute =>
        AssignedRouteId is not null
        && string.Equals(AssignedRouteId, ObservedRouteId, StringComparison.Ordinal)
        && ExecutionState is Domain.Enums.ExecutionState.Succeeded;

    public static WorkflowReviewRequestRoleOutcome RefusedRole(
        string role,
        string? assignedRouteId,
        string? assignedModelId,
        string refusal) =>
        new(role, assignedRouteId, assignedModelId, refusal, null, null, null, null, readOnly: false, boundToArtifact: false);
}

/// <summary>
/// The whole answer of one request: which stage of which run was about to be reviewed, which artifact it
/// would have been, and what each required reviewer role did or refused.
/// </summary>
public sealed record WorkflowReviewRequestResult
{
    public WorkflowReviewRequestResult(
        WorkflowReviewRequestOutcome outcome,
        string runId,
        string? stageId,
        string? requiredArtifactKind,
        string? currentArtifactHash,
        IReadOnlyList<WorkflowReviewRequestRoleOutcome> roles,
        string detail)
    {
        Outcome = outcome;
        RunId = runId;
        StageId = stageId;
        RequiredArtifactKind = requiredArtifactKind;
        CurrentArtifactHash = currentArtifactHash;
        Roles = roles;
        Detail = detail;
    }

    public WorkflowReviewRequestOutcome Outcome { get; }

    public string RunId { get; }

    public string? StageId { get; }

    public string? RequiredArtifactKind { get; }

    /// <summary>The hash of the newest stored artifact of the required kind, or null when none is stored.</summary>
    public string? CurrentArtifactHash { get; }

    public IReadOnlyList<WorkflowReviewRequestRoleOutcome> Roles { get; }

    /// <summary>One line naming what happened, or why nothing was attempted.</summary>
    public string Detail { get; }

    /// <summary>True when every required role refused and nothing was persisted.</summary>
    public bool IsRefusal => Outcome == WorkflowReviewRequestOutcome.Refused;

    /// <summary>How many required roles actually reached a dispatch boundary.</summary>
    public int DispatchedRoleCount => Roles.Count(role => role.Refusal is null);

    /// <summary>The status line a product screen shows: refused, or dispatched with honest route evidence.</summary>
    public string StateDisplay => Outcome switch
    {
        WorkflowReviewRequestOutcome.Refused => "Отказ",
        _ => $"{DispatchedRoleCount}/{Roles.Count} ролей отправлено"
    };
}
