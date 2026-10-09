using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Observability;

public sealed class ActivityTimelineService : IActivityTimelineService
{
    public ActivityTimeline BuildTimeline(IReadOnlyList<ObservableRunProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(projections);

        var items = projections
            .OrderBy(projection => projection.LastActivityAtUtc)
            .ThenBy(projection => projection.StartedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(projection => projection.ExecutionId, StringComparer.Ordinal)
            .Select((projection, index) => new ActivityTimelineItem(index, projection))
            .ToArray();

        return new ActivityTimeline(items);
    }

    public ActivityTimeline BuildProjectTimeline(
        string projectId,
        IReadOnlyList<Execution> executions,
        IReadOnlyList<Session> sessions,
        EvidenceSourceKind evidenceSource = EvidenceSourceKind.NotReported)
    {
        ApplicationGuard.NotBlank(projectId, nameof(projectId));
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(sessions);

        var sessionsById = sessions
            .Where(session => string.Equals(session.ProjectId, projectId, StringComparison.Ordinal))
            .ToDictionary(session => session.Id, StringComparer.Ordinal);

        var projections = new List<ObservableRunProjection>(executions.Count);

        foreach (var execution in executions)
        {
            if (sessionsById.TryGetValue(execution.SessionId, out var session))
            {
                projections.Add(CreateProjection(execution, session, evidenceSource));
            }
        }

        return BuildTimeline(projections);
    }

    public ActivityTimeline BuildSessionTimeline(
        string sessionId,
        IReadOnlyList<Execution> executions,
        IReadOnlyList<Session> sessions,
        EvidenceSourceKind evidenceSource = EvidenceSourceKind.NotReported)
    {
        ApplicationGuard.NotBlank(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(sessions);

        var session = sessions.FirstOrDefault(candidate => string.Equals(candidate.Id, sessionId, StringComparison.Ordinal));

        if (session is null)
        {
            return ActivityTimeline.Empty;
        }

        var projections = executions
            .Where(execution => string.Equals(execution.SessionId, session.Id, StringComparison.Ordinal))
            .Select(execution => CreateProjection(execution, session, evidenceSource))
            .ToArray();

        return BuildTimeline(projections);
    }

    private static ObservableRunProjection CreateProjection(
        Execution execution,
        Session session,
        EvidenceSourceKind evidenceSource)
    {
        var role = WorkflowRoleParser.Parse(session.Role);

        var displayLabel = string.IsNullOrWhiteSpace(session.Role)
            ? role.ToString()
            : session.Role;

        return ObservableRunProjection.FromExecution(
            execution,
            session,
            role,
            displayLabel,
            evidenceSource);
    }
}
