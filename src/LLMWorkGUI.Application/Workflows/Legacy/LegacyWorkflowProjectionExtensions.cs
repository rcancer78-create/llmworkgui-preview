using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Legacy;

/// <summary>
/// Projects a terminal legacy execution into the shared observable-run shapes without inventing
/// evidence. Route, role, native session, and stage stay unproven until the entrypoint reports them
/// through a verified contract, so every such field is absent and renders as
/// <see cref="ObservableRunProjection.NotReportedPlaceholder"/> (ТЗ §6.15, ROADMAP Phase 10B).
/// </summary>
public static class LegacyWorkflowProjectionExtensions
{
    public const string LegacyRequestedRouteId = "legacy-entrypoint";

    public const string LegacyDisplayLabel = "Legacy entrypoint";

    public static ObservableRunProjection ToObservableRunProjection(this LegacyWorkflowExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return new ObservableRunProjection(
            execution.ExecutionId,
            execution.WorkflowRunId ?? execution.ExecutionId,
            WorkflowRole.Unknown,
            LegacyDisplayLabel,
            execution.TerminationReason is Processes.ProcessTerminationReason.StartupPending or Processes.ProcessTerminationReason.CleanupPending ? ExecutionState.Ambiguous :
                execution.IsSuccess ? ExecutionState.Succeeded : ExecutionState.Failed,
            LegacyRequestedRouteId,
            observedRouteId: null,
            nativeSessionId: null,
            execution.StartedAtUtc,
            execution.ExitedAtUtc,
            execution.TerminationReason is Processes.ProcessTerminationReason.StartupPending or Processes.ProcessTerminationReason.CleanupPending ? null : execution.ExitedAtUtc,
            EvidenceSourceKind.NotReported,
            isSynthetic: false);
    }

    public static WorkflowRunTimelineItem ToWorkflowRunTimelineItem(
        this LegacyWorkflowExecutionResult execution,
        int sequence = 0)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var projection = execution.ToObservableRunProjection();

        return new WorkflowRunTimelineItem(
            sequence,
            WorkflowRunTimelineItemKind.Execution,
            projection.LastActivityAtUtc,
            stageId: null,
            role: null,
            observedRouteId: null,
            nativeSessionId: null,
            execution.ExecutionId,
            $"Legacy entrypoint execution: {projection.State}",
            projection.State,
            EvidenceSourceKind.NotReported,
            Array.Empty<ReviewerVerdictRecord>(),
            userApproval: null);
    }
}
