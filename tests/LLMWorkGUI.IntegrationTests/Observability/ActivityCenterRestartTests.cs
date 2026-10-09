using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Observability;

/// <summary>
/// The restart claim, tested as a restart.
/// <para>
/// The previous evidence for "100 000 already-saved events" was a fresh host reading rows out of SQLite.
/// That is a journal read, not a product reload: nothing bound those rows to an Activity Center or to a
/// screen, so a real process restart left the shipped Activity Center empty while the rows were still
/// sitting on disk. These tests close the difference by composing two real hosts over one application-data
/// root, writing through the production ingestion boundary in the first, and asserting in the second that
/// the service, the durable counts, the search and the paging all tell the same truthful story.
/// </para>
/// <para>
/// It runs against the real <c>HostBootstrapper</c> rather than a hand-built service, because the thing
/// under test is the startup path itself: if <c>InitializeAsync</c> stopped restoring the journal, every
/// assertion here would fail while a service-level unit test would keep passing.
/// </para>
/// </summary>
public sealed partial class ActivityCenterRestartTests : IDisposable
{
    /// <summary>
    /// Four hundred durable events overflow this fixture's explicit 100-event window, exercising restart,
    /// paging and retention through HostBootstrapper. This bounded substitute is not evidence of a
    /// 100,000-event production-capacity restart; separate search/load controls cover their stated bounds.
    /// </summary>
    private const int SeedEvents = 400;

    [Fact]
    public async Task DurablePageKeepsRedactedLivePayloadWithoutAttachingItToANewerObservation()
    {
        using var host = BuildHost();
        await host.StartAsync();
        await HostBootstrapper.InitializeAsync(host);
        var journal = host.Services.GetRequiredService<IActivityEventJournal>();
        var queue = host.Services.GetRequiredService<ActivityJournalWriteQueue>();
        var service = new ActivityCenterService(text => text.Replace("SECRET", "[redacted]"),
            journal: journal, journalQueue: queue);
        service.Append(new ActivityEvent("payload", Now, ActivityEventKind.System, ActivityRoleNames.System,
            ActivityEventState.Completed, ActivityEventSource.Synthetic, "Fixture", "Details",
            diffText: "+SECRET", artifactName: "example.txt", artifactContent: "SECRET"));
        await queue.DrainAsync();
        var displayed = Assert.Single(service.QueryPage().Items);
        Assert.Equal(1, service.QueryPage().Retention.JournalRetained);
        Assert.Equal("+[redacted]", displayed.DiffText);
        Assert.Equal("[redacted]", displayed.ArtifactContent);
        Assert.Null(Assert.Single(journal.QueryPage(new ActivityJournalQuery(limit: 10)).Items).DiffText);
        var newerMemory = new ActivityCenterService(text => text, journal: journal);
        newerMemory.Append(new ActivityEvent("payload", Now.AddMinutes(1), ActivityEventKind.System,
            ActivityRoleNames.System, ActivityEventState.Warning, ActivityEventSource.Synthetic, "Updated", "New details",
            diffText: "+different observation", artifactContent: "new body"));
        var persisted = Assert.Single(newerMemory.QueryPage().Items);
        Assert.Equal("Fixture", persisted.Title);
        Assert.Null(persisted.DiffText);
        Assert.Null(persisted.ArtifactContent);
    }

    private const int WindowCapacity = 100;

    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _appData;

