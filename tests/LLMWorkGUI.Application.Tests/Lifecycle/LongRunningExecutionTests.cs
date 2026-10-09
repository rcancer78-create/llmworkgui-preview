using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Infrastructure.Lifecycle;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Lifecycle;

/// <summary>
/// Phase 12 Milestone 12B: the long-running execution supervisor detects timeouts and silent
/// heartbeats, cancels stalled executions and always releases its timeout timer and tracking entry.
/// </summary>
public sealed class LongRunningExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReapStaleExecutions_DetectsTimeoutAndOverdueHeartbeat()
    {
        var time = new MutableTimeProvider(Now);
        using var service = new LongRunningExecutionService(time);

        var stalledEvents = new List<LongRunningExecutionReport>();
        service.ExecutionStalled += (_, args) => stalledEvents.Add(args.Report);

        service.Track(new LongRunningExecutionStartRequest("execution-timeout", TimeSpan.FromMinutes(10)));
        service.Track(new LongRunningExecutionStartRequest(
            "execution-heartbeat",
            TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(1)));

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(service.Heartbeat("execution-heartbeat"));

        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30));

        var reports = service.ReapStaleExecutions();

        Assert.Equal(2, reports.Count);
        Assert.Equal(
            LongRunningExecutionState.TimedOut,
            reports.Single(report => report.ExecutionId == "execution-timeout").Outcome);
        Assert.True(reports.Single(report => report.ExecutionId == "execution-timeout").WasTimedOut);
        Assert.Equal(
            LongRunningExecutionState.HeartbeatOverdue,
            reports.Single(report => report.ExecutionId == "execution-heartbeat").Outcome);
        Assert.All(reports, report => Assert.True(report.ResourcesReleased));
        Assert.Null(service.GetSnapshot("execution-heartbeat"));
        Assert.Null(service.GetSnapshot("execution-timeout"));

        Assert.Equal(2, stalledEvents.Count);
        Assert.Equal(0, service.ActiveExecutionCount);

        // A second sweep finds nothing new and heartbeats of finished executions are rejected.
        Assert.Empty(service.ReapStaleExecutions());
        Assert.False(service.Heartbeat("execution-timeout"));
    }

    [Fact]
    public void Heartbeat_RecordsLivenessForActiveExecutions()
    {
        var time = new MutableTimeProvider(Now);
        using var service = new LongRunningExecutionService(time);

        service.Track(new LongRunningExecutionStartRequest(
            "execution-1",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(30)));

        time.Advance(TimeSpan.FromSeconds(20));

        Assert.True(service.Heartbeat("execution-1"));
        Assert.True(service.Heartbeat("execution-1", time.GetUtcNow()));

        var snapshot = service.GetSnapshot("execution-1");

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.HeartbeatCount);
        Assert.True(snapshot.IsActive);
        Assert.False(service.Heartbeat("unknown-execution"));
    }

    [Fact]
    public async Task RunAsync_CompletesAndReleasesTheTimeoutTimer()
    {
        var time = new CountingTimeProvider();
        using var service = new LongRunningExecutionService(time);

        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("execution-ok", TimeSpan.FromSeconds(30)),
            _ => Task.CompletedTask);

        Assert.True(report.IsSuccess);
        Assert.Equal(LongRunningExecutionState.Completed, report.Outcome);
        Assert.True(report.ResourcesReleased);
        Assert.False(report.WasTimedOut);
        Assert.False(report.WasCancelled);
        Assert.Equal(0, service.ActiveExecutionCount);
        Assert.Equal(0, time.OutstandingTimerCount);
    }

    [Fact]
    public async Task RunAsync_TimeoutCancelsTheOperationAndReleasesTheTimeoutTimer()
    {
        var time = new CountingTimeProvider();
        using var service = new LongRunningExecutionService(time);

        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("execution-hang", TimeSpan.FromMilliseconds(100)),
            token => Task.Delay(Timeout.InfiniteTimeSpan, token));

        Assert.Equal(LongRunningExecutionState.TimedOut, report.Outcome);
        Assert.True(report.WasTimedOut);
        Assert.True(report.WasCancelled);
        Assert.True(report.ResourcesReleased);
        Assert.NotNull(report.FailureReason);
        Assert.Equal(0, service.ActiveExecutionCount);
        Assert.Equal(0, time.OutstandingTimerCount);
    }

    [Fact]
    public async Task RunAsync_CallerCancellationIsReportedAsCancelled()
    {
        var time = new CountingTimeProvider();
        using var service = new LongRunningExecutionService(time);
        using var cancellation = new CancellationTokenSource();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runTask = service.RunAsync(
            new LongRunningExecutionStartRequest("execution-cancel", TimeSpan.FromSeconds(30)),
            async token =>
            {
                operationStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            cancellation.Token);

        await operationStarted.Task;
        cancellation.Cancel();

        var report = await runTask;

        Assert.Equal(LongRunningExecutionState.Cancelled, report.Outcome);
        Assert.True(report.WasCancelled);
        Assert.False(report.WasTimedOut);
        Assert.Equal(0, service.ActiveExecutionCount);
        Assert.Equal(0, time.OutstandingTimerCount);
    }

    [Fact]
    public async Task RunAsync_FailureIsReportedWithoutLeakingExceptionDetails()
    {
        var time = new CountingTimeProvider();
        using var service = new LongRunningExecutionService(time);

        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("execution-failure", TimeSpan.FromSeconds(30)),
            _ => Task.FromException(new InvalidOperationException("secret-material")));

        Assert.Equal(LongRunningExecutionState.Failed, report.Outcome);
        Assert.Contains("InvalidOperationException", report.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-material", report.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, time.OutstandingTimerCount);
    }

    [Fact]
    public void StartRequest_RejectsNonPositiveTimeouts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LongRunningExecutionStartRequest("execution-1", TimeSpan.Zero));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LongRunningExecutionStartRequest(
                "execution-1",
                TimeSpan.FromMinutes(1),
                TimeSpan.Zero));
    }

    [Fact]
    public async Task RunAsync_SuccessDoesNotInvokeCancellationCallbacks()
    {
        var time = new CountingTimeProvider();
        using var service = new LongRunningExecutionService(time);
        var callbacks = 0;

        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("success-callback", TimeSpan.FromMinutes(1)),
            token =>
            {
                token.Register(() =>
                {
                    Interlocked.Increment(ref callbacks);
                    throw new InvalidOperationException("callback failure");
                });
                return Task.CompletedTask;
            });

        Assert.True(report.IsSuccess);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, service.ActiveExecutionCount);
        Assert.Equal(0, time.OutstandingTimerCount);
    }

    [Fact]
    public async Task RunAsync_TimerCreationFailureReleasesTrackingEntry()
    {
        using var service = new LongRunningExecutionService(new ThrowingTimerProvider());
        var invoked = false;

        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("timer-failure", TimeSpan.FromMinutes(1)),
            _ => { invoked = true; return Task.CompletedTask; });

        Assert.Equal(LongRunningExecutionState.Failed, report.Outcome);
        Assert.False(invoked);
        Assert.True(report.ResourcesReleased);
        Assert.Equal(0, service.ActiveExecutionCount);
    }

    [Fact]
    public async Task RunAsync_CallerCancellationCannotBecomeSuccessWhenOperationSwallowsIt()
    {
        using var service = new LongRunningExecutionService();
        using var cancellation = new CancellationTokenSource();

        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("swallowed-cancel", TimeSpan.FromMinutes(1)),
            _ => { cancellation.Cancel(); return Task.CompletedTask; },
            cancellation.Token);

        Assert.Equal(LongRunningExecutionState.Cancelled, report.Outcome);
        Assert.True(report.WasCancelled);
        Assert.False(report.WasTimedOut);
        Assert.Equal(0, service.ActiveExecutionCount);
    }

    private sealed class ThrowingTimerProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("timer unavailable");
    }

    [Fact]
    public async Task RunAsync_TimeoutCallbackFailureDoesNotEscapeAndCannotBecomeSuccess()
    {
        var time = new ControlledTimerProvider();
        using var service = new LongRunningExecutionService(time);
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = service.RunAsync(
            new LongRunningExecutionStartRequest("throwing-timeout-callback", TimeSpan.FromMinutes(1)),
            token =>
            {
                token.Register(() => operation.TrySetResult());
                token.Register(() => throw new InvalidOperationException("callback failure"));
                return operation.Task;
            });

        time.Fire();
        var report = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LongRunningExecutionState.TimedOut, report.Outcome);
        Assert.True(report.WasTimedOut);
        Assert.Equal(0, service.ActiveExecutionCount);
        Assert.Equal(0, time.OutstandingTimers);
    }

    [Fact]
    public async Task Dispose_ThrowingCallbackDoesNotPreventCancellationOfOtherExecutions()
    {
        var time = new CountingTimeProvider();
        using var service = new LongRunningExecutionService(time);
        var firstOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = service.RunAsync(
            new LongRunningExecutionStartRequest("dispose-throws", TimeSpan.FromMinutes(1)),
            token =>
            {
                token.Register(() => firstOperation.TrySetResult());
                token.Register(() => throw new InvalidOperationException("callback failure"));
                return firstOperation.Task;
            });
        var second = service.RunAsync(
            new LongRunningExecutionStartRequest("dispose-other", TimeSpan.FromMinutes(1)),
            token => Task.Delay(Timeout.InfiniteTimeSpan, token));

        service.Dispose();
        var reports = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(reports, report => Assert.Equal(LongRunningExecutionState.Cancelled, report.Outcome));
        Assert.Equal(0, service.ActiveExecutionCount);
        Assert.Equal(0, time.OutstandingTimerCount);
    }

    [Fact]
    public async Task RunAsync_FaultedOperationDoesNotInvokeCancellationCallback()
    {
        using var service = new LongRunningExecutionService();
        var callbacks = 0;
        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("fault-callback", TimeSpan.FromMinutes(1)),
            token =>
            {
                token.Register(() => { callbacks++; throw new InvalidOperationException("callback failure"); });
                return Task.FromException(new FormatException("operation failure"));
            });

        Assert.Equal(LongRunningExecutionState.Failed, report.Outcome);
        Assert.Contains("FormatException", report.FailureReason);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, service.ActiveExecutionCount);
    }

    [Fact]
    public async Task RunAsync_TimerDisposalFailureStillRemovesTrackingAndDoesNotClaimRelease()
    {
        var time = new ControlledTimerProvider { ThrowOnDispose = true };
        using var service = new LongRunningExecutionService(time);
        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("timer-disposal", TimeSpan.FromMinutes(1)),
            _ => Task.CompletedTask);

        Assert.Equal(LongRunningExecutionState.Failed, report.Outcome);
        Assert.Contains("InvalidOperationException", report.FailureReason);
        Assert.DoesNotContain("private timer details", report.FailureReason);
        Assert.False(report.ResourcesReleased);
        Assert.Equal(0, service.ActiveExecutionCount);
    }

    private sealed class ControlledTimerProvider : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public bool ThrowOnDispose { get; init; }
        public bool HoldDisposal { get; init; }
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OutstandingTimers { get; private set; }
        public void Fire() => _callback!(_state);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            OutstandingTimers++;
            return new ControlledTimer(this);
        }
        private sealed class ControlledTimer(ControlledTimerProvider owner) : ITimer
        {
            private int _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose()
            {
                if (owner.ThrowOnDispose) { throw new InvalidOperationException("private timer details"); }
                if (Interlocked.Exchange(ref _disposed, 1) == 0) { owner.OutstandingTimers--; }
            }
            public async ValueTask DisposeAsync()
            {
                owner.DisposalStarted.TrySetResult();
                if (owner.HoldDisposal) { await owner.ContinueDisposal.Task; }
                Dispose();
            }
        }
    }

    [Fact]
    public async Task RunAsync_CompletedOperationCannotBeCancelledDuringTimerCleanup()
    {
        var time = new ControlledTimerProvider { HoldDisposal = true };
        using var service = new LongRunningExecutionService(time);
        using var caller = new CancellationTokenSource();
        var callbacks = 0;
        var task = service.RunAsync(
            new LongRunningExecutionStartRequest("terminal-before-cleanup", TimeSpan.FromMinutes(1)),
            token => { token.Register(() => Interlocked.Increment(ref callbacks)); return Task.CompletedTask; },
            caller.Token);
        try
        {
            await time.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(service.GetSnapshot("terminal-before-cleanup")!.IsActive);
            Assert.Equal(0, service.ActiveExecutionCount);
            service.Dispose();
            caller.Cancel();
            time.Fire();
            Assert.Equal(0, callbacks);
        }
        finally { time.ContinueDisposal.TrySetResult(); }

        var report = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(report.IsSuccess);
        Assert.True(report.ResourcesReleased);
        Assert.Equal(0, time.OutstandingTimers);
    }

    [Fact]
    public async Task RunAsync_PreCancelledCallerDoesNotInvokeOperation()
    {
        using var service = new LongRunningExecutionService();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var invoked = false;
        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("pre-cancelled", TimeSpan.FromMinutes(1)),
            _ => { invoked = true; return Task.CompletedTask; }, caller.Token);

        Assert.False(invoked);
        Assert.Equal(LongRunningExecutionState.Cancelled, report.Outcome);
        Assert.True(report.ResourcesReleased);
        Assert.Equal(0, service.ActiveExecutionCount);
    }

    [Fact]
    public async Task RunAsync_ElapsedDeadlineWithoutTimerCallbackDoesNotReportSuccess()
    {
        var time = new MutableTimeProvider(Now);
        using var service = new LongRunningExecutionService(time);
        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("late-timer", TimeSpan.FromMinutes(1)),
            _ => { time.Advance(TimeSpan.FromMinutes(2)); return Task.CompletedTask; });

        Assert.Equal(LongRunningExecutionState.TimedOut, report.Outcome);
        Assert.True(report.WasTimedOut);
        Assert.True(report.WasCancelled);
        Assert.True(report.ResourcesReleased);
        Assert.Equal(0, service.ActiveExecutionCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_TimerReleaseFailurePreservesPrimaryCancellationOutcome(bool timedOut)
    {
        var time = new ControlledTimerProvider { ThrowOnDispose = true };
        using var service = new LongRunningExecutionService(time);
        using var cancellation = new CancellationTokenSource();
        var report = await service.RunAsync(
            new LongRunningExecutionStartRequest("cancel-and-release-failure", TimeSpan.FromMinutes(1)),
            _ =>
            {
                if (timedOut) { time.Fire(); } else { cancellation.Cancel(); }
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(timedOut ? LongRunningExecutionState.TimedOut : LongRunningExecutionState.Cancelled, report.Outcome);
        Assert.Equal(timedOut, report.WasTimedOut);
        Assert.True(report.WasCancelled);
        Assert.False(report.ResourcesReleased);
        Assert.Contains("InvalidOperationException", report.FailureReason);
        Assert.DoesNotContain("private timer details", report.FailureReason);
        Assert.Equal(0, service.ActiveExecutionCount);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration)
        {
            _now = _now.Add(duration);
        }
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        private long _created;
        private long _disposed;

        public long OutstandingTimerCount =>
            Interlocked.Read(ref _created) - Interlocked.Read(ref _disposed);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            Interlocked.Increment(ref _created);

            return new CountingTimer(
                base.CreateTimer(callback, state, dueTime, period),
                this);
        }

        private sealed class CountingTimer : ITimer
        {
            private readonly ITimer _inner;
            private readonly CountingTimeProvider _owner;
            private int _disposed;

            public CountingTimer(ITimer inner, CountingTimeProvider owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => _inner.Change(dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Increment(ref _owner._disposed);
                }

                _inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();

                return ValueTask.CompletedTask;
            }
        }
    }
}
