using LLMWorkGUI.Domain.Enums;
using System.Text.Json.Serialization;

namespace LLMWorkGUI.Domain.StateMachines;

/// <summary>One counted breaker failure, retaining the class and original rolling-window timestamp.</summary>
public sealed record HealthFailureObservation(
    [property: JsonRequired] HealthErrorClass ErrorClass,
    [property: JsonRequired] DateTimeOffset OccurredAt);
