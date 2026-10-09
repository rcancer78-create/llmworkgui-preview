using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Xunit;
using ActivityEvent = LLMWorkGUI.Application.Observability.ActivityEvent;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// The Activity Center's off-dispatcher query contract, driven by hand.
/// <para>
/// The screen's query is no longer a synchronous block on the UI thread, which is the point of the change
/// and also the reason it needs its own tests. A design where a query, its publication and the operator's
/// next keystroke can all be in flight at once has exactly one correct answer to "what does the screen
/// show now", and every test here is about that answer: the newest request wins, a superseded result and a
/// superseded error are both discarded, disposal stops publication, and an append is folded rather than
/// cancelled.
/// </para>
/// <para>
/// Both halves are stepped explicitly. <see cref="ManualQueryExecutor"/> holds the query work instead of
/// running it, and <see cref="ManualActivityUiScheduler"/> holds the callbacks the view model would post -
/// including the delayed ones, because a delayed post that ran immediately would quietly delete the typing
/// debounce these tests are about. So "the first query finishes after the second one" is a statement in the
/// test rather than a race decided by the thread scheduler. The production halves - the bounded worker and
/// the dispatcher scheduler - are covered by their own tests.
/// </para>
/// </summary>
public sealed class ActivityCenterAsyncQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly SensitiveDataFilter Filter = new();

    [Fact]
    public void ShrinkingTheLastPagePublishesTheRowsOfTheClampedPage()
    {
        var service = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        for (var index = 0; index < 5; index++)
            service.Append(Event($"row-{index}", occurredAt: Now.AddMinutes(-5).AddSeconds(index)));
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();
        using var viewModel = new ActivityCenterViewModel(service, timeProvider: new FixedTimeProvider(Now),
            pageSize: 2, scheduler: scheduler, queryExecutor: executor,
            minimumRefreshInterval: TimeSpan.Zero, searchDebounce: TimeSpan.Zero);
        Settle(scheduler, executor);
        viewModel.GoToPage(3);
        Settle(scheduler, executor);
        Assert.Equal("row-0", Assert.Single(viewModel.Events).Id);

        Assert.True(service.Remove("row-4"));
        Assert.True(service.Remove("row-3"));
        service.Append(Event("new", occurredAt: Now));
        Settle(scheduler, executor);

        Assert.Equal(2, viewModel.CurrentPage);
        Assert.Equal(2, viewModel.PageCount);
        Assert.Equal(new[] { "row-1", "row-0" }, viewModel.Events.Select(item => item.Id));
        Assert.False(viewModel.IsLoading);
    }

    /// <summary>
    /// Runs the screen to a standstill: fire the delayed posts, run whatever they queued on the worker,
    /// publish what came back, and repeat until nothing is left to do.
    /// <para>
    /// The repetition is not defensive padding. A publication can post a rate-limited follow-up, which is a
    /// timer, which starts another query, which posts another publication; draining one round of each half
    /// would leave the screen mid-refresh and every later assertion would be about the wrong state.
    /// </para>
    /// </summary>
    private static void Settle(ManualActivityUiScheduler scheduler, ManualQueryExecutor executor)
    {
        // Work driven, not a fixed number of rounds: one round can advance only one step of the
        // post -> query -> publish -> follow-up cycle, and a fixed count leaves the screen mid-refresh on
        // exactly the tests that are about a request arriving during one.
        for (var round = 0; round < 64; round++)
        {
            if (scheduler.PendingTimerCount == 0 && scheduler.ReadyCount == 0 && executor.PendingCount == 0)
            {
                break;
            }

            scheduler.FireTimersAndRun();
            executor.RunAll();
        }

        scheduler.FireTimersAndRun();
        scheduler.RunAll();
    }

    [Fact]
    public async Task ADelayedCommitRefreshesTheFinalEventWithoutAnotherAppendOrDuplicateLatency()
    {
        var journal = new HeldCommitJournal();
        var queue = new ActivityJournalWriteQueue(journal);
        var service = new ActivityCenterService(Filter.Redact, journalQueue: queue, journal: journal);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();
        var latency = new ActivityVisibilityLatencyRecorder(TimeProvider.System);
        using var viewModel = new ActivityCenterViewModel(service, scheduler: scheduler, queryExecutor: executor,
            latencyRecorder: latency, minimumRefreshInterval: TimeSpan.Zero, searchDebounce: TimeSpan.Zero);
        var ingestionNotices = 0;
        service.Appended += (_, _) => ingestionNotices++;

        try
        {
            Settle(scheduler, executor);
            service.Append(Event("last-event"));
            await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Settle(scheduler, executor);
            Assert.Empty(viewModel.Events);
            Assert.Equal(0, latency.ObservedCount);

            journal.Release.TrySetResult();
            await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Settle(scheduler, executor);

            Assert.Equal("last-event", Assert.Single(viewModel.Events).Id);
            Assert.Equal(1, ingestionNotices);
            Assert.Equal(1, latency.ObservedCount);
            viewModel.Refresh();
            Settle(scheduler, executor);
            Assert.Equal(1, latency.ObservedCount);
        }
        finally
        {
            journal.Release.TrySetResult();
            await queue.DisposeAsync();
        }
    }

    [Fact]
    public void ASetterDoesNotQueryAtAll_TheWorkOnlyStartsWhenTheWorkerRunsIt()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        inner.Append(Event("row", description: "observation"));

        Settle(scheduler, executor);

        var queriesBeforeTyping = service.QueryCount;
        Assert.True(queriesBeforeTyping >= 1, "the initial refresh never queried.");
        Assert.Equal(1, viewModel.FilteredCount);

        // The whole point of moving the query off the setter: typing a character returns having done no
        // durable work at all. A design that queried inline would answer a keystroke only after the p95 of a
        // 190 000-row search had already elapsed, which is what the accepted R1 profile measured.
        viewModel.SearchQuery = "observation";

        Assert.Equal(queriesBeforeTyping, service.QueryCount);

        // What the screen shows is still the previous query's answer - unchanged and honestly unchanged -
        // until the new one lands. It is not a new answer, and it is not an empty list.
        Assert.Equal(1, viewModel.FilteredCount);
        Assert.Equal("row", Assert.Single(viewModel.Events).Id);

        Settle(scheduler, executor);

        Assert.Equal(queriesBeforeTyping + 1, service.QueryCount);
        Assert.Equal("observation", service.LastQuery.Criteria.SearchQuery);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public void TheBoundedWorkerRunsTheQueryOffTheRequestingThread_AndReusesOneThread()
    {
        var inner = new ActivityCenterService(Filter.Redact, TimeProvider.System);
        var service = new FaultInjectingActivityCenterService(inner);

        using var executor = new BoundedActivityQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();
        var requesterThread = Environment.CurrentManagedThreadId;

        // Offered before the screen is bound, so the searches below are the only queries that run and the
        // executor's own accounting is about the searches rather than about the stream.
        for (var index = 0; index < 200; index++)
        {
            inner.Append(Event($"row-{index:D3}", description: $"observation {index}"));
        }

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        Assert.True(
            Drain(scheduler, () => service.QueryCount >= 1),
            "the bounded worker never ran the initial query.");

        // Ten searches against a real worker thread. One thread, reused, and never the requester's: a thread
        // per keystroke - each with its own SQLite connection and WAL read transaction - would show up here
        // as ten distinct ids.
        for (var index = 0; index < 10; index++)
        {
            var before = service.QueryCount;
            viewModel.SearchQuery = $"observation {index}";

            Assert.True(
                Drain(scheduler, () => service.QueryCount > before),
                $"search {index} never reached the worker.");
        }

        Assert.True(service.QueryCount >= 11, $"only {service.QueryCount} queries ran.");
        Assert.DoesNotContain(requesterThread, service.QueryThreads);
        Assert.Single(service.QueryThreads.Distinct());

        Assert.Equal(BoundedActivityQueryExecutor.DefaultThreadName, executor.ThreadName);
        Assert.Equal(0, executor.RefusedCount);
        Assert.True(
            executor.PendingHighWaterMark <= executor.QueueCapacity,
            $"the pending queue reached {executor.PendingHighWaterMark} of {executor.QueueCapacity}.");
    }

    /// <summary>Pumps the held callbacks until the condition holds or the budget is spent.</summary>
    private static bool Drain(ManualActivityUiScheduler scheduler, Func<bool> condition, TimeSpan? budget = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = budget ?? TimeSpan.FromSeconds(10);

        while (deadline.Elapsed < limit)
        {
            scheduler.FireTimersAndRun();

            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        scheduler.FireTimersAndRun();

        return condition();
    }

    [Fact]
    public void ABurstOfQueriesNeverOutrunsTheBoundedQueueAndRefusalsAreCounted()
    {
        using var executor = new BoundedActivityQueryExecutor(queueCapacity: 2);
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);

        // Hold the single worker inside one item so the queue behind it actually fills.
        executor.Enqueue(() =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "the worker never picked up the first item.");

        for (var index = 0; index < 20; index++)
        {
            executor.Enqueue(() => { });
        }

        // The bound is enforced rather than assumed, and what did not fit is counted, never dropped
        // silently: a refused query is a screen that stays on its loading state.
        Assert.Equal(2, executor.PendingHighWaterMark);
        Assert.Equal(3, executor.EnqueuedCount);
        Assert.Equal(18, executor.RefusedCount);

        release.Set();

        Assert.True(
            WaitFor(() => executor.ExecutedCount >= 3, TimeSpan.FromSeconds(5)),
            $"the worker executed {executor.ExecutedCount} items.");

        executor.Dispose();

        var ran = 0;
        executor.Enqueue(() => Interlocked.Increment(ref ran));
        Thread.Sleep(100);

        // Nothing after disposal runs, and a query the shell threw away is not resurrected by it.
        Assert.Equal(0, ran);
        Assert.Equal(3, executor.EnqueuedCount);

        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedActivityQueryExecutor(queueCapacity: 0));
        Assert.Throws<ArgumentNullException>(() => executor.Enqueue(null!));
    }

    [Fact]
    public void OnlyTheLatestRequestIsPublished_EvenWhenAnEarlierOneIsStillRunning()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        inner.Append(Event("alpha-one", description: "alpha observation"));
        inner.Append(Event("alpha-two", description: "alpha observation"));
        inner.Append(Event("beta-row", description: "beta observation"));

        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        Settle(scheduler, executor);

        // "beta" is requested while the query for "alpha" is still on the worker. The fold is what makes this
        // possible: there is one query in flight, one queued, and the queued one asks for the newest text.
        viewModel.SearchQuery = "alpha";
        scheduler.FireTimersAndRun();

        var queriesAfterAlpha = service.QueryCount;
        Assert.Equal(1, executor.PendingCount);

        viewModel.SearchQuery = "beta";

        Assert.Equal(1, executor.PendingCount);
        Assert.Equal(queriesAfterAlpha, service.QueryCount);

        Settle(scheduler, executor);

        // Two queries, not one: the in-flight one is discarded, and the fold it leaves behind becomes the
        // follow-up that carries the answer the operator is waiting for.
        Assert.Equal(queriesAfterAlpha + 2, service.QueryCount);
        Assert.Equal("beta", service.LastQuery.Criteria.SearchQuery);
        Assert.Equal(new[] { "beta-row" }, viewModel.Events.Select(item => item.Id).ToArray());
        Assert.Equal(1, viewModel.FilteredCount);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public void AResultSupersededBeforeTheOperatorReplacedIt_IsNeverShownEvenForAMoment()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        inner.Append(Event("alpha-one", description: "alpha observation"));
        inner.Append(Event("alpha-two", description: "alpha observation"));
        inner.Append(Event("beta-row", description: "beta observation"));

        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        Settle(scheduler, executor);

        var publishedCounts = new List<int>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ActivityCenterViewModel.FilteredCount))
            {
                publishedCounts.Add(viewModel.FilteredCount);
            }
        };

        viewModel.SearchQuery = "alpha";
        scheduler.FireTimersAndRun();
        viewModel.SearchQuery = "beta";

        // Nothing of "alpha" may reach the screen - not even for a frame, and not even as a count. The
        // publication that follows it is recorded as stale and discarded.
        executor.RunAll();
        scheduler.FireTimersAndRun();

        Assert.Equal(1, viewModel.StaleResultCount);
        Assert.Empty(publishedCounts);
        Assert.Equal(3, viewModel.FilteredCount);
        Assert.Equal(3, viewModel.Events.Count);

        Settle(scheduler, executor);

        Assert.Equal(1, viewModel.FilteredCount);
        Assert.Equal(new[] { "beta-row" }, viewModel.Events.Select(item => item.Id).ToArray());

        // Exactly one count was ever published, and it is the answer to the text the operator ended up with.
        Assert.Equal(new[] { 1 }, publishedCounts);
    }

    [Fact]
    public void AQueryErrorIsShownForTheCurrentRequest_AndDiscardedForASupersededOne()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        Settle(scheduler, executor);

        // A failure for a query the operator has already replaced is not the operator's problem any more:
        // showing it would raise an error state over a screen that is about to show a result.
        viewModel.SearchQuery = "doomed";
        scheduler.FireTimersAndRun();
        viewModel.SearchQuery = "kept";
        service.QueryFailure = new InvalidOperationException("journal unavailable");

        executor.RunAll();
        scheduler.FireTimersAndRun();

        Assert.False(viewModel.HasError);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        Assert.Equal(1, viewModel.StaleResultCount);
        Assert.Equal(0, viewModel.QueryFailureCount);

        // A failure for the current request is the existing error state, and no result is published.
        service.QueryFailure = new InvalidOperationException("journal unavailable");
        Settle(scheduler, executor);

        Assert.True(viewModel.HasError);
        Assert.Equal(nameof(InvalidOperationException), viewModel.ErrorState!.TechnicalDetails);
        Assert.Equal(1, viewModel.QueryFailureCount);
        Assert.Empty(viewModel.Events);
        Assert.Equal(0, viewModel.FilteredCount);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public void AnUnexpectedSqliteFailureBecomesTheErrorState_AndNotAnUnhandledException()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        Settle(scheduler, executor);

        // The shape a malformed FTS MATCH used to take: a genuine SQLite error raised on whatever thread
        // asked, with nothing between it and the dispatcher. It has to arrive as the standard error state
        // instead. The exception is produced by really asking SQLite for the broken expression rather than
        // by hand, so the test cannot drift from the failure it claims to cover.
        service.QueryFailure = RealFtsSyntaxFailure();
        viewModel.SearchQuery = "observation OR";

        Settle(scheduler, executor);

        Assert.True(viewModel.HasError);
        Assert.Contains(nameof(SqliteException), viewModel.ErrorState!.TechnicalDetails, StringComparison.Ordinal);
        Assert.Equal(1, viewModel.QueryFailureCount);
        Assert.False(viewModel.IsLoading);

        // The screen recovers: the next successful query clears the error rather than latching it.
        service.QueryFailure = null;
        viewModel.SearchQuery = string.Empty;

        Settle(scheduler, executor);

        Assert.False(viewModel.HasError);
        Assert.Equal(1, viewModel.QueryFailureCount);
        Assert.Empty(viewModel.Events);
        Assert.Equal(0, viewModel.FilteredCount);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public void QueryFailure_DoesNotPublishRawCredentialsOrPrivatePathsToErrorDetails()
    {
        var service = new FaultInjectingActivityCenterService(
            new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now)));
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();
        using var viewModel = new ActivityCenterViewModel(service,
            timeProvider: new FixedTimeProvider(Now), scheduler: scheduler,
            queryExecutor: executor, searchDebounce: TimeSpan.Zero);
        Settle(scheduler, executor);
        service.QueryFailure = new InvalidOperationException(
            @"token=synthetic-private-value; path=D:\synthetic-private-customer\data.db");
        viewModel.SearchQuery = "trigger failure";

        Settle(scheduler, executor);

        Assert.True(viewModel.HasError);
        var visible = viewModel.ErrorMessage + viewModel.ErrorState.Message + viewModel.ErrorState.TechnicalDetails;
        Assert.DoesNotContain("synthetic-private-value", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-customer", visible, StringComparison.Ordinal);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public void AFailedQueryKeepsTheRowsItAlreadyShowed_AndDoesNotPublishAStaleCount()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        inner.Append(Event("visible-row"));

        // Settle on a real result first, so the rows the error has to preserve are rows the screen showed.
        Settle(scheduler, executor);

        Assert.Equal(new[] { "visible-row" }, viewModel.Events.Select(item => item.Id).ToArray());
        Assert.False(viewModel.HasError);

        var refreshesBefore = viewModel.RefreshCount;

        service.QueryFailure = new InvalidOperationException("database is locked");
        viewModel.RefreshCommand.Execute(null);
        Settle(scheduler, executor);

        // The rows it already showed stay: an error overlay over an emptied list would look like data loss.
        // What must not happen is the previous query's answer being republished as if it were this one's.
        Assert.Equal(new[] { "visible-row" }, viewModel.Events.Select(item => item.Id).ToArray());
        Assert.Equal(1, viewModel.FilteredCount);
        Assert.Equal(1, viewModel.TotalCount);
        Assert.Equal(refreshesBefore, viewModel.RefreshCount);
        Assert.True(viewModel.HasError);
        Assert.Equal(1, viewModel.QueryFailureCount);
    }

    [Fact]
    public void DisposalStopsPublication_EvenWhenAQueryIsAlreadyInFlight()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        inner.Append(Event("alpha-row", description: "alpha observation"));

        viewModel.SearchQuery = "alpha";
        scheduler.FireTimersAndRun();

        Assert.Equal(1, executor.PendingCount);

        viewModel.Dispose();

        // The durable read is already under way and cannot be un-issued; what must not happen is its result
        // repainting a screen the shell has thrown away.
        executor.RunAll();
        scheduler.FireTimersAndRun();

        Assert.Empty(viewModel.Events);
        Assert.False(viewModel.HasError);

        // And nothing scheduled after disposal runs at all: no query, no list rebuild.
        var queriesAtDisposal = service.QueryCount;
        var refreshesAtDisposal = viewModel.RefreshCount;

        viewModel.SearchQuery = "beta";
        viewModel.RefreshCommand.Execute(null);
        viewModel.RequestCoalescedRefresh();

        scheduler.FireTimersAndRun();
        executor.RunAll();
        scheduler.FireTimersAndRun();

        Assert.Equal(queriesAtDisposal, service.QueryCount);
        Assert.Equal(refreshesAtDisposal, viewModel.RefreshCount);
        Assert.Equal(0, executor.PendingCount);
        Assert.Empty(viewModel.Events);
    }

    [Fact]
    public void NewCriteriaDuringAnInFlightQueryNotifyThePagerImmediately()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();
        for (var index = 0; index < 25; index++)
        {
            inner.Append(Event($"row-{index:D2}"));
        }

        using var viewModel = new ActivityCenterViewModel(
            inner,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            pageSize: 10,
            minimumRefreshInterval: TimeSpan.Zero,
            searchDebounce: TimeSpan.Zero);
        Settle(scheduler, executor);
        viewModel.GoToPage(2);
        Settle(scheduler, executor);

        viewModel.RefreshCommand.Execute(null);
        scheduler.FireTimersAndRun();
        Assert.Equal(1, executor.PendingCount);

        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notified.Add(args.PropertyName);
        viewModel.SearchQuery = "row-01";

        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Contains(nameof(viewModel.CurrentPage), notified);
        Assert.Contains(nameof(viewModel.PageDisplay), notified);
        Assert.Contains(nameof(viewModel.HasPreviousPage), notified);
        Assert.Contains(nameof(viewModel.HasNextPage), notified);
        Assert.False(viewModel.PreviousPageCommand.CanExecute(null));
        Assert.Equal(1, executor.PendingCount);

        Settle(scheduler, executor);
        Assert.Equal("row-01", Assert.Single(viewModel.Events).Id);
        Assert.Equal("Страница 1 из 1", viewModel.PageDisplay);
    }

    [Fact]
    public void AnAppendDuringAQueryIsFoldedIntoAFollowUp_AndNeverCancelled()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            minimumRefreshInterval: TimeSpan.Zero,
            searchDebounce: TimeSpan.Zero);

        Settle(scheduler, executor);

        // The screen is settled and empty, then the stream starts.
        inner.Append(Event("stream-1"));
        scheduler.FireTimersAndRun();

        Assert.Equal(1, executor.PendingCount);

        // Fifty more appends arrive while that one query is still on the worker.
        for (var index = 2; index <= 51; index++)
        {
            inner.Append(Event($"stream-{index}"));
        }

        // Folded, not cancelled and not queued one-per-append.
        Assert.Equal(50, viewModel.CoalescedAppendCount);
        Assert.Equal(1, executor.PendingCount);
        Assert.Equal(1, service.QueryCount);

        executor.RunAll();
        scheduler.FireTimersAndRun();

        // The folded work produced exactly one follow-up - not one per append, and not none. The earlier
        // version of this design returned from the already-posted refresh as superseded and re-posted
        // nothing, which is how the screen froze on one row under a live stream.
        Assert.Equal(2, service.QueryCount);
        Assert.Equal(1, executor.PendingCount);

        executor.RunAll();
        scheduler.FireTimersAndRun();

        Assert.Equal(0, executor.PendingCount);
        Assert.Equal(51, viewModel.TotalCount);
        Assert.Equal(51, viewModel.FilteredCount);
        Assert.Equal("stream-51", viewModel.Events[0].Id);
        Assert.False(viewModel.IsLoading);

        // Nothing was discarded. An append is not a request that changes the answer - the query reads the
        // journal as it stands when it runs - so discarding its result would mean a screen that never
        // publishes anything while the stream is running.
        Assert.Equal(0, viewModel.StaleResultCount);
    }

    [Fact]
    public void APageChangeIsPredictable_AndResetsThePageForNewCriteria()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        for (var index = 0; index < 25; index++)
        {
            inner.Append(Event($"row-{index:D2}", description: "observation"));
        }

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            pageSize: 10,
            searchDebounce: TimeSpan.Zero);

        Settle(scheduler, executor);

        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(3, viewModel.PageCount);
        Assert.Equal(10, viewModel.Events.Count);

        // A page change is a deliberate action: it is not delayed behind a typing pause, and it asks for the
        // offset of the page it is about to show.
        viewModel.GoToPage(2);
        scheduler.FireTimersAndRun();
        executor.RunAll();
        scheduler.FireTimersAndRun();

        Assert.Equal(2, service.LastQuery.Page);
        Assert.Equal(10, service.LastQuery.Offset);
        Assert.Equal(2, viewModel.CurrentPage);
        Assert.Equal("Страница 2 из 3", viewModel.PageDisplay);
        Assert.Equal("row-14", viewModel.Events[0].Id);
        Assert.True(viewModel.HasPreviousPage);
        Assert.True(viewModel.HasNextPage);

        // New criteria always return to the first page, and the pager says so before the result lands.
        viewModel.SearchQuery = "row-01";
        Assert.Equal(1, viewModel.CurrentPage);

        Settle(scheduler, executor);

        Assert.Equal(1, service.LastQuery.Page);
        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(1, viewModel.PageCount);
        Assert.False(viewModel.HasNextPage);
        Assert.Equal("row-01", Assert.Single(viewModel.Events).Id);
    }

    [Fact]
    public void SelectionStaysConsistentWithThePageThatWasPublished()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        inner.Append(new ActivityEvent(
            "artifact-row",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Completed,
            ActivityEventSource.Native,
            "Artifact delivered",
            "with a payload",
            artifactName: "plan.json",
            artifactContent: "{\"stage\":\"plan\"}",
            artifactChangeStatus: "Modified"));
        inner.Append(Event("plain-row", occurredAt: Now.AddSeconds(-1), description: "plain"));

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        Settle(scheduler, executor);

        var selected = viewModel.Events.Single(item => item.Id == "artifact-row");
        viewModel.SelectedEvent = selected;

        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.DiffViewer.IsArtifactMode);
        Assert.Equal("plan.json", viewModel.DiffViewer.ArtifactNameDisplay);

        // A refresh must not drop or silently repoint the selection: the detail pane is showing this row and
        // the operator did not ask for it to change.
        viewModel.RefreshCommand.Execute(null);
        Settle(scheduler, executor);

        Assert.True(viewModel.HasSelection);
        Assert.Same(selected, viewModel.SelectedEvent);
        Assert.True(viewModel.DiffViewer.IsArtifactMode);

        // Clearing the selection still closes the viewer, after a refresh too.
        viewModel.ClearSelectionCommand.Execute(null);

        Assert.False(viewModel.HasSelection);
        Assert.False(viewModel.IsDiffViewerOpen);
    }

    [Fact]
    public void TheLoadingStateIsVisibleWhileAQueryRuns_AndNeverStacksOnTheEmptyState()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        scheduler.FireTimersAndRun();

        // A query is in flight and nothing has been shown yet: the spinner, not the "no events" message. The
        // two overlays share one cell in the view, so both being true would paint both at once and the empty
        // message would be a lie - the history is being counted, not being absent.
        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.IsEmpty);

        Settle(scheduler, executor);

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.IsEmpty);
        Assert.Equal("Событий активности пока нет", viewModel.EmptyState.Title);
    }

    [Fact]
    public void ATypingBurstCollapsesIntoOneQueryForTheFinalText()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        inner.Append(Event("match", description: "alpha beta gamma"));

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.FromMilliseconds(200));

        Settle(scheduler, executor);

        var queriesBefore = service.QueryCount;

        // Ten keystrokes, each of which would previously have started a durable query over the whole
        // retained set. The first arms the debounce; the rest fold into it, and nothing at all runs until the
        // pause elapses - so the worker never sees an intermediate state of the word.
        foreach (var text in new[]
                 {
                     "a", "al", "alp", "alph", "alpha", "alpha ", "alpha b", "alpha be", "alpha bet", "alpha beta"
                 })
        {
            viewModel.SearchQuery = text;
            scheduler.RunAll();
        }

        Assert.Equal(queriesBefore, service.QueryCount);
        Assert.Equal(0, executor.PendingCount);
        Assert.Equal(1, scheduler.PendingTimerCount);
        Assert.Equal(TimeSpan.FromMilliseconds(200), scheduler.LastDelay);

        // The screen still shows the previous answer and is not claiming to be loading anything. A debounced
        // search box that flashed a spinner for 200 ms and then revealed the old rows would be worse than one
        // that simply waits, and the loading state stays reserved for a query that has actually started.
        Assert.False(viewModel.IsLoading);
        Assert.Equal(new[] { "match" }, viewModel.Events.Select(item => item.Id).ToArray());

        scheduler.FireTimersAndRun();

        Assert.Equal(queriesBefore, service.QueryCount);
        Assert.True(viewModel.IsLoading);

        executor.RunAll();
        scheduler.FireTimersAndRun();

        // One durable query for the whole burst, and it asked for the text the operator ended up with.
        Assert.Equal(queriesBefore + 1, service.QueryCount);
        Assert.Equal("alpha beta", service.LastQuery.Criteria.SearchQuery);

        Assert.Equal(new[] { "match" }, viewModel.Events.Select(item => item.Id).ToArray());
        Assert.False(viewModel.IsLoading);
        Assert.Equal(0, scheduler.PendingTimerCount);
    }

    [Fact]
    public void TheRefreshAndFilterActionsIgnoreTheTypingDebounce()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();

        inner.Append(Event("row", description: "alpha"));

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            scheduler: scheduler,
            queryExecutor: executor,
            searchDebounce: TimeSpan.FromSeconds(30));

        Settle(scheduler, executor);

        // A typed query is waiting out its debounce, which is a 30 second timer this test never fires.
        viewModel.SearchQuery = "alpha";
        scheduler.RunAll();

        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.LastDelay);
        Assert.Equal(1, scheduler.PendingTimerCount);
        Assert.Equal(0, executor.PendingCount);

