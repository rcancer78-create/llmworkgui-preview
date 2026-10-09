using System.Globalization;

namespace LLMWorkGUI.Application.Quotas;

/// <summary>
/// The outcome of a quota polling soak. The report records the cycle, success and failure counts, the
/// observed backoff envelope, the managed memory delta and the timer/in-flight evidence that no
/// resource leaked while the UI thread stayed free.
/// </summary>
public sealed record QuotaSoakReport
{
    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset CompletedAtUtc { get; init; }

    public required int RequestedCycles { get; init; }

    public required int CompletedCycles { get; init; }

    public required int SuccessCount { get; init; }

    public required int FailureCount { get; init; }

    public required int MaxConsecutiveFailures { get; init; }

    public required TimeSpan MinObservedBackoff { get; init; }

    public required TimeSpan MaxObservedBackoff { get; init; }

    /// <summary>The slowest single polling cycle; a large value points at a blocking call.</summary>
    public required TimeSpan MaxCycleDuration { get; init; }

    public required long ManagedMemoryStartBytes { get; init; }

    public required long ManagedMemoryEndBytes { get; init; }

    public required long MemoryGrowthBudgetBytes { get; init; }

    /// <summary>Refresh statuses still marked in-flight after the last cycle; must be zero.</summary>
    public required int ActiveRefreshCountAtEnd { get; init; }

    /// <summary>Timers still outstanding at the end; -1 when no timer probe is attached.</summary>
    public required long OutstandingTimersAtEnd { get; init; }

    /// <summary>True when an <see cref="IQuotaSoakTimerProbe"/> supplied the timer evidence.</summary>
    public required bool TimerProbeAttached { get; init; }

    public required bool BackgroundSchedulerExercised { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;

    public long ManagedMemoryDeltaBytes => ManagedMemoryEndBytes - ManagedMemoryStartBytes;

    public bool IsMemoryStable => ManagedMemoryDeltaBytes <= MemoryGrowthBudgetBytes;

    public bool IsHealthy =>
        CompletedCycles == RequestedCycles
        && ActiveRefreshCountAtEnd == 0
        && (!TimerProbeAttached || OutstandingTimersAtEnd == 0)
        && IsMemoryStable
        && Warnings.Count == 0;

    public string Summary => string.Format(
        CultureInfo.InvariantCulture,
        "Quota soak: cycles={0}/{1}, success={2}, failures={3}, maxConsecutiveFailures={4}, "
        + "backoff={5:0.###}..{6:0.###}s, memoryDelta={7} bytes, activeRefreshes={8}, "
        + "outstandingTimers={9}, healthy={10}.",
        CompletedCycles,
        RequestedCycles,
        SuccessCount,
        FailureCount,
        MaxConsecutiveFailures,
        MinObservedBackoff.TotalSeconds,
        MaxObservedBackoff.TotalSeconds,
        ManagedMemoryDeltaBytes,
        ActiveRefreshCountAtEnd,
        OutstandingTimersAtEnd,
        IsHealthy);
}
