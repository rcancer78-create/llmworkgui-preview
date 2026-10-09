using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Routing;

/// <summary>
/// Result of route selection containing the chosen route, scores, rejected candidates, and explanation (ТЗ §6.5).
/// </summary>
public sealed record RoutingDecision
{
    public required bool IsSuccess { get; init; }
    public SessionBinding? SelectedBinding { get; init; }
    public Account? SelectedAccount { get; init; }
    public string? QuotaSnapshotId { get; init; }
    public required RoutingPolicy Policy { get; init; }
    public required string PolicySource { get; init; }
    public CandidateScore? Score { get; init; }
    public IReadOnlyList<RejectedCandidateExplanation> RejectedCandidates { get; init; } = Array.Empty<RejectedCandidateExplanation>();
    public required string ExplanationText { get; init; }
    public bool RequiresReplacementSession { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
}
