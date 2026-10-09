using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Observability;

public sealed class ActivityTimelineItem
{
    internal ActivityTimelineItem(int sequence, ObservableRunProjection run)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Sequence must not be negative.");
        }

        ArgumentNullException.ThrowIfNull(run);

        Sequence = sequence;
        Run = run;
    }

    public int Sequence { get; }

    public ObservableRunProjection Run { get; }

    public string ExecutionId => Run.ExecutionId;

    public string SessionId => Run.SessionId;

    public WorkflowRole Role => Run.Role;

    public string DisplayLabel => Run.DisplayLabel;

    public ExecutionState State => Run.State;

    public string RequestedRouteId => Run.RequestedRouteId;

    public string ObservedRouteIdDisplay => Run.ObservedRouteIdDisplay;

    public string NativeSessionIdDisplay => Run.NativeSessionIdDisplay;

    public DateTimeOffset? StartedAtUtc => Run.StartedAtUtc;

    public DateTimeOffset LastActivityAtUtc => Run.LastActivityAtUtc;

    public DateTimeOffset? EndedAtUtc => Run.EndedAtUtc;

    public EvidenceSourceKind EvidenceSource => Run.EvidenceSource;

    public bool IsSynthetic => Run.IsSynthetic;

    public bool IsActive => Run.IsActive;
}
