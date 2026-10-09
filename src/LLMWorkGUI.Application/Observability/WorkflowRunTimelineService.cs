using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Observability;

public sealed class WorkflowRunTimelineService : IWorkflowRunTimelineService
{
    public WorkflowRunTimeline BuildTimeline(
        WorkflowRun run,
        IReadOnlyList<Execution> executions,
        IReadOnlyList<Session> sessions,
        EvidenceSourceKind evidenceSource = EvidenceSourceKind.NotReported)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(sessions);

        var runSessions = ResolveRunSessions(run, sessions);
        var projections = new List<ObservableRunProjection>();

        foreach (var execution in executions)
        {
            var session = FindSession(runSessions, execution.SessionId);

            if (session is null)
            {
                continue;
            }

            projections.Add(ObservableRunProjection.FromExecution(
                execution,
                session,
                WorkflowRoleParser.Parse(session.Role),
                session.Role,
                evidenceSource));
        }

        return BuildTimeline(run, projections);
    }

    public WorkflowRunTimeline BuildTimeline(
        WorkflowRun run,
        IReadOnlyList<ObservableRunProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(projections);

        var items = new List<WorkflowRunTimelineItem>(run.Transitions.Count + projections.Count);

        items.AddRange(run.Transitions.Select(CreateTransitionItem));
        items.AddRange(projections.Select(CreateExecutionItem));

        var ordered = items
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.ExecutionId, StringComparer.Ordinal)
            .Select((item, index) => item.WithSequence(index))
            .ToArray();

        return new WorkflowRunTimeline(run, ordered);
    }

    private static WorkflowRunTimelineItem CreateTransitionItem(WorkflowTransitionRecord transition) =>
        new(
            sequence: 0,
            WorkflowRunTimelineItemKind.Transition,
            transition.TriggeredAtUtc,
            transition.ToStageId,
            role: null,
            observedRouteId: transition.ReviewerVerdicts.Count == 0
                ? null
                : transition.ReviewerVerdicts[^1].RouteId,
            nativeSessionId: null,
            executionId: null,
            transition.Reason,
            executionState: null,
            EvidenceSourceKind.NotReported,
            transition.ReviewerVerdicts,
            transition.UserApproval);

    private static WorkflowRunTimelineItem CreateExecutionItem(ObservableRunProjection projection) =>
        new(
            sequence: 0,
            WorkflowRunTimelineItemKind.Execution,
            projection.LastActivityAtUtc,
            stageId: null,
            role: ResolveRoleLabel(projection),
            observedRouteId: projection.ObservedRouteId,
            nativeSessionId: projection.NativeSessionId,
            executionId: projection.ExecutionId,
            $"Execution state: {projection.State}",
            projection.State,
            projection.EvidenceSource,
            Array.Empty<ReviewerVerdictRecord>(),
            userApproval: null);

    private static string? ResolveRoleLabel(ObservableRunProjection projection)
    {
        if (projection.Role != WorkflowRole.Unknown)
        {
            return projection.Role.ToString();
        }

        return string.Equals(
            projection.DisplayLabel,
            nameof(WorkflowRole.Unknown),
            StringComparison.Ordinal)
            ? null
            : projection.DisplayLabel;
    }

    private static IReadOnlyList<Session> ResolveRunSessions(
        WorkflowRun run,
        IReadOnlyList<Session> sessions)
    {
        var result = new List<Session>();

        foreach (var session in sessions)
        {
            var isRunSession = run.SessionId is not null
                && string.Equals(session.Id, run.SessionId, StringComparison.Ordinal);

            var belongsToRun = string.Equals(session.WorkflowRunId, run.Id, StringComparison.Ordinal);

            if (isRunSession || belongsToRun)
            {
                result.Add(session);
            }
        }

        return result;
    }

    private static Session? FindSession(IReadOnlyList<Session> sessions, string sessionId)
    {
        foreach (var session in sessions)
        {
            if (string.Equals(session.Id, sessionId, StringComparison.Ordinal))
            {
                return session;
            }
        }

        return null;
    }
}
