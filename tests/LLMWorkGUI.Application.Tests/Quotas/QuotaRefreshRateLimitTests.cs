using System.Collections.Concurrent;
using System.Threading.Channels;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Quotas;

public sealed class QuotaRefreshRateLimitTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(500)]
    [InlineData(2500)]
    public async Task QueuedAccountsCountLeaseWaitTowardsTheIntervalBetweenCalls(int firstCallDurationMs)
    {
        var time = new ControlledTimeProvider();
        var adapter = new HeldFirstCallAdapter(time);
        var snapshots = new SnapshotSink();
        var interval = TimeSpan.FromSeconds(1);
        using var cancellation = new CancellationTokenSource();
        await using var scheduler = new QuotaRefreshScheduler(adapter, snapshots,
            options: Options.Create(new QuotaSchedulerOptions
            {
                AutoStartBackgroundPolling = false,
                ProviderRateLimitDelay = interval,
                JitterRatio = 0
            }), timeProvider: time);
        var requests = new List<Task<QuotaSnapshot>>();
        var initialTime = time.GetUtcNow();

        try
        {
            requests.Add(scheduler.RefreshAccountNowAsync("provider", "first", force: true,
                cancellationToken: cancellation.Token));
            Assert.Equal(initialTime, await adapter.ReadStartAsync());

            // All three accounts enter the provider queue at the same virtual instant.
            for (var index = 0; index < 3; index++)
            {
                requests.Add(scheduler.RefreshAccountNowAsync("provider", $"queued-{index}", force: true,
                    cancellationToken: cancellation.Token));
            }

            var firstCallDuration = TimeSpan.FromMilliseconds(firstCallDurationMs);
            time.Advance(firstCallDuration);
            adapter.ReleaseFirst.TrySetResult();

            if (firstCallDuration < interval)
            {
                await time.CompleteNextDelayAsync(interval - firstCallDuration);
            }

            // A slow first fetch has already satisfied the interval; a faster one waits
            // only for the remainder. Every subsequent queued call waits one interval.
            var expectedStart = initialTime + (firstCallDuration > interval ? firstCallDuration : interval);
            Assert.Equal(expectedStart, await adapter.ReadStartAsync());
            for (var index = 0; index < 2; index++)
            {
                await time.CompleteNextDelayAsync(interval);
                expectedStart += interval;
                Assert.Equal(expectedStart, await adapter.ReadStartAsync());
            }

            await Task.WhenAll(requests).WaitAsync(TestTimeout);
            Assert.Equal(4, snapshots.SavedAccounts.Count);
            Assert.All(scheduler.GetAllStatuses(), status => Assert.False(status.IsRefreshing));
        }
        finally
        {
            cancellation.Cancel();
            adapter.ReleaseFirst.TrySetResult();
            try { await Task.WhenAll(requests).WaitAsync(TestTimeout); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class HeldFirstCallAdapter(TimeProvider time) : IQuotaSourceAdapter
    {
        private readonly Channel<DateTimeOffset> _starts = Channel.CreateUnbounded<DateTimeOffset>();
        private int _calls;
        public string SourceKind => "rate-limit-fixture";
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DateTimeOffset> ReadStartAsync() => _starts.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public async Task<QuotaSnapshot> FetchQuotaAsync(string providerProfileId, string accountId,
            string? modelId = null, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            _starts.Writer.TryWrite(time.GetUtcNow());
            if (call == 1) { await ReleaseFirst.Task.WaitAsync(cancellationToken); }
            return QuotaSnapshot.CreateUnknown(accountId, providerProfileId, modelId);
        }
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly Channel<DelayTimer> _delays = Channel.CreateUnbounded<DelayTimer>();
        private DateTimeOffset _now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() { lock (_gate) { return _now; } }
        public void Advance(TimeSpan duration) { lock (_gate) { _now += duration; } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new DelayTimer(callback, state, dueTime);
            _delays.Writer.TryWrite(timer);
            return timer;
        }

        public async Task CompleteNextDelayAsync(TimeSpan expectedDelay)
        {
            var timer = await _delays.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
            Assert.Equal(expectedDelay, timer.Delay);
            Advance(timer.Delay);
            timer.Fire();
        }

        private sealed class DelayTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
        {
            private int _disposed;
            public TimeSpan Delay { get; } = delay;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Fire()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) { callback(state); }
            }
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class SnapshotSink : IQuotaSnapshotRepository
    {
        public ConcurrentBag<string> SavedAccounts { get; } = new();
        public Task SaveAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedAccounts.Add(snapshot.AccountId);
            return Task.CompletedTask;
        }
        public Task<QuotaSnapshot?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<QuotaSnapshot?>(null);
        public Task<QuotaSnapshot?> GetLatestForAccountAsync(string accountId, string? modelId = null,
            CancellationToken cancellationToken = default) => Task.FromResult<QuotaSnapshot?>(null);
        public Task<IReadOnlyList<QuotaSnapshot>> ListLatestByAccountIdAsync(string accountId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<QuotaSnapshot>>(Array.Empty<QuotaSnapshot>());
        public Task<IReadOnlyList<QuotaSnapshot>> ListAllLatestAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<QuotaSnapshot>>(Array.Empty<QuotaSnapshot>());
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }
}
