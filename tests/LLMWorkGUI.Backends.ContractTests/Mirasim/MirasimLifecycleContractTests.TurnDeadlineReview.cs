using System.Net;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed partial class MirasimLifecycleContractTests
{
    [Fact]
    public async Task ModelTurnMayOutlastControlRequestBudgetWithoutBecomingUncertain()
    {
        var clock = new ManualDeadlineTimeProvider();
        var handler = new RecordingHttpMessageHandler((_, token) =>
        {
            // Advance the deadline clock, not wall time: a busy runner must not
            // turn a 200 ms model response into an accidental hard timeout.
            clock.Advance(TimeSpan.FromMilliseconds(200));
            token.ThrowIfCancellationRequested();
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, DoneJson()));
        });
        var locks = new FakeCheckoutLockService();
        using var service = CreateService(handler, locks, requestTimeout: TimeSpan.FromMilliseconds(50),
            turnHardTimeout: TimeSpan.FromSeconds(2), timeProvider: clock);
        var result = await service.ExecuteTurnAsync(CreateRequest()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MirasimTurnStatus.Completed, result.Status);
        Assert.True(result.IsTerminal);
        Assert.Single(handler.Requests);
        Assert.Equal(1, locks.Token.ReleaseCount);
    }

    [Fact]
    public async Task TurnHardDeadlineStillPreservesUncertainDeliveryAndDoesNotResend()
    {
        var clock = new ManualDeadlineTimeProvider();
        var handler = new RecordingHttpMessageHandler(async (_, token) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The hard deadline must cancel the pending response.");
        });
        var locks = new FakeCheckoutLockService();
        using var service = CreateService(handler, locks, requestTimeout: TimeSpan.FromSeconds(2),
            turnHardTimeout: TimeSpan.FromMilliseconds(50), timeProvider: clock);
        var result = await service.ExecuteTurnAsync(CreateRequest()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.Equal(MirasimTurnErrorClass.Timeout, result.ErrorClass);
        Assert.False(result.IsTerminal);
        Assert.Single(handler.Requests);
        Assert.True(locks.Token.IsHeld);
        Assert.Equal(0, locks.Token.ReleaseCount);
    }

    [Fact]
    public async Task CallerCancellationAfterDeliveryStillRetainsWriterUntilTerminalEvidence()
    {
        using var caller = new CancellationTokenSource();
        var handler = new RecordingHttpMessageHandler(async (_, token) =>
        {
            caller.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            return JsonResponse(HttpStatusCode.OK, DoneJson());
        });
        var locks = new FakeCheckoutLockService();
        var service = CreateService(handler, locks, requestTimeout: TimeSpan.FromMilliseconds(50),
            turnHardTimeout: TimeSpan.FromSeconds(2));
        var result = await service.ExecuteTurnAsync(CreateRequest(), caller.Token);
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.False(result.IsTerminal);
        Assert.Single(handler.Requests);
        Assert.True(locks.Token.IsHeld);
        Assert.Equal(0, locks.Token.ReleaseCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4294967295L)]
    public void InvalidModelTurnDeadlineIsRejectedBeforeAnyRequest(long milliseconds)
    {
        var options = new MirasimOptions { TurnHardTimeout = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    // Only one-shot CancellationTokenSource timers are needed by this fixture.
    // Callbacks execute during Advance, making control and turn budgets independent
    // of test-runner scheduling while still exercising the real cancellation path.
    private sealed class ManualDeadlineTimeProvider : TimeProvider
    {
        private readonly List<DeadlineTimer> _timers = [];
        private TimeSpan _elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }

        private sealed class DeadlineTimer(ManualDeadlineTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? _dueAt;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                if (period != Timeout.InfiniteTimeSpan)
                    throw new NotSupportedException("This deadline fixture only supports one-shot timers.");
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._elapsed + dueTime;
                return true;
            }

            public void FireIfDue()
            {
                if (_dueAt is not { } dueAt || dueAt > clock._elapsed) return;
                _dueAt = null;
                callback(state);
            }

            public void Dispose()
            {
                _disposed = true;
                _dueAt = null;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
