using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Observability;

public sealed class ActivityReloadAccountingRegressionTests
{
    [Fact]
    public async Task StartupReloadCountsRehydratedOffersWithoutRewritingTheJournal()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var journal = new SqliteActivityEventJournal(database.Factory);
        await journal.AppendRangeAsync([Event("old"), Event("new")]);
        await using var queue = new ActivityJournalWriteQueue(journal);
        var service = new ActivityCenterService(text => text, journal: journal, journalQueue: queue);
        var notices = new List<ActivityEventAppendedEventArgs>();
        service.Appended += (_, notice) => notices.Add(notice);

        var load = await service.LoadAsync();

        Assert.Equal(2, load.Reloaded);
        Assert.Equal(2, service.RehydratedCount);
        Assert.Equal(new[] { 1L, 2L }, notices.Select(notice => notice.OfferedCount));
        Assert.Equal(2, service.Statistics.Offered);
        Assert.Equal(2, service.Statistics.Retained);
        Assert.False(service.Statistics.IsLossy);
        Assert.Equal(0, queue.OfferedCount);
        Assert.Equal(0, queue.WrittenCount);
        Assert.Equal(2, await journal.CountAsync());

        service.Append(Event("live"));
        await queue.DrainAsync();
        Assert.Equal(3, service.Statistics.Offered);
        Assert.Equal(3, service.Statistics.Retained);
        Assert.False(service.Statistics.IsLossy);
        Assert.Equal(1, queue.OfferedCount);
        Assert.Equal(1, queue.WrittenCount);
        Assert.Equal(3, await journal.CountAsync());
    }

    private static ActivityEvent Event(string id) => new(id, DateTimeOffset.UnixEpoch,
        ActivityEventKind.System, ActivityRoleNames.System, ActivityEventState.Completed,
        ActivityEventSource.Synthetic, "Fixture", "Metadata");
}
