using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

/// <summary>
/// Literal free-text search over the real durable FTS5 index.
/// <para>
/// The claim under test is that what the operator types is data, not syntax - and that the durable index
/// and the in-memory index agree on that claim. The unquoted MATCH expression did not hold it: measured
/// against real SQLite 3.41.2 with the <c>unicode61</c> tokenizer, the query <c>observation OR</c> raised
/// <c>fts5: syntax error near "OR"</c> and a lone double quote raised <c>fts5: syntax error near "'"</c>.
/// On the WPF dispatcher that is an unhandled exception from a keystroke. Whether a reserved word happened
/// to be legal depended on the position it landed in - a lone <c>or</c> parsed as an ordinary term while
/// the same word in operand position was a syntax error - so the unquoted form was only accidentally
/// working and nothing tested it.
/// </para>
/// <para>
/// These run against a real SQLite file in a temporary application-data root, never an in-memory double:
/// the tokenizer, the MATCH parser and the WAL read transaction under test are exactly the ones the
/// shipped screen uses.
/// </para>
/// </summary>
public sealed class ActivityCenterLiteralSearchTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _appData;
    private readonly SqliteConnectionFactory _factory;

    public ActivityCenterLiteralSearchTests()
    {
        _appData = Path.Combine(
            Path.GetTempPath(),
            "llmworkgui-literal-search-tests",
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
    public void EveryOperatorTokenIsEmittedAsAQuotedFts5Literal()
    {
        // The expression is the whole mechanism, so it is pinned directly as well as through the database.
        Assert.Equal("\"observation\"", ActivityEventSearchText.BuildMatchExpression("observation"));
        Assert.Equal(
            "\"observation\" AND \"or\"",
            ActivityEventSearchText.BuildMatchExpression("observation OR"));

        // Every FTS5 keyword becomes a term rather than an operator, in any position.
        foreach (var reserved in new[] { "and", "or", "not", "near", "AND", "OR", "NOT" })
        {
            Assert.Equal($"\"{reserved.ToLowerInvariant()}\"", ActivityEventSearchText.BuildMatchExpression(reserved));
            Assert.Equal(
                $"\"observation\" AND \"{reserved.ToLowerInvariant()}\"",
                ActivityEventSearchText.BuildMatchExpression($"observation {reserved}"));
        }

        // A quote is a token separator, not part of a token: "a"b" tokenizes to two tokens and asks for
        // both. The expression builder still escapes an embedded quote by doubling it, which is what makes
        // it literal if the tokenizer is ever widened - the current tokenizer simply cannot produce one.
        Assert.Equal("\"a\" AND \"b\"", ActivityEventSearchText.BuildMatchExpression("a\"b"));

        // A query with no alphanumeric token has no expression at all; the caller decides that this
        // matches nothing rather than dropping the predicate and matching everything.
        Assert.Equal(string.Empty, ActivityEventSearchText.BuildMatchExpression("()"));
        Assert.Equal(string.Empty, ActivityEventSearchText.BuildMatchExpression("  "));
        Assert.Equal(string.Empty, ActivityEventSearchText.BuildMatchExpression("\""));
    }

    [Fact]
    public async Task AReservedWordInOperandPositionMatchesNothing_AndDoesNotRaise()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        await journal.AppendAsync(Event("has-observation", Now, description: "an ordinary observation"));
        await journal.AppendAsync(Event("has-or", Now.AddSeconds(1), description: "or rather not"));

        // The exact shape that raised fts5: syntax error near "OR" before the tokens were quoted.
        var both = journal.QueryPage(new ActivityJournalQuery(searchQuery: "observation OR", limit: 10));

        Assert.Equal(0, both.MatchedRows);
        Assert.Empty(both.Items);

        // A lone reserved word is a literal term, not an operator: it finds the row that carries it.
        var loneOr = journal.QueryPage(new ActivityJournalQuery(searchQuery: "or", limit: 10));

        Assert.Equal(1, loneOr.MatchedRows);
        Assert.Equal("has-or", Assert.Single(loneOr.Items).Id);
    }

    [Fact]
    public async Task EveryFts5OperatorWordIsSearchableAsALiteralToken()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        // One document per operator word, so a word that is treated as syntax cannot be found at all and a
        // word that is treated as a term is found by exactly its own document.
        var words = new[] { "and", "or", "not", "near", "match" };

        for (var index = 0; index < words.Length; index++)
        {
            await journal.AppendAsync(Event(
                $"event-{index}",
                Now.AddSeconds(index),
                description: $"pipeline stage uses {words[index]} semantics"));
        }

        foreach (var word in words)
        {
            var page = journal.QueryPage(new ActivityJournalQuery(searchQuery: word, limit: 10));

            Assert.Equal(1, page.MatchedRows);
            Assert.Equal($"event-{Array.IndexOf(words, word)}", Assert.Single(page.Items).Id);
        }
    }

    [Fact]
    public async Task QuotationMarksPunctuationAndWildcardsAreData_NotSyntax()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);
        var inMemory = new EventSearchIndex(text => text, capacity: 100);

        // Two rows, so a query that quietly lost its predicate is distinguishable from one that matched:
        // the failure mode under test shows up as 2 where 1 or 0 is correct.
        foreach (var (id, description) in new[]
                 {
                     ("ordinary", "an ordinary observation"),
                     ("unrelated", "a completely different pipeline stage")
                 })
        {
            var activityEvent = Event(id, Now, description: description);
            await journal.AppendAsync(activityEvent);
            inMemory.Index(activityEvent);
        }

        // Every one of these is a query an operator can produce by typing. The expected value is the
        // all-tokens-required literal rule over the two rows above: 1 when the tokens name the
        // "ordinary" row, 0 when any required token is absent, and never 2.
        foreach (var (query, expected) in new (string Query, int Expected)[]
                 {
                     // A quoted term is a literal term, and a trailing or leading quote is not syntax.
                     ("\"observation\"", 1),
                     ("observation\"", 1),
                     ("^observation", 1),
                     ("{observation}", 1),
                     ("[observation]", 1),
                     ("observation:", 1),
                     ("-observation", 1),
                     // A wildcard is not a prefix operator here: the tokenizer keeps the bare token, so
                     // "observation*" is the plain literal query and "observatio?" is a different word.
                     ("observation*", 1),
                     ("observatio?", 0),
                     ("*", 0),
                     // Tokenizing to nothing matches nothing rather than becoming no filter.
                     ("()", 0),
                     ("\"", 0),
                     // Two or more tokens are all required, so any absent token empties the result.
                     ("() OR observation", 0),
                     ("NOT observation", 0),
                     ("observation NEAR/2 x", 0),
                     ("a\"b\"c", 0),
                     ("observation pipeline", 0)
                 })
        {
            var page = journal.QueryPage(new ActivityJournalQuery(searchQuery: query, limit: 10));

            Assert.Equal(expected, page.MatchedRows);

            // The in-memory index applies the same rule, and the screen reads one and reports the other, so
            // a divergence here would be a screen that counts differently from what it searched.
            Assert.Equal(expected, inMemory.Search(query, limit: 100).Count);
        }

        // Nothing above broadened to the unfiltered set: the whole history is still two rows.
        Assert.Equal(2, journal.QueryPage(new ActivityJournalQuery(limit: 10)).TotalRows);
    }

    [Fact]
    public async Task MixedUnicodeAndCasedTokensMatchTheSameRowsInBothIndexes()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);
        var inMemory = new EventSearchIndex(text => text, capacity: 1_000);

        var documents = new (string Id, string Description)[]
        {
            ("cyrillic", "наблюдение привет мир этап два"),
            ("mixed", "Ünïcode42 ÜBERLAUF ÜBERLAUF stage"),
            ("greek", "παρατήρηση τελικό ελληνικά"),
            ("ascii", "plain ascii observation stage two"),
            ("digits", "stage 42 stage 3 stage 7"),
            ("camel", "loadExec03 loadExec03 mixedCaseIdentifier")
        };

        foreach (var (id, description) in documents)
        {
            var activityEvent = Event(id, Now, description: description);
            await journal.AppendAsync(activityEvent);
            inMemory.Index(activityEvent);
        }

        var queries = new[]
        {
            "привет",
            "ПРИВЕТ",
            "наблюдение",
            "ünïcode42",
            "ÜNÏCODE42",
            "überlauf",
            "παρατήρηση",
            "42",
            "loadExec03",
            "mixedCaseIdentifier",
            "привет observation",
            "stage 42"
        };

        foreach (var query in queries)
        {
            var memory = inMemory.Search(query, limit: 100);
            var durable = journal.QueryPage(new ActivityJournalQuery(searchQuery: query, limit: 100));

            // A literal match has to be the same match in both engines, or the operator sees one count on
            // the screen and a different one behind it.
            Assert.Equal(memory.Count, durable.MatchedRows);
            Assert.Equal(
                memory.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal),
                durable.Items.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));
        }

        // Case folding is the one normalization both engines apply, and it is symmetric.
        Assert.Equal(
            journal.QueryPage(new ActivityJournalQuery(searchQuery: "ПРИВЕТ", limit: 10)).MatchedRows,
            journal.QueryPage(new ActivityJournalQuery(searchQuery: "привет", limit: 10)).MatchedRows);
    }

    [Fact]
    public async Task TheDurableCountStaysExact_AndAGenerousMatchSetIsCountedNotTruncated()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        const int Total = 900;

        for (var sequence = 0; sequence < Total; sequence++)
        {
            await journal.AppendAsync(Event(
                $"event-{sequence:D4}",
                Now.AddSeconds(sequence),
                description: sequence % 3 == 0 ? "deployed stage three" : "observed stage two"));
        }

        const int Deployed = 300;

        var page = journal.QueryPage(new ActivityJournalQuery(searchQuery: "deployed", offset: 0, limit: 25));

        // The page is bounded, the count is not: this is the difference between a search and a scan.
        // Only every third row is a "deployed" one, so the newest match is the newest divisible index -
        // asserting the wrong row here would be a test that passes for the wrong reason.
        Assert.Equal(Total, page.TotalRows);
        Assert.Equal(Deployed, page.MatchedRows);
        Assert.Equal(25, page.Items.Count);
        Assert.Equal($"event-{Total - 1 - (Total - 1) % 3:D4}", page.Items[0].Id);

        // Paging through the whole match set never changes the count and never repeats a row.
        var seen = new List<string>();
        for (var offset = 0; offset < Deployed; offset += 25)
        {
            var chunk = journal.QueryPage(new ActivityJournalQuery(searchQuery: "deployed", offset: offset, limit: 25));
            Assert.Equal(Deployed, chunk.MatchedRows);
            seen.AddRange(chunk.Items.Select(item => item.Id));
        }

        Assert.Equal(Deployed, seen.Count);
        Assert.Equal(Deployed, seen.Distinct(StringComparer.Ordinal).Count());

        // Two tokens are both required, exactly as the in-memory index requires them.
        Assert.Equal(
            Deployed,
            journal.QueryPage(new ActivityJournalQuery(searchQuery: "deployed stage three", limit: 5)).MatchedRows);
        Assert.Equal(
            0,
            journal.QueryPage(new ActivityJournalQuery(searchQuery: "deployed stage two", limit: 5)).MatchedRows);
    }

    /// <summary>
    /// A commit that lands between the retained count and the match count must not produce a page whose
    /// match count exceeds the total. All three statements share one deferred WAL read transaction, so
    /// they see one snapshot; the invariant is asserted under a writer that is committing continuously.
    /// </summary>
    [Fact]
    public async Task ConcurrentCommitsNeverProduceAMatchCountAboveTheTotal_OrAPageOutsideItsMatchSet()
    {
        await MigrateAsync();
        var journal = new SqliteActivityEventJournal(_factory);

        const int Seed = 2_000;

        for (var sequence = 0; sequence < Seed; sequence++)
        {
            await journal.AppendAsync(Event(
                $"seed-{sequence:D5}",
                Now.AddSeconds(sequence),
                description: sequence % 2 == 0 ? "alpha observation" : "beta observation"));
        }

        using var cancelling = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var writer = Task.Run(
            async () =>
            {
                var sequence = 0;

                while (!cancelling.IsCancellationRequested)
                {
                    await journal.AppendAsync(Event(
                        $"live-{sequence:D5}",
                        Now.AddSeconds(Seed + sequence),
                        description: "alpha observation live"));

                    sequence++;
                }
            },
            cancelling.Token);

        var queries = 0;

        try
        {
            while (!cancelling.IsCancellationRequested)
            {
                // One snapshot spans all three statements, so every combination of total, matched and page
                // is internally consistent no matter where the writer's commits land.
                var unfiltered = journal.QueryPage(new ActivityJournalQuery(offset: 0, limit: 50));
                Assert.True(unfiltered.MatchedRows <= unfiltered.TotalRows);
                Assert.True(unfiltered.Items.Count <= unfiltered.MatchedRows);

                var searched = journal.QueryPage(new ActivityJournalQuery(
                    searchQuery: "alpha observation",
                    offset: 0,
                    limit: 50));
                Assert.True(searched.MatchedRows <= searched.TotalRows);
                Assert.True(searched.Items.Count <= searched.MatchedRows);

                // The page is newest first and each row of it is distinct.
                Assert.Equal(searched.Items.Count, searched.Items.Select(item => item.Id).Distinct().Count());
                for (var index = 1; index < searched.Items.Count; index++)
                {
                    Assert.True(
                        searched.Items[index - 1].OccurredAtUtc >= searched.Items[index].OccurredAtUtc,
                        "the durable page is not newest first.");
                }

                queries++;
            }
        }
        finally
        {
            await cancelling.CancelAsync();

            try
            {
                await writer;
            }
            catch (OperationCanceledException)
            {
                // The writer is cancelled deliberately.
            }
        }

        Assert.True(queries > 20, $"only {queries} queries ran under concurrent commits; too few to be evidence.");

        // The rows the writer committed are part of the retained set afterwards, and the count is exact.
        var total = journal.QueryPage(new ActivityJournalQuery(limit: 1));
        Assert.Equal(total.TotalRows, total.MatchedRows);
    }

    private async Task MigrateAsync() => await new DatabaseMigrator(_factory).MigrateAsync();

    private static ActivityEvent Event(
        string id,
        DateTimeOffset occurredAt,
        string description = "") => new(
        id,
        occurredAt,
        ActivityEventKind.Execution,
        ActivityRoleNames.Coder,
        ActivityEventState.Running,
        ActivityEventSource.Native,
        $"title {id}",
        description,
        executionId: "exec-01");
}
