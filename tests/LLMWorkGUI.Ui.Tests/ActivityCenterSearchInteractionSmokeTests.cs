using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;
using ActivityEvent = LLMWorkGUI.Application.Observability.ActivityEvent;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// The shipped Activity Center search box, driven for real, over a real journal, on a real dispatcher.
/// <para>
/// This is the end-to-end WPF search acceptance walk. It opens the shipped <see cref="ActivityCenterView"/>,
/// types into the shipped <c>SearchBox</c> through the shipped <c>UpdateSourceTrigger=PropertyChanged</c>
/// binding, replaces the text while a durable query is in flight, clears it, narrows by role, pages, opens
/// the details pane - all while a 50 events/second stream keeps arriving and the production bounded worker
/// and the production dispatcher scheduler do the querying.
/// </para>
/// <para>
/// It holds 190 000 retained rows for the real SearchBox overlap and theme/DPI matrix. The separate
/// 30-minute load driver measures the normative event-to-visible profile.
/// </para>
/// <para>
/// Two budgets are declared and asserted separately, because they answer different questions. Search
/// interaction is the operator-visible time from the keystroke to the answer being on screen. Dispatcher
/// work is what the UI thread was actually charged. The point of the change is that the first is no longer
/// dominated by the second: a durable query that runs on the dispatcher puts its whole cost into that second
/// number, and the second budget is what forbids it.
/// </para>
/// </summary>
[Collection("ActivityCenter coalescing isolation")]
[Trait("Category", "VisualUi")]
public sealed class ActivityCenterSearchInteractionSmokeTests : IDisposable
{
    /// <summary>Retained rows for the full-volume WPF search acceptance walk.</summary>
    private const int RetainedRows = 190_000;

    /// <summary>Declared budget for one operator-visible search interaction: keystroke to published answer.</summary>
    private const double SearchInteractionBudgetMilliseconds = 1_500;

    /// <summary>
    /// Declared budget for the work the WPF dispatcher does per refresh, at this scale. The accepted R1
    /// profile measured 270.4 ms for a single keystroke on the shipped screen because the durable count and
    /// page ran there; 250 ms is the ceiling below which that can no longer be what happened.
    /// </summary>
    private const double DispatcherWorkBudgetMilliseconds = 250;

    private const double SettleBudgetSeconds = 90;

    /// <summary>Events offered while the screen is open, enough to cover every interaction below.</summary>
    private const int LiveEvents = 1_200;

    /// <summary>Pacing of the live stream: 20 ms per event, so 50 events/second.</summary>
    private const int StreamIntervalMilliseconds = 20;

    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _appData;
    private readonly SqliteConnectionFactory _factory;
    private readonly CancellationTokenSource _streamStop = new();
    private Task _stream = Task.CompletedTask;
    private ActivityJournalWriteQueue? _journalQueue;

    public ActivityCenterSearchInteractionSmokeTests()
    {
        _appData = Path.Combine(
            Path.GetTempPath(),
            "llmworkgui-search-smoke",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_appData);
        _factory = new SqliteConnectionFactory(Path.Combine(_appData, "llmworkgui.db"));
    }

    public void Dispose()
    {
        StopStreamAndJournal();
        _streamStop.Dispose();
        using var connection = _factory.CreateConnection();
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        Directory.Delete(_appData, recursive: true);
    }

    private void StopStreamAndJournal()
    {
        _streamStop.Cancel();
        _stream.GetAwaiter().GetResult();
        var queue = _journalQueue;
        _journalQueue = null;
        queue?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(AppTheme.Dark, 1280, 800, "dark 1280x800 @100%")]
    [InlineData(AppTheme.Light, 1280, 800, "light 1280x800 @100%")]
    [InlineData(AppTheme.Dark, 2560, 1600, "dark 2560x1600 @200% DPI")]
    public async Task TheShippedSearchBoxStaysResponsible_AndEndsOnTheLatestRequest(
        AppTheme theme,
        double width,
        double height,
        string variation)
    {
        StaTestRunner.EnsureApplication();

        var service = await ComposeAsync();

        using var executor = new BoundedActivityQueryExecutor();
        var latency = new ActivityVisibilityLatencyRecorder(TimeProvider.System);
        var interactions = new List<double>();
        ActivityCenterViewModel? screen = null;
        using var firstSearchGate = new FirstSearchGate(executor, () => screen?.SearchQuery);
        var streamEstablishSeconds = 0d;

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);

