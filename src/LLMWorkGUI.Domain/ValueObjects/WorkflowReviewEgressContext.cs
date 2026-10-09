namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>Local identifiers to re-read stored admission; never permission or native identity.</summary>
public sealed record WorkflowReviewEgressContext(string ExecutionId, string RouteId, string StageId, string ReviewerRole);
