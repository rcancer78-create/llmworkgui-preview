using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LLMWorkGUI.ActivityLoadDriver;

/// <summary>
/// The Phase 11 normative load scenario, driven against the shipped product.
/// <para>
/// The load enters through <see cref="IActivityCenterService.Append"/> - the product ingestion boundary -
/// with unique event ids and eight execution identities, exactly as ТЗ §9.2 requires. Nothing here
/// pokes at a private index, a test double or a parallel toy screen: the WPF window shown on the desktop
/// is the shipped <see cref="MainWindow"/> hosting the shipped Activity Center, and the numbers are read
/// off that screen.
/// </para>
/// </summary>
internal sealed class ActivityLoadWalk
{
    private readonly IServiceProvider _services;
    private readonly Window _window;
    private readonly UnifiedWorkspaceShellViewModel _shell;
    private readonly ActivityCenterViewModel _activity;
    private readonly ActivityLoadOptions _options;
    private readonly ActivityLoadReport _report;
    private readonly IActivityUiScheduler _scheduler;
    private readonly DriverTrace _trace;

    public ActivityLoadWalk(
        IServiceProvider services,
        Window window,
        UnifiedWorkspaceShellViewModel shell,
        ActivityCenterViewModel activity,
        ActivityLoadOptions options,
        ActivityLoadReport report,
        DriverTrace trace)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(trace);

        _services = services;
        _window = window;
        _shell = shell;
        _activity = activity;
        _options = options;
        _report = report;
        _trace = trace;
        _scheduler = services.GetService<IActivityUiScheduler>() ?? ImmediateActivityUiScheduler.Instance;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _trace.Step("walk: resolving services");
        var service = _services.GetRequiredService<IActivityCenterService>();
        var journal = _services.GetService<IActivityEventJournal>();
        var queue = _services.GetService<ActivityJournalWriteQueue>();
        var appData = _services.GetRequiredService<StorageOptions>().AppDataDirectory
            ?? throw new InvalidOperationException("The host has no configured application-data root.");

        var databasePath = _services.GetRequiredService<
            LLMWorkGUI.Infrastructure.Data.ISqliteConnectionFactory>().DatabasePath;
        var factory = new ActivityLoadEventFactory(_options.Profile.ExecutionIdentities);
        var now = DateTimeOffset.UtcNow;

        _trace.Step("walk: journal=" + (journal?.GetType().Name ?? "none")
            + " queue=" + (queue is null ? "none" : "present"));
        _trace.Step("walk: database=" + databasePath);

        _report.SetFact("appDataDirectory", appData);
        _report.SetFact("databasePath", databasePath);
        _report.SetFact("journal.repository", journal?.GetType().Name ?? "none (memory-only)");
        _report.SetFact("journal.queue", queue is null ? "none" : $"capacity {queue.QueueCapacity}, batches of {queue.BatchCapacity}");
        _report.SetFact("ingestion.boundary", "IActivityCenterService.Append with unique event ids");
        _report.SetFact("executionIdentities", string.Join(", ", factory.ExecutionIds));

        // Read the production ingestion state before the driver produces a single event. After the seed, the
        // journal is indistinguishable from driver traffic, and "the shipped app generates activity" can no
        // longer be told apart from "the load driver generated activity".
        await RecordProductionIngestionAsync(journal, queue);

        _trace.Step("walk: dismissing the first-run onboarding overlay through its own control");
        await DismissOnboardingAsync();

        _trace.Step("walk: opening the Activity Center overlay through the shipped shell command");
        _shell.OpenActivityCenterCommand.Execute(null);
        await ActivityWindowCapture.SettleAsync();
        _trace.Step("walk: overlay open=" + _shell.IsActivityCenterOpen);

        _trace.Step("walk: before SeedAsync");
        await SeedAsync(service, factory, now, cancellationToken);
        _trace.Step("walk: after SeedAsync");

        _trace.Step("walk: before queue drain + journal count");
        await VerifyJournalAsync(journal, queue, cancellationToken);
        _trace.Step("walk: after queue drain + journal count");
        // Durable queries see committed rows. Flush the asynchronous journal before comparing the
        // seeded service with its screen, then refresh the same production view model.
        _activity.Refresh();
        await ActivityWindowCapture.SettleAsync();

        // The load is only meaningful if the events entered the very stream the shipped screen is bound to.
        // A driver that appends into one service and screenshots another would report perfect numbers for
        // a screen nobody was looking at, so the identity of both ends is recorded as evidence.
        _report.SetFact("ingestion.serviceCount", service.TotalCount.ToString("N0", CultureInfo.InvariantCulture));
        _report.SetFact("ingestion.viewModelTotalCount", _activity.TotalCount.ToString("N0", CultureInfo.InvariantCulture));
        _report.SetFact(
            "ingestion.shellOwnsTheSameViewModel",
            ReferenceEquals(_shell.ActivityCenter, _activity) ? "yes" : "NO");
        _report.SetFact("ingestion.shellActivityCenterTotalCount", _shell.ActivityCenter?.TotalCount
            .ToString("N0", CultureInfo.InvariantCulture) ?? "none");

        _trace.Step(
            $"walk: service.TotalCount={service.TotalCount} viewModel.TotalCount={_activity.TotalCount} "
            + $"shellSharesViewModel={ReferenceEquals(_shell.ActivityCenter, _activity)}");

        if (service.TotalCount != Math.Min(_activity.TotalCount, service.Capacity)
            || !ReferenceEquals(_shell.ActivityCenter, _activity))
        {
            throw new InvalidOperationException(
                "The load entered a different activity stream than the shipped screen is bound to: "
                + $"serviceWindow={service.TotalCount}, capacity={service.Capacity}, "
                + $"viewModelDurableTotal={_activity.TotalCount}. "
                + "Any latency measured against that screen would be meaningless.");
        }


        _trace.Step("walk: before VerifyRestartAndReloadAsync");
        await VerifyRestartAndReloadAsync(appData, _report);
        _trace.Step("walk: after VerifyRestartAndReloadAsync");

        // Open the measurement window. The bulk pre-load filled an empty page from scratch, which is not
        // what the §9.2 figure is about; the criterion is the latency of an event arriving under the load,
        // on a screen that is already full. The pre-load samples are kept and reported separately rather
        // than being mixed in or discarded.
        var preLoad = _activity.Latency.Reset();
        _report.PreLoadVisibilityLatency = ActivityLatencyStatistics.FromSamples(
            preLoad.Select(sample => sample.Milliseconds).ToArray(),
            observedCount: preLoad.Count);
        _trace.Step(
            $"walk: measurement window opened; {preLoad.Count} pre-load samples set aside");

        _trace.Step("walk: before StreamAsync");
        await StreamAsync(service, factory, now, cancellationToken);
        _trace.Step("walk: after StreamAsync");

        _trace.Step("walk: before ExerciseUiAsync");
        await ExerciseUiAsync(cancellationToken);
        _trace.Step("walk: after ExerciseUiAsync");

        _trace.Step("walk: before MeasureSearchAsync");
        await MeasureSearchAsync(service, cancellationToken);
        _trace.Step("walk: after MeasureSearchAsync");
        _report.AddCheckpoint(Measure("after search checks"));

