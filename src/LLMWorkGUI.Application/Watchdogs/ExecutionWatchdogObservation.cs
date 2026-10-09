namespace LLMWorkGUI.Application.Watchdogs;

public sealed record ExecutionWatchdogObservation(
    ExecutionWatchdogObservationKind Kind,
    string ExecutionId,
    DateTimeOffset TimestampUtc,
    string? Detail = null);
