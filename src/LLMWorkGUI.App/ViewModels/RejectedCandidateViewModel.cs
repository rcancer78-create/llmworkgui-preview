namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Presentation item for a routing candidate rejected by eligibility gates, including the explicit
/// deterministic rejection reason reported by the routing engine (ТЗ §6.5).
/// </summary>
public sealed class RejectedCandidateViewModel
{
    public RejectedCandidateViewModel(string accountId, string reason)
    {
        AccountId = accountId;
        Reason = reason;
    }

    public string AccountId { get; }

    public string Reason { get; }

    public string BadgeText => "ОТКЛОНЁН";

    public string Display => $"{AccountId}: {Reason}";
}
