using System.Globalization;

namespace LLMWorkGUI.Application.Lifecycle;

/// <summary>Lifecycle states of a tracked long-running execution.</summary>
public enum LongRunningExecutionState
{
    Running,
    HeartbeatOverdue,
    TimedOut,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// A registration of a long-running execution. The timeout bounds the total wall-clock duration;
/// the optional heartbeat interval declares how often the execution is expected to report liveness.
/// </summary>
public sealed record LongRunningExecutionStartRequest
{
    public LongRunningExecutionStartRequest(
        string executionId,
        TimeSpan timeout,
        TimeSpan? heartbeatInterval = null,
        DateTimeOffset? startedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be positive.");
        }

        if (heartbeatInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatInterval),
                interval,
                "The heartbeat interval must be positive when provided.");
        }

        ExecutionId = executionId;
        Timeout = timeout;
        HeartbeatInterval = heartbeatInterval;
        StartedAtUtc = startedAtUtc;
    }

    public string ExecutionId { get; }

    public TimeSpan Timeout { get; }

    public TimeSpan? HeartbeatInterval { get; }

    /// <summary>Overrides the clock reading used to start the deadline; null uses the service clock.</summary>
    public DateTimeOffset? StartedAtUtc { get; }
}

/// <summary>A point-in-time view of one tracked long-running execution.</summary>
public sealed record LongRunningExecutionSnapshot(
    string ExecutionId,
    LongRunningExecutionState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastHeartbeatAtUtc,
    DateTimeOffset DeadlineAtUtc,
    TimeSpan? HeartbeatInterval,
    int HeartbeatCount,
    bool IsActive)
{
    public bool IsStalled => State is LongRunningExecutionState.HeartbeatOverdue
        or LongRunningExecutionState.TimedOut;
}

/// <summary>
/// The terminal report of a long-running execution. <see cref="ResourcesReleased"/> proves that the
/// service released its cancellation source and tracking entry and successfully disposed its timer.
/// A timer-disposal failure sets ResourcesReleased to false and adds a sanitized diagnostic;
/// it changes Completed to Failed while preserving cancellation, timeout and operation failure.
/// </summary>
public sealed record LongRunningExecutionReport(
    string ExecutionId,
    LongRunningExecutionState Outcome,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int HeartbeatCount,
    bool WasTimedOut,
    bool WasCancelled,
    bool ResourcesReleased,
    string? FailureReason)
{
    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;

    public bool IsSuccess => Outcome == LongRunningExecutionState.Completed;

    public string Summary => string.Format(
        CultureInfo.InvariantCulture,
        "Execution '{0}' finished as {1} after {2:0.###}s (heartbeats={3}, timedOut={4}, cancelled={5}).",
        ExecutionId,
        Outcome,
        Duration.TotalSeconds,
        HeartbeatCount,
        WasTimedOut,
        WasCancelled);
}

/// <summary>Raised when the monitor detects a stalled long-running execution.</summary>
public sealed class LongRunningExecutionEventArgs : EventArgs
{
    public LongRunningExecutionEventArgs(
        LongRunningExecutionSnapshot snapshot,
        LongRunningExecutionReport report)
    {
        Snapshot = snapshot;
        Report = report;
    }

    public LongRunningExecutionSnapshot Snapshot { get; }

    public LongRunningExecutionReport Report { get; }
}

/// <summary>
/// Supervision of long-running executions: heartbeat monitoring, hang detection, bounded timeouts and
/// managed cancellation. Every execution is tracked until it completes or is reaped, and the service
/// releases its cancellation source and registration on completion (ROADMAP Phase 12).
/// </summary>
public interface ILongRunningExecutionService : IDisposable
{
    /// <summary>Raised for every execution the monitor reaps as stalled.</summary>
    event EventHandler<LongRunningExecutionEventArgs>? ExecutionStalled;

    /// <summary>The number of executions currently tracked as active.</summary>
    int ActiveExecutionCount { get; }

    /// <summary>Registers an externally driven execution so its heartbeats and deadline are monitored.</summary>
    LongRunningExecutionSnapshot Track(LongRunningExecutionStartRequest request);

    LongRunningExecutionSnapshot? GetSnapshot(string executionId);

    /// <summary>Records a heartbeat. Returns false for unknown or already finished executions.</summary>
    bool Heartbeat(string executionId, DateTimeOffset? occurredAtUtc = null);

    /// <summary>
    /// Evaluates every externally driven execution against its deadline and heartbeat interval, cancels
    /// the stalled ones and returns their reports. Executions started through <see cref="RunAsync"/> are
    /// governed by their own timeout and are not reported twice.
    /// </summary>
    IReadOnlyList<LongRunningExecutionReport> ReapStaleExecutions(DateTimeOffset? nowUtc = null);

    /// <summary>
    /// Runs the operation under a managed cancellation source with a bounded timeout. The returned
    /// report distinguishes completion, timeout, cancellation and failure. The deadline uses service-
    /// observed completion, including scheduling latency, rather than a native completion timestamp.
    /// Cancellation remains cooperative: the service awaits the operation. ResourcesReleased includes
    /// timer cleanup and is false when timer disposal fails. Such failure changes Completed to Failed
    /// while preserving cancellation, timeout and operation failure.
    /// </summary>
    Task<LongRunningExecutionReport> RunAsync(
        LongRunningExecutionStartRequest request,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);
}
