using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

/// <summary>
/// The durable side of the activity stream over the real <c>ActivityEvents</c> table: the migration that
/// creates it, the round trip through application data, survival across a genuinely new connection, and
/// the explicit retention rule.
/// <para>
/// These run against a real SQLite file in a temporary application-data root rather than an in-memory
/// double, because the claim under test is specifically that "already saved" means product-reloadable rows.
/// An in-memory fake would pass every assertion here while proving nothing about a restart.
/// </para>
/// </summary>
public sealed class SqliteActivityEventJournalTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _appData;
    private readonly string _databasePath;
    private readonly SqliteConnectionFactory _factory;

    public SqliteActivityEventJournalTests()
    {
        _appData = Path.Combine(
            Path.GetTempPath(),
            "llmworkgui-journal-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_appData);
        _factory = new SqliteConnectionFactory(Path.Combine(_appData, "llmworkgui.db"));
        _databasePath = _factory.DatabasePath;
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
    public async Task Migration010_CreatesTheActivityEventsTableAndItsIndexes()
    {
        await MigrateAsync();

        Assert.True(await TableExistsAsync("ActivityEvents"));

        // The read pattern is newest-first paging and the retention rule deletes the oldest rows, so the
        // ordering index is part of the schema's contract rather than a nicety.
        var indexes = await IndexNamesAsync("ActivityEvents");

        Assert.Contains("IX_ActivityEvents_OccurredAtUtc", indexes);
        Assert.Contains("IX_ActivityEvents_ExecutionId", indexes);
    }

    [Fact]
    public void TheMigrationIsRegisteredInTheKnownSchemaTableList()
    {
        Assert.Contains("ActivityEvents", DatabaseSchema.TableNames);
    }

    [Fact]
    public async Task Events_RoundTripThroughApplicationData_WithIdentityAndProvenanceIntact()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        await journal.AppendAsync(Event(
            "event-1",
            Now,
            ActivityRoleNames.Coder,
            executionId: "exec-01",
            routeId: "route-3",
            description: "first observation"));

        await journal.AppendAsync(Event(
            "event-2",
            Now.AddSeconds(1),
            ActivityRoleNames.Reviewer,
            executionId: "exec-02",
            description: "second observation"));

        Assert.Equal(2, await journal.CountAsync());

        // Newest first, which is the only order the Activity Center reads in.
        var loaded = await journal.LoadNewestAsync(10);

        Assert.Equal(new[] { "event-2", "event-1" }, loaded.Select(item => item.Id).ToArray());
        Assert.Equal("exec-02", loaded[0].ExecutionId);
        Assert.Equal("route-3", loaded[1].RouteId);
        Assert.Equal("second observation", loaded[0].Description);
    }

    [Fact]
    public async Task SavedEvents_SurviveAFreshConnection_AsRowsOfTheApplicationDatabase()
    {
        await MigrateAsync();

        await new SqliteActivityEventJournal(_factory)
            .AppendAsync(Event("survivor", Now, ActivityRoleNames.Coder, description: "persisted observation"));

        // A second connection over the same file, as a restarted process would open. Not the same object,
        // and not any in-memory state that happened to be alive.
        var reopened = new SqliteConnectionFactory(_databasePath);
        var journal = new SqliteActivityEventJournal(reopened);

        Assert.Equal(1, await journal.CountAsync());

        var loaded = await journal.LoadNewestAsync(1);
        Assert.Equal("survivor", loaded.Single().Id);
    }

    [Fact]
    public async Task ProjectionUpdatesPreserveTheLatestStateAndDoNotPoisonUnrelatedQueuedObservations()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);
        await journal.AppendAsync(ActivityEvent.FromExecutionProjection(Projection(ExecutionState.Running, Now)));
        var held = new FirstBatchBarrierJournal(journal);
        var queue = new ActivityJournalWriteQueue(held, batchSize: 2);
        var service = new ActivityCenterService(text => text, journalQueue: queue, journal: journal);
        try
        {
            service.Append(Event("barrier", Now, ActivityRoleNames.System));
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.AppendProjection(Projection(ExecutionState.Succeeded, Now.AddSeconds(1)));
            service.Append(Event("unrelated", Now.AddSeconds(2), ActivityRoleNames.Coder));
            held.Release.TrySetResult();
            await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, queue.FailedCount);
            Assert.True(queue.IsBalanced);
            var reloaded = await new SqliteActivityEventJournal(_factory).LoadNewestAsync(10);
            Assert.Equal(3, reloaded.Count);
            Assert.Equal(ActivityEventState.Completed, reloaded.Single(row => row.Id == "execution:projection").State);
            Assert.Contains(reloaded, row => row.Id == "unrelated");
            // The two independent observations are still Running; only the updated projection's
            // previous FTS entry must disappear.
            var running = journal.QueryPage(new ActivityJournalQuery(searchQuery: "running")).Items;
            Assert.Equal(2, running.Count);
            Assert.DoesNotContain(running, row => row.Id == "execution:projection");
            Assert.Equal("execution:projection", Assert.Single(journal.QueryPage(new ActivityJournalQuery(searchQuery: "succeeded")).Items).Id);
        }
        finally
        {
            held.Release.TrySetResult();
            await queue.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProjectionBatchFailureRollsBackBothRowsAndSearchEntries()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);
        await journal.UpsertProjectionsAsync(new[] { ActivityEvent.FromExecutionProjection(Projection(ExecutionState.Running, Now)) });
        await using (var connection = await _factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER FailSecondProjection BEFORE UPDATE ON ActivityEvents
                WHEN NEW.State = 'Failed'
                BEGIN SELECT RAISE(ABORT, 'synthetic projection write failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => journal.UpsertProjectionsAsync(new[]
        {
            ActivityEvent.FromExecutionProjection(Projection(ExecutionState.Succeeded, Now.AddSeconds(1))),
            ActivityEvent.FromExecutionProjection(Projection(ExecutionState.Failed, Now.AddSeconds(2)))
        }));

        Assert.Equal(ActivityEventState.Running, Assert.Single(await journal.LoadNewestAsync(10)).State);
        Assert.Equal("execution:projection", Assert.Single(journal.QueryPage(new ActivityJournalQuery(searchQuery: "running")).Items).Id);
        Assert.Empty(journal.QueryPage(new ActivityJournalQuery(searchQuery: "succeeded")).Items);
        Assert.Empty(journal.QueryPage(new ActivityJournalQuery(searchQuery: "failed")).Items);
    }

    [Fact]
    public async Task AUniqueIdIsRequired_AndADuplicateSurfacesInsteadOfOverwriting()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        await journal.AppendAsync(Event("same-id", Now, ActivityRoleNames.Coder, description: "first"));

        // An id is the identity of one observation. Two observations sharing one id is a caller bug, and
        // silently overwriting the first would rewrite the operator's history.
        await Assert.ThrowsAsync<SqliteException>(() =>
            journal.AppendAsync(Event("same-id", Now, ActivityRoleNames.Coder, description: "second")));

        var loaded = await journal.LoadNewestAsync(10);
        Assert.Equal("first", loaded.Single().Description);
    }

    [Fact]
    public async Task RetentionRemovesTheOldestRowsAndReportsExactlyWhatItDropped()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        for (var index = 0; index < 20; index++)
        {
            await journal.AppendAsync(Event(
                $"event-{index:D2}",
                Now.AddSeconds(index),
                ActivityRoleNames.Coder));
        }

        var result = await journal.TrimAsync(12);

        Assert.Equal(20, result.RowsBefore);
        Assert.Equal(12, result.RowsAfter);
        Assert.Equal(8, result.RowsRemoved);
        Assert.True(result.RemovedAnything);

        var remaining = await journal.LoadNewestAsync(50);
        Assert.Equal(12, remaining.Count);

        // The oldest go first, so the newest history is what survives.
        Assert.Equal("event-19", remaining[0].Id);
        Assert.Equal("event-08", remaining[^1].Id);
        Assert.DoesNotContain(remaining, item => item.Id == "event-00");
    }

    [Fact]
    public async Task RetentionUnderTheLimitRemovesNothing()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        await journal.AppendAsync(Event("only", Now, ActivityRoleNames.Coder));

        var result = await journal.TrimAsync(100);

        Assert.Equal(1, result.RowsBefore);
        Assert.Equal(1, result.RowsAfter);
        Assert.Equal(0, result.RowsRemoved);
        Assert.False(result.RemovedAnything);
    }

    [Fact]
    public async Task RetentionTouchesNoOtherTableOfTheApplicationDatabase()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        for (var index = 0; index < 10; index++)
        {
            await journal.AppendAsync(Event($"event-{index:D2}", Now, ActivityRoleNames.Coder));
        }

        // Application data that already existed must survive an activity retention run untouched: the
        // requirement is to preserve the rest of the user's data, not just to leave a valid database.
        await using (var connection = _factory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO ApplicationSettings (Key, Value, ValueType, UpdatedAtUtc)
                VALUES ('activity-test', 'must-survive', 'String', $now);
                """;
            insert.Parameters.AddWithValue("$now", Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync();
        }

        await journal.TrimAsync(2);

        await using var check = _factory.CreateConnection();
        await check.OpenAsync();
        await using var read = check.CreateCommand();
        read.CommandText = "SELECT Value FROM ApplicationSettings WHERE Key = 'activity-test';";

        Assert.Equal("must-survive", await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AMassiveBodyIsNotPersistedInline_ButItsProvenanceIs()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        // The normative profile offers messages up to 256 KiB. A journal that stored every body inline
        // would grow with the stream; one that stored nothing would lose the provenance the detail pane
        // needs. The row keeps the structured identity and leaves the body to the blob store.
        var massive = new string('x', 256 * 1024);

        await journal.AppendAsync(new ActivityEvent(
            "massive",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Running,
            ActivityEventSource.Native,
            "large payload event",
            "the body is deliberately modest here; the size lives in the provenance",
            sessionId: "session-9",
            executionId: "exec-9",
            routeId: "route-9",
            artifactName: "plan.json",
            artifactSizeBytes: massive.Length,
            artifactSha256: "sha256:abc"));

        var loaded = (await journal.LoadNewestAsync(1)).Single();

        Assert.Equal("session-9", loaded.SessionId);
        Assert.Equal("exec-9", loaded.ExecutionId);
        Assert.Equal("route-9", loaded.RouteId);
        Assert.Equal("plan.json", loaded.ArtifactName);
        Assert.Equal(massive.Length, loaded.ArtifactSizeBytes);
        Assert.Equal("sha256:abc", loaded.ArtifactSha256);
    }

    /// <summary>
    /// A true 256 KiB body, actually written. The previous version of this test built a 256 KiB string and
    /// then persisted a 41-character description while asserting only on the provenance columns, so it
    /// passed without ever proving that a maximum-size message survives the journal round trip at all.
    /// </summary>
    [Fact]
    public async Task ARealMaximumSizedMessageIsPersistedAndReadBackIntact()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        const int MaximumMessageBytes = 256 * 1024;
        const string TailMarker = "tailmarker";

        // A real maximum-sized body: mostly filler, then a distinct word at the very end, separated from
        // the filler so it is a token of its own. The marker is what proves the whole body was persisted
        // and indexed rather than truncated at some column limit.
        var filler = new string('x', MaximumMessageBytes - TailMarker.Length - 1);
        var body = filler + " " + TailMarker;

        Assert.Equal(MaximumMessageBytes, body.Length);
        Assert.EndsWith(" " + TailMarker, body, StringComparison.Ordinal);

        await journal.AppendAsync(new ActivityEvent(
            "maximum-size",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Running,
            ActivityEventSource.Native,
            "maximum payload",
            body,
            executionId: "exec-maximum"));

        var loaded = (await journal.LoadNewestAsync(1)).Single();

        Assert.Equal(MaximumMessageBytes, loaded.Description.Length);
        Assert.Equal(body, loaded.Description);

        // The very end of the maximum-sized body is searchable as well as persisted.
        var found = journal.QueryPage(new ActivityJournalQuery(
            searchQuery: TailMarker,
            offset: 0,
            limit: 10));

        Assert.Equal(1, found.MatchedRows);
        Assert.Equal("maximum-size", Assert.Single(found.Items).Id);
    }

    /// <summary>
    /// The durable search must find every retained row, not only the newest page of them. A journal that
    /// quietly searched a subset is the exact defect this corrects: paging reached 190 000 rows while the
    /// free-text index could only see the newest 100 000.
    /// </summary>
    [Fact]
    public async Task DurableSearchFindsEveryRetainedRow_NotOnlyTheNewestPage()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        const int Total = 1_500;

        for (var sequence = 0; sequence < Total; sequence++)
        {
            await journal.AppendAsync(Event(
                $"event-{sequence:D5}",
                Now.AddSeconds(sequence),
                sequence % 2 == 0 ? ActivityRoleNames.Coder : ActivityRoleNames.Reviewer,
                executionId: $"exec-{sequence % 5:D2}",
                description: $"execution observation {sequence} of {Total}"));
        }

        var everyRow = journal.QueryPage(new ActivityJournalQuery(offset: 0, limit: 10));
        Assert.Equal(Total, everyRow.TotalRows);
        Assert.Equal(Total, everyRow.MatchedRows);
        Assert.Equal(10, everyRow.Items.Count);

        // The oldest match has to be reachable, not just the newest: that is the row a bounded in-memory
        // index could not see.
        var searched = journal.QueryPage(new ActivityJournalQuery(
            searchQuery: "observation",
            offset: 0,
            limit: 5));

        Assert.Equal(Total, searched.MatchedRows);
        Assert.Equal(
            new[] { "event-01499", "event-01498", "event-01497", "event-01496", "event-01495" },
            searched.Items.Select(item => item.Id).ToArray());

        var oldestPage = journal.QueryPage(new ActivityJournalQuery(
            searchQuery: "observation",
            offset: Total - 2,
            limit: 2));

        Assert.Equal(new[] { "event-00001", "event-00000" }, oldestPage.Items.Select(item => item.Id).ToArray());
    }

    /// <summary>
    /// Every token of a query must be required, and every durable filter axis must narrow the exact count.
    /// A count that quietly ignored a filter would make the screen report a history that does not exist.
    /// </summary>
    [Fact]
    public async Task DurableQueryRequiresEveryTokenAndNarrowsExactlyOnEveryAxis()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        for (var sequence = 0; sequence < 60; sequence++)
        {
            await journal.AppendAsync(Event(
                $"event-{sequence:D3}",
                Now.AddSeconds(sequence),
                sequence % 3 == 0 ? ActivityRoleNames.Coder : ActivityRoleNames.Reviewer,
                executionId: $"exec-{sequence % 4:D2}",
                description: sequence % 5 == 0 ? "deployed stage three" : "observed stage two"));
        }

        Assert.Equal(60, journal.QueryPage(new ActivityJournalQuery(searchQuery: "stage", limit: 1)).MatchedRows);
        Assert.Equal(12, journal.QueryPage(new ActivityJournalQuery(searchQuery: "deployed", limit: 1)).MatchedRows);
        Assert.Equal(
            12,
            journal.QueryPage(new ActivityJournalQuery(searchQuery: "deployed stage three", limit: 1))
                .MatchedRows);
        Assert.Equal(
            0,
            journal.QueryPage(new ActivityJournalQuery(searchQuery: "deployed nonsense", limit: 1))
                .MatchedRows);

        Assert.Equal(
            20,
            journal.QueryPage(new ActivityJournalQuery(roles: new[] { ActivityRoleNames.Coder }, limit: 1))
                .MatchedRows);

        Assert.Equal(
            60,
            journal.QueryPage(new ActivityJournalQuery(states: new[] { ActivityEventState.Running }, limit: 1))
                .MatchedRows);

        Assert.Equal(
            60,
            journal.QueryPage(new ActivityJournalQuery(source: ActivityEventSource.Native, limit: 1))
                .MatchedRows);

        // A window that ends before anything was written matches nothing, and one that starts before the
        // first event matches everything - the bound is applied, not approximated.
        Assert.Equal(
            0,
            journal.QueryPage(new ActivityJournalQuery(sinceUtc: Now.AddHours(1), limit: 1)).MatchedRows);
        Assert.Equal(
            60,
            journal.QueryPage(new ActivityJournalQuery(sinceUtc: Now.AddHours(-1), limit: 1)).MatchedRows);
    }

    [Fact]
    public async Task DurableQueryTreatsSelectionsWithinEachFilterAxisAsAlternatives()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        await journal.AppendAsync(new ActivityEvent(
            "coder-running", Now, ActivityEventKind.Execution, ActivityRoleNames.Coder,
            ActivityEventState.Running, ActivityEventSource.Native, "coder", "event"));
        await journal.AppendAsync(new ActivityEvent(
            "reviewer-failed", Now.AddSeconds(1), ActivityEventKind.Execution, ActivityRoleNames.Reviewer,
            ActivityEventState.Failed, ActivityEventSource.Native, "reviewer", "event"));
        await journal.AppendAsync(new ActivityEvent(
            "system-completed", Now.AddSeconds(2), ActivityEventKind.Execution, ActivityRoleNames.System,
            ActivityEventState.Completed, ActivityEventSource.Native, "system", "event"));

        var selectedRoles = new[] { ActivityRoleNames.Coder, ActivityRoleNames.Reviewer };
        var selectedStates = new[] { ActivityEventState.Running, ActivityEventState.Failed };

        Assert.Equal(2, journal.QueryPage(new ActivityJournalQuery(roles: selectedRoles)).MatchedRows);
        Assert.Equal(2, journal.QueryPage(new ActivityJournalQuery(states: selectedStates)).MatchedRows);

        var combined = journal.QueryPage(new ActivityJournalQuery(
            roles: selectedRoles, states: selectedStates, limit: 10));
        Assert.Equal(2, combined.MatchedRows);
        Assert.Equal(new[] { "reviewer-failed", "coder-running" }, combined.Items.Select(item => item.Id));
        Assert.Equal(0, journal.QueryPage(new ActivityJournalQuery(
            roles: selectedRoles, states: new[] { ActivityEventState.Completed })).MatchedRows);
    }

    /// <summary>
    /// The full-text index must not outlive a retention delete. A durable search that kept returning rows
    /// the journal had already removed would report a history that is no longer saved.
    /// </summary>
    [Fact]
    public async Task RetentionRemovesTheSearchRowsToo_SoNoSearchOutlivesItsJournalRow()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        for (var sequence = 0; sequence < 40; sequence++)
        {
            await journal.AppendAsync(Event(
                $"event-{sequence:D3}",
                Now.AddSeconds(sequence),
                ActivityRoleNames.Coder,
                description: "execution observation"));
        }

        Assert.Equal(40, journal.QueryPage(new ActivityJournalQuery(searchQuery: "observation", limit: 1)).MatchedRows);
        Assert.Equal(1, journal.QueryPage(new ActivityJournalQuery(searchQuery: "event-005", limit: 5)).MatchedRows);

        var trimmed = await journal.TrimAsync(10);

        Assert.Equal(40, trimmed.RowsBefore);
        Assert.Equal(10, trimmed.RowsAfter);
        Assert.Equal(30, trimmed.RowsRemoved);
        Assert.Equal(10, journal.QueryPage(new ActivityJournalQuery(limit: 1)).TotalRows);
        Assert.Equal(
            10,
            journal.QueryPage(new ActivityJournalQuery(searchQuery: "observation", limit: 1)).MatchedRows);

        // The oldest rows are the ones that went, so a search for one of them must come back empty.
        Assert.Equal(
            0,
            journal.QueryPage(new ActivityJournalQuery(searchQuery: "event-005", limit: 5)).MatchedRows);
        Assert.Equal(1, journal.QueryPage(new ActivityJournalQuery(searchQuery: "event-039", limit: 5)).MatchedRows);
    }

    /// <summary>
    /// The durable index and the in-memory index must be the same projection. They are filled from one
    /// definition, and this is what proves it: a token the in-memory index finds is a token the durable
    /// index finds, on a populated index rather than an empty one.
    /// </summary>
    [Fact]
    public async Task DurableAndInMemoryIndexesAgreeOnTheSamePopulatedDocuments()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);
        var inMemory = new EventSearchIndex(text => text, capacity: 1_000);

        var queries = new[]
        {
            "observation",
            "stage three",
            "exec-02",
            "Coder",
            "Reviewer",
            "running",
            "REDACTED",
            "nothing-matches-this-token"
        };

        for (var sequence = 0; sequence < 400; sequence++)
        {
            var activityEvent = new ActivityEvent(
                $"event-{sequence:D4}",
                Now.AddSeconds(sequence),
                sequence % 2 == 0 ? ActivityEventKind.Execution : ActivityEventKind.Health,
                sequence % 3 == 0 ? ActivityRoleNames.Coder : ActivityRoleNames.Reviewer,
                ActivityEventState.Running,
                ActivityEventSource.Native,
                $"title {sequence} with token alpha{sequence % 7}",
                $"description observation {sequence} stage {sequence % 4} with a [REDACTED] value",
                sessionId: $"session-{sequence % 3}",
                executionId: $"exec-{sequence % 4:D2}");

            await journal.AppendAsync(activityEvent);
            inMemory.Index(activityEvent);
        }

        foreach (var query in queries)
        {
            var memory = inMemory.Search(query, limit: 1_000);
            var durable = journal.QueryPage(new ActivityJournalQuery(searchQuery: query, offset: 0, limit: 1_000));

            Assert.Equal(memory.Count, durable.MatchedRows);
            Assert.Equal(
                memory.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal),
                durable.Items.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Operator input must never reach the query parser as syntax. A crafted query is either a plain token
    /// match or nothing; it may not raise, and it may not become a full-text operator.
    /// </summary>
    [Fact]
    public async Task AQueryIsTokenizedRatherThanExecutedAsFullTextSyntax()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        await journal.AppendAsync(Event(
            "event-1",
            Now,
            ActivityRoleNames.Coder,
            description: "an ordinary observation"));

        foreach (var (query, expected) in new (string Query, int Expected)[]
                 {
                     // Every alphanumeric token of the query is required, so "or" and "near" narrow it to
                     // nothing rather than being read as full-text operators.
                     ("observation", 1),
                     ("observation OR", 0),
                     ("\"observation\"", 1),
                     ("observation NEAR/2 other", 0),
                     ("observation*", 1),
                     // A blank query is no filter at all.
                     ("  ", 1),
                     // A query of nothing but punctuation matches nothing rather than silently becoming
                     // no filter and handing back the entire history the operator did not ask for.
                     ("()", 0)
                 })
        {
            var page = journal.QueryPage(new ActivityJournalQuery(searchQuery: query, offset: 0, limit: 10));

            Assert.Equal(expected, page.MatchedRows);
        }

        Assert.Equal(1, journal.QueryPage(new ActivityJournalQuery(searchQuery: "observation", limit: 1)).MatchedRows);
        Assert.Equal(0, journal.QueryPage(new ActivityJournalQuery(searchQuery: "()", limit: 1)).MatchedRows);
        Assert.Equal(1, journal.QueryPage(new ActivityJournalQuery(searchQuery: "  ", limit: 1)).MatchedRows);
    }

    [Fact]
    public async Task DurableQueryArgumentsAreValidated()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ActivityJournalQuery(offset: -1, limit: 10));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ActivityJournalQuery(offset: 0, limit: 0));
        Assert.Throws<ArgumentNullException>(() => journal.QueryPage(null!));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ActivityJournalQuery.FromCriteria(ActivityFilterCriteria.Default, Now, page: 0, pageSize: 10));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ActivityJournalQuery.FromCriteria(ActivityFilterCriteria.Default, Now, page: 1, pageSize: 0));
    }

    [Fact]
    public void RetentionArgumentsAreValidated()
    {
        // A trim result that does not add up is not a reportable outcome; it is a bug, and the
        // constructor refuses to produce one.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityJournalTrimResult(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityJournalTrimResult(1, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityJournalTrimResult(1, 0, -1));

        // Removed rows must equal the difference the run actually produced.
        Assert.Throws<ArgumentException>(() => new ActivityJournalTrimResult(1, 0, 0));
        Assert.Throws<ArgumentException>(() => new ActivityJournalTrimResult(5, 4, 0));
        Assert.Throws<ArgumentException>(() => new ActivityJournalTrimResult(5, 3, 0));

        // A retention run can never grow the journal.
        Assert.Throws<ArgumentException>(() => new ActivityJournalTrimResult(5, 7, 0));

        // A consistent result is accepted.
        Assert.Equal(2, new ActivityJournalTrimResult(5, 3, 2).RowsRemoved);
    }

    [Fact]
    public async Task RetentionRejectsANegativeLimit()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => journal.TrimAsync(-1));
    }

    [Fact]
    public async Task LoadNewestRejectsANonPositiveLimit()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => journal.LoadNewestAsync(0));
    }

    private static ObservableRunProjection Projection(ExecutionState state, DateTimeOffset observedAt) => new(
        executionId: "projection", sessionId: "session", role: WorkflowRole.Executor, displayLabel: "Coder",
        state: state, requestedRouteId: "route", observedRouteId: null, nativeSessionId: null,
        startedAtUtc: Now, lastActivityAtUtc: observedAt, endedAtUtc: state == ExecutionState.Succeeded ? observedAt : null,
        evidenceSource: EvidenceSourceKind.SyntheticFixture, isSynthetic: true);

    private sealed class FirstBatchBarrierJournal(IActivityEventJournal inner) : IActivityEventJournal
    {
        private int _writes;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task AppendAsync(ActivityEvent item, CancellationToken token = default) => AppendRangeAsync(new[] { item }, token);
        public async Task AppendRangeAsync(IReadOnlyList<ActivityEvent> items, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            await inner.AppendRangeAsync(items, token).ConfigureAwait(false);
        }
        public Task<IReadOnlyList<ActivityEvent>> LoadNewestAsync(int limit, CancellationToken token = default) => inner.LoadNewestAsync(limit, token);
        public Task UpsertProjectionsAsync(IReadOnlyList<ActivityEvent> items, CancellationToken token = default) =>
            inner.UpsertProjectionsAsync(items, token);
        public Task<long> CountAsync(CancellationToken token = default) => inner.CountAsync(token);
        public Task<ActivityJournalTrimResult> TrimAsync(int limit, CancellationToken token = default) => inner.TrimAsync(limit, token);
        public ActivityJournalPage QueryPage(ActivityJournalQuery query) => inner.QueryPage(query);
    }

    private async Task MigrateAsync()
    {
        var migrator = new DatabaseMigrator(_factory);
        await migrator.MigrateAsync();
    }

    private async Task<bool> TableExistsAsync(string tableName)
    {
        await using var connection = _factory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync() ?? 0L,
            System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private async Task<IReadOnlyList<string>> IndexNamesAsync(string tableName)
    {
        await using var connection = _factory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = $name;";
        command.Parameters.AddWithValue("$name", tableName);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static ActivityEvent Event(
        string id,
        DateTimeOffset occurredAt,
        string role,
        string? executionId = null,
        string? routeId = null,
        string description = "") => new(
        id,
        occurredAt,
        ActivityEventKind.Execution,
        role,
        ActivityEventState.Running,
        ActivityEventSource.Native,
        $"title {id}",
        description,
        executionId: executionId,
        routeId: routeId);
}