viewModel.RoleFilters.Single(filter => filter.Display == ActivityRoleNames.Coder).IsSelected = true;
        scheduler.RunAll();

        // The deliberate action took the slot immediately - subject to the refresh rate limit, but never
        // behind the 30 second typing pause the operator is not waiting for.
        Assert.NotEqual(TimeSpan.FromSeconds(30), scheduler.LastDelay);
        Assert.True(
            scheduler.LastDelay <= TimeSpan.FromMilliseconds(50),
            $"a deliberate action waited {scheduler.LastDelay}.");

        // The superseded 30 second timer is counted when it eventually fires, rather than starting a
        // redundant query: a debounce that cannot be cancelled is a debounce that queries twice.
        var queriesBeforeTimer = service.QueryCount;
        var staleBeforeTimer = viewModel.StaleRefreshCount;

        Settle(scheduler, executor);

        Assert.Equal(1, viewModel.FilteredCount);
        Assert.True(
            viewModel.StaleRefreshCount > staleBeforeTimer,
            "the superseded typing timer was not counted as stale.");
        Assert.True(
            service.QueryCount < queriesBeforeTimer + 5,
            $"the typing timer caused {service.QueryCount - queriesBeforeTimer} extra queries.");
    }

    [Fact]
    public void DispatcherWorkAndOffDispatcherQueryTimeAreCountedSeparately()
    {
        var inner = new ActivityCenterService(Filter.Redact, TimeProvider.System);
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();
var latency = new ActivityVisibilityLatencyRecorder(TimeProvider.System);

        // Offered before the screen is bound, so the initial settle leaves no folded follow-up behind and the
        // refresh below is the only thing that adds a rebuild.
        inner.Append(Event("row-1"));
        inner.Append(Event("row-2"));

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            scheduler: scheduler,
            latencyRecorder: latency,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        Settle(scheduler, executor);

        var refreshes = viewModel.RefreshCount;
        Assert.Equal(1, refreshes);

        // The initial refresh is harvested away, so the two series below are the refresh under test and
        // nothing else.
        latency.DrainQueryWorkSamples();
        latency.DrainDispatcherWorkSamples();


        // The worker is made to take real time inside the query, so the two series cannot accidentally agree
        // and the dispatcher figure cannot accidentally contain the query's own time.
service.QueryDelay = TimeSpan.FromMilliseconds(80);
        viewModel.RefreshCommand.Execute(null);
        Settle(scheduler, executor);

        Assert.Equal(refreshes + 1, viewModel.RefreshCount);
        Assert.Equal(1, latency.QuerySampleCount);
        Assert.Equal(1, latency.DispatcherSampleCount);

        var query = latency.DrainQueryWorkSamples().Single();
        var dispatcher = latency.DrainDispatcherWorkSamples().Single();

        Assert.True(query >= 60, $"the query sample was {query} ms; the worker's own time was not measured.");
        Assert.True(
            dispatcher < query,
            $"dispatcher work ({dispatcher} ms) included the off-dispatcher query ({query} ms).");

        // Both are harvested, not accumulated: a second drain is empty for both series.
        Assert.Empty(latency.DrainQueryWorkSamples());
        Assert.Empty(latency.DrainDispatcherWorkSamples());
    }

    [Fact]
    public void TheVisibilityStampIsTakenAfterPublication_AndSpansTheWaitForIt()
    {
        var inner = new ActivityCenterService(Filter.Redact, TimeProvider.System);
        var service = new FaultInjectingActivityCenterService(inner);
        var executor = new ManualQueryExecutor();
        var scheduler = new ManualActivityUiScheduler();
        var latency = new ActivityVisibilityLatencyRecorder(TimeProvider.System);

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            scheduler: scheduler,
            latencyRecorder: latency,
            queryExecutor: executor,
            searchDebounce: TimeSpan.Zero);

        // The two events are offered after the screen is bound, so their ingest stamps are pending and
        // nothing is visible yet.
        inner.Append(Event("row-1"));
        inner.Append(Event("row-2"));

        scheduler.FireTimersAndRun();

        service.QueryDelay = TimeSpan.FromMilliseconds(60);
        executor.RunAll();

        // The query is done and its result is waiting to be dispatched. Nothing is visible yet, so nothing
        // is stamped: stamping at worker completion would report the query's cost as if it were the whole
        // latency and would hide the wait for the publication the operator actually experiences.
        Assert.Equal(0, latency.SampleCount);
        Assert.False(viewModel.HasEvents);

        Thread.Sleep(80);
        scheduler.FireTimersAndRun();

        var samples = viewModel.HarvestLatencySamples();

        Assert.Equal(2, samples.Count);
        Assert.All(samples, sample => Assert.True(
            sample.Milliseconds >= 60,
            $"a sample was {sample.Milliseconds} ms; the wait before publication was not counted."));

        // Marking the same rows visible again on a refresh must not inflate the percentile.
        viewModel.RefreshCommand.Execute(null);
        Settle(scheduler, executor);

        // Two events, made visible once each: the refresh re-marked rows that were already accounted for,
        // so the percentile is not inflated by a row the screen merely re-rendered.
        Assert.Equal(2, latency.ObservedCount);
    }

    [Fact]
    public void ANegativeSearchDebounceIsRejected()
    {
        var inner = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityCenterViewModel(
            inner,
            timeProvider: new FixedTimeProvider(Now),
            searchDebounce: TimeSpan.FromMilliseconds(-1)));
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan budget)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < budget)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    private static ActivityEvent Event(
        string id,
        string? description = null,
        DateTimeOffset? occurredAt = null) => new(
        id,
        occurredAt ?? Now.AddSeconds(-60),
        ActivityEventKind.Execution,
        ActivityRoleNames.Coder,
        ActivityEventState.Running,
        ActivityEventSource.Native,
        $"title {id}",
        description ?? string.Empty);

    /// <summary>
    /// A real <see cref="SqliteException"/> from a real FTS5 syntax error, produced by asking SQLite for the
    /// malformed expression. Hand-building the exception would let this test keep passing if the real failure
    /// changed shape, which is the one thing it exists to pin.
    /// </summary>
    private static SqliteException RealFtsSyntaxFailure()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE ActivityEventsSearch USING fts5(body, tokenize='unicode61');";
            create.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT rowid FROM ActivityEventsSearch WHERE ActivityEventsSearch MATCH 'observation OR';";

        return Assert.Throws<SqliteException>(() => command.ExecuteScalar());
    }

    /// <summary>
    /// Holds the query work instead of running it, so a test decides when a query completes. A real thread
    /// would turn every one of these assertions into a race.
    /// </summary>
    private sealed class HeldCommitJournal : IActivityEventJournal
    {
        private readonly ActivityCenterService _committed = new(text => text);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task AppendAsync(ActivityEvent item, CancellationToken token = default) => AppendRangeAsync(new[] { item }, token);
        public async Task AppendRangeAsync(IReadOnlyList<ActivityEvent> items, CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token).ConfigureAwait(false);
            _committed.AppendRange(items);
        }
        public Task<IReadOnlyList<ActivityEvent>> LoadNewestAsync(int limit, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<ActivityEvent>>(_committed.Snapshot().Take(limit).ToArray());
        public Task<long> CountAsync(CancellationToken token = default) => Task.FromResult((long)_committed.TotalCount);
        public Task<ActivityJournalTrimResult> TrimAsync(int limit, CancellationToken token = default) =>
            throw new NotSupportedException("This test does not run retention.");
        public ActivityJournalPage QueryPage(ActivityJournalQuery query)
        {
            var rows = _committed.Snapshot();
            return new ActivityJournalPage(rows.Count, rows.Count, rows.Skip(query.Offset).Take(query.Limit).ToArray());
        }
    }

    private sealed class ManualQueryExecutor : IActivityQueryExecutor
    {
        private int _pending;

        public int PendingCount => Volatile.Read(ref _pending);

        public void Enqueue(Action work)
        {
            Interlocked.Increment(ref _pending);
            _queued.Add(work);
        }

        /// <summary>Runs every queued query in the order it was queued, oldest first.</summary>
        public void RunAll()
        {
            while (_queued.Count > 0)
            {
                var work = _queued[0];
                _queued.RemoveAt(0);
                Interlocked.Decrement(ref _pending);
                work();
            }
        }

        private readonly List<Action> _queued = new();
    }

    /// <summary>
    /// Holds the posted callbacks instead of running them, so a publication can be inspected before it
    /// happens. A post that asked for a delay is held as a timer and only becomes runnable when the test
    /// fires it, which is what a <c>DispatcherTimer</c> does; running a delayed post immediately would
    /// quietly delete the typing debounce these tests are about.
    /// </summary>
    private sealed class ManualActivityUiScheduler : IActivityUiScheduler
    {
        private readonly List<Action> _ready = new();
        private readonly List<Action> _timers = new();

        public bool IsOnUiThread => true;

        /// <summary>Delay of the most recent post, which is what "predictable" is asserted against.</summary>
        public TimeSpan? LastDelay { get; private set; }

        /// <summary>Number of delayed posts that have not fired yet.</summary>
        public int PendingTimerCount => _timers.Count;

        /// <summary>Number of posts that are runnable but have not run yet.</summary>
        public int ReadyCount => _ready.Count;

        public void Post(Action action, TimeSpan? delay = null)
        {
            ArgumentNullException.ThrowIfNull(action);

            LastDelay = delay;

            if (delay is { } wait && wait > TimeSpan.Zero)
            {
                _timers.Add(action);
                return;
            }

            _ready.Add(action);
        }

        public void Send(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            action();
        }

        /// <summary>Runs everything that is runnable, including what those callbacks post in turn.</summary>
        public void RunAll()
        {
            while (_ready.Count > 0)
            {
                var action = _ready[0];
                _ready.RemoveAt(0);
                action();
            }
        }

        /// <summary>Moves the delayed posts to runnable without running them.</summary>
        public void FireTimers()
        {
            while (_timers.Count > 0)
            {
                _ready.Add(_timers[0]);
                _timers.RemoveAt(0);
            }
        }

        public void FireTimersAndRun()
        {
            FireTimers();
            RunAll();
        }
    }

    /// <summary>
    /// Forwards every member to a real service, can fail one query on demand, and can take real time inside
    /// it. Both have to come from the query itself: an exception or a delay applied around the whole work
    /// item would sit outside the view model's measurement and every assertion about it would pass for the
    /// wrong reason.
    /// </summary>
    private sealed class FaultInjectingActivityCenterService : IActivityCenterService
    {
        private readonly IActivityCenterService _inner;

        public FaultInjectingActivityCenterService(IActivityCenterService inner)
        {
            _inner = inner;
        }

        public event EventHandler? PersistenceChanged
        {
            add => _inner.PersistenceChanged += value;
            remove => _inner.PersistenceChanged -= value;
        }

        public Exception? QueryFailure { get; set; }

        public TimeSpan QueryDelay { get; set; } = TimeSpan.Zero;

        /// <summary>Criteria and page of the most recent query the worker actually ran.</summary>
        public (ActivityFilterCriteria Criteria, int Page, int PageSize, int Offset) LastQuery { get; private set; }

        /// <summary>Number of queries the worker ran: a bound on the durable work, not on the keystrokes.</summary>
        public int QueryCount { get; private set; }

        /// <summary>Managed thread id of every query, so "one reused worker" is checkable.</summary>
        public List<int> QueryThreads { get; } = new();

        public event EventHandler<ActivityEventAppendedEventArgs>? Appended
        {
            add => _inner.Appended += value;
            remove => _inner.Appended -= value;
        }

        public int TotalCount => _inner.TotalCount;

        public int Capacity => _inner.Capacity;

        public ActivityRetentionStatistics Statistics => _inner.Statistics;

        public void Append(ActivityEvent activityEvent) => _inner.Append(activityEvent);

        public void AppendRange(IEnumerable<ActivityEvent> activityEvents) => _inner.AppendRange(activityEvents);

        public void AppendProjection(ObservableRunProjection projection) => _inner.AppendProjection(projection);

        public void AppendTimeline(ActivityTimeline timeline) => _inner.AppendTimeline(timeline);

        public void AppendWorkflowTimeline(WorkflowRunTimeline timeline) => _inner.AppendWorkflowTimeline(timeline);

        public void AppendHealthTransition(string scopeId, string stateDisplay, string detail) =>
            _inner.AppendHealthTransition(scopeId, stateDisplay, detail);

        public void AppendUserAction(string id, string title, string description) =>
            _inner.AppendUserAction(id, title, description);

        public void AppendSystemEvent(
            string id,
            string title,
            string description,
            ActivityEventState state = ActivityEventState.Warning) =>
            _inner.AppendSystemEvent(id, title, description, state);

        public bool Remove(string eventId) => _inner.Remove(eventId);

        public void Clear() => _inner.Clear();

        public ActivityFilterResult Query(ActivityFilterCriteria? criteria = null) => _inner.Query(criteria);

        public ActivityFilterResult QueryPage(
            ActivityFilterCriteria? criteria = null,
            int page = 1,
            int pageSize = ActivityFilterCriteria.DefaultLimit)
        {
            QueryCount++;
            QueryThreads.Add(Environment.CurrentManagedThreadId);
            LastQuery = (criteria ?? ActivityFilterCriteria.Default, page, pageSize, (page - 1) * pageSize);

            if (QueryDelay > TimeSpan.Zero)
            {
                Thread.Sleep(QueryDelay);
            }

            if (QueryFailure is { } failure)
            {
                throw failure;
            }

            return _inner.QueryPage(criteria, page, pageSize);
        }

        public IReadOnlyList<ActivityEvent> Snapshot() => _inner.Snapshot();

        public Task<ActivityCenterLoadReport> LoadAsync(CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(cancellationToken);

        public Task<ActivityJournalTrimResult> ApplyJournalRetentionAsync(
            CancellationToken cancellationToken = default) =>
            _inner.ApplyJournalRetentionAsync(cancellationToken);
    }
}
