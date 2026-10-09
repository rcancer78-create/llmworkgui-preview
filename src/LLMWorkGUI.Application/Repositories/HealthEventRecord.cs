using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Repositories;

/// <summary>
/// One append-only health transition. This is the recovery audit trail required by Phase 7: every
/// state change carries who/what caused it, so a <c>ForcedEnabled</c> route can never be confused
/// with a route that actually passed a probe (ТЗ §7.2).
/// </summary>
/// <param name="PreviousState">
/// State before the transition, or <c>null</c> for the first recorded observation of a scope. It is
/// never silently defaulted to <see cref="HealthState.Healthy"/>, because an unobserved history is
/// not the same as a healthy one.
/// </param>
/// <param name="ErrorClass">
/// Normalized error class when the transition was caused by a failure, otherwise <c>null</c>.
/// </param>
/// <param name="Reason">
/// Human-readable cause of the transition. It must already be redacted: no secrets, tokens or raw
/// provider payloads.
/// </param>
public sealed record HealthEventRecord(
    string Id,
    string ScopeType,
    string ScopeId,
    HealthState? PreviousState,
    HealthState NewState,
    HealthErrorClass? ErrorClass,
    string? Reason,
    string? EvidenceRedactedJson,
    DateTimeOffset OccurredAt);
