using LLMWorkGUI.Application.Lifecycle;

namespace LLMWorkGUI.Infrastructure.Lifecycle;

/// <summary>
/// Default supervision of long-running executions. Executions are tracked with a bounded deadline and
/// an optional heartbeat cadence; the monitor reaps executions whose deadline passed or whose
/// heartbeat went silent and reports every remediation. Executions started through the managed runner
/// receive an owned cancellation source. Once a cooperative operation ends, its tracking entry and
/// cancellation source are released; timer-release failures are reported (ROADMAP Phase 12).
/// </summary>
public sealed class LongRunningExecutionService : ILongRunningExecutionService
{
    /// <summary>How many missed heartbeat intervals are tolerated before an execution is stalled.</summary>
    public const int HeartbeatOverdueFactor = 3;

    private readonly TimeProvider _timeProvider;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, TrackedExecution> _executions = new(StringComparer.Ordinal);

    private bool _disposed;

    public LongRunningExecutionService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<LongRunningExecutionEventArgs>? ExecutionStalled;

    public int ActiveExecutionCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _executions.Values.Count(execution => execution.IsActive);
            }
        }
    }

    public LongRunningExecutionSnapshot Track(LongRunningExecutionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            var startedAt = request.StartedAtUtc ?? _timeProvider.GetUtcNow();
            var execution = new TrackedExecution(request, startedAt, isManaged: false);

            Register(execution);

            return execution.CreateSnapshot();
        }
    }

    public LongRunningExecutionSnapshot? GetSnapshot(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        lock (_syncRoot)
        {
            return _executions.TryGetValue(executionId, out var execution)
                ? execution.CreateSnapshot()
                : null;
        }
    }

    public bool Heartbeat(string executionId, DateTimeOffset? occurredAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        lock (_syncRoot)
        {
            if (!_executions.TryGetValue(executionId, out var execution) || !execution.IsActive)
            {
                return false;
            }

            execution.RecordHeartbeat(occurredAtUtc ?? _timeProvider.GetUtcNow());

            return true;
        }
    }

    public IReadOnlyList<LongRunningExecutionReport> ReapStaleExecutions(DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? _timeProvider.GetUtcNow();
        var results = new List<(LongRunningExecutionSnapshot Snapshot, LongRunningExecutionReport Report)>();

        lock (_syncRoot)
        {
            foreach (var execution in _executions.Values)
            {
                if (!execution.IsActive || execution.IsManaged)
                {
                    continue;
                }

                var isTimedOut = now >= execution.DeadlineAtUtc;
                var isHeartbeatOverdue = !isTimedOut
                    && execution.HeartbeatInterval is { } interval
                    && now - execution.LastHeartbeatAtUtc >= interval * HeartbeatOverdueFactor;

                if (!isTimedOut && !isHeartbeatOverdue)
                {
                    continue;
                }

                var outcome = isTimedOut
                    ? LongRunningExecutionState.TimedOut
                    : LongRunningExecutionState.HeartbeatOverdue;

                var report = execution.Complete(
                    outcome,
                    now,
                    wasTimedOut: isTimedOut,
                    wasCancelled: true,
                    failureReason: isTimedOut
                        ? "The execution exceeded its bounded timeout."
                        : "The execution missed its heartbeat cadence.",
                    resourcesReleased: true);

                _executions.Remove(execution.ExecutionId);
                results.Add((execution.CreateSnapshot(), report));
            }
        }

        foreach (var (snapshot, report) in results)
        {
            ExecutionStalled?.Invoke(this, new LongRunningExecutionEventArgs(snapshot, report));
        }

        return results.Select(result => result.Report).ToArray();
    }

    public async Task<LongRunningExecutionReport> RunAsync(
        LongRunningExecutionStartRequest request,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);

        var startedAt = request.StartedAtUtc ?? _timeProvider.GetUtcNow();
        var linkedSource = new CancellationTokenSource();

        TrackedExecution execution;

        try
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                execution = new TrackedExecution(request, startedAt, isManaged: true, linkedSource);
                Register(execution);
            }
        }
        catch
        {
            linkedSource.Dispose();
            throw;
        }

        var wasTimedOut = false;
        var timeoutTimerFired = 0;
        var timerReleased = true;
        var outcome = LongRunningExecutionState.Failed;
        string? failureReason = null;
        ITimer? timeoutTimer = null;
        CancellationTokenRegistration callerRegistration = default;

        try
        {
            callerRegistration = cancellationToken.UnsafeRegister(
                _ => execution.RequestCancellation(), null);
            timeoutTimer = _timeProvider.CreateTimer(
                _ =>
                {
                    execution.RequestCancellation(
                        isTimeout: true,
                        onRequested: () => Interlocked.Exchange(ref timeoutTimerFired, 1));
                },
                null,
                request.Timeout,
                Timeout.InfiniteTimeSpan);

            linkedSource.Token.ThrowIfCancellationRequested();

            await operation(linkedSource.Token).ConfigureAwait(false);

            outcome = LongRunningExecutionState.Completed;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                outcome = LongRunningExecutionState.Cancelled;
            }
            else
            {
                wasTimedOut = Volatile.Read(ref timeoutTimerFired) != 0 || _timeProvider.GetUtcNow() >= execution.DeadlineAtUtc;
                outcome = wasTimedOut
                    ? LongRunningExecutionState.TimedOut
                    : LongRunningExecutionState.Cancelled;
            }

            failureReason = wasTimedOut ? "The execution exceeded its bounded timeout." : null;
        }
        catch (Exception exception)
        {
            outcome = LongRunningExecutionState.Failed;
            failureReason = "The execution failed with " + exception.GetType().Name + ".";
        }
        finally
        {
            try
            {
                // Deadline refers to observed completion, including scheduling latency. No native
                // completion timestamp or forced preemption is inferred from the delegate's Task.
                outcome = execution.MarkOperationTerminal(
                    outcome,
                    cancellationToken.IsCancellationRequested,
                    _timeProvider.GetUtcNow() >= execution.DeadlineAtUtc);
                wasTimedOut = outcome == LongRunningExecutionState.TimedOut;
                if (wasTimedOut)
                {
                    failureReason = "The execution exceeded its bounded timeout.";
                }

                if (timeoutTimer is not null)
                {
                    try
                    {
                        await timeoutTimer.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        timerReleased = false;
                        if (outcome == LongRunningExecutionState.Completed)
                        {
                            outcome = LongRunningExecutionState.Failed;
                        }
                        var releaseFailure = "The timeout timer could not be released: " + exception.GetType().Name + ".";
                        failureReason = failureReason is null ? releaseFailure : failureReason + " " + releaseFailure;
                    }
                }
                try
                {
                    await callerRegistration.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await execution.ReleaseSourceAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_syncRoot)
                {
                    _executions.Remove(execution.ExecutionId);
                }
            }
        }

        lock (_syncRoot)
        {
            var report = execution.Complete(
                outcome,
                _timeProvider.GetUtcNow(),
                wasTimedOut,
                wasCancelled: outcome is LongRunningExecutionState.Cancelled
                    or LongRunningExecutionState.TimedOut,
                failureReason,
                resourcesReleased: timerReleased);

            return report;
        }
    }

    public void Dispose()
    {
        List<TrackedExecution> activeExecutions;

        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            activeExecutions = _executions.Values.Where(execution => execution.IsActive).ToList();
            _executions.Clear();
        }

        foreach (var execution in activeExecutions)
        {
            execution.CancelFromDisposal();
        }
    }

    private void Register(TrackedExecution execution)
    {
        if (!_executions.TryAdd(execution.ExecutionId, execution))
        {
            throw new InvalidOperationException(
                $"The long-running execution '{execution.ExecutionId}' is already tracked.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void CancelSource(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
            // Cancel invokes every callback before reporting their failures. One bad callback
            // must not crash the timer thread or prevent disposal of the remaining executions.
        }
    }

    private sealed class TrackedExecution
    {
        private readonly CancellationTokenSource? _ownedSource;
        private readonly object _sourceGate = new();
        private readonly TaskCompletionSource _sourceReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellationRequests;
        private int _cancellationReason;
        private bool _releaseRequested;
        private volatile LongRunningExecutionState _state;
        private volatile bool _isActive;

        public TrackedExecution(
            LongRunningExecutionStartRequest request,
            DateTimeOffset startedAt,
            bool isManaged,
            CancellationTokenSource? ownedSource = null)
        {
            ExecutionId = request.ExecutionId;
            StartedAtUtc = startedAt;
            DeadlineAtUtc = startedAt + request.Timeout;
            HeartbeatInterval = request.HeartbeatInterval;
            LastHeartbeatAtUtc = startedAt;
            IsManaged = isManaged;
            _ownedSource = ownedSource;
            State = LongRunningExecutionState.Running;
            IsActive = true;
        }

        public string ExecutionId { get; }

        public DateTimeOffset StartedAtUtc { get; }

        public DateTimeOffset DeadlineAtUtc { get; }

        public TimeSpan? HeartbeatInterval { get; }

        public DateTimeOffset LastHeartbeatAtUtc { get; private set; }

        public int HeartbeatCount { get; private set; }

        public LongRunningExecutionState State { get => _state; private set => _state = value; }

        public bool IsActive { get => _isActive; private set => _isActive = value; }

        public bool IsManaged { get; }

        public void RecordHeartbeat(DateTimeOffset occurredAtUtc)
        {
            HeartbeatCount++;
            LastHeartbeatAtUtc = occurredAtUtc;
        }

        public LongRunningExecutionSnapshot CreateSnapshot() => new(
            ExecutionId,
            State,
            StartedAtUtc,
            LastHeartbeatAtUtc,
            DeadlineAtUtc,
            HeartbeatInterval,
            HeartbeatCount,
            IsActive);

        public LongRunningExecutionReport Complete(
            LongRunningExecutionState outcome,
            DateTimeOffset completedAtUtc,
            bool wasTimedOut,
            bool wasCancelled,
            string? failureReason,
            bool resourcesReleased)
        {
            State = outcome;
            IsActive = false;

            return new LongRunningExecutionReport(
                ExecutionId,
                outcome,
                StartedAtUtc,
                completedAtUtc,
                HeartbeatCount,
                wasTimedOut,
                wasCancelled,
                ResourcesReleased: resourcesReleased,
                failureReason);
        }

        public void CancelFromDisposal()
        {
            if (_ownedSource is not null)
            {
                RequestCancellation();
            }
            else
            {
                MarkOperationTerminal(LongRunningExecutionState.Cancelled, true, false);
            }
        }

        public LongRunningExecutionState MarkOperationTerminal(
            LongRunningExecutionState outcome, bool callerCancelled, bool deadlineElapsed)
        {
            lock (_sourceGate)
            {
                if (outcome is LongRunningExecutionState.Completed
                    or LongRunningExecutionState.Cancelled or LongRunningExecutionState.TimedOut)
                {
                    if (callerCancelled || _cancellationReason == 1)
                    {
                        outcome = LongRunningExecutionState.Cancelled;
                    }
                    else if (_cancellationReason == 2 || deadlineElapsed)
                    {
                        outcome = LongRunningExecutionState.TimedOut;
                    }
                }
                State = outcome;
                IsActive = false;
                return outcome;
            }
        }

        public void RequestCancellation(bool isTimeout = false, Action? onRequested = null)
        {
            if (_ownedSource is null) { return; }
            lock (_sourceGate)
            {
                if (!IsActive || _releaseRequested) { return; }
                _cancellationRequests++;
                if (_cancellationReason == 0) { _cancellationReason = isTimeout ? 2 : 1; }
                onRequested?.Invoke();
            }
            try
            {
                // User callbacks run with neither the service nor source gate held.
                CancelSource(_ownedSource);
            }
            finally
            {
                var release = false;
                lock (_sourceGate)
                {
                    _cancellationRequests--;
                    release = _releaseRequested && _cancellationRequests == 0;
                }
                if (release) { DisposeSource(); }
            }
        }

        public Task ReleaseSourceAsync()
        {
            var release = false;
            lock (_sourceGate)
            {
                if (!_releaseRequested)
                {
                    _releaseRequested = true;
                    release = _cancellationRequests == 0;
                }
            }
            if (release) { DisposeSource(); }
            return _sourceReleased.Task;
        }

        private void DisposeSource()
        {
            _ownedSource?.Dispose();
            _sourceReleased.TrySetResult();
        }
    }
}
