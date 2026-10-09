using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Watchdogs;

public sealed record ExecutionTurnResult
{
    public required string ExecutionId { get; init; }

    public required ExecutionTurnOutcome Outcome { get; init; }

    /// <summary>
    /// True only when the failure is attributable to the model/route and therefore consumes
    /// the model retry budget. User cancellation, ambiguous completion, startup/orchestration
    /// failures and <see cref="ExecutionTurnOutcome.InteractiveInputWait"/> are not model
    /// failures and do not consume the budget.
    /// </summary>
    public required bool ConsumesModelRetryBudget { get; init; }

    /// <summary>Normalized class for health reporting; null for a successful turn.</summary>
    public HealthErrorClass? NormalizedHealthErrorClass { get; init; }

    public int? ProcessId { get; init; }

    public int? ExitCode { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset CompletedAtUtc { get; init; }

    public bool SessionConfirmed { get; init; }

    public bool TransportActivityObserved { get; init; }

    public bool ModelSilenceObserved { get; init; }

    public string? FailureMessage { get; init; }

    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;
}
