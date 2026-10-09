using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.OpenCode;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Observability;

public sealed class ActivityJournalReplayRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidRowSummaryFailureCannotDiscardValidDurableReplay(bool observerFails)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await database.SeedSessionAsync();
        await database.SeedExecutionAsync("execution-1", state: "Succeeded", endedAt: DateTimeOffset.UtcNow);
        await database.InsertExecutionEventAsync("valid", "execution-1", 0, DateTimeOffset.UnixEpoch,
            "OpenCodeTerminal", "{\"state\":\"Succeeded\",\"reason\":\"Synthetic\"}");
        await database.InsertExecutionEventAsync("invalid", "execution-1", 1, DateTimeOffset.UnixEpoch,
            "OpenCodeTerminal", "{broken");
        var activity = new ActivityCenterService(text => observerFails && text.StartsWith("Некоторые события OpenCode", StringComparison.Ordinal)
            ? throw new IOException("synthetic-private-observer-failure") : text);
        using var guard = new ApplicationInstanceGuard(Path.Combine(database.Root, "instance"));
        var recovery = new SqliteOpenCodeJournalRecoveryService(database.Factory, guard, TimeProvider.System, activity);
        Assert.Equal(1, await recovery.ReplayActivityAsync());
        Assert.Contains(activity.Snapshot(), row => row.Id == "opencode-journal:valid");
        await using var connection = await database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ExecutionEvents";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("OpenCode", true)]
    [InlineData("NativeGateway", true)]
    [InlineData("OpenCode", false)]
    [InlineData("NativeGateway", false)]
    public async Task RestartReplayAndRepeatedReplayPreserveStableRowsAndNewSibling(string backend, bool alreadyPersisted)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await database.SeedSessionAsync();
        await database.SeedExecutionAsync("execution-1", state: "Succeeded", endedAt: DateTimeOffset.UtcNow);
        await using (var connection = await database.Factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Sessions SET Backend=$backend";
            command.Parameters.AddWithValue("$backend", backend);
            await command.ExecuteNonQueryAsync();
        }
        await database.InsertExecutionEventAsync("stable-event", "execution-1", 0, DateTimeOffset.UnixEpoch,
            backend == "OpenCode" ? "OpenCodeTerminal" : "NativeGatewayLifecycle",
            backend == "OpenCode" ? "{\"state\":\"Succeeded\",\"reason\":\"Synthetic\"}"
                : "{\"state\":\"Succeeded\",\"failure\":\"None\",\"nativeIdentityConfirmed\":false}");
        using var guard = new ApplicationInstanceGuard(Path.Combine(database.Root, "instance"));
        var journal = new SqliteActivityEventJournal(database.Factory);
        async Task<int> Replay(IActivityCenterService activity) => backend == "OpenCode"
            ? await new SqliteOpenCodeJournalRecoveryService(database.Factory, guard, TimeProvider.System, activity).ReplayActivityAsync()
            : await new SqliteNativeGatewayRecoveryService(database.Factory, guard, TimeProvider.System, activity).ReplayActivityAsync();

        if (alreadyPersisted)
        {
            await using var firstQueue = new ActivityJournalWriteQueue(journal);
            var first = new ActivityCenterService(text => text, journal: journal, journalQueue: firstQueue);
            Assert.Equal(1, await Replay(first));
            await firstQueue.DrainAsync();
            Assert.Equal(0, firstQueue.FailedCount);
            Assert.Equal(1, await journal.CountAsync());
        }

        // Match product startup: durable activity reload precedes execution-journal replay.
        await using var restartedQueue = new ActivityJournalWriteQueue(journal);
        var restarted = new ActivityCenterService(text => text, journal: journal, journalQueue: restartedQueue);
        await restarted.LoadAsync();
        Assert.Equal(1, await Replay(restarted));
        Assert.Equal(1, await Replay(restarted)); // No intervening drain: both may still be queued.
        restarted.Append(new ActivityEvent("new-sibling", DateTimeOffset.UnixEpoch.AddSeconds(1),
            ActivityEventKind.System, ActivityRoleNames.System, ActivityEventState.Completed,
            ActivityEventSource.Synthetic, "Sibling", "Synthetic metadata"));
        await restartedQueue.DrainAsync();

        Assert.Equal(0, restartedQueue.FailedCount);
        Assert.Equal(0, restartedQueue.DroppedCount);
        Assert.True(restartedQueue.IsBalanced);
        Assert.Equal(2, await journal.CountAsync());
        var rows = await journal.LoadNewestAsync(10);
        Assert.Single(rows, row => row.Id == "new-sibling");
        Assert.Single(rows, row => row.Id == (backend == "OpenCode" ? "opencode-journal:" : "native-gateway-journal:") + "stable-event");
        Assert.Equal(2, restarted.TotalCount);
    }

    [Fact]
    public async Task OrdinaryDuplicateObservationsStillFailInsteadOfReplacingEvidence()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var journal = new SqliteActivityEventJournal(database.Factory);
        var original = new ActivityEvent("ordinary", DateTimeOffset.UnixEpoch, ActivityEventKind.System,
            ActivityRoleNames.System, ActivityEventState.Completed, ActivityEventSource.Synthetic, "Original", "Evidence");
        await journal.AppendAsync(original);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => journal.AppendAsync(original));
        Assert.Equal("Original", Assert.Single(await journal.LoadNewestAsync(10)).Title);
    }
}
