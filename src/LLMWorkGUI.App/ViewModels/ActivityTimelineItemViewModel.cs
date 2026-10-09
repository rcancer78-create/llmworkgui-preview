using System.Globalization;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed class ActivityTimelineItemViewModel
{
    public const string Spacer = " · ";

    public ActivityTimelineItemViewModel(ActivityTimelineItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        Sequence = item.Sequence;
        StageLabel = item.DisplayLabel;
        Role = item.Role;
        State = item.State;
        IsSynthetic = item.IsSynthetic;
        IsActive = item.IsActive;
        EvidenceSource = item.EvidenceSource;
        LastActivityDisplay = item.LastActivityAtUtc
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        RouteDisplay = item.ObservedRouteIdDisplay;
        SessionDisplay = item.NativeSessionIdDisplay;
    }

    public int Sequence { get; }

    public string SequenceDisplay => (Sequence + 1).ToString("00", CultureInfo.InvariantCulture);

    public string StageLabel { get; }

    public WorkflowRole Role { get; }

    public string RoleDisplay => Role.ToString();

    public ExecutionState State { get; }

    public string StateDisplay => State.ToString();

    public string LastActivityDisplay { get; }

    public string RouteDisplay { get; }

    public string SessionDisplay { get; }

    public EvidenceSourceKind EvidenceSource { get; }

    public string EvidenceDisplay => EvidenceSource switch
    {
        EvidenceSourceKind.SyntheticFixture => "Синтетический образец",
        EvidenceSourceKind.NativeProtocolEvent => "Событие нативного протокола",
        _ => ObservableRunProjection.NotReportedPlaceholder
    };

    public bool IsSynthetic { get; }

    public bool IsActive { get; }

    public string SyntheticBadge => IsSynthetic ? "SYNTHETIC" : string.Empty;

    public string Summary => $"{StageLabel}{Spacer}{RoleDisplay}{Spacer}{StateDisplay}";
}
