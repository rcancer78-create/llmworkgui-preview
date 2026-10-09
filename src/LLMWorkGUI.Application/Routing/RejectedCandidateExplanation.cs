namespace LLMWorkGUI.Application.Routing;

/// <summary>
/// Record explaining why a candidate account failed eligibility gates during routing (ТЗ §6.5).
/// </summary>
public sealed record RejectedCandidateExplanation(
    string AccountId,
    string Reason);