        _trace.Step("walk: before ProbeSecretsAsync");
        await ProbeSecretsAsync(service, journal, databasePath, cancellationToken);
        _trace.Step("walk: after ProbeSecretsAsync");
        _report.AddCheckpoint(Measure("after secret checks"));

        _trace.Step("walk: before RunNegativeControlAsync");
        await RunNegativeControlAsync();
        _trace.Step("walk: after RunNegativeControlAsync");

        _trace.Step("walk: before CaptureEvidenceAsync");
        await CaptureEvidenceAsync();
        _trace.Step("walk: after CaptureEvidenceAsync");
    }

    /// <summary>
    /// Records what the product itself had already ingested before the driver touched anything.
    /// <para>
    /// The R1 review found that no shipped execution, session or health path called
    /// <c>Append</c>, so every "the journal persisted 190 000 events" claim in the previous report was
    /// about synthetic traffic. This step is what makes the distinction visible: it resolves the bridge
    /// that connects the product's own notification seams, reads the row count that existed before the
    /// first synthetic append, and reports both. If the wired source list is empty, or nothing had been
    /// ingested yet, the production-ingestion criterion fails rather than inheriting the driver's traffic.
    /// </para>
    /// </summary>
    private async Task RecordProductionIngestionAsync(IActivityEventJournal? journal, ActivityJournalWriteQueue? queue)
    {
        var bridge = _services.GetService<ActivityCenterEventBridge>();

        if (queue is not null)
        {
            await queue.DrainAsync();
        }

        var rows = journal is null ? 0 : await journal.CountAsync();

        _report.ProductionIngestionSources = bridge?.Sources ?? Array.Empty<string>();
        _report.ProductionRowsAtStartup = rows;
        _report.SetFact(
            "ingestion.productionSources",
            _report.ProductionIngestionSources.Count == 0
                ? "none wired"
                : string.Join("; ", _report.ProductionIngestionSources));
        _report.SetFact(
            "ingestion.productionRowsAtStartup",
            rows.ToString("N0", CultureInfo.InvariantCulture));

        _trace.Step(
            $"walk: production sources={_report.ProductionIngestionSources.Count} "
            + $"journal rows at startup={rows}");

        _report.Add(
            new ActivityCriterionResult(
                "production-ingestion",
                "The shipped application itself generates activity: the product's own notification seams are "
                + "wired into the Activity Center and had already produced journal rows before this run "
                + "produced its first synthetic event.",
                _report.ProductionIngestionIsProven
                    ? ActivityCriterionOutcome.Pass
                    : ActivityCriterionOutcome.Fail,
                $"wired sources: "
                + (_report.ProductionIngestionSources.Count == 0
                    ? "none"
                    : string.Join("; ", _report.ProductionIngestionSources))
                + $"; rows already in the journal before the first synthetic append: {rows:N0}. "
                + "Synthetic driver traffic is never counted towards this criterion."));
    }

    private async Task SeedAsync(
        IActivityCenterService service,
        ActivityLoadEventFactory factory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Deliberately off the UI thread: the ingestion boundary is called from execution, health and
        // worker code in production, and a driver that appended from the dispatcher would be measuring a
        // scenario the product never runs.
        var seedQueue = _services.GetService<ActivityJournalWriteQueue>();
        var elapsed = await Task.Run(
            async () =>
            {
                var watch = Stopwatch.StartNew();

                for (var sequence = 0L; sequence < _options.Profile.SeedEvents; sequence++)
                {
                    service.Append(factory.CreateSeedEvent(sequence, now));

                    if (sequence % 2_000 == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    // The profile starts with saved history, not an unbounded ingestion burst.
                    // Keep each seed batch below the journal queue's capacity and persist it before
                    // offering the next one. Streaming measurements remain paced at 50 events/sec.
                    if (seedQueue is not null && (sequence + 1) % ActivityJournalWriteQueue.BatchSize == 0)
                        await seedQueue.DrainAsync(cancellationToken).ConfigureAwait(false);
                }

                watch.Stop();
                return watch.Elapsed;
            },
            cancellationToken).ConfigureAwait(false);

        _trace.Step($"seed: complete, {_options.Profile.SeedEvents} events in {elapsed.TotalSeconds:N1}s");

        // Let the UI catch up before anything is asserted or measured about it.
        await ActivityWindowCapture.SettleAsync();

        _report.SetFact("seed.durationSeconds", elapsed.TotalSeconds.ToString("N1", CultureInfo.InvariantCulture));
        _report.AddCheckpoint(Measure("after seed, before drain"));
    }

    private async Task VerifyJournalAsync(
        IActivityEventJournal? journal,
        ActivityJournalWriteQueue? queue,
        CancellationToken cancellationToken)
    {
        if (journal is null || queue is null)
        {
            _report.Add(new ActivityCriterionResult(
                "durable-journal",
                "Already-saved activity events are product-persisted rows, not an in-memory prefill.",
                ActivityCriterionOutcome.NotTested,
                "no durable journal is composed in this composition, so nothing was persisted."));
            return;
        }

        _trace.Step("verify-journal: before queue drain");
        await queue.DrainAsync(cancellationToken);
        _trace.Step($"verify-journal: queue drained; offered={queue.OfferedCount} written={queue.WrittenCount} "
            + $"dropped={queue.DroppedCount} failed={queue.FailedCount} pending={queue.PendingCount} "
            + $"balanced={queue.IsBalanced}");

        _trace.Step("verify-journal: before journal CountAsync");
        var rows = await journal.CountAsync(cancellationToken);
        _trace.Step("verify-journal: journal rows=" + rows.ToString("N0", CultureInfo.InvariantCulture));
        _report.PersistedRows = rows;
        _report.JournalDroppedOffers = queue.DroppedCount;
        _report.JournalFailedOffers = queue.FailedCount;

        // The seed has to be exact. "rows > 0" passed for a journal that had lost 99 000 of its 100 000
        // rows, and the queue's own drop and fault counters were printed next to it rather than gating it.
        // Both the count and the accounting are now decided, not decorated.
        //
        // The expected total is the seed plus whatever the product itself had already ingested before the
        // run started. That offset is not slack: it is measured, and it is the product's own rows - the
        // Activity Center's startup record and any event a wired source produced. Counting it as a seed
        // mismatch would be wrong, and ignoring it would hide a product that writes rows nobody accounted for.
        var expected = _options.Profile.SeedEvents + _report.ProductionRowsAtStartup;
        var exactRows = rows == expected;
        var nothingLost = queue.DroppedCount == 0 && queue.FailedCount == 0;
        var balanced = queue.IsBalanced;

        _report.Add(
            new ActivityCriterionResult(
                "durable-journal",
                "Every seeded event is a product-persisted row: after the drain the journal holds exactly the "
                + "seed plus the rows the product had already ingested, and the write queue dropped nothing, "
                + "faulted nothing and settled.",
                exactRows && nothingLost && balanced
                    ? ActivityCriterionOutcome.Pass
                    : ActivityCriterionOutcome.Fail,
                $"{rows:N0} rows in the application database against an expected {expected:N0} "
                + $"(seed {_options.Profile.SeedEvents:N0} + {_report.ProductionRowsAtStartup:N0} product rows "
                + "already present before the first synthetic append), counted after draining the write queue; "
                + $"queue offered {queue.OfferedCount:N0}, written {queue.WrittenCount:N0}, dropped "
                + $"{queue.DroppedCount:N0}, faulted {queue.FailedCount:N0}, pending {queue.PendingCount:N0}, "
                + $"balanced {balanced}."));
    }

    /// <summary>
    /// After draining the original journal, composes a brand new host over the same WAL database while
    /// the original host stays open. This proves fresh-host reload rather than a process restart:
    /// the shipped Activity Center is bound to the new host's service,
    /// with the saved history on the screen and searchable through the durable index.
    /// <para>
    /// The previous version of this check read rows straight out of SQLite and stopped there. That proved
    /// the journal survives; it did not prove the product reloads, and a genuine process restart left the
    /// Activity Center empty while the rows were still on disk. So the reload host's production startup
    /// path runs, and the numbers below come off its <see cref="IActivityCenterService"/> and its
    /// <see cref="ActivityCenterViewModel"/> - the two objects the window actually binds.
    /// </para>
    /// </summary>
    private async Task VerifyRestartAndReloadAsync(string appData, ActivityLoadReport report)
    {
        var databasePath = _services.GetRequiredService<
            LLMWorkGUI.Infrastructure.Data.ISqliteConnectionFactory>().DatabasePath;
        var persisted = report.PersistedRows;
        var seeded = _options.Profile.SeedEvents;

        _trace.Step("restart: before building the reload host");
        var reloadHost = HostBootstrapper.CreateHostBuilder(appDataDirectory: appData)
            .ConfigureServices((_, services) =>
            {
                services.AddAppUi();
                services.AddUnifiedWorkspaceShell();
                services.AddSingleton(_ => new ActivityJournalOptions
                {
                    RetentionLimit = _options.JournalRetentionLimit
                });
            })
            .Build();
        _trace.Step("restart: reload host built");

        try
        {
            _trace.Step("restart: before reload host StartAsync");
            await reloadHost.StartAsync();
            _trace.Step("restart: reload host started");

            _trace.Step("restart: before HostBootstrapper.InitializeAsync on the reload host");
            await HostBootstrapper.InitializeAsync(reloadHost);
            _trace.Step("restart: reload host initialized");

            var journal = reloadHost.Services.GetRequiredService<IActivityEventJournal>();
            var reloadedService = reloadHost.Services.GetRequiredService<IActivityCenterService>();

            _trace.Step("restart: before reload CountAsync");
            var rows = await journal.CountAsync(CancellationToken.None);
            _trace.Step("restart: reload rows=" + rows.ToString("N0", CultureInfo.InvariantCulture));

            _trace.Step("restart: before LoadNewestAsync");
            var reloaded = await journal.LoadNewestAsync(
                (int)Math.Min(persisted, int.MaxValue),
                CancellationToken.None);
            _trace.Step("restart: loaded " + reloaded.Count.ToString("N0", CultureInfo.InvariantCulture) + " events");

            // The shipped screen over the reloaded host. This is the assertion the previous version did not
            // make: the Activity Center a restarted application opens has to show the saved history.
            var reloadScreen = reloadHost.Services.GetRequiredService<ActivityCenterViewModel>();
            await ActivityWindowCapture.OnUiAsync(reloadScreen.Refresh);
            // The shipped screen now queries on its bounded worker. Reading it immediately after Refresh
            // measures the old page, so wait for the actual UI publication with a fixed upper bound.
            var reloadWait = Stopwatch.StartNew();
            while (reloadWait.Elapsed < TimeSpan.FromSeconds(10))
            {
                await ActivityWindowCapture.SettleAsync(TimeSpan.FromSeconds(1));
                var published = await ActivityWindowCapture.OnUiAsync(
                    () => reloadScreen.TotalCount == rows && reloadScreen.Events.Count > 0);
                if (published)
                {
                    break;
                }

                await Task.Delay(20);
            }
            _trace.Step($"restart: visible publication wait {reloadWait.ElapsedMilliseconds:N0} ms");
            _trace.Step(
                $"restart: reloaded screen total={reloadScreen.TotalCount} filtered={reloadScreen.FilteredCount} "
                + $"rows={reloadScreen.Events.Count} pages={reloadScreen.PageCount}");

            var oldestReachable = reloadedService.QueryPage(
                new ActivityFilterCriteria { SearchQuery = "seed:0000000", Limit = 5 },
                pageSize: 5);

            report.ReloadedAfterRestart = rows;
            report.SetFact("restart.reloadedRows", rows.ToString("N0", CultureInfo.InvariantCulture));
            report.SetFact("restart.reloadedDistinctIds", reloaded.Select(item => item.Id).Distinct().Count()
                .ToString("N0", CultureInfo.InvariantCulture));
            report.SetFact("restart.windowCapacity", reloadedService.Capacity.ToString("N0", CultureInfo.InvariantCulture));
            report.SetFact("restart.screenTotalCount", reloadScreen.TotalCount.ToString("N0", CultureInfo.InvariantCulture));
            report.SetFact("restart.screenRows", reloadScreen.Events.Count.ToString("N0", CultureInfo.InvariantCulture));
            report.SetFact("restart.oldestSearchMatched", oldestReachable.FilteredCount.ToString("N0", CultureInfo.InvariantCulture));
            report.ReloadedScreenTotal = reloadScreen.TotalCount;
            report.ReloadedScreenRows = reloadScreen.Events.Count;
            report.ReloadedWindowCount = reloadedService.TotalCount;
            report.ReloadedOldestSearchMatched = oldestReachable.FilteredCount;

            var rowsSurvived = rows == persisted && persisted > 0;
            var screenShowsSavedHistory = reloadScreen.TotalCount == rows && reloadScreen.Events.Count > 0;
            var windowStayedBounded = reloadedService.TotalCount == Math.Min(rows, reloadedService.Capacity);
            var oldestVisible = oldestReachable.FilteredCount >= 1;

            report.Add(
                new ActivityCriterionResult(
                    "restart-reload",
                    "A restarted application shows the saved history: the production startup path reloads the "
                    + "durable journal into the Activity Center the window binds, the exact counts are truthful, "
                    + "and the oldest saved row is still searchable.",
                    rowsSurvived && screenShowsSavedHistory && windowStayedBounded && oldestVisible
                        ? ActivityCriterionOutcome.Pass
                        : ActivityCriterionOutcome.Fail,
                    $"{rows:N0} rows read by a freshly composed host over '{appData}' "
                    + $"(persisted before restart: {persisted:N0}, seeded: {seeded:N0}); "
                    + $"{reloaded.Count:N0} events materialized newest-first with "
                    + $"{reloaded.Select(item => item.Id).Distinct().Count():N0} distinct ids; "
                    + $"the reloaded screen reports {reloadScreen.TotalCount:N0} events over "
                    + $"{reloadScreen.PageCount:N0} pages with {reloadScreen.Events.Count:N0} rows materialized; "
                    + $"the in-memory window holds {reloadedService.TotalCount:N0} of its "
                    + $"{reloadedService.Capacity:N0} bound; a search for the oldest saved row matched "
                    + $"{oldestReachable.FilteredCount:N0}."));
        }
        finally
        {
            _trace.Step("restart: stopping the reload host");
            using (var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await reloadHost.StopAsync(shutdown.Token);
            }

            reloadHost.Dispose();
            _trace.Step("restart: reload host disposed");
        }
    }

    private async Task StreamAsync(
        IActivityCenterService service,
        ActivityLoadEventFactory factory,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var profile = _options.Profile;
        var distribution = _options.SizeDistribution;
        var journal = _services.GetService<IActivityEventJournal>();
        var queue = _services.GetService<ActivityJournalWriteQueue>();

        var streamStart = Stopwatch.GetTimestamp();
        var wallStart = Stopwatch.StartNew();
        var ticksPerEvent = Stopwatch.Frequency / (double)profile.EventsPerSecond;

        _trace.Step($"stream: starting; {profile.OfferedEvents} events at {profile.EventsPerSecond}/s");

        // The whole paced loop runs on the thread pool so the dispatcher is free to run the refreshes
        // under measurement. Nothing here touches a WPF object.
        var counters = await Task.Run(
            () =>
            {
                long acceptedCount = 0;
                var largestBytes = 0;
                long bytes = 0;
                long largeBytes = 0;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var queuePending = new List<long>();

                for (var sequence = 0L; sequence < profile.OfferedEvents; sequence++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Pace against a fixed schedule rather than a fixed delay, so a slow append does not
                    // push the whole run late and the achieved rate stays close to the nominal one.
                    WaitUntil(streamStart + (long)(sequence * ticksPerEvent));

                    var activityEvent = factory.CreateStreamEvent(
                        sequence,
                        now.AddSeconds(sequence / (double)profile.EventsPerSecond),
                        distribution);

                    service.Append(activityEvent);
                    queuePending.Add(queue?.PendingCount ?? 0);
                    acceptedCount++;

                    var size = activityEvent.Description.Length;
                    bytes += size;

                    if (size > largestBytes)
                    {
                        largestBytes = size;
                    }

                    if (size > 1024)
                    {
                        largeBytes += size;
                    }

                    if (activityEvent.ExecutionId is { } executionId)
                    {
                        seen.Add(executionId);
                    }
                }

                queuePending.Sort();
                return (acceptedCount, largestBytes, bytes, largeBytes, seen.Count,
                    PendingP95: queuePending[(int)((queuePending.Count - 1) * .95)],
                    PendingMax: queuePending[^1]);
            },
            cancellationToken).ConfigureAwait(false);

        wallStart.Stop();
        _report.ActualStreamDuration = wallStart.Elapsed;
        _report.OfferedEvents = profile.OfferedEvents;
        _report.AcceptedEvents = counters.acceptedCount;
        _report.SetFact("journal.pendingP95", counters.PendingP95.ToString(CultureInfo.InvariantCulture));
        _report.SetFact("journal.pendingMax", counters.PendingMax.ToString(CultureInfo.InvariantCulture));
        _report.MaxMessageBytesObserved = counters.largestBytes;
        _report.AggregateOfferedBytes = counters.bytes;
        _report.AggregateLargePayloadBytes = counters.largeBytes;
        _report.DistinctExecutionIdentities = counters.Item5;
        _report.OfferedRatePerSecond = wallStart.Elapsed.TotalSeconds <= 0
            ? 0
            : counters.acceptedCount / wallStart.Elapsed.TotalSeconds;
        _report.Retention = service.Statistics;

        // The three loss counters, taken from the product's own statistics rather than re-derived here, so
        // the report cannot disagree with the label the operator saw on the screen.
        _report.EvictedEvents = _report.Retention.Evicted;
        _report.JournalEvictedRows = _report.Retention.JournalEvicted;
        _report.JournalDroppedOffers = _report.Retention.QueueDropped;
        _report.JournalFailedOffers = _report.Retention.QueueFailed;

        var bridge = _services.GetService<ActivityCenterEventBridge>();
        _report.ProductionIngestedEvents = bridge?.TotalObserved ?? 0;

        _trace.Step(
            $"stream: complete; offered={counters.acceptedCount} distinctExecutions={counters.Item5} "
            + $"largestMessage={counters.largestBytes} bytes in {wallStart.Elapsed.TotalSeconds:N1}s");

        _report.AddCheckpoint(Measure("after stream, before drain"));
        _trace.Step("stream: after memory checkpoint");

        if (journal is not null && queue is not null)
        {
            _trace.Step("stream: before queue drain");
            await queue.DrainAsync(cancellationToken);
            _trace.Step($"stream: queue drained; written={queue.WrittenCount} dropped={queue.DroppedCount} "
                + $"failed={queue.FailedCount} pending={queue.PendingCount} balanced={queue.IsBalanced}");

            _trace.Step("stream: before journal CountAsync");
            _report.PersistedRows = await journal.CountAsync(cancellationToken);
            _report.JournalDroppedOffers = queue.DroppedCount;
            _report.JournalFailedOffers = queue.FailedCount;
            _trace.Step("stream: journal rows=" + _report.PersistedRows.ToString("N0", CultureInfo.InvariantCulture));

            // The exact accounting, checked rather than printed. A drain that returned while rows were still
            // pending would make every persisted count below a lower bound rather than a measurement.
            if (!queue.IsBalanced)
            {
                _report.Add(new ActivityCriterionResult(
                    "durable-journal",
                    "Already-saved activity events are product-persisted rows, not an in-memory prefill.",
                    ActivityCriterionOutcome.Fail,
                    $"the write queue did not settle: offered {queue.OfferedCount:N0}, written "
                    + $"{queue.WrittenCount:N0}, dropped {queue.DroppedCount:N0}, faulted "
                    + $"{queue.FailedCount:N0}, still pending {queue.PendingCount:N0}."));
            }
        }

        // The in-memory window peak, for the memory gate. Reported rather than assumed: a run that grew the
        // window to hold the whole stream would show up here, and the gate fails on it.
        _report.WindowStayedWithinCapacity = service.Statistics.Retained;
        _trace.Step($"stream: window holds {service.Statistics.Retained} of a bound {service.Capacity}");

        // Let the dispatcher drain its rate limit so the last refresh actually happens.
        await ActivityWindowCapture.SettleAsync();
        _trace.Step($"stream: first settle took {ActivityWindowCapture.LastSettleMilliseconds:N0} ms");
        await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        await ActivityWindowCapture.SettleAsync();
        _trace.Step($"stream: second settle took {ActivityWindowCapture.LastSettleMilliseconds:N0} ms");
        _trace.Step("stream: after final settle");
    }

    /// <summary>
    /// Drives the shipped Activity Center controls while the journal is large: search, time-window filter,
    /// selection and the detail hand-off. Each action is timed with the same monotonic clock the latency
    /// recorder uses, so the reported dispatcher interaction latency is a real measurement.
    /// </summary>
    private async Task ExerciseUiAsync(CancellationToken cancellationToken)
    {
        await ActivityWindowCapture.SettleAsync();

        // Everything below runs on the UI thread: the walk's own continuations are on the thread pool, and
        // mutating a bound control or reading a live view-model property from there is both incorrect WPF
        // and the reason a driver can report a latency the product would never actually experience.
        // Lookups are scoped to the one Activity Center view, because the shell also hosts a command
        // palette with a control of the same name.
        _trace.Step("ui: reading the shipped control tree on the UI thread");
        await ActivityWindowCapture.OnUiAsync(() =>
        {
            var center = ActivityUiProbe.RequireActivityCenter<ActivityCenterView>(_window);
            var list = ActivityUiProbe.Find<System.Windows.Controls.ListBox>(center, "EventsList");
            _report.DisplayedRows = list?.Items.Count ?? 0;
            _report.OverflowLabelSeen = ReadOverflowLabel(center);
        });

        _trace.Step($"ui: displayedRows={_report.DisplayedRows} overflowLabel=\"{_report.OverflowLabelSeen}\"");
        await ActivityWindowCapture.SettleAsync();

        if (await ActivityWindowCapture.OnUiAsync(
                () => ActivityUiProbe.Find<System.Windows.Controls.TextBox>(
                    ActivityUiProbe.RequireActivityCenter<ActivityCenterView>(_window),
                    "SearchBox") is not null))
        {
            await TimeActionAsync("type a search query in the shipped search box", () =>
            {
                var center = ActivityUiProbe.RequireActivityCenter<ActivityCenterView>(_window);
                var searchBox = ActivityUiProbe.Require<System.Windows.Controls.TextBox>(center, "SearchBox");
                ActivityUiProbe.SetSearchText(searchBox, "execution load-exec-03");
                return "typed 'execution load-exec-03'";
            }, () =>
                $"filtered to {_activity.FilteredCount:N0} of {_activity.TotalCount:N0} "
                + $"(query \"{_activity.SearchQuery}\", {(_activity.IsLoading ? "still loading" : "settled")})");

            await TimeActionAsync("clear the search query", () =>
            {
                var center = ActivityUiProbe.RequireActivityCenter<ActivityCenterView>(_window);
                var searchBox = ActivityUiProbe.Require<System.Windows.Controls.TextBox>(center, "SearchBox");
                ActivityUiProbe.SetSearchText(searchBox, string.Empty);
                return "cleared the search box";
            }, () =>
                $"back to {_activity.FilteredCount:N0} of {_activity.TotalCount:N0} "
                + $"(query \"{_activity.SearchQuery}\", {(_activity.IsLoading ? "still loading" : "settled")})");
        }

        await TimeActionAsync("narrow the time-window filter", () =>
        {
            var center = ActivityUiProbe.RequireActivityCenter<ActivityCenterView>(_window);
            var timeRange = ActivityUiProbe.Find<System.Windows.Controls.ComboBox>(center, "TimeRangeComboBox");

            if (timeRange is null || timeRange.Items.Count == 0)
            {
                return "NOT PERFORMED: no time-window combo on the shown screen";
            }

            timeRange.SelectedIndex = 0;
            System.Windows.Data.BindingOperations
                .GetBindingExpression(timeRange, System.Windows.Controls.ComboBox.SelectedItemProperty)
                ?.UpdateSource();
            return "selected the first time window";
        }, () =>
            $"time range '{_activity.SelectedTimeRangeOption.Display}' gives "
            + $"{_activity.FilteredCount:N0} of {_activity.TotalCount:N0}");

        if (_activity.HasNextPage)
        {
            await TimeActionAsync("page forward with the shipped pager", () =>
            {
                var center = ActivityUiProbe.RequireActivityCenter<ActivityCenterView>(_window);
                var nextPage = ActivityUiProbe.Find<System.Windows.Controls.Button>(center, "NextPageButton");

                if (nextPage is null)
                {
                    return "NOT PERFORMED: no pager button on the shown screen";
                }

                ActivityUiProbe.Invoke(nextPage);
                return "pressed next page";
            }, () =>
                $"page {_activity.CurrentPage} of {_activity.PageCount} showing {_activity.Events.Count:N0} rows "
                + $"({_activity.FilteredCount:N0} matched)");
        }

        if (_activity.HasPreviousPage)
        {
            await TimeActionAsync("return to the first page", () =>
            {
                var center = ActivityUiProbe.RequireActivityCenter<ActivityCenterView>(_window);
                var firstPage = ActivityUiProbe.Find<System.Windows.Controls.Button>(center, "FirstPageButton");

                if (firstPage is not null)
                {
                    ActivityUiProbe.Invoke(firstPage);
                }
                else
                {
                    return "NOT PERFORMED: no first-page button on the shown screen";
                }

                return "returned to the first page";
            }, () =>
                $"page {_activity.CurrentPage} of {_activity.PageCount} showing {_activity.Events.Count:N0} rows");
        }

        await TimeActionAsync("select a row and read its detail", () =>
        {
            var item = _activity.Events.FirstOrDefault();

            if (item is null)
            {
                return "NOT PERFORMED: no row to select";
            }

            _activity.SelectedEvent = item;
            return $"selected {item.Id}; diff open: {_activity.IsDiffViewerOpen}; "
                + $"detail pane bound: {_activity.HasSelection}";
        });

        await TimeActionAsync("press the shipped refresh button", () =>
        {
            var center = ActivityUiProbe.RequireActivityCenter<ActivityCenterView>(_window);
            var refresh = ActivityUiProbe.Find<System.Windows.Controls.Button>(center, "RefreshButton");

            if (refresh is null)
            {
                return "NOT PERFORMED: no refresh button on the shown screen";
            }

            ActivityUiProbe.Invoke(refresh);
            return "pressed the shipped refresh button";
        }, () =>
            $"{_activity.TotalCount:N0} events reported by the product; "
            + $"{_activity.RefreshCount:N0} list rebuilds, {(_activity.IsLoading ? "still loading" : "settled")}");

        _report.AddCheckpoint(Measure("after UI interaction"));
    }

    private Task MeasureSearchAsync(IActivityCenterService service, CancellationToken cancellationToken)
    {
        var queries = new[]
        {
            "load-exec-01",
            "stage 3",
            "execution",
            "Reviewer",
            "[REDACTED]"
        };

        var latencies = new List<double>(queries.Length);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var watch = Stopwatch.StartNew();
            var result = service.Query(new ActivityFilterCriteria { SearchQuery = query, Limit = 50 });
            watch.Stop();

            latencies.Add(watch.Elapsed.TotalMilliseconds);
            counts[query] = result.FilteredCount;
            _report.SetFact($"search.{query}.matched", result.FilteredCount.ToString("N0", CultureInfo.InvariantCulture));
        }

        // Every event this driver offers carries "execution"; the product's own rows are not driver-shaped
        // (the Activity Center's startup record does not contain the word), so the exact expectation is the
        // number of driver events - not the retained total. The previous version of this check recorded the
        // counts and asserted nothing, so a search that could see only the newest 100 000 of 190 000 rows
        // produced evidence indistinguishable from one that could see all of them.
        var driverRows = (long)_options.Profile.SeedEvents + _options.Profile.OfferedEvents;
        var retained = service.Query(new ActivityFilterCriteria { Limit = 1 }).TotalCount;

        // Roles cycle through five values, so the free-text reviewer search must agree exactly with the
        // structured role filter. That agreement is a coherence check between two independent search paths.
        var reviewerRows = service.Query(new ActivityFilterCriteria
        {
            Roles = new[] { ActivityRoleNames.Reviewer },
            Limit = 1
        }).FilteredCount;

        // The decisive case is the overflow one: once the window has dropped events, a search that still
        // matches every offered event can only be resolving against the durable set.
        var windowOverflowed = _report.EvictedEvents > 0;

        _report.SearchMatchCounts = counts;
        _report.DriverEventsExpected = driverRows;
        _report.WindowCapacityDuringRun = _report.ComposedWindowCapacity;
        _report.DurableSearchCoversTheRetainedSet =
            driverRows > 0
            && counts["execution"] >= driverRows
            && counts["Reviewer"] == reviewerRows
            && reviewerRows > 0
            && (!windowOverflowed || driverRows > _report.ComposedWindowCapacity);

        // The oldest retained row must be findable. It is outside the in-memory window by construction, so
        // this is only true when the search resolves against the durable set.
        var oldest = service.Query(
            new ActivityFilterCriteria { SearchQuery = "seed:0000000", Limit = 5 });
        _report.OldestRetainedRowIsSearchable = oldest.FilteredCount >= 1;
        _report.SetFact(
            "search.oldestRetainedRowMatched",
            oldest.FilteredCount.ToString("N0", CultureInfo.InvariantCulture));

        _report.Add(new ActivityCriterionResult(
            "durable-search-covers-retained-set",
            "Free-text search resolves against every retained driver event, not only the newest slice of it: "
            + "the exact match count reaches past the in-memory window, the free-text and structured role "
            + "filters agree, and the oldest saved row is still findable.",
            _report.DurableSearchCoversTheRetainedSet && _report.OldestRetainedRowIsSearchable
                ? ActivityCriterionOutcome.Pass
                : ActivityCriterionOutcome.Fail,
            $"retained rows {retained:N0} of which {driverRows:N0} were offered by this driver "
            + $"(the window bound was {_report.ComposedWindowCapacity:N0} and it evicted "
            + $"{_report.EvictedEvents:N0}); "
            + string.Join(
                ", ",
                counts.Select(pair => $"'{pair.Key}' matched {pair.Value:N0}"))
            + $"; a search for the oldest saved row matched {oldest.FilteredCount:N0}."));

        _report.SearchLatency = ActivityLatencyStatistics.FromSamples(latencies, observedCount: latencies.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Probes every surface a secret could escape through: the raw free-text search, the durable query
    /// result objects at <em>both</em> ends of the retained set, the selected row and its detail, the
    /// rendered WPF text tree, the persisted journal file bytes and the reloaded journal rows. A value found
    /// on any surface fails the criterion.
    /// <para>
    /// The previous version inspected <c>Query()</c>, whose default limit is the newest 500 rows. The planted
    /// stream fixtures sit at sequences 3, 11 and 19, so 90 000 further events pushed every one of them out
    /// of the page that was inspected: the probe reported "not found" for a document it had never looked at.
    /// Here the oldest range is read explicitly through a deep durable page, so the planted secrets are
    /// inspected where they actually are.
    /// </para>
    /// </summary>
    private async Task ProbeSecretsAsync(
        IActivityCenterService service,
        IActivityEventJournal? journal,
        string databasePath,
        CancellationToken cancellationToken)
    {
        var retained = service.Query(new ActivityFilterCriteria { Limit = 1 }).TotalCount;
        var pageSize = 200;

        // Newest page and oldest page of the retained durable set. Between them they cover both ends of the
        // range the planted fixtures can occupy.
        var newestPage = service.QueryPage(new ActivityFilterCriteria { Limit = pageSize }, page: 1, pageSize: pageSize);
        var pageCount = newestPage.PageCount;
        var oldestPage = service.QueryPage(
            new ActivityFilterCriteria { Limit = pageSize },
            page: pageCount,
            pageSize: pageSize);

        _trace.Step(
            $"secrets: retained={retained} newestPage={newestPage.Items.Count} "
            + $"oldestPage(page {pageCount})={oldestPage.Items.Count}");

        foreach (var fixture in SecretFixtures.All)
        {
            foreach (var term in fixture.SearchTerms)
            {
                _report.AddSecretProbe(new SecretProbeResult(
                    fixture.Id,
                    "raw free-text search",
                    term,
                    service.Query(new ActivityFilterCriteria { SearchQuery = term, Limit = 10 }).Items.Count > 0,
                    "the durable full-text index must not make a secret findable"));

                _report.AddSecretProbe(new SecretProbeResult(
                    fixture.Id,
                    "query result objects (newest range)",
                    term,
                    ContainsTerm(
                        newestPage.Items.SelectMany(item => new[]
                        {
                            item.Title,
                            item.Description,
                            item.DiffText ?? string.Empty,
                            item.ArtifactContent ?? string.Empty
                        }),
                        term),
                    "ingestion redaction must mean no returned object holds the raw value"));

                _report.AddSecretProbe(new SecretProbeResult(
                    fixture.Id,
                    "query result objects (oldest range)",
                    term,
                    ContainsTerm(
                        oldestPage.Items.SelectMany(item => new[]
                        {
                            item.Title,
                            item.Description,
                            item.DiffText ?? string.Empty,
                            item.ArtifactContent ?? string.Empty
                        }),
                        term),
                    $"the oldest {oldestPage.Items.Count} retained rows, reached by durable paging "
                    + $"(page {pageCount} of {pageCount}); the previous probe only ever read the newest page"));
            }

            var selected = _activity.SelectedEvent?.Model
                ?? newestPage.Items.FirstOrDefault();

            if (selected is not null)
            {
                _report.AddSecretProbe(new SecretProbeResult(
                    fixture.Id,
                    "selected row and detail binding",
                    fixture.RawValues[0],
                    ContainsTerm(new[] { selected.Title, selected.Description, selected.DiffText ?? string.Empty }, fixture.RawValues[0]),
                    "the row bound to the screen must already be redacted"));
            }

            var texts = await ActivityWindowCapture.OnUiAsync(() => ActivityUiProbe.Texts(_window));

            _report.AddSecretProbe(new SecretProbeResult(
                fixture.Id,
                "rendered WPF text tree",
                fixture.RawValues[0],
                ContainsTerm(texts, fixture.RawValues[0]),
                $"{texts.Count:N0} rendered text elements were scanned"));
        }

        // The persisted bytes are the strongest assertion available: even direct SQL over the database
        // file must not turn up a raw secret. SQLite holds the file open, so the read has to share the
        // handle instead of demanding exclusive access the way File.ReadAllBytes would.
        //
        // Both the database and its write-ahead log are scanned. A WAL database keeps recently written
        // pages in the -wal sidecar until a checkpoint, so scanning only the main file would prove
        // nothing at all: it is routinely almost empty while the run is still live.
        _trace.Step("secrets: scanning persisted database bytes with a bounded buffer");
        var paths = new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists).ToArray();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            foreach (var value in await ScanSharedSecretsAsync(path, cancellationToken)) found.Add(value);
            _trace.Step($"secrets: scanned {Path.GetFileName(path)} ({new FileInfo(path).Length:N0} bytes)");
        }
        foreach (var fixture in SecretFixtures.All)
        {
            foreach (var raw in fixture.RawValues)
            {
                _report.AddSecretProbe(new SecretProbeResult(
                    fixture.Id, "persisted journal bytes (database + WAL)", raw, found.Contains(raw),
                    "scanned the live database, its write-ahead log and its shared-memory index"));
            }
        }

        if (journal is not null)
        {
            // Read the journal back through the same durable query the screen uses, at both ends of the
            // retained range. Reading only the newest rows is what let the previous run claim the oldest
            // planted fixtures were clean without having loaded them.
            var newest = service.QueryPage(new ActivityFilterCriteria { Limit = 200 }, page: 1, pageSize: 200);
            var oldest = service.QueryPage(
                new ActivityFilterCriteria { Limit = 200 },
                page: newest.PageCount,
                pageSize: 200);

            foreach (var fixture in SecretFixtures.All)
            {
                _report.AddSecretProbe(new SecretProbeResult(
                    fixture.Id,
                    "reloaded journal rows (newest and oldest ranges)",
                    fixture.RawValues[0],
                    ContainsTerm(
                        newest.Items.Concat(oldest.Items)
                            .SelectMany(item => new[] { item.Title, item.Description }),
                        fixture.RawValues[0]),
                    $"{newest.Items.Count + oldest.Items.Count:N0} durable rows re-inspected across both "
                    + "ends of the retained range"));
            }
        }
    }

    /// <summary>
    /// The two negative controls, because "nothing was lost" is only evidence if the thing that detects
    /// loss has been shown to fire.
    /// <para>
    /// Control one is a capacity+1 overflow: the bound is deliberately too small, and the run asserts a
    /// counted eviction plus a visible label. Control two is a deliberately too-small write queue in front
    /// of a journal that never commits: the run asserts that the loss is counted as <em>queue loss</em>, not
    /// folded into durable retention, and that the counters still add up. Without the second control a
    /// "journal held every row" claim is unfalsifiable, because the only run that ever exercised it was a
    /// run where nothing went wrong.
    /// </para>
    /// </summary>
    private async Task RunNegativeControlAsync()
    {
        if (_options.SkipNegativeControl)
        {
            _report.NegativeControlSummary = "skipped by request";
            return;
        }

        const int capacity = 500;

        var service = new ActivityCenterService(
            text => text,
            TimeProvider.System,
            capacity: capacity);
        var factory = new ActivityLoadEventFactory(1);
        var now = DateTimeOffset.UtcNow;

        for (var sequence = 0; sequence <= capacity; sequence++)
        {
            service.Append(factory.CreateSeedEvent(sequence, now));
        }

        var statistics = service.Statistics;
        var label = statistics.OverflowDisplay;
        var overflowed = statistics.HasOverflowed;
        var evictedExactlyOne = statistics.Evicted == 1;
        var retainedAtCapacity = statistics.Retained == capacity;

        _report.NegativeControlSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"capacity {capacity}, offered {capacity + 1}: retained {statistics.Retained:N0}, "
            + $"evicted {statistics.Evicted:N0}, label \"{label}\"");

        _report.Add(
            new ActivityCriterionResult(
                "overflow-negative-control",
                "A capacity+1 overflow is reported, not silent.",
                overflowed && evictedExactlyOne && retainedAtCapacity && label.Length > 0
                    ? ActivityCriterionOutcome.Pass
                    : ActivityCriterionOutcome.Fail,
                _report.NegativeControlSummary));

        await RunLossyQueueControlAsync();
    }

    /// <summary>
    /// The loss negative control: a two-deep queue in front of a journal that accepts nothing. Every offer
    /// beyond the depth has to be counted as dropped, the drain has to settle, and the durable-retention
    /// counter has to stay at zero - because reporting queue loss as "the retention rule removed it" is
    /// precisely the mislabelling this correction closes.
    /// </summary>
    private async Task RunLossyQueueControlAsync()
    {
        var journal = new NeverCommittingJournal();
        await using var queue = new ActivityJournalWriteQueue(journal, capacity: 2, batchSize: 2);
        var service = new ActivityCenterService(
            text => text,
            TimeProvider.System,
            capacity: 1_000,
            journalQueue: queue);

        var factory = new ActivityLoadEventFactory(1);
        var now = DateTimeOffset.UtcNow;

        for (var sequence = 0; sequence < 60; sequence++)
        {
            service.Append(factory.CreateSeedEvent(sequence, now));
        }

        // The drain must settle rather than hang, and settle with every offered row accounted for.
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                await queue.DrainAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _report.Add(new ActivityCriterionResult(
                    "loss-negative-control",
                    "A lossy write queue is counted as queue loss, never as durable retention.",
                    ActivityCriterionOutcome.Fail,
                    "the drain of a lossy queue did not settle within 30 seconds."));
                return;
            }
        }

        var statistics = service.Statistics;
        var facts = queue.ToFacts();
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"queue depth {queue.QueueCapacity}, offered {queue.OfferedCount:N0}, written "
            + $"{queue.WrittenCount:N0}, dropped {queue.DroppedCount:N0}, faulted {queue.FailedCount:N0}, "
            + $"balanced {queue.IsBalanced} (facts {string.Join(", ", facts.Select(pair => $"{pair.Key}={pair.Value}"))}); "
            + $"statistics label \"{statistics.OverflowDisplay}\"");

        _report.LossyQueueControlSummary = summary;
        _report.Add(
            new ActivityCriterionResult(
                "loss-negative-control",
                "A lossy or faulting write queue is counted as queue loss and never as durable retention, "
                + "and the counters still add up.",
                queue.FailedCount > 0
                && queue.WrittenCount == 0
                && queue.DroppedCount + queue.FailedCount == queue.OfferedCount
                && queue.IsBalanced
                && statistics.JournalEvicted == 0
                && statistics.QueueFailed == queue.FailedCount
                && statistics.IsDurablyLossy
                    ? ActivityCriterionOutcome.Pass
                    : ActivityCriterionOutcome.Fail,
                summary));
    }

    /// <summary>A durable journal that never commits anything, used only by the loss negative control.</summary>
    private sealed class NeverCommittingJournal : IActivityEventJournal
    {
        public Task AppendAsync(
            LLMWorkGUI.Application.Observability.ActivityEvent activityEvent,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the durable store is unavailable");

        public Task AppendRangeAsync(
            IReadOnlyList<LLMWorkGUI.Application.Observability.ActivityEvent> activityEvents,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the durable store is unavailable");

        public Task<IReadOnlyList<LLMWorkGUI.Application.Observability.ActivityEvent>> LoadNewestAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LLMWorkGUI.Application.Observability.ActivityEvent>>(
                Array.Empty<LLMWorkGUI.Application.Observability.ActivityEvent>());

        public Task<long> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

        public Task<ActivityJournalTrimResult> TrimAsync(
            int retentionLimit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActivityJournalTrimResult(0, 0, 0));

        public ActivityJournalPage QueryPage(ActivityJournalQuery query) =>
            new(0, 0, Array.Empty<LLMWorkGUI.Application.Observability.ActivityEvent>());
    }

    private async Task CaptureEvidenceAsync()
    {
        var directory = _options.ScreenshotDirectory;
        Directory.CreateDirectory(directory);

        _trace.Step("capture: rendering the shown window");
        var path = await ActivityWindowCapture.OnUiAsync(() =>
            ActivityWindowCapture.Capture(_window, directory, "activity-center-under-load"));
        _report.AddEvidence(path);
        _trace.Step("capture: wrote " + path);

        _trace.Step("capture: reading the rendered text tree");
        var textPath = Path.Combine(directory, "activity-center-text-tree.txt");
        var texts = await ActivityWindowCapture.OnUiAsync(() => ActivityUiProbe.Texts(_window));
        await File.WriteAllTextAsync(textPath, string.Join(Environment.NewLine, texts), Encoding.UTF8);
        _report.AddEvidence(textPath);
        _trace.Step($"capture: wrote {textPath} ({texts.Count} text elements)");

        _report.SetFact("screenshot.renderedTextElements", texts.Count.ToString("N0", CultureInfo.InvariantCulture));
        _report.AddCheckpoint(Measure("end of run, after full GC"));
    }

    private async Task DismissOnboardingAsync()
    {
        if (!_shell.IsOnboardingOpen)
        {
            _trace.Step("walk: onboarding was not open for this run");
            return;
        }

        // The shipped Dismiss control, not a view-model property. The onboarding overlay auto-opens on a
        // first run and sits on top of the Activity Center, so a load run that left it up would screenshot
        // a wizard instead of the screen it is measuring.
        var dismissed = await ActivityWindowCapture.OnUiAsync(() =>
        {
            var button = ActivityUiProbe.Find<
                System.Windows.Controls.Primitives.ButtonBase>(_window, "DismissOnboardingButton");

            if (button is null)
            {
                return "no dismiss control on the shown window";
            }

            ActivityUiProbe.Invoke(button);
            return "invoked the shipped dismiss control";
        });

        await ActivityWindowCapture.SettleAsync();

        _report.SetFact("ui.onboardingDismissed", _shell.IsOnboardingOpen ? "no" : "yes");
        _trace.Step($"walk: onboarding dismissal - {dismissed}; still open={_shell.IsOnboardingOpen}");

        if (_shell.IsOnboardingOpen)
        {
            _report.AddSecretProbe(new SecretProbeResult(
                "onboarding",
                "first-run overlay",
                "dismiss",
                _shell.IsOnboardingOpen,
                "the onboarding overlay occludes the Activity Center, so the visual evidence would not "
                + "show the screen under test"));
        }
    }

    private string ReadOverflowLabel(ActivityCenterView center) =>
        ActivityUiProbe.Find<System.Windows.Controls.TextBlock>(center, "OverflowText")?.Text ?? string.Empty;

    /// <summary>
    /// Performs one interaction on the UI thread and records how long it took.
    /// <para>
    /// <paramref name="describe"/> exists because the screen's query is no longer synchronous. Typing a
    /// query, changing a filter or paging now returns to the caller's thread before the durable query has
    /// run, so a note written inside <paramref name="action"/> would read the counts of the <em>previous</em>
    /// result and the report would claim a filter that was never applied. The describe callback is
    /// evaluated after the settle, on the UI thread, so the note describes what the screen settled on.
    /// </para>
    /// </summary>
    private async Task TimeActionAsync(string name, Func<string> action, Func<string>? describe = null)
    {
        _report.AddUiAction(await UiActionMeasurement.MeasureAsync(name, action, describe,
            callback => ActivityWindowCapture.OnUiAsync(callback),
            () => ActivityWindowCapture.SettleAsync(),
            exception => _trace.Step($"ui: action '{name}' could not be performed: {exception.Message}")));
    }

    private static bool ContainsTerm(IEnumerable<string?> haystack, string needle) =>
        haystack.Any(text => text is not null && text.Contains(needle, StringComparison.Ordinal));

    /// <summary>
    /// Reads a file that another handle already has open, as text. The default share mode of
    /// <c>File.ReadAllBytesAsync</c> demands exclusive access, which a live SQLite connection refuses, so
    /// the scan of the persisted journal has to open the file the way every other reader would.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ScanSharedSecretsAsync(string path, CancellationToken cancellationToken)
    {
        // All planted credentials are ASCII. Search their bytes directly, with a fixed overlap,
        // rather than allocating UTF-16 strings on the large-object heap for every file chunk.
        var values = SecretFixtures.AllRawValues;
        if (values.Any(value => value.Any(character => character > 127)))
            throw new InvalidOperationException("The byte scanner requires ASCII secret fixtures.");
        var patterns = values.Select(value => Encoding.ASCII.GetBytes(value.ToLowerInvariant())).ToArray();
        var overlap = patterns.Max(value => value.Length) - 1;
        var buffer = new byte[(1 << 16) + overlap];
        var found = new bool[patterns.Length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 16, useAsync: true);
        var tailCount = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(tailCount, 1 << 16), cancellationToken)
            .ConfigureAwait(false)) > 0)
        {
            var available = tailCount + read;
            for (var index = tailCount; index < available; index++)
                if (buffer[index] is >= (byte)'A' and <= (byte)'Z') buffer[index] += (byte)('a' - 'A');
            for (var index = 0; index < patterns.Length; index++)
                if (buffer.AsSpan(0, available).IndexOf(patterns[index]) >= 0) found[index] = true;
            tailCount = Math.Min(overlap, available);
            Buffer.BlockCopy(buffer, available - tailCount, buffer, 0, tailCount);
        }
        return values.Where((_, index) => found[index]).ToArray();
    }

    private static void WaitUntil(long dueTimestamp)
    {
        // Sleep for the bulk of the interval and spin only for the last fraction, so a 20 ms cadence costs
        // almost no CPU while still landing on the nominal schedule.
        const double SpinThresholdTicks = 0.004;

        while (true)
        {
            var remaining = dueTimestamp - Stopwatch.GetTimestamp();

            if (remaining <= 0)
            {
                return;
            }

            if (remaining / (double)Stopwatch.Frequency < SpinThresholdTicks)
            {
                Thread.SpinWait(64);
                continue;
            }

            Thread.Sleep(Math.Max(1, (int)(remaining * 1000d / Stopwatch.Frequency) - 1));
        }
    }

    private static MemoryCheckpoint Measure(string name)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        using var process = Process.GetCurrentProcess();
        process.Refresh();

        return new MemoryCheckpoint(
            name,
            GC.GetTotalMemory(forceFullCollection: true),
            process.PrivateMemorySize64,
            process.WorkingSet64);
    }

    /// <summary>
    /// Hands the UI thread back so the coalesced refreshes scheduled by the appends just made can run.
    /// <para>
    /// This exists because a load driver that runs its own generation loop <em>on</em> the dispatcher
    /// cannot observe UI latency at all: the queued refresh cannot run until the loop yields, so the
    /// screen appears frozen for exactly as long as the loop takes, and the measured latency is the
    /// producer's own blocking rather than the product's responsiveness. Production appends arrive on
    /// execution and health threads, not the UI thread, so the stream runs on the thread pool and the
    /// dispatcher stays free to do its work.
    /// </para>
    /// </summary>
    private static async Task YieldToUiAsync()
    {
        var dispatcher = ActivityWindowCapture.UiDispatcher;

        if (dispatcher.CheckAccess())
        {
            // Already on the UI thread: run a nested frame so pending work is processed, then continue.
            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            return;
        }

        await dispatcher
            .InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background)
            .Task
            .ConfigureAwait(false);
    }
}
