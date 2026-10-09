using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Quotas;

public sealed class QuotaAcknowledgementD001Tests
{
    [Fact]
    public async Task CancelledJoinerReturnsBeforeOwnerAndDoesNotCancelItsSqliteCommit()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedAsync(database);
        var repository = new SqliteQuotaSnapshotRepository(database.Factory);
        var adapter = new BarrierAdapter();
        await using var scheduler = CreateScheduler(adapter, repository);
        var notices = 0;
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.QuotaRefreshed += (_, _) => { notices++; notified.TrySetResult(); };
        var owner = scheduler.RefreshAccountNowAsync("profile", "account", force: true);
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancelled = new CancellationTokenSource();
        try
        {
            var joiner = scheduler.RefreshAccountNowAsync("profile", "account", force: true,
                cancellationToken: cancelled.Token);
            cancelled.Cancel();
            // The bounded wait fails on the old implementation; release still runs in finally.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(owner.IsCompleted);
            Assert.True(scheduler.GetStatus("account")!.IsRefreshing);
            Assert.Equal(1, adapter.Calls);
            Assert.Null(await repository.GetLatestForAccountAsync("account"));
        }
        finally
        {
            adapter.Release.TrySetResult();
            await owner.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var result = await owner;
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(result.Id, (await repository.GetByIdAsync(result.Id))!.Id);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, notices);
        Assert.False(scheduler.GetStatus("account")!.IsRefreshing);
    }

    [Fact]
    public async Task SuccessfulSqliteSaveIsPublishedEvenIfCallerCancelsDuringItsAcknowledgement()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedAsync(database);
        var real = new SqliteQuotaSnapshotRepository(database.Factory);
        using var cancellation = new CancellationTokenSource();
        var repository = new CancelAfterSaveRepository(real, cancellation);
        var adapter = new BarrierAdapter { Hold = false };
        await using var scheduler = CreateScheduler(adapter, repository);
        var notices = new List<QuotaRefreshedEventArgs>();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.QuotaRefreshed += (_, args) => { notices.Add(args); notified.TrySetResult(); };

        var result = await scheduler.RefreshAccountNowAsync("profile", "account",
            cancellationToken: cancellation.Token);
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(result.Id, (await real.GetByIdAsync(result.Id))!.Id);
        Assert.Equal(result.Id, scheduler.GetStatus("account")!.LatestSnapshotId);
        Assert.False(scheduler.GetStatus("account")!.IsRefreshing);
        Assert.Equal(result.Id, Assert.Single(notices).Snapshot.Id);
        var cached = await scheduler.RefreshAccountNowAsync("profile", "account");
        Assert.Equal(result.Id, cached.Id);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, repository.Saves);
        Assert.Single(notices);
    }

    private static async Task SeedAsync(TestDatabase database)
    {
        await new SqliteProviderProfileRepository(database.Factory).UpsertAsync(new ProviderProfile(
            "profile", "Synthetic quota profile", BackendType.OpenCode, null, null, DataClassification.PrivateSource, true));
        await new SqliteAccountRepository(database.Factory).SaveAsync(new Account("account", "profile",
            "Synthetic quota account", null, AuthState.Valid, 1, true, HealthState.Healthy, null, null, 1, null));
    }

    private static QuotaRefreshScheduler CreateScheduler(IQuotaSourceAdapter adapter, IQuotaSnapshotRepository repository) =>
        new(adapter, repository, options: Options.Create(new QuotaSchedulerOptions
        {
            AutoStartBackgroundPolling = false, ProviderRateLimitDelay = TimeSpan.Zero,
            MinRefreshInterval = TimeSpan.FromMinutes(1), JitterRatio = 0
        }));

    private sealed class BarrierAdapter : IQuotaSourceAdapter
    {
        public string SourceKind => "synthetic-quota-barrier";
        public bool Hold { get; init; } = true;
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<QuotaSnapshot> FetchQuotaAsync(string providerProfileId, string accountId,
            string? modelId = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(cancellationToken);
            return QuotaSnapshot.CreateUnknown(accountId, providerProfileId, modelId);
        }
    }

    private sealed class CancelAfterSaveRepository(IQuotaSnapshotRepository inner, CancellationTokenSource cancellation)
        : IQuotaSnapshotRepository
    {
        public int Saves;
        public Task<QuotaSnapshot?> GetByIdAsync(string id, CancellationToken token = default) => inner.GetByIdAsync(id, token);
        public Task<QuotaSnapshot?> GetLatestForAccountAsync(string id, string? modelId = null, CancellationToken token = default) =>
            inner.GetLatestForAccountAsync(id, modelId, token);
        public Task<IReadOnlyList<QuotaSnapshot>> ListLatestByAccountIdAsync(string id, CancellationToken token = default) =>
            inner.ListLatestByAccountIdAsync(id, token);
        public Task<IReadOnlyList<QuotaSnapshot>> ListAllLatestAsync(CancellationToken token = default) => inner.ListAllLatestAsync(token);
        public async Task SaveAsync(QuotaSnapshot snapshot, CancellationToken token = default)
        {
            await inner.SaveAsync(snapshot, token);
            Saves++;
            cancellation.Cancel();
        }
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken token = default) =>
            inner.DeleteOlderThanAsync(cutoff, token);
    }
}
