using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Quotas;

public sealed class QuotaRefreshCleanupTests
{
    [Fact]
    public async Task PreCancelledRequestDoesNotLeaveRefreshingStatus()
    {
        var adapter = new HeldAdapter();
        using var scheduler = CreateScheduler(adapter);
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.RefreshAccountNowAsync("provider", "account", force: true, cancellationToken: caller.Token));

        Assert.False(scheduler.GetStatus("account")?.IsRefreshing ?? false);
        Assert.Equal(0, adapter.Calls);
    }

    [Fact]
    public async Task CancellationWhileQueuedDoesNotReleaseAnotherAccountsProviderLock()
    {
        var adapter = new HeldAdapter { Hold = true };
        using var scheduler = CreateScheduler(adapter);
        using var caller = new CancellationTokenSource();
        var first = scheduler.RefreshAccountNowAsync("provider", "first", force: true);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = scheduler.RefreshAccountNowAsync("provider", "second", force: true, cancellationToken: caller.Token);
        Assert.True(scheduler.GetStatus("second")!.IsRefreshing);
        caller.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(scheduler.GetStatus("second")!.IsRefreshing);
            Assert.True(scheduler.GetStatus("first")!.IsRefreshing);
            Assert.Equal(1, adapter.Calls);
        }
        finally
        {
            adapter.Release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await scheduler.RefreshAccountNowAsync("provider", "second", force: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, adapter.Calls);
        Assert.All(scheduler.GetAllStatuses(), status => Assert.False(status.IsRefreshing));
    }

    [Fact]
    public async Task CancellationDuringRateLimitDelayClearsStatusAndAllowsRetry()
    {
        var adapter = new HeldAdapter();
        var options = new QuotaSchedulerOptions { ProviderRateLimitDelay = TimeSpan.FromHours(1) };
        using var scheduler = CreateScheduler(adapter, options: options);
        await scheduler.RefreshAccountNowAsync("provider", "first", force: true);
        using var caller = new CancellationTokenSource();
        var queued = scheduler.RefreshAccountNowAsync("provider", "second", force: true, cancellationToken: caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(scheduler.GetStatus("second")!.IsRefreshing);
        Assert.Equal(1, adapter.Calls);

        options.ProviderRateLimitDelay = TimeSpan.Zero;
        await scheduler.RefreshAccountNowAsync("provider", "second", force: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, adapter.Calls);
    }

    [Fact]
    public async Task CallerCancellationDuringFetchDoesNotPublishAProviderFailure()
    {
        var adapter = new HeldAdapter { Hold = true };
        var repository = new SaveProbe();
        using var scheduler = CreateScheduler(adapter, repository);
        using var caller = new CancellationTokenSource();
        var events = 0;
        scheduler.QuotaRefreshed += (_, _) => events++;
        var task = scheduler.RefreshAccountNowAsync("provider", "account", force: true, cancellationToken: caller.Token);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var status = scheduler.GetStatus("account")!;
        Assert.False(status.IsRefreshing);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Null(status.LatestSnapshotId);
        Assert.Equal(0, repository.Saves);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task FailedPersistenceClearsBusyFlagWithoutInventingASuccessfulRefresh()
    {
        var repository = new SaveProbe();
        using var scheduler = CreateScheduler(new HeldAdapter(), repository);
        var events = 0;
        scheduler.QuotaRefreshed += (_, _) => events++;
        var previous = await scheduler.RefreshAccountNowAsync("provider", "account", force: true);
        var previousStatus = scheduler.GetStatus("account")!;
        repository.Fail = true;

        await Assert.ThrowsAsync<IOException>(() => scheduler.RefreshAccountNowAsync("provider", "account", force: true));
        var status = scheduler.GetStatus("account")!;
        Assert.False(status.IsRefreshing);
        Assert.Equal(previous.Id, status.LatestSnapshotId);
        Assert.Equal(previousStatus.LastSuccessfulRefreshAt, status.LastSuccessfulRefreshAt);
        Assert.Equal(previousStatus.ConsecutiveFailures, status.ConsecutiveFailures);
        Assert.Equal(1, events);

        repository.Fail = false;
        await scheduler.RefreshAccountNowAsync("provider", "account", force: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, events);
    }

    [Fact]
    public async Task CancellationDuringPersistenceClearsBusyFlagAndPublishesNoSuccess()
    {
        using var caller = new CancellationTokenSource();
        var repository = new SaveProbe { BeforeSave = () => caller.Cancel() };
        using var scheduler = CreateScheduler(new HeldAdapter(), repository);
        var events = 0;
        scheduler.QuotaRefreshed += (_, _) => events++;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RefreshAccountNowAsync(
            "provider", "account", force: true, cancellationToken: caller.Token));

        Assert.False(scheduler.GetStatus("account")!.IsRefreshing);
        Assert.Null(scheduler.GetStatus("account")!.LatestSnapshotId);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task SynchronousEventReentryJoinsThePublishedInFlightRefresh()
    {
        var adapter = new HeldAdapter();
        using var scheduler = CreateScheduler(adapter);
        Task<QuotaSnapshot>? joined = null;
        var reentered = false;
        scheduler.QuotaRefreshed += (_, _) =>
        {
            if (reentered) { return; }
            reentered = true;
            joined = scheduler.RefreshAccountNowAsync("provider", "account", force: true);
        };

        var original = await scheduler.RefreshAccountNowAsync("provider", "account", force: true);
        Assert.NotNull(joined);
        Assert.Same(original, await joined.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, adapter.Calls);
        Assert.False(scheduler.GetStatus("account")!.IsRefreshing);
    }

    private static QuotaRefreshScheduler CreateScheduler(
        IQuotaSourceAdapter adapter, IQuotaSnapshotRepository? repository = null, QuotaSchedulerOptions? options = null)
        => new(adapter, repository ?? new InMemoryQuotaSnapshotRepository(),
            options: Options.Create(options ?? new QuotaSchedulerOptions { ProviderRateLimitDelay = TimeSpan.Zero }));

    [Fact]
    public async Task PreCancelledRequestDoesNotReturnTheCachedSnapshot()
    {
        var adapter = new HeldAdapter();
        using var scheduler = CreateScheduler(adapter);
        await scheduler.RefreshAccountNowAsync("provider", "account");
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.RefreshAccountNowAsync("provider", "account", cancellationToken: caller.Token));
        Assert.Equal(1, adapter.Calls);
        Assert.False(scheduler.GetStatus("account")!.IsRefreshing);
    }

    [Fact]
    public async Task ImmediateRetryAfterAsyncCancellationStartsANewRefresh()
    {
        var adapter = new AlternatingCancellationAdapter();
        using var scheduler = CreateScheduler(adapter);
        for (var cycle = 0; cycle < 128; cycle++)
        {
            var account = "retry-" + cycle;
            adapter.Prepare();
            using var caller = new CancellationTokenSource();
            var first = scheduler.RefreshAccountNowAsync("provider", account, force: true, cancellationToken: caller.Token);
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            // No polling, delay or second retry masks a stale cancelled ownership entry.
            var result = await scheduler.RefreshAccountNowAsync("provider", account, force: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(account, result.AccountId);
            Assert.False(scheduler.GetStatus(account)!.IsRefreshing);
        }
        Assert.Equal(256, adapter.Calls);
    }

    private sealed class AlternatingCancellationAdapter : IQuotaSourceAdapter
    {
        public string SourceKind => "fixture";
        public int Calls;
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Prepare() => Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<QuotaSnapshot> FetchQuotaAsync(string providerProfileId, string accountId,
            string? modelId = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref Calls) % 2 == 1)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return QuotaSnapshot.CreateUnsupported(accountId, providerProfileId, modelId);
        }
    }

    [Fact]
    public async Task BlockingEventReentryCanReadTheCompletedRefreshResult()
    {
        var adapter = new HeldAdapter();
        using var scheduler = CreateScheduler(adapter);
        var once = false;
        var completedInsideHandler = false;
        QuotaSnapshot? observed = null;
        scheduler.QuotaRefreshed += (_, _) =>
        {
            if (once) { return; }
            once = true;
            var joined = scheduler.RefreshAccountNowAsync("provider", "account", force: true);
            // A bounded wait detects the dependency cycle without leaving a stuck test thread.
#pragma warning disable xUnit1031 // Deliberate synchronous subscriber; wait is bounded to 250ms.
            completedInsideHandler = joined.Wait(TimeSpan.FromMilliseconds(250));
            if (completedInsideHandler) { observed = joined.GetAwaiter().GetResult(); }
#pragma warning restore xUnit1031
        };

        var result = await scheduler.RefreshAccountNowAsync("provider", "account", force: true);
        Assert.True(completedInsideHandler);
        Assert.Same(result, observed);
        Assert.Equal(1, adapter.Calls);
        Assert.False(scheduler.GetStatus("account")!.IsRefreshing);
    }

    [Fact]
    public async Task BadSubscriberCannotReplacePersistedSuccessOrSkipOtherSubscribers()
    {
        var repository = new SaveProbe();
        using var scheduler = CreateScheduler(new HeldAdapter(), repository);
        QuotaSnapshot? notified = null;
        scheduler.QuotaRefreshed += (_, _) => throw new InvalidOperationException("fixture subscriber failure");
        scheduler.QuotaRefreshed += (_, args) => notified = args.Snapshot;

        var result = await scheduler.RefreshAccountNowAsync("provider", "account", force: true);
        Assert.Equal(1, repository.Saves);
        Assert.Same(result, notified);
        Assert.Equal(result.Id, scheduler.GetStatus("account")!.LatestSnapshotId);
        Assert.False(scheduler.GetStatus("account")!.IsRefreshing);
    }

    [Fact]
    public async Task SwallowedFetchCancellationCannotPublishASuccessfulRefresh()
    {
        using var caller = new CancellationTokenSource();
        var repository = new InMemoryQuotaSnapshotRepository();
        using var scheduler = CreateScheduler(new HeldAdapter { BeforeReturn = () => caller.Cancel() }, repository);
        var events = 0;
        scheduler.QuotaRefreshed += (_, _) => events++;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RefreshAccountNowAsync(
            "provider", "account", force: true, cancellationToken: caller.Token));
        Assert.Empty(await repository.ListAllLatestAsync());
        Assert.Equal(0, events);
        Assert.Null(scheduler.GetStatus("account")!.LatestSnapshotId);
        Assert.False(scheduler.GetStatus("account")!.IsRefreshing);
    }

    [Fact]
    public async Task PersistenceReturningAfterCancellationPublishesItsCommittedSuccess()
    {
        using var caller = new CancellationTokenSource();
        var repository = new SaveProbe { BeforeSave = () => caller.Cancel(), IgnoreCancellation = true };
        var adapter = new HeldAdapter();
        using var scheduler = CreateScheduler(adapter, repository);
        var events = 0;
        QuotaSnapshot? notified = null;
        scheduler.QuotaRefreshed += (_, args) => { events++; notified = args.Snapshot; };

        var result = await scheduler.RefreshAccountNowAsync(
            "provider", "account", force: true, cancellationToken: caller.Token);
        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(1, repository.Saves); // Persistence already committed; no rollback is invented.
        Assert.Equal(1, events);
        Assert.Same(result, notified);
        Assert.Equal(result.Id, (await repository.GetByIdAsync(result.Id))!.Id);
        Assert.Equal(result.Id, scheduler.GetStatus("account")!.LatestSnapshotId);
        Assert.NotNull(scheduler.GetStatus("account")!.LastSuccessfulRefreshAt);
        Assert.Equal(0, scheduler.GetStatus("account")!.ConsecutiveFailures);
        Assert.False(scheduler.GetStatus("account")!.IsRefreshing);
        var cached = await scheduler.RefreshAccountNowAsync("provider", "account");
        Assert.Equal(result.Id, cached.Id);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, repository.Saves);
        Assert.Equal(1, events);
    }

    private sealed class HeldAdapter : IQuotaSourceAdapter
    {
        public string SourceKind => "fixture";
        public bool Hold { get; init; }
        public Action? BeforeReturn { get; init; }
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<QuotaSnapshot> FetchQuotaAsync(string providerProfileId, string accountId,
            string? modelId = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            if (Hold) { await Release.Task.WaitAsync(cancellationToken); }
            BeforeReturn?.Invoke();
            return QuotaSnapshot.CreateUnsupported(accountId, providerProfileId, modelId);
        }
    }

    private sealed class SaveProbe : IQuotaSnapshotRepository
    {
        private readonly InMemoryQuotaSnapshotRepository _inner = new();
        public bool Fail { get; set; }
        public Action? BeforeSave { get; init; }
        public bool IgnoreCancellation { get; init; }
        public int Saves { get; private set; }
        public Task<QuotaSnapshot?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
            => _inner.GetByIdAsync(id, cancellationToken);
        public Task<QuotaSnapshot?> GetLatestForAccountAsync(string accountId, string? modelId = null, CancellationToken cancellationToken = default)
            => _inner.GetLatestForAccountAsync(accountId, modelId, cancellationToken);
        public Task<IReadOnlyList<QuotaSnapshot>> ListLatestByAccountIdAsync(string accountId, CancellationToken cancellationToken = default)
            => _inner.ListLatestByAccountIdAsync(accountId, cancellationToken);
        public Task<IReadOnlyList<QuotaSnapshot>> ListAllLatestAsync(CancellationToken cancellationToken = default)
            => _inner.ListAllLatestAsync(cancellationToken);
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
            => _inner.DeleteOlderThanAsync(cutoff, cancellationToken);
        public Task SaveAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            BeforeSave?.Invoke();
            if (!IgnoreCancellation) { cancellationToken.ThrowIfCancellationRequested(); }
            if (Fail) { throw new IOException("fixture persistence failure"); }
            Saves++;
            return _inner.SaveAsync(snapshot, cancellationToken);
        }
    }
}
