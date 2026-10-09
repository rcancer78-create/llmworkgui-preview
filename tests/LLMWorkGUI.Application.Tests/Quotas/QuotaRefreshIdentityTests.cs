using System.Collections.Concurrent;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Quotas;

public sealed class QuotaRefreshIdentityTests
{
    [Theory]
    [InlineData("model-a", "model-b")]
    [InlineData("model-a", null)]
    [InlineData(null, "model-b")]
    public async Task OverlappingRefreshes_DoNotJoinDifferentModelScopes(string? firstModel, string? secondModel)
    {
        var adapter = new ModelQuotaAdapter { HoldFirst = true };
        await using var scheduler = CreateScheduler(adapter);
        var first = scheduler.RefreshAccountNowAsync("provider", "account", firstModel, force: true);
        await adapter.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var second = scheduler.RefreshAccountNowAsync("provider", "account", secondModel, force: true);
            adapter.ReleaseFirst.TrySetResult();
            var snapshots = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(firstModel, snapshots[0].ModelId);
            Assert.Equal(secondModel, snapshots[1].ModelId);
            Assert.NotEqual(snapshots[0].Id, snapshots[1].Id);
            Assert.Equal(2, adapter.Calls);
        }
        finally
        {
            adapter.ReleaseFirst.TrySetResult();
        }
    }

    [Theory]
    [InlineData("model-a", "model-b")]
    [InlineData("model-a", null)]
    [InlineData(null, "model-b")]
    public async Task RapidRefresh_DoesNotReturnAnotherModelScope(string? firstModel, string? secondModel)
    {
        var adapter = new ModelQuotaAdapter();
        await using var scheduler = CreateScheduler(adapter);

        var first = await scheduler.RefreshAccountNowAsync("provider", "account", firstModel);
        var second = await scheduler.RefreshAccountNowAsync("provider", "account", secondModel);

        Assert.Equal(firstModel, first.ModelId);
        Assert.Equal(secondModel, second.ModelId);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, adapter.Calls);
    }

    [Fact]
    public async Task AccountStatus_RemainsRefreshingWhileAnotherModelOwnerIsPending()
    {
        var adapter = new ModelQuotaAdapter { HoldFirst = true, HoldSecond = true };
        await using var scheduler = CreateScheduler(adapter);
        var first = scheduler.RefreshAccountNowAsync("provider", "account", "model-a", force: true);
        await adapter.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = scheduler.RefreshAccountNowAsync("provider", "account", "model-b", force: true);
        try
        {
            // The first gate resumes synchronously, so its status cleanup runs before
            // release returns. The second owner stays suspended in the adapter.
            adapter.ReleaseFirst.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            await adapter.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(second.IsCompleted);
            Assert.True(scheduler.GetStatus("account")!.IsRefreshing);
            Assert.True(Assert.Single(scheduler.GetAllStatuses()).IsRefreshing);
        }
        finally
        {
            adapter.ReleaseFirst.TrySetResult();
            adapter.ReleaseSecond.TrySetResult();
            await second.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(scheduler.GetStatus("account")!.IsRefreshing);
    }

    [Fact]
    public async Task NonForcedRefresh_JoinsPendingSameScopeInsteadOfReturningOlderCache()
    {
        var adapter = new ModelQuotaAdapter { HoldSecond = true };
        await using var scheduler = CreateScheduler(adapter);
        var old = await scheduler.RefreshAccountNowAsync("provider", "account", "model");
        var owner = scheduler.RefreshAccountNowAsync("provider", "account", "model", force: true);
        await adapter.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var joined = scheduler.RefreshAccountNowAsync("provider", "account", "model");
            adapter.ReleaseSecond.TrySetResult();
            var results = await Task.WhenAll(owner, joined).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotEqual(old.Id, results[0].Id);
            Assert.Equal(results[0].Id, results[1].Id);
            Assert.Equal(2, adapter.Calls);
        }
        finally
        {
            adapter.ReleaseSecond.TrySetResult();
        }
    }

    private static QuotaRefreshScheduler CreateScheduler(IQuotaSourceAdapter adapter) => new(
        adapter,
        new SnapshotRepository(),
        options: Options.Create(new QuotaSchedulerOptions
        {
            AutoStartBackgroundPolling = false,
            ProviderRateLimitDelay = TimeSpan.Zero,
            MinRefreshInterval = TimeSpan.FromMinutes(1),
            JitterRatio = 0
        }),
        timeProvider: new TestTimeProvider());

    private sealed class ModelQuotaAdapter : IQuotaSourceAdapter
    {
        private int _calls;
        public string SourceKind => "synthetic-model-scoped";
        public bool HoldFirst { get; init; }
        public bool HoldSecond { get; init; }
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new();
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<QuotaSnapshot> FetchQuotaAsync(
            string providerProfileId, string accountId, string? modelId = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                if (HoldFirst)
                {
                    await ReleaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else if (call == 2)
            {
                SecondStarted.TrySetResult();
                if (HoldSecond)
                {
                    await ReleaseSecond.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return QuotaSnapshot.CreateUnknown(accountId, providerProfileId, modelId);
        }
    }

    private sealed class SnapshotRepository : IQuotaSnapshotRepository
    {
        private readonly ConcurrentDictionary<string, QuotaSnapshot> _snapshots = new();

        public Task<QuotaSnapshot?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshots.GetValueOrDefault(id));

        public Task<QuotaSnapshot?> GetLatestForAccountAsync(
            string accountId, string? modelId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshots.Values.Where(s => s.AccountId == accountId && (modelId is null || s.ModelId == modelId))
                .OrderByDescending(s => s.CapturedAt).FirstOrDefault());

        public Task<IReadOnlyList<QuotaSnapshot>> ListLatestByAccountIdAsync(
            string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QuotaSnapshot>>(_snapshots.Values.Where(s => s.AccountId == accountId).ToArray());

        public Task<IReadOnlyList<QuotaSnapshot>> ListAllLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<QuotaSnapshot>>(_snapshots.Values.ToArray());

        public Task SaveAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            _snapshots[snapshot.Id] = snapshot;
            return Task.CompletedTask;
        }

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            var removed = _snapshots.Values.Where(s => s.CapturedAt < cutoff).Count(s => _snapshots.TryRemove(s.Id, out _));
            return Task.FromResult(removed);
        }
    }
}
