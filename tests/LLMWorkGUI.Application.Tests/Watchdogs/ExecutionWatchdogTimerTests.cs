using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Watchdogs;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Watchdogs;

public sealed class ExecutionWatchdogTimerTests
{
    [Theory]
    [InlineData("hard-timeout", ExecutionTurnOutcome.TimedOut, true)]
    [InlineData("user", ExecutionTurnOutcome.UserCancelled, false)]
    [InlineData("lifetime", ExecutionTurnOutcome.Ambiguous, false)]
    public async Task CompletedCancellationResultPreservesTheActualCancellationAuthority(
        string authority, ExecutionTurnOutcome expected, bool consumesModelBudget)
    {
        var time = new ManualTimerProvider();
        var hardDeadline = TimeSpan.FromMinutes(10);
        using var user = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        var supervisor = new CancellationResultSupervisor(() =>
        {
            if (authority == "hard-timeout") time.Fire(hardDeadline);
            else if (authority == "user") user.Cancel();
            else lifetime.Cancel();
        });
        var result = await new ExecutionWatchdog(supervisor, time).WatchAsync(new()
        {
            Specification = new ProcessStartSpecification { ExecutionId = "owned-cancellation-result", FileName = "unused" },
            ProcessIsLongLived = false, TurnHardTimeout = hardDeadline, SessionConfirmationTimeout = null,
            SilenceObservationInterval = TimeSpan.FromHours(1), ProcessLifetimeToken = lifetime.Token
        }, turnCancellationToken: user.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(supervisor.ReturnedCompletedResult); // Both deadline and native result exist before WhenAny.
        Assert.Equal(ProcessTerminationReason.UserCancelled, supervisor.Result!.TerminationReason);
        Assert.NotNull(result.ExitCode); // Classification uses a completed supervisor result, not an OCE or abandoned task.
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(consumesModelBudget, result.ConsumesModelRetryBudget);
        Assert.Equal(0, time.ActiveTimerCount);
    }

    [Fact]
    public async Task UnrepresentableSessionDeadlineIsRejectedBeforeLaunchingTheProcess()
    {
        var time = new ManualTimerProvider();
        var supervisor = new HeldSupervisor();
        var watchdog = new ExecutionWatchdog(supervisor, time);
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => watchdog.WatchAsync(new ExecutionWatchdogRequest
            {
                Specification = new ProcessStartSpecification { ExecutionId = "invalid-deadline", FileName = "unused" },
                SessionConfirmationTimeout = TimeSpan.MaxValue
            }));
            Assert.Equal(0, supervisor.CallCount);
            Assert.Equal(0, time.ActiveTimerCount);
        }
        finally
        {
            supervisor.Complete();
        }
    }

    [Fact]
    public async Task CancellationDuringTimerSetupPreventsLongLivedProcessLaunch()
    {
        using var cancellation = new CancellationTokenSource();
        var time = new ManualTimerProvider { OnTimerCreated = cancellation.Cancel };
        var supervisor = new HeldSupervisor();
        var watchdog = new ExecutionWatchdog(supervisor, time);
        try
        {
            var result = await watchdog.WatchAsync(new ExecutionWatchdogRequest
            {
                Specification = new ProcessStartSpecification { ExecutionId = "cancel-before-launch", FileName = "unused" },
                ProcessIsLongLived = true
            }, turnCancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ExecutionTurnOutcome.UserCancelled, result.Outcome);
            Assert.Equal(0, supervisor.CallCount);
            Assert.Equal(0, time.ActiveTimerCount);
        }
        finally
        {
            supervisor.Complete();
        }
    }

    [Theory]
    [InlineData(true, ExecutionTurnOutcome.Ambiguous)]
    [InlineData(false, ExecutionTurnOutcome.Ambiguous)]
    public async Task InjectedClockControlsDeadlinesAndCompletedWatchRetiresTimers(
        bool sessionDeadline, ExecutionTurnOutcome expectedOutcome)
    {
        var time = new ManualTimerProvider();
        var supervisor = new HeldSupervisor();
        var watchdog = new ExecutionWatchdog(supervisor, time);
        var deadline = sessionDeadline ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(10);
        var watching = watchdog.WatchAsync(new ExecutionWatchdogRequest
        {
            Specification = new ProcessStartSpecification { ExecutionId = "manual-clock", FileName = "unused" },
            ProcessIsLongLived = true,
            TurnHardTimeout = TimeSpan.FromMinutes(10),
            SessionConfirmationTimeout = sessionDeadline ? TimeSpan.FromMinutes(3) : null,
            SilenceObservationInterval = TimeSpan.FromHours(1)
        });

        try
        {
            Assert.True(time.HasActiveTimer(deadline), "The watchdog deadline must use its injected TimeProvider.");
            time.Fire(deadline);

            var result = await watching.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expectedOutcome, result.Outcome);
            Assert.False(result.ConsumesModelRetryBudget);
            Assert.False(supervisor.ReceivedToken.IsCancellationRequested);
            Assert.Equal(0, time.ActiveTimerCount);
        }
        finally
        {
            supervisor.Complete();
            await watching.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledTurnWithoutTerminationEvidenceIsAmbiguous(bool longLived)
    {
        var time = new ManualTimerProvider();
        var supervisor = new HeldSupervisor();
        using var cancellation = new CancellationTokenSource();
        var watching = new ExecutionWatchdog(supervisor, time).WatchAsync(new ExecutionWatchdogRequest
        {
            Specification = new ProcessStartSpecification { ExecutionId = "pending-stop", FileName = "unused" },
            ProcessIsLongLived = longLived, SessionConfirmationTimeout = null
        }, turnCancellationToken: cancellation.Token);
        try
        {
            Assert.Equal(1, supervisor.CallCount);
            cancellation.Cancel();
            if (!longLived)
            {
                var grace = TimeSpan.FromSeconds(45);
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (!time.HasActiveTimer(grace) && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
                Assert.True(time.HasActiveTimer(grace));
                time.Fire(grace);
            }
            var result = await watching.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ExecutionTurnOutcome.Ambiguous, result.Outcome);
            Assert.False(result.ConsumesModelRetryBudget);
            Assert.Null(result.ExitCode);
            Assert.Equal(!longLived, supervisor.ReceivedToken.IsCancellationRequested);
        }
        finally { supervisor.Complete(); await watching.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class CancellationResultSupervisor(Action cancel) : IProcessSupervisor
    {
        public bool ReturnedCompletedResult { get; private set; }
        public ProcessExecutionResult? Result { get; private set; }
        public Task<ProcessExecutionResult> ExecuteAsync(ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<ProcessExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => completion.TrySetResult(Result = new()
            {
                ExecutionId = specification.ExecutionId, TerminationReason = ProcessTerminationReason.UserCancelled,
                ExitCode = 1, StartedAtUtc = DateTimeOffset.UnixEpoch, ExitedAtUtc = DateTimeOffset.UnixEpoch,
                RunDirectory = "unused", StandardOutputLogPath = "unused", StandardErrorLogPath = "unused",
                StandardOutputBytes = 0, StandardErrorBytes = 0, StandardOutputHead = "", StandardOutputTail = "",
                StandardErrorHead = "", StandardErrorTail = "", OutputOverflowed = false
            }));
            // Complete synchronously inside the cancellation callback before returning to the
            // watchdog. This forces its completed-execution branch without scheduler heuristics.
            cancel();
            ReturnedCompletedResult = completion.Task.IsCompletedSuccessfully;
            return completion.Task;
        }
    }

    private sealed class HeldSupervisor : IProcessSupervisor
    {
        private readonly TaskCompletionSource<ProcessExecutionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReceivedToken { get; private set; }
        public int CallCount { get; private set; }

        public Task<ProcessExecutionResult> ExecuteAsync(ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            ReceivedToken = cancellationToken;
            return _completion.Task;
        }

        public void Complete() => _completion.TrySetResult(new ProcessExecutionResult
        {
            ExecutionId = "manual-clock", TerminationReason = ProcessTerminationReason.None, ExitCode = 0,
            StartedAtUtc = DateTimeOffset.UnixEpoch, ExitedAtUtc = DateTimeOffset.UnixEpoch,
            RunDirectory = "unused", StandardOutputLogPath = "unused", StandardErrorLogPath = "unused",
            StandardOutputBytes = 0, StandardErrorBytes = 0, StandardOutputHead = "", StandardOutputTail = "",
            StandardErrorHead = "", StandardErrorTail = "", OutputOverflowed = false
        });
    }

    private sealed class ManualTimerProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();
        public Action? OnTimerCreated { get; init; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
        public int ActiveTimerCount { get { lock (_timers) return _timers.Count(t => !t.IsDisposed); } }
        public bool HasActiveTimer(TimeSpan due) { lock (_timers) return _timers.Any(t => !t.IsDisposed && t.Due == due); }
        public void Fire(TimeSpan due)
        {
            ManualTimer timer;
            lock (_timers) timer = Assert.Single(_timers.Where(t => !t.IsDisposed && t.Due == due));
            timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            lock (_timers) _timers.Add(timer);
            OnTimerCreated?.Invoke();
            return timer;
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private int _disposed;
            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
            public TimeSpan Due { get; private set; } = due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (IsDisposed) return false;
                Due = dueTime;
                return true;
            }
            public void Fire() { if (!IsDisposed) callback(state); }
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
