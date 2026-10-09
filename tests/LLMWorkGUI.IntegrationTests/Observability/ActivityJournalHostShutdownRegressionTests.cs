using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Observability;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Observability;

public sealed class ActivityJournalHostShutdownRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettledDurableLossCannotAuthorizeGracefulMarkerCompletion(bool dropped)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var journal = new SqliteActivityEventJournal(database.Factory);
        if (!dropped) await journal.AppendAsync(Event()); // Duplicate ordinary identity causes an actual SQLite commit failure.
        await using var queue = new ActivityJournalWriteQueue(journal,
            byteCapacity: dropped ? 1 : ActivityJournalWriteQueue.DefaultByteCapacity);
        using var host = new HostBuilder().ConfigureServices((_, services) =>
        {
            services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
            services.AddSingleton<IActivityEventJournal>(journal);
            services.AddSingleton(queue);
            services.AddActivityCenter();
        }).Build();
        await host.StartAsync();
        var marker = new ApplicationRunMarker(database.Factory, new Guard());
        marker.Begin();
        host.Services.GetRequiredService<IActivityCenterService>().Append(Event());
        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(queue.IsSettled);
        Assert.True(queue.IsBalanced);
        Assert.Equal(dropped ? 1 : 0, queue.DroppedCount);
        Assert.Equal(dropped ? 0 : 1, queue.FailedCount);

        var failure = await Record.ExceptionAsync(() => StopAndCompleteMarkerAsync(host, marker));

        Assert.NotNull(failure);
        Assert.True(File.Exists(database.Factory.DatabasePath + ".running"));
        Assert.True(queue.IsLossy); // Stopping never resets the durable-loss evidence.
    }

    [Fact]
    public async Task HostStopCannotClearTheRunMarkerBeforeAnAcceptedWriteCommits()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var journal = new DeferredJournal(new SqliteActivityEventJournal(database.Factory));
        using var host = BuildHost(database.Factory, journal);
        await host.StartAsync();
        var queue = host.Services.GetRequiredService<ActivityJournalWriteQueue>();
        var marker = new ApplicationRunMarker(database.Factory, new Guard());
        marker.Begin();
        var markerPath = database.Factory.DatabasePath + ".running";
        host.Services.GetRequiredService<IActivityCenterService>().Append(Event());
        Task? stop = null;
        try
        {
            await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop = StopAndCompleteMarkerAsync(host, marker);
            Assert.False(stop.IsCompleted, "Host stop returned before the accepted SQLite write committed.");
            Assert.True(File.Exists(markerPath));
            Assert.Equal(0, await journal.CountAsync());
            journal.Release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, await journal.CountAsync());
            Assert.Equal(1, queue.WrittenCount);
            Assert.Equal(0, queue.PendingCount);
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            journal.Release.TrySetResult();
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelledHostStopPreservesTheMarkerWhileThePhysicalWriteIsUnconfirmed()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var journal = new DeferredJournal(new SqliteActivityEventJournal(database.Factory));
        using var host = BuildHost(database.Factory, journal);
        await host.StartAsync();
        var queue = host.Services.GetRequiredService<ActivityJournalWriteQueue>();
        var marker = new ApplicationRunMarker(database.Factory, new Guard());
        marker.Begin();
        using var cancellation = new CancellationTokenSource();
        host.Services.GetRequiredService<IActivityCenterService>().Append(Event());
        try
        {
            await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = StopAndCompleteMarkerAsync(host, marker, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(File.Exists(database.Factory.DatabasePath + ".running"));
            Assert.Equal(1, queue.PendingCount);
            Assert.Equal(0, await journal.CountAsync());
            Assert.False(queue.Completion.IsCompleted);
        }
        finally
        {
            journal.Release.TrySetResult();
            await queue.DisposeAsync();
        }
        Assert.Equal(1, await journal.CountAsync());
    }

    private static IHost BuildHost(ISqliteConnectionFactory factory, IActivityEventJournal journal) =>
        new HostBuilder().ConfigureServices((_, services) =>
        {
            services.AddSingleton(factory);
            services.AddSingleton(journal);
            services.AddActivityCenter();
        }).Build();

    // Same marker ordering as App's actual closing handler; no fake write completion or marker owner.
    private static async Task StopAndCompleteMarkerAsync(IHost host, ApplicationRunMarker marker,
        CancellationToken cancellationToken = default)
    {
        await host.StopAsync(cancellationToken);
        marker.CompleteGracefulShutdown();
    }

    private static ActivityEvent Event() => new("shutdown-write", DateTimeOffset.UtcNow,
        ActivityEventKind.System, ActivityRoleNames.System, ActivityEventState.Completed,
        ActivityEventSource.Synthetic, "Owned fixture", "Metadata only");

    private sealed class DeferredJournal(IActivityEventJournal inner) : IActivityEventJournal
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task AppendAsync(ActivityEvent item, CancellationToken token = default) => AppendRangeAsync([item], token);
        public async Task AppendRangeAsync(IReadOnlyList<ActivityEvent> items, CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            await inner.AppendRangeAsync(items, token).ConfigureAwait(false);
        }
        public Task<IReadOnlyList<ActivityEvent>> LoadNewestAsync(int limit, CancellationToken token = default) =>
            inner.LoadNewestAsync(limit, token);
        public Task<long> CountAsync(CancellationToken token = default) => inner.CountAsync(token);
        public Task<ActivityJournalTrimResult> TrimAsync(int limit, CancellationToken token = default) => inner.TrimAsync(limit, token);
        public ActivityJournalPage QueryPage(ActivityJournalQuery query) => inner.QueryPage(query);
    }

    private sealed class Guard : IApplicationInstanceGuard
    {
        public string InstanceId => "owned-activity-shutdown-fixture";
        public bool IsPrimarySupervisor => true;
        public bool IsViewOnly => false;
        public void EnsureSupervisorPermitted() { }
        public void Dispose() { }
    }
}
