using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Quotas;

public sealed class QuotaSchedulerLifetimeTests
{
    [Fact]
    public async Task DisposedSchedulerRejectsBulkRefresh()
    {
        var scheduler = Create(new ProbeAdapter(), new TimerTime());
        scheduler.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scheduler.RefreshAllEligibleAccountsAsync());
    }

    [Fact]
    public void DisposedSchedulerRejectsSchedulingButKeepsStatusInspection()
    {
        var scheduler = Create(new ProbeAdapter(), new TimerTime());
        scheduler.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scheduler.ScheduleNextRefresh("a", "p"));
        Assert.Null(scheduler.GetStatus("a"));
        Assert.Empty(scheduler.GetAllStatuses());
    }

    [Fact]
    public async Task LifecycleLogsRecordActualTimerCleanupCompletion()
    {
        var time = new TimerTime();
        var logger = new ProbeLogger();
        using var scheduler = Create(new ProbeAdapter(), time, logger: logger);
        await scheduler.StartAsync();
        Assert.Single(logger.Messages, message => message.Contains("background timer started.", StringComparison.Ordinal));
        var timer = time.Timers[0];
        timer.DisposalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = scheduler.StopAsync();
        try
        {
            Assert.DoesNotContain(logger.Messages, message => message.Contains("background timer stopped.", StringComparison.Ordinal));
            timer.DisposalGate.TrySetResult();
            await stop;
            await scheduler.StopAsync();
            Assert.Single(logger.Messages, message => message.Contains("background timer stopped.", StringComparison.Ordinal));
        }
        finally { timer.DisposalGate.TrySetResult(); }
    }

    [Fact]
    public async Task BackgroundPersistenceFailureIsAttributedWithoutExceptionMessage()
    {
        var time = new TimerTime();
        var logger = new ProbeLogger();
        using var scheduler = Create(new ProbeAdapter(), time, new FailingRepository(), logger);
        await scheduler.StartAsync();
        scheduler.ScheduleNextRefresh("fixture-account-17", "fixture-provider-42");
        time.Advance();
        time.Timers[0].Fire();
        var message = await logger.Error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("fixture-account-17", message, StringComparison.Ordinal);
        Assert.Contains("fixture-provider-42", message, StringComparison.Ordinal);
        Assert.Contains(nameof(IOException), message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture sensitive message", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposedSchedulerRejectsRefreshAndRestart()
    {
        var time = new TimerTime();
        var adapter = new ProbeAdapter();
        var scheduler = Create(adapter, time);
        scheduler.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scheduler.RefreshAccountNowAsync("p", "a", force: true));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scheduler.StartAsync());
        Assert.Empty(time.Timers);
        Assert.Equal(0, adapter.Calls);
    }

    [Fact]
    public async Task DisposedSchedulerRejectsCachedRefresh()
    {
        var scheduler = Create(new ProbeAdapter(), new TimerTime());
        await scheduler.RefreshAccountNowAsync("p", "a", force: true);
        scheduler.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scheduler.RefreshAccountNowAsync("p", "a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalDoesNotDestroyAnActiveManualProviderLease(bool asynchronous)
    {
        var adapter = new ProbeAdapter { Hold = true };
        var scheduler = Create(adapter, new TimerTime());
        using var caller = new CancellationTokenSource();
        var first = scheduler.RefreshAccountNowAsync("p", "first", force: true);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = scheduler.RefreshAccountNowAsync("p", "second", force: true, cancellationToken: caller.Token);
        try
        {
            if (asynchronous) { await scheduler.DisposeAsync(); } else { scheduler.Dispose(); }
            adapter.Release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, adapter.Calls);
            Assert.All(scheduler.GetAllStatuses(), status => Assert.False(status.IsRefreshing));
        }
        finally
        {
            caller.Cancel();
            adapter.Release.TrySetResult();
            try { await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task StopStillDisposesTimerWhenAnAdapterCancellationCallbackThrows()
    {
        var time = new TimerTime();
        var adapter = new ProbeAdapter { Hold = true, OnCancellation = () => throw new InvalidOperationException("fixture callback") };
        using var scheduler = Create(adapter, time);
        await scheduler.StartAsync();
        scheduler.ScheduleNextRefresh("a", "p");
        time.Advance();
        time.Timers[0].Fire();
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(time.Timers[0].Disposed);
        await scheduler.StartAsync();
        Assert.Equal(2, time.Timers.Count);
        adapter.Release.TrySetResult();
    }

    [Fact]
    public async Task StopDoesNotRunForeignCancellationCallbacksUnderSchedulerGate()
    {
        var time = new TimerTime();
        var nestedFinished = false;
        Task? nested = null;
        QuotaRefreshScheduler? scheduler = null;
        var adapter = new ProbeAdapter
        {
            Hold = true,
            OnCancellation = () =>
            {
                nested = Task.Run(() => scheduler!.StartAsync());
#pragma warning disable xUnit1031 // Deliberate bounded cross-thread gate probe in a synchronous cancellation callback.
                nestedFinished = nested.Wait(TimeSpan.FromMilliseconds(250));
#pragma warning restore xUnit1031
            }
        };
        using (scheduler = Create(adapter, time))
        {
            await scheduler.StartAsync();
            scheduler.ScheduleNextRefresh("a", "p");
            time.Advance();
            time.Timers[0].Fire();
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (nested is not null) { await nested.WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.True(nestedFinished);
            Assert.True(time.Timers[0].Disposed);
            Assert.Equal(2, time.Timers.Count);
            adapter.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task AStaleTimerCallbackCannotScheduleWorkForTheRestartedEpoch()
    {
        var time = new TimerTime();
        var adapter = new ProbeAdapter();
        using var scheduler = Create(adapter, time);
        await scheduler.StartAsync();
        var oldTimer = time.Timers[0];
        await scheduler.StopAsync();
        await scheduler.StartAsync();
        scheduler.ScheduleNextRefresh("a", "p");
        time.Advance();
        oldTimer.Fire(); // Simulates a callback already queued when the old timer was detached.
        var winner = await Task.WhenAny(adapter.Started.Task, Task.Delay(250));
        Assert.NotSame(adapter.Started.Task, winner);
        Assert.Equal(0, adapter.Calls);
        time.Timers[1].Fire();
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelledFirstAttemptRemainsScheduledWithoutInventingAFailure()
    {
        var time = new TimerTime();
        var adapter = new ProbeAdapter { Hold = true };
        using var scheduler = Create(adapter, time);
        using var caller = new CancellationTokenSource();
        var refresh = scheduler.RefreshAccountNowAsync("p", "a", force: true, cancellationToken: caller.Token);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        var status = scheduler.GetStatus("a")!;
        Assert.NotNull(status.NextScheduledRefreshAt);
        Assert.True(status.NextScheduledRefreshAt > time.GetUtcNow());
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Null(status.LatestSnapshotId);
        Assert.Null(status.LastSuccessfulRefreshAt);
        Assert.False(status.IsRefreshing);
    }

    [Fact]
    public async Task StopDetachesTheEpochEvenWhenTimerCleanupFails()
    {
        var time = new TimerTime();
        using var scheduler = Create(new ProbeAdapter(), time);
        await scheduler.StartAsync();
        time.Timers[0].FailDisposal = true;
        await Assert.ThrowsAsync<IOException>(() => scheduler.StopAsync());
        await scheduler.StartAsync();
        Assert.Equal(2, time.Timers.Count);
    }

    [Fact]
    public async Task PreCancelledStartDoesNotAllocateATimer()
    {
        var time = new TimerTime();
        using var scheduler = Create(new ProbeAdapter(), time);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.StartAsync(caller.Token));
        Assert.Empty(time.Timers);
    }

    [Fact]
    public async Task ConcurrentStopsWaitForActualAsyncTimerCleanup()
    {
        var time = new TimerTime();
        using var scheduler = Create(new ProbeAdapter(), time);
        await scheduler.StartAsync();
        var timer = time.Timers[0];
        timer.DisposalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.StopAsync();
        var second = scheduler.StopAsync();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            timer.DisposalGate.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(timer.Disposed);
        }
        finally { timer.DisposalGate.TrySetResult(); }
    }

    private static QuotaRefreshScheduler Create(ProbeAdapter adapter, TimerTime time,
        IQuotaSnapshotRepository? repository = null, ILogger<QuotaRefreshScheduler>? logger = null) => new(
        adapter, repository ?? new InMemoryQuotaSnapshotRepository(), options: Options.Create(new QuotaSchedulerOptions
        {
            ProviderRateLimitDelay = TimeSpan.Zero, JitterRatio = 0,
            MinRefreshInterval = TimeSpan.FromSeconds(10), DefaultTtl = TimeSpan.FromSeconds(30),
            BackgroundPollInterval = TimeSpan.FromSeconds(10)
        }), timeProvider: time, logger: logger);

    private sealed class FailingRepository : IQuotaSnapshotRepository
    {
        public Task SaveAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default) =>
            throw new IOException("fixture sensitive message");
        public Task<QuotaSnapshot?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<QuotaSnapshot?>(null);
        public Task<QuotaSnapshot?> GetLatestForAccountAsync(string accountId, string? modelId = null, CancellationToken cancellationToken = default) => Task.FromResult<QuotaSnapshot?>(null);
        public Task<IReadOnlyList<QuotaSnapshot>> ListLatestByAccountIdAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<QuotaSnapshot>>(Array.Empty<QuotaSnapshot>());
        public Task<IReadOnlyList<QuotaSnapshot>> ListAllLatestAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<QuotaSnapshot>>(Array.Empty<QuotaSnapshot>());
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class ProbeLogger : ILogger<QuotaRefreshScheduler>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public TaskCompletionSource<string> Error { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Enqueue(message);
            if (logLevel == LogLevel.Error) { Error.TrySetResult(message); }
        }
    }

    private sealed class ProbeAdapter : IQuotaSourceAdapter
    {
        public string SourceKind => "lifetime-fixture";
        public bool Hold { get; init; }
        public Action? OnCancellation { get; init; }
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<QuotaSnapshot> FetchQuotaAsync(string providerProfileId, string accountId, string? modelId = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            var pending = Hold ? Release.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
            using var registration = cancellationToken.Register(() => OnCancellation?.Invoke());
            Started.TrySetResult();
            await pending;
            return QuotaSnapshot.CreateUnsupported(accountId, providerProfileId, modelId);
        }
    }

    private sealed class TimerTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        public List<ProbeTimer> Timers { get; } = new();
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now += TimeSpan.FromMinutes(1);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ProbeTimer(callback, state);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ProbeTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }
        public bool FailDisposal { get; set; }
        public TaskCompletionSource? DisposalGate { get; set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Fire() => callback(state);
        public void Dispose()
        {
            Disposed = true;
            if (FailDisposal) { throw new IOException("fixture timer cleanup"); }
        }
        public async ValueTask DisposeAsync()
        {
            if (DisposalGate is not null) { await DisposalGate.Task; }
            Dispose();
        }
    }
}
