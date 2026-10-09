using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// The shipped screen after a process restart.
/// <para>
/// The R1 review rejected "100 000 saved and reloaded events" because a fresh host read the rows while the
/// Activity Center the operator was looking at stayed bound to whatever the previous process had in memory.
/// These tests close that gap at the screen: a second ingestion boundary over the same database, refilled
/// by the production startup load, is bound to a real <see cref="ActivityCenterViewModel"/>, and the counts,
/// the search and the materialized rows are read off that view model - not off a service that happens to
/// agree.
/// </para>
/// <para>
/// The window is deliberately small and the journal deliberately larger, because that is the whole design:
/// the screen shows a bounded working set while the exact totals and the search cover everything the
/// retention rule keeps.
/// </para>
/// </summary>
public sealed class ActivityCenterDurableScreenTests : IDisposable
{
    private const int SeedEvents = 600;

    private const int WindowCapacity = 50;

    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _appData;
    private readonly SqliteConnectionFactory _factory;

    public ActivityCenterDurableScreenTests()
    {
        _appData = Path.Combine(
            Path.GetTempPath(),
            "llmworkgui-durable-screen-tests",
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
    public async Task ARestartedScreenShowsTheSavedHistoryAndItsExactCounts()
    {
        await MigrateAsync();

        await using (var writer = new ActivityJournalWriteQueue(new SqliteActivityEventJournal(_factory)))
        {
            var service = Compose(writer, new SqliteActivityEventJournal(_factory));

            for (var sequence = 0; sequence < SeedEvents; sequence++)
            {
                service.Append(Event($"seed:{sequence:D7}", Now));
            }

            await writer.DrainAsync();
            Assert.True(writer.IsBalanced);
            Assert.Equal(SeedEvents, writer.WrittenCount);
        }

        // A brand new ingestion boundary and a brand new journal over the same file: nothing of the first
        // process survives except the rows on disk.
        var journal = new SqliteActivityEventJournal(_factory);
        await using var queue = new ActivityJournalWriteQueue(journal);
        var restarted = Compose(queue, journal);

        Assert.Equal(0, restarted.TotalCount);

        var load = await restarted.LoadAsync();

        Assert.Equal(SeedEvents, load.RetainedRows);
        Assert.Equal(WindowCapacity, load.Reloaded);

        using var screen = new ActivityCenterViewModel(
            restarted,
            new DiffArtifactViewerViewModel(),
            TimeProvider.System,
            scheduler: ImmediateActivityUiScheduler.Instance);

        // The screen's own numbers, read off the view model the shell binds.
        Assert.Equal(SeedEvents, screen.TotalCount);
        Assert.Equal(SeedEvents, screen.FilteredCount);
        Assert.Contains(
            SeedEvents.ToString(System.Globalization.CultureInfo.InvariantCulture),
            screen.TotalCountDisplay,
            StringComparison.Ordinal);
        Assert.Contains(
            SeedEvents.ToString(System.Globalization.CultureInfo.InvariantCulture),
            screen.FilteredCountDisplay,
            StringComparison.Ordinal);
        Assert.False(screen.IsEmpty);
        Assert.True(screen.HasEvents);

        // One page of rows, not the whole history.
        Assert.Equal(ActivityCenterViewModel.DefaultPageSize, screen.Events.Count);
        Assert.Equal(SeedEvents / ActivityCenterViewModel.DefaultPageSize, screen.PageCount);
        Assert.Equal($"seed:{SeedEvents - 1:D7}", screen.Events[0].Id);

        // The oldest saved row is reachable by search on the restarted screen, and by paging.
        screen.SearchQuery = "seed:0000000";
        Assert.Equal(1, screen.FilteredCount);
        Assert.Equal("seed:0000000", Assert.Single(screen.Events).Id);

        screen.SearchQuery = "execution";
        Assert.Equal(SeedEvents, screen.FilteredCount);

        // A page far past the window: the pager has to reach rows the in-memory window never held.
        screen.SearchQuery = string.Empty;
        var lastPage = SeedEvents / ActivityCenterViewModel.DefaultPageSize;
        screen.GoToPage(lastPage);

        Assert.Equal(lastPage, screen.CurrentPage);
        Assert.Equal(SeedEvents, screen.FilteredCount);
        Assert.Equal("seed:0000000", screen.Events[^1].Id);
    }

    [Fact]
    public async Task ANotificationsFreeReloadStillRefreshesTheScreenItself()
    {
        await MigrateAsync();

        await using var queue = new ActivityJournalWriteQueue(new SqliteActivityEventJournal(_factory));
        var service = Compose(queue, new SqliteActivityEventJournal(_factory));

        for (var sequence = 0; sequence < 200; sequence++)
        {
            service.Append(Event($"seed:{sequence:D7}", Now));
        }

        await queue.DrainAsync();

        // The screen is bound first, exactly as it is on a real start: composed when the window opens,
        // and then told about the restored history by the startup reload rather than by an operator.
        using var screen = new ActivityCenterViewModel(
            service,
            new DiffArtifactViewerViewModel(),
            TimeProvider.System,
            scheduler: ImmediateActivityUiScheduler.Instance);

        var refreshesBefore = screen.RefreshCount;

        await service.LoadAsync();
        screen.Refresh();

        Assert.True(screen.RefreshCount > refreshesBefore);
        Assert.Equal(200, screen.TotalCount);
        Assert.Equal(ActivityCenterViewModel.DefaultPageSize, screen.Events.Count);
    }

    private ActivityCenterService Compose(ActivityJournalWriteQueue? queue, IActivityEventJournal? journal) =>
        new(
            text => text,
            TimeProvider.System,
            new EventSearchIndex(text => text, WindowCapacity),
            WindowCapacity,
            queue,
            journal,
            retentionLimit: ActivityJournalOptions.DefaultRetentionLimit);

    private async Task MigrateAsync() => await new DatabaseMigrator(_factory).MigrateAsync();

    private static ActivityEvent Event(string id, DateTimeOffset occurredAt) => new(
        id,
        occurredAt,
        ActivityEventKind.Execution,
        ActivityRoleNames.Coder,
        ActivityEventState.Running,
        ActivityEventSource.Native,
        $"execution observation {id}",
        $"stage payload for {id}",
        sessionId: "session-1",
        executionId: $"exec-{id[^1]}");
}
