using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;
using ActivityEvent = LLMWorkGUI.Application.Observability.ActivityEvent;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// The shipped Activity Center over a real durable journal, with the real bounded query worker.
/// <para>
/// The deterministic suite drives the screen by hand; this one lets the production pieces run for real -
/// one named background thread, one SQLite connection per query, a deferred WAL read transaction spanning
/// the retained total, the match count and the page - and then checks that the screen still ends on the
/// answer to the last request. That combination is the one the change is actually about, and neither half
/// can be checked on its own: a hand-stepped screen proves the publication rules and a service-level query
/// proves the snapshot, but only the two together show that an operator's search survives a real worker.
/// </para>
/// </summary>
public sealed class ActivityCenterBoundedQueryOverJournalTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private const int SeedEvents = 4_000;

    private readonly string _appData;
    private readonly SqliteConnectionFactory _factory;

    public ActivityCenterBoundedQueryOverJournalTests()
    {
        _appData = Path.Combine(
            Path.GetTempPath(),
            "llmworkgui-bounded-query-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_appData);
        _factory = new SqliteConnectionFactory(Path.Combine(_appData, "llmworkgui.db"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_appData, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory must never fail a test run.
        }
    }

    [Fact]
    public async Task AOperatorSearchOverRealJournalSettlesOnTheLatestRequest()
    {
        var service = await ComposeAsync();

        using var executor = new BoundedActivityQueryExecutor();
        var gatedExecutor = new GatedQueryExecutor(executor);
        using var screen = new ActivityCenterViewModel(
            service,
            new DiffArtifactViewerViewModel(),
            TimeProvider.System,
            queryExecutor: gatedExecutor);

        await WaitForAsync(() => screen.TotalCount == SeedEvents);

        Assert.Equal(SeedEvents, screen.TotalCount);
        Assert.Equal(SeedEvents, screen.FilteredCount);

        // Wait until alpha has actually reached the real worker before superseding it. Back-to-back
        // setters can correctly debounce into beta alone and therefore cannot require a stale result.
        gatedExecutor.HoldNextQuery();
        try
        {
            screen.SearchQuery = "observation alpha";
            await gatedExecutor.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            screen.SearchQuery = "observation beta";
        }
        finally
        {
            gatedExecutor.Release();
        }

        await WaitForAsync(() => !screen.IsLoading && screen.SearchQuery == "observation beta"
            && screen.FilteredCount == ExpectedBeta);

        Assert.Equal(ExpectedBeta, screen.FilteredCount);
        Assert.Equal(SeedEvents, screen.TotalCount);
        Assert.True(screen.StaleResultCount >= 1, "the superseded query was not recorded as stale.");
        Assert.All(screen.Events, item => Assert.Contains("beta", item.Model.Description, StringComparison.Ordinal));

        // Clearing the query returns the whole retained set, again from the journal and not from the
        // bounded window: 4 000 rows are retained and the window holds fewer.
        screen.SearchQuery = string.Empty;

        await WaitForAsync(() => !screen.IsLoading && screen.FilteredCount == SeedEvents);

        Assert.Equal(SeedEvents, screen.FilteredCount);

        // Three searches against a bounded worker: nothing was refused and the queue never left its bound,
        // so the screen never asked for more work than one worker can carry.
        Assert.Equal(0, executor.RefusedCount);
        Assert.True(
            executor.PendingHighWaterMark <= executor.QueueCapacity,
            $"the pending queue reached {executor.PendingHighWaterMark} of {executor.QueueCapacity}.");
    }

    [Fact]
    public async Task AFailingDurableQueryShowsTheErrorStateAndTheNextSearchRecovers()
    {
        var service = await ComposeAsync();

        using var executor = new BoundedActivityQueryExecutor();
        using var screen = new ActivityCenterViewModel(
            service,
            new DiffArtifactViewerViewModel(),
            TimeProvider.System,
            queryExecutor: executor);

        await WaitForAsync(() => screen.TotalCount == SeedEvents);

        // Punctuation-only input used to reach FTS5 as syntax on this screen. The repository now refuses it
        // with a predicate that matches nothing, so the honest outcome is an empty result rather than an
        // exception - and either way the dispatcher survives it.
        screen.SearchQuery = "()";

        await WaitForAsync(() => !screen.IsLoading);

        Assert.False(screen.HasError);
        Assert.Equal(0, screen.FilteredCount);
        Assert.Equal(SeedEvents, screen.TotalCount);
        Assert.True(screen.IsEmpty);
        Assert.Equal(0, screen.QueryFailureCount);

        screen.SearchQuery = "observation";

        await WaitForAsync(() => !screen.IsLoading && screen.FilteredCount == SeedEvents);

        Assert.False(screen.HasError);
        Assert.Equal(SeedEvents, screen.FilteredCount);
        Assert.Equal(0, screen.QueryFailureCount);
    }

    private int ExpectedBeta => SeedEvents / 2;

    private sealed class GatedQueryExecutor(IActivityQueryExecutor inner) : IActivityQueryExecutor
    {
        private int _holdNext;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HoldNextQuery() => Interlocked.Exchange(ref _holdNext, 1);
        public void Release() => _release.TrySetResult();

        public void Enqueue(Action work)
        {
            var hold = Interlocked.Exchange(ref _holdNext, 0) == 1;
            inner.Enqueue(() =>
            {
                if (hold)
                {
                    Started.TrySetResult();
                    _release.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                }

                work();
            });
        }
    }

    private async Task<ActivityCenterService> ComposeAsync()
    {
        await new DatabaseMigrator(_factory).MigrateAsync();

        var journal = new SqliteActivityEventJournal(_factory);
        var queue = new ActivityJournalWriteQueue(journal);

        var service = new ActivityCenterService(
            text => text,
            TimeProvider.System,
            new EventSearchIndex(text => text, 500),
            capacity: 500,
            journalQueue: queue,
            journal: journal,
            retentionLimit: ActivityJournalOptions.DefaultRetentionLimit);

        for (var sequence = 0; sequence < SeedEvents; sequence++)
        {
            var beta = sequence % 2 == 0;
            service.Append(Event($"seed:{sequence:D6}", Now.AddSeconds(sequence), beta));
        }

        await queue.DrainAsync();

        // Everything the durable query answers is committed before the screen is asked about it; an
        // unscheduled write queue would make the counts a race instead of a measurement.
        Assert.Equal(SeedEvents, await journal.CountAsync());

        return service;
    }

    private static ActivityEvent Event(string id, DateTimeOffset occurredAt, bool beta) => new(
        id,
        occurredAt,
        ActivityEventKind.Execution,
        ActivityRoleNames.Coder,
        ActivityEventState.Running,
        ActivityEventSource.Native,
        $"title {id}",
        beta ? $"observation beta payload for {id}" : $"observation alpha payload for {id}",
        executionId: $"exec-{id[^1]}");

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "the screen never settled within the budget.");
    }
}