            // Composed here, on the dispatcher's own thread, with the production scheduler: the view model's
            // default is the inline scheduler, which publishes on whichever thread finished the query - the
            // worker. Publishing a WPF-bound list from the worker is not a scheduling choice, it is a
            // cross-thread binding violation, and it would throw inside the publication instead of producing
            // a measurement.
            screen = new ActivityCenterViewModel(
                service,
                new DiffArtifactViewerViewModel(),
                TimeProvider.System,
                latencyRecorder: latency,
                scheduler: new DispatcherActivityUiScheduler(Dispatcher.CurrentDispatcher),
                queryExecutor: firstSearchGate);

            var view = new ActivityCenterView { DataContext = screen };
            var window = new Window
            {
                Width = width,
                Height = height,
                Content = view,
                ShowActivated = false,
                WindowStyle = WindowStyle.None
            };

            try
            {
                window.Show();
                Pump(TimeSpan.FromSeconds(10));

                var searchBox = (TextBox)view.FindName("SearchBox");
                var eventList = (ListBox)view.FindName("EventsList");
                view.UpdateLayout();

                PumpUntil(
                    () => screen.TotalCount == RetainedRows && screen.FilteredCount == RetainedRows,
                    variation,
                    "the initial refresh never reported the retained rows");

                Assert.True(eventList.Items.Count > 0, $"{variation}: the list rendered no rows.");

                // The stream starts here and never stops during the interaction. These appends are what the
                // latency samples measure: an event's stamp is taken inside Append and its visible stamp only
                // after the dispatcher has published a page containing it, so a stream that is not running
                // would leave the whole §9.2 series empty rather than merely small.
                //
                // Their text is deliberately free of the search terms, so the expected counts below stay exact
                // while the retained total moves underneath them.
                var liveEvents = new List<ActivityEvent>();

                for (var sequence = RetainedRows; sequence < RetainedRows + LiveEvents; sequence++)
                {
                    liveEvents.Add(LiveEvent(sequence));
                }

                var streamStarted = Stopwatch.StartNew();

                _stream = Task.Run(() =>
                {
                    foreach (var live in liveEvents)
                    {
                        if (_streamStop.IsCancellationRequested) break;
                        service.Append(live);
                        if (_streamStop.Token.WaitHandle.WaitOne(StreamIntervalMilliseconds)) break;
                    }
                });

                PumpUntil(
                    () => screen.TotalCount > RetainedRows,
                    variation,
                    "the live stream never reached the screen");

                // Let the stream establish itself before the operator acts, so the coalescing evidence below
                // is about a screen that has already been following the stream rather than about its first
                // few appends. Measured, but deliberately not counted as an operator interaction: waiting for
                // the stream to reach a rate is the stream's own time, not the operator's.
                var establishWatch = Stopwatch.StartNew();

                PumpUntil(
                    () => screen.CoalescedAppendCount >= 100,
                    variation,
                    "the screen never folded the stream into bounded refreshes");
                establishWatch.Stop();
                streamEstablishSeconds = establishWatch.Elapsed.TotalSeconds;

                Assert.True(
                    screen.RefreshCount < screen.CoalescedAppendCount,
                    $"{variation}: {screen.RefreshCount} list rebuilds for {screen.CoalescedAppendCount} "
                    + "folded appends; the stream is not being coalesced.");

                // A multi-token query typed through the shipped binding and replaced while the screen is
                // still loading. The screen has to end on the text in the box: this is the
                // operator-visible form of "the latest request wins", and it is checked on the rows
                // themselves rather than on a counter.
                firstSearchGate.Arm();
                Type(searchBox, "observation alpha zeta");

                interactions.Add(PumpUntil(
                    () => firstSearchGate.Entered,
                    variation,
                    "the first search never reached the query worker"));

                Type(searchBox, "observation beta");
                firstSearchGate.Release();

                interactions.Add(PumpUntil(
                    () => screen.SearchQuery == "observation beta" && screen.FilteredCount == RetainedRows / 2,
                    variation,
                    "the screen never settled on the second query"));

                // Every row on screen answers the text in the box. A superseded result published for even one
                // frame would leave a row that does not, which is the failure this step exists to catch.
                AssertRowsAnswer(screen, "beta", variation);

                // Clearing the query brings the whole retained set back, from the journal and not from the
                // bounded in-memory window.
                interactions.Add(Timed(
                    searchBox,
                    string.Empty,
                    () => screen.SearchQuery.Length == 0 && screen.FilteredCount > RetainedRows,
                    variation));

                // A reserved FTS5 operator word is a literal term now: it narrows to nothing rather than
                // raising an unhandled SQLite error out of the dispatcher or broadening to every row.
                interactions.Add(Timed(searchBox, "observation OR", () => screen.FilteredCount == 0, variation));

                // A genuine multi-token query.
                interactions.Add(Timed(
                    searchBox,
                    "observation alpha",
                    () => screen.FilteredCount == RetainedRows / 2,
                    variation));

                AssertRowsAnswer(screen, "alpha", variation);

                // The role filter, combined with the query.
                var watch = Stopwatch.StartNew();
                screen.RoleFilters.Single(filter => filter.Display == ActivityRoleNames.Reviewer).IsSelected = true;
                interactions.Add(PumpUntil(
                    () => screen.FilteredCount > 0 && screen.FilteredCount < RetainedRows / 2,
                    variation,
                    "the role filter never narrowed the result"));
                watch.Stop();

                var reviewerRows = screen.FilteredCount;

                // Every row also answers the role filter, so a count without the rows behind it cannot pass.
                AssertRowsAnswer(screen, "alpha", variation);
                Assert.All(
                    screen.Events,
                    item => Assert.Equal(ActivityRoleNames.Reviewer, item.RoleDisplay));

                // Paging: a page the bounded in-memory window cannot hold on its own.
                watch = Stopwatch.StartNew();
                screen.NextPageCommand.Execute(null);
                interactions.Add(PumpUntil(
                    () => screen.CurrentPage == 2 && screen.FilteredCount == reviewerRows,
                    variation,
                    "the pager never moved to page two"));
                watch.Stop();

                Assert.True(screen.HasPreviousPage);
                Assert.True(reviewerRows > 0, $"{variation}: the role filter matched nothing.");

                // Selection and the details pane, after all of the above.
                watch = Stopwatch.StartNew();
                screen.SelectedEvent = screen.Events.FirstOrDefault();
                interactions.Add(PumpUntil(() => screen.HasSelection, variation, "no row could be selected"));
                watch.Stop();

                view.UpdateLayout();

                Assert.Equal(2, screen.CurrentPage);
                Assert.True(screen.HasSelection);
                Assert.True(eventList.Items.Count > 0, $"{variation}: the list rendered no rows after paging.");

                Console.WriteLine(
                    $"[{variation}] streamElapsedSeconds={streamStarted.Elapsed.TotalSeconds:N1}");
            }
            finally
            {
                // Stop producers before closing their consumers. A final FIFO sentinel proves every
                // accepted query returned before its SQLite files and gate handles can be removed.
                screen.Dispose();
                _streamStop.Cancel();
                PumpCleanup(_stream, variation);
                firstSearchGate.Release();
                var queriesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                executor.Enqueue(() => queriesDrained.TrySetResult());
                PumpCleanup(queriesDrained.Task, variation);
                StopStreamAndJournal();
                window.Close();
            }
        });

        using (screen)
        {
            Assert.NotNull(screen);
            Assert.Equal(0, screen.QueryFailureCount);
            Assert.Equal(0, executor.RefusedCount);
        }

        var dispatcher = latency.DrainDispatcherWorkSamples().ToArray();
        var query = latency.DrainQueryWorkSamples().ToArray();
        var eventToVisible = screen.HarvestLatencySamples();

        Assert.NotEmpty(dispatcher);
        Assert.NotEmpty(query);
        Assert.NotEmpty(eventToVisible);

        var dispatcherMax = dispatcher.Max();
        var dispatcherP95 = Percentile(dispatcher, 0.95);
        var queryP50 = Percentile(query, 0.50);
        var interactionP95 = Percentile(interactions.ToArray(), 0.95);

        Assert.True(
            dispatcherMax <= DispatcherWorkBudgetMilliseconds,
            $"{variation}: the dispatcher was charged {dispatcherMax:N1} ms for one refresh, over the declared "
            + $"{DispatcherWorkBudgetMilliseconds} ms budget. A durable query still runs on the UI thread.");

        // The dispatcher/query series include initial loading and background stream refreshes;
        // operator samples contain only requested interactions. Summing unlike intervals would not
        // establish responsiveness. The independent per-refresh and interaction budgets remain enforced.
        Assert.True(screen.MaxQueryWorkMilliseconds > 0, $"{variation}: no off-dispatcher query time was recorded.");

        Assert.True(
            interactionP95 <= SearchInteractionBudgetMilliseconds,
            $"{variation}: search interaction p95 was {interactionP95:N1} ms, over the declared "
            + $"{SearchInteractionBudgetMilliseconds} ms budget.");

        Assert.True(
            executor.PendingHighWaterMark <= executor.QueueCapacity,
            $"{variation}: the pending queue reached {executor.PendingHighWaterMark} of {executor.QueueCapacity}.");

        // Reported rather than asserted: these belong in the writer's handoff, not in a boolean.
        Console.WriteLine(
            $"[{variation}] retained={RetainedRows:N0} streamEstablishSeconds={streamEstablishSeconds:N1} "
            + $"interactions={interactions.Count:N0} "
            + $"interactionP50={Percentile(interactions.ToArray(), 0.50):N1}ms "
            + $"interactionP95={interactionP95:N1}ms interactionMax={interactions.Max():N1}ms "
            + $"dispatcherSamples={dispatcher.Length} dispatcherP50={Percentile(dispatcher, 0.50):N2}ms "
            + $"dispatcherP95={dispatcherP95:N2}ms dispatcherMax={dispatcherMax:N2}ms "
            + $"querySamples={query.Length} queryP50={queryP50:N1}ms queryMax={query.Max():N1}ms "
            + $"rebuilds={screen.RefreshCount:N0} staleResults={screen.StaleResultCount:N0} "
            + $"coalescedAppends={screen.CoalescedAppendCount:N0} "
            + $"refused={executor.RefusedCount} queueHigh={executor.PendingHighWaterMark}/{executor.QueueCapacity} "
            + $"eventToVisibleSamples={eventToVisible.Count:N0}");
    }

    private static void PumpCleanup(Task operation, string variation)
    {
        PumpUntil(() => operation.IsCompleted, variation, "cleanup did not complete within the declared settle budget");
        // Completion was established while pumping the dispatcher. This only propagates a completed
        // operation's failure and never synchronously waits on UI work.
        operation.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Every row currently on screen carries the given term. This is the check that a superseded result was
    /// never published: a count can be right while the rows behind it belong to a query the operator has
    /// already replaced, and only the rows settle that.
    /// </summary>
    private static void AssertRowsAnswer(ActivityCenterViewModel screen, string term, string variation)
    {
        Assert.NotEmpty(screen.Events);
        Assert.All(
            screen.Events,
            item => Assert.True(
                item.Model.Description.Contains(term, StringComparison.Ordinal),
                $"{variation}: row '{item.Id}' does not answer the query in the box ('{screen.SearchQuery}')."));
    }

    /// <summary>Types through the shipped binding and waits for the screen to answer, returning the elapsed time.</summary>
    private static double Timed(TextBox searchBox, string text, Func<bool> settled, string variation)
    {
        var watch = Stopwatch.StartNew();

        Type(searchBox, text);

        var elapsed = PumpUntil(settled, variation, $"the screen never answered '{text}'");

        watch.Stop();
        Assert.True(watch.Elapsed.TotalMilliseconds <= SearchInteractionBudgetMilliseconds);

        return elapsed;
    }

    private static void Type(TextBox searchBox, string text)
    {
        searchBox.Text = text;
        BindingOperations.GetBindingExpression(searchBox, TextBox.TextProperty)?.UpdateSource();
    }

    /// <summary>Pumps the dispatcher until the condition holds; returns the time it took.</summary>
    private static double PumpUntil(Func<bool> condition, string variation, string failure)
    {
        var watch = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(SettleBudgetSeconds);

        while (watch.Elapsed < budget)
        {
            if (condition())
            {
                return watch.Elapsed.TotalMilliseconds;
            }

            Pump(TimeSpan.FromMilliseconds(25));
        }

        Assert.Fail($"{variation}: {failure} within {SettleBudgetSeconds} s.");
        return 0;
    }

    /// <summary>Runs the dispatcher until the queued work has had its turn.</summary>
    private static void Pump(TimeSpan budget)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < budget)
        {
            var frame = new DispatcherFrame();

            // One argument only: BeginInvoke(priority, delegate, args) would otherwise bind the second
            // priority to the delegate's parameter list and fail on invocation.
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));

            Dispatcher.PushFrame(frame);
        }
    }

    private static double Percentile(double[] samples, double percentile)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var ordered = samples.OrderBy(value => value).ToArray();
        var rank = (int)Math.Ceiling(percentile * ordered.Length) - 1;

        return ordered[Math.Clamp(rank, 0, ordered.Length - 1)];
    }

    private async Task<ActivityCenterService> ComposeAsync()
    {
        await new DatabaseMigrator(_factory).MigrateAsync();

        var journal = new SqliteActivityEventJournal(_factory);
        var queue = new ActivityJournalWriteQueue(journal);
        _journalQueue = queue;

        var service = new ActivityCenterService(
            text => text,
            TimeProvider.System,
            new EventSearchIndex(text => text, 2_000),
            capacity: 2_000,
            journalQueue: queue,
            journal: journal,
            retentionLimit: ActivityJournalOptions.DefaultRetentionLimit);

        for (var sequence = 0; sequence < RetainedRows; sequence++)
        {
            service.Append(Event(sequence));

            if ((sequence + 1) % ActivityJournalWriteQueue.BatchSize == 0)
            {
                await queue.DrainAsync();
            }
        }

        await queue.DrainAsync();

        Assert.Equal(RetainedRows, await journal.CountAsync());

        return service;
    }

    private static ActivityEvent Event(int sequence)
    {
        var alpha = sequence % 2 == 0;

        // Every tenth alpha row also carries "zeta", so a query for it has a different answer from the plain
        // alpha query. Two searches with different answers are what makes a superseded publication visible.
        var zeta = alpha && sequence % 20 == 0;
        var body = alpha
            ? $"observation alpha payload for run {sequence}"
            : $"observation beta payload for run {sequence}";

        return new ActivityEvent(
            $"seed:{sequence:D6}",
            Now.AddSeconds(sequence),
            ActivityEventKind.Execution,
            sequence % 3 == 0 ? ActivityRoleNames.Reviewer : ActivityRoleNames.Coder,
            sequence % 4 == 0 ? ActivityEventState.Completed : ActivityEventState.Running,
            ActivityEventSource.Native,
            $"title {sequence}",
            zeta ? body + " zeta" : body,
            executionId: $"exec-{sequence % 7:D2}");
    }

    /// <summary>
    /// A live-stream event. Its text carries none of the search terms, so a search keeps answering with the
    /// exact count of the seeded rows while the retained total moves underneath it.
    /// </summary>
    private static ActivityEvent LiveEvent(int sequence) => new(
        $"live:{sequence:D6}",
        Now.AddSeconds(sequence),
        ActivityEventKind.Health,
        ActivityRoleNames.System,
        ActivityEventState.Warning,
        ActivityEventSource.Native,
        $"live title {sequence}",
        $"live telemetry sample {sequence}",
        sessionId: $"session-{sequence % 5}");

    /// <summary>
    /// Pauses the first real durable query for the first typed text on the production bounded worker.
    /// The replacement keystroke is sent through the real TextBox before that query may return.
    /// </summary>
    private sealed class FirstSearchGate : IActivityQueryExecutor, IDisposable
    {
        private readonly IActivityQueryExecutor _inner;
        private readonly Func<string?> _currentSearch;
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _armed;
        private int _intercepted;

        public FirstSearchGate(IActivityQueryExecutor inner, Func<string?> currentSearch)
        {
            _inner = inner;
            _currentSearch = currentSearch;
        }

        public bool Entered => _entered.IsSet;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.Set();

        public void Enqueue(Action work)
        {
            if (Volatile.Read(ref _armed) == 1
                && _currentSearch() == "observation alpha zeta"
                && Interlocked.CompareExchange(ref _intercepted, 1, 0) == 0)
            {
                _inner.Enqueue(() =>
                {
                    _entered.Set();
                    if (!_release.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The replacement search was not typed before the first query returned.");
                    }

                    work();
                });
                return;
            }

            _inner.Enqueue(work);
        }

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }
}
