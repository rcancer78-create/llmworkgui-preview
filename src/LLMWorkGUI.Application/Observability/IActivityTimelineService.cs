using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Observability;

public interface IActivityTimelineService
{
    ActivityTimeline BuildTimeline(IReadOnlyList<ObservableRunProjection> projections);

    ActivityTimeline BuildProjectTimeline(
        string projectId,
        IReadOnlyList<Execution> executions,
        IReadOnlyList<Session> sessions,
        EvidenceSourceKind evidenceSource = EvidenceSourceKind.NotReported);

    ActivityTimeline BuildSessionTimeline(
        string sessionId,
        IReadOnlyList<Execution> executions,
        IReadOnlyList<Session> sessions,
        EvidenceSourceKind evidenceSource = EvidenceSourceKind.NotReported);
}
