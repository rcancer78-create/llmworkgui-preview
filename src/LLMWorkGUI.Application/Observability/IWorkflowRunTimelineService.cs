using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// Projects a workflow run, its stage transitions and the Phase 1 observable run projections into one
/// timeline. Fields that are not backed by actual evidence are reported as
/// <see cref="ObservableRunProjection.NotReportedPlaceholder"/> instead of being invented.
/// </summary>
public interface IWorkflowRunTimelineService
{
    WorkflowRunTimeline BuildTimeline(
        WorkflowRun run,
        IReadOnlyList<Execution> executions,
        IReadOnlyList<Session> sessions,
        EvidenceSourceKind evidenceSource = EvidenceSourceKind.NotReported);

    WorkflowRunTimeline BuildTimeline(
        WorkflowRun run,
        IReadOnlyList<ObservableRunProjection> projections);
}

public enum WorkflowRunTimelineItemKind
{
    Transition,
    Execution
}

public sealed class WorkflowRunTimelineItem
{
    public WorkflowRunTimelineItem(
        int sequence,
        WorkflowRunTimelineItemKind kind,
        DateTimeOffset occurredAtUtc,
        string? stageId,
        string? role,
        string? observedRouteId,
        string? nativeSessionId,
        string? executionId,
        string description,
        ExecutionState? executionState,
        EvidenceSourceKind evidenceSource,
        IReadOnlyList<ReviewerVerdictRecord> reviewerVerdicts,
        UserApprovalEvidence? userApproval)
    {
        ApplicationGuard.NotBlank(description, nameof(description));
        ArgumentNullException.ThrowIfNull(reviewerVerdicts);

        Sequence = sequence;
        Kind = kind;
        OccurredAtUtc = occurredAtUtc;
        StageId = ApplicationGuard.OptionalNotBlank(stageId, nameof(stageId));
        Role = ApplicationGuard.OptionalNotBlank(role, nameof(role));
        ObservedRouteId = ApplicationGuard.OptionalNotBlank(observedRouteId, nameof(observedRouteId));
        NativeSessionId = ApplicationGuard.OptionalNotBlank(nativeSessionId, nameof(nativeSessionId));
        ExecutionId = ApplicationGuard.OptionalNotBlank(executionId, nameof(executionId));
        Description = description;
        ExecutionState = executionState;
        EvidenceSource = evidenceSource;
        ReviewerVerdicts = reviewerVerdicts.ToArray();
        UserApproval = userApproval;
    }

    public int Sequence { get; }

    public WorkflowRunTimelineItemKind Kind { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string? StageId { get; }

    public string? Role { get; }

    public string? ObservedRouteId { get; }

    public string? NativeSessionId { get; }

    public string? ExecutionId { get; }

    public string Description { get; }

    public ExecutionState? ExecutionState { get; }

    public EvidenceSourceKind EvidenceSource { get; }

    public IReadOnlyList<ReviewerVerdictRecord> ReviewerVerdicts { get; }

    public UserApprovalEvidence? UserApproval { get; }

    public string StageDisplay => StageId ?? ObservableRunProjection.NotReportedPlaceholder;

    public string RoleDisplay => Role ?? ObservableRunProjection.NotReportedPlaceholder;

    public string ObservedRouteDisplay => ObservedRouteId ?? ObservableRunProjection.NotReportedPlaceholder;

    public string NativeSessionDisplay => NativeSessionId ?? ObservableRunProjection.NotReportedPlaceholder;

    internal WorkflowRunTimelineItem WithSequence(int sequence) =>
        new(
            sequence,
            Kind,
            OccurredAtUtc,
            StageId,
            Role,
            ObservedRouteId,
            NativeSessionId,
            ExecutionId,
            Description,
            ExecutionState,
            EvidenceSource,
            ReviewerVerdicts,
            UserApproval);
}

public sealed class WorkflowRunTimeline
{
    public WorkflowRunTimeline(WorkflowRun run, IReadOnlyList<WorkflowRunTimelineItem> items)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(items);

        Run = run;
        Items = items.ToArray();
    }

    public WorkflowRun Run { get; }

    public IReadOnlyList<WorkflowRunTimelineItem> Items { get; }

    public string CurrentStageDisplay => Run.CurrentStageId;

    public string CurrentRoleDisplay => Run.CurrentRole;

    public string RunSessionDisplay =>
        Run.SessionId ?? ObservableRunProjection.NotReportedPlaceholder;

    public bool IsTerminal => WorkflowRun.IsTerminalState(Run.State);
}