    public ActivityCenterRestartTests()
    {
        _appData = Path.Combine(
            Path.GetTempPath(),
            "llmworkgui-activity-restart-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_appData);
    }

    public void Dispose()
    {
        TestSqlitePool.Clear(new LLMWorkGUI.Infrastructure.Data.SqliteConnectionFactory(
            Path.Combine(_appData, "llmworkgui.db")));
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
    public async Task ARestartedHostRebuildsTheShippedActivityCenterFromTheDurableJournal()
    {
        var seeded = SeedEvents;

        using (var first = BuildHost())
        {
            await first.StartAsync();
            await HostBootstrapper.InitializeAsync(first);

            var activity = first.Services.GetRequiredService<IActivityCenterService>();
            var queue = first.Services.GetRequiredService<ActivityJournalWriteQueue>();

            for (var sequence = 0; sequence < seeded; sequence++)
            {
                activity.Append(Event($"seed:{sequence:D7}", Now));
            }

            await queue.DrainAsync();

            // The drain is the product's own promise that everything offered is either committed or
            // explicitly counted. A run that reports success without it is reporting a race.
            Assert.True(queue.IsBalanced);
            Assert.False(queue.IsLossy);
            Assert.Equal(seeded, queue.WrittenCount);
        }

        // A brand new host over the same application-data root: no shared objects, no shared memory, only
        // the database file survives. This is the process restart the criterion is about.
        using var second = BuildHost();
        await second.StartAsync();

        var load = await HostBootstrapper.RestoreActivityCenterAsync(second);

        Assert.NotNull(load);
        Assert.Equal(seeded, load!.RetainedRows);
        Assert.Equal(0, load.TrimmedRows);
        Assert.Equal(WindowCapacity, load.Reloaded);
        Assert.Equal(WindowCapacity, load.WindowCapacity);
        Assert.NotNull(load.Queue);
        Assert.True(load.Queue!.IsBalanced);

        var reloaded = second.Services.GetRequiredService<IActivityCenterService>();

        // The window is bounded, and bounded to the shipped bound rather than to whatever the profile wants.
        Assert.Equal(WindowCapacity, reloaded.Capacity);
        Assert.Equal(WindowCapacity, reloaded.TotalCount);
        Assert.Equal(seeded, reloaded.Statistics.JournalRetained);
        Assert.Equal(WindowCapacity, reloaded.Statistics.Retained);

        // The screen's own numbers: the exact retained total, and an exact match count over every retained
        // row rather than over the 100 rows the window happens to hold.
        var browsed = reloaded.QueryPage(pageSize: 10);
        Assert.Equal(seeded, browsed.TotalCount);
        Assert.Equal(seeded, browsed.FilteredCount);
        Assert.Equal(10, browsed.Items.Count);

        var searched = reloaded.QueryPage(
            new ActivityFilterCriteria { SearchQuery = "execution", Limit = 10 },
            pageSize: 10);

        Assert.Equal(seeded, searched.TotalCount);
        Assert.Equal(seeded, searched.FilteredCount);

        // The oldest retained row is reachable by search and by paging, even though the window only holds
        // the newest ones. This is the row the previous in-memory-only search could never see.
        var oldestSearch = reloaded.QueryPage(
            new ActivityFilterCriteria { SearchQuery = "seed:0000000", Limit = 5 },
            pageSize: 5);

        Assert.Equal(1, oldestSearch.FilteredCount);
        Assert.Equal("seed:0000000", Assert.Single(oldestSearch.Items).Id);

        var lastPage = reloaded.QueryPage(page: 4, pageSize: 100);
        Assert.Equal(400, lastPage.FilteredCount);
        Assert.Equal(100, lastPage.Items.Count);
        Assert.Equal("seed:0000000", lastPage.Items[^1].Id);

        // And the newest page is the newest row, exactly as a screen that just started would show it.
        Assert.Equal($"seed:{seeded - 1:D7}", browsed.Items[0].Id);
    }

    [Fact]
    public async Task ASecondRestartWithoutNewWritesRestoresTheSameHistory()
    {
        using (var first = BuildHost())
        {
            await first.StartAsync();
            await HostBootstrapper.InitializeAsync(first);

            var activity = first.Services.GetRequiredService<IActivityCenterService>();

            for (var sequence = 0; sequence < 120; sequence++)
            {
                activity.Append(Event($"seed:{sequence:D7}", Now));
            }

            await first.Services.GetRequiredService<ActivityJournalWriteQueue>().DrainAsync();
        }

        for (var restart = 1; restart <= 2; restart++)
        {
            using var host = BuildHost();
            await host.StartAsync();

            var load = await HostBootstrapper.RestoreActivityCenterAsync(host);

            Assert.NotNull(load);
            Assert.Equal(120, load!.RetainedRows);

            // Replaying the window must not write the rows it just read back, so a restart loop cannot
            // inflate the journal or trip a duplicate-id insert.
            Assert.Equal(0, load.Queue!.Written);
            Assert.Equal(0, load.Queue.Offered);
            Assert.True(load.Queue!.IsBalanced);

            var activity = host.Services.GetRequiredService<IActivityCenterService>();
            Assert.Equal(120, activity.QueryPage(pageSize: 5).TotalCount);
        }
    }

    /// <summary>
    /// The retention rule is a product behaviour, not an aspiration: it runs at startup, it removes the
    /// oldest rows, it is counted, and it is visible on the screen's overflow accounting.
    /// </summary>
    [Fact]
    public async Task StartupRetentionTrimsTheOldestRowsAndCountsThemOnTheOverflowLabel()
    {
        const int RetentionLimit = 150;

        using (var first = BuildHost(retentionLimit: RetentionLimit))
        {
            await first.StartAsync();
            await HostBootstrapper.InitializeAsync(first);

            var seeding = first.Services.GetRequiredService<IActivityCenterService>();

            for (var sequence = 0; sequence < SeedEvents; sequence++)
            {
                seeding.Append(Event($"seed:{sequence:D7}", Now));
            }

            await first.Services.GetRequiredService<ActivityJournalWriteQueue>().DrainAsync();
        }

        using var second = BuildHost(retentionLimit: RetentionLimit);
        await second.StartAsync();

        var load = await HostBootstrapper.RestoreActivityCenterAsync(second);

        Assert.NotNull(load);
        Assert.Equal(SeedEvents - RetentionLimit, load!.TrimmedRows);
        Assert.Equal(RetentionLimit, load.RetainedRows);

        var trimmed = second.Services.GetRequiredService<IActivityCenterService>();
        var statistics = trimmed.Statistics;

        Assert.Equal(RetentionLimit, statistics.JournalRetained);
        Assert.Equal(SeedEvents - RetentionLimit, statistics.JournalEvicted);

        // The removal is reported on the label the operator sees, and it is reported as retention - not as
        // an in-memory eviction and not as queue loss.
        var label = statistics.OverflowDisplay;
        Assert.Contains(
            (SeedEvents - RetentionLimit).ToString(System.Globalization.CultureInfo.InvariantCulture),
            label,
            StringComparison.Ordinal);
        Assert.Equal("0", statistics.ToFacts()["queueDropped"]);
        Assert.Equal("0", statistics.ToFacts()["queueFailed"]);
        Assert.Equal("0", statistics.ToFacts()["evicted"]);

        // The trimmed rows are gone from both the journal and its search: the retained set really is 150.
        Assert.Equal(RetentionLimit, trimmed.QueryPage(pageSize: 5).TotalCount);
        Assert.Equal(
            0,
            trimmed.QueryPage(new ActivityFilterCriteria { SearchQuery = "seed:0000000", Limit = 5 }, pageSize: 5)
                .FilteredCount);
    }

    /// <summary>
    /// The production producers, on a real host. Until the bridge existed nothing in <c>src</c> ever called
    /// <c>Append</c>, so this asserts that a real health transition - not a driver - lands in the journal.
    /// </summary>
    [Fact]
    public async Task AProductHealthTransitionReachesTheJournalThroughTheIngestionBoundary()
    {
        using var host = BuildHost();
        await host.StartAsync();
        await HostBootstrapper.InitializeAsync(host);

        var bridge = host.Services.GetRequiredService<
            LLMWorkGUI.Infrastructure.Observability.ActivityCenterEventBridge>();

        Assert.NotEmpty(bridge.Sources);

        var health = host.Services.GetRequiredService<LLMWorkGUI.Application.Health.IHealthCenterService>();

        await health.ReportFailureAsync(
            LLMWorkGUI.Application.Health.HealthScope.ForBackend("cursor-agent-acp"),
            LLMWorkGUI.Domain.Enums.HealthErrorClass.NetworkOrTimeout,
            "connection refused");

        await host.Services.GetRequiredService<ActivityJournalWriteQueue>().DrainAsync();

        Assert.Equal(1, bridge.ObservedHealthTransitions);

        var activity = host.Services.GetRequiredService<IActivityCenterService>();
        var found = activity.QueryPage(
            new ActivityFilterCriteria { SearchQuery = "cursor-agent-acp", Limit = 10 },
            pageSize: 10);

        Assert.True(found.FilteredCount >= 1, "a real health transition never reached the Activity Center.");

        var healthEvent = found.Items.First(item => item.Kind == ActivityEventKind.Health);

        // The state on the row is the product's own mapping of the health state, not a guess in the test.
        var snapshot = await health.GetSnapshotAsync(
            LLMWorkGUI.Application.Health.HealthScope.ForBackend("cursor-agent-acp"));

        Assert.Equal(
            ActivityEvent.MapHealthState(snapshot.State),
            healthEvent.State);
        Assert.Contains("NetworkOrTimeout", healthEvent.Description, StringComparison.Ordinal);

        var journal = host.Services.GetRequiredService<IActivityEventJournal>();
        Assert.True(
            await journal.CountAsync() >= found.FilteredCount,
            "the journal holds fewer rows than the screen shows.");
    }

    [Fact]
    public async Task ConcurrentJournalReads_SeeEveryAlreadyCommittedWrite()
    {
        using var host = BuildHost();
        await host.StartAsync();
        await HostBootstrapper.InitializeAsync(host);
        var journal = host.Services.GetRequiredService<IActivityEventJournal>();
        var committed = 0;
        var writer = Task.Run(async () =>
        {
            for (var index = 0; index < 200; index++)
            {
                await journal.AppendAsync(Event($"concurrent-{index:D4}", Now.AddSeconds(index)));
                System.Threading.Volatile.Write(ref committed, index + 1);
                await Task.Delay(2);
            }
        });
        var observations = new List<(int Committed, long Read)>();
        while (!writer.IsCompleted)
        {
            var before = System.Threading.Volatile.Read(ref committed);
            var page = journal.QueryPage(new ActivityJournalQuery(limit: 5));
            observations.Add((before, page.TotalRows));
            await Task.Delay(1);
        }
        await writer;
        Assert.NotEmpty(observations);
        Assert.All(observations, observation => Assert.True(observation.Read >= observation.Committed,
            $"Read {observation.Read} rows after {observation.Committed} were committed."));
        Assert.Equal(200, journal.QueryPage(new ActivityJournalQuery(limit: 5)).TotalRows);
    }

    private IHost BuildHost(int retentionLimit = ActivityJournalOptions.DefaultRetentionLimit) =>
        HostBootstrapper
            .CreateHostBuilder(appDataDirectory: _appData)
            .ConfigureServices((_, services) =>
            {
                // The shipped Activity Center composition, with one deliberate substitution: the window
                // bound. What is under test is the startup path, the durable accounting and the
                // window-plus-durable design, not the size of the window itself.
                services.AddActivityCenter(WindowCapacity);
                services.AddSingleton(new ActivityJournalOptions { RetentionLimit = retentionLimit });

                services.AddSingleton<LLMWorkGUI.Infrastructure.Observability.ActivityCenterEventBridge>(
                    serviceProvider => new LLMWorkGUI.Infrastructure.Observability.ActivityCenterEventBridge(
                        serviceProvider.GetRequiredService<IActivityCenterService>(),
                        new object?[]
                        {
                            serviceProvider.GetService<
                                LLMWorkGUI.Application.Health.IHealthCenterService>()
                        },
                        serviceProvider.GetService<TimeProvider>()));
            })
            .Build();

    private static ActivityEvent Event(string id, DateTimeOffset occurredAt) => new(
        id,
        occurredAt,
        ActivityEventKind.Execution,
        ActivityRoleNames.Coder,
        ActivityEventState.Running,
        ActivityEventSource.Native,
        $"execution observation {id}",
        $"stage {(id.Length % 7)} payload for {id}",
        sessionId: "session-1",
        executionId: $"exec-{id[^1]}");
}
