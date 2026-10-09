using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;

namespace LLMWorkGUI.Application.Repositories;

public sealed record HealthStateRecord(
    string Id,
    string ScopeType,
    string ScopeId,
    HealthState State,
    HealthErrorClass? ErrorClass,
    int FailureCount,
    DateTimeOffset? WindowStartedAt,
    DateTimeOffset? CooldownUntil,
    string? EvidenceRedactedJson,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Null identifies a legacy aggregate-only row; an empty list is an exact empty window.</summary>
    public IReadOnlyList<HealthFailureObservation>? FailureHistory { get; init; }
}
