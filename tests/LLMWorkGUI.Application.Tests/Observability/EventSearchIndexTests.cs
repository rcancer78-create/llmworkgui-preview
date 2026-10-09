using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;
using Xunit.Abstractions;
using ActivityEvent = LLMWorkGUI.Application.Observability.ActivityEvent;

namespace LLMWorkGUI.Application.Tests.Observability;

/// <summary>
/// Behaviour and performance of the Activity Center search index. The central invariant under test is
/// that the index only ever sees redacted text: API keys, bearer tokens and passwords must neither be
/// indexed nor be findable, while ordinary operator text stays searchable (ТЗ §9.3, ROADMAP Phase 11).
/// </summary>
public sealed class EventSearchIndexTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public EventSearchIndexTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Index_SearchResultsRedactAllDisplayPayloadsNotOnlySearchTokens()
    {
        const string syntheticSecret = "synthetic-index-display-secret";
        var filter = new SensitiveDataFilter();
        var index = new EventSearchIndex(filter.RedactDiagnostic);
        var raw = new ActivityEvent("safe-event", Now, ActivityEventKind.Execution, ActivityRoleNames.Coder,
            ActivityEventState.Running, ActivityEventSource.Native,
            "Searchmarker password=" + syntheticSecret, "password=" + syntheticSecret,
            diffText: "+ password=" + syntheticSecret, artifactName: "fixture.txt",
            artifactContent: "password=" + syntheticSecret);

        index.Index(raw);
        var found = Assert.Single(index.Search("Searchmarker"));

        Assert.All(new[] { found.Title, found.Description, found.DiffText!, found.ArtifactContent! }, text =>
        {
            Assert.DoesNotContain(syntheticSecret, text);
            Assert.Contains(SensitiveDataFilter.Placeholder, text);
        });
        Assert.Equal("safe-event", found.Id);
        Assert.Equal("fixture.txt", found.ArtifactName);
        Assert.Contains(syntheticSecret, raw.Title);
        Assert.Empty(index.Search(syntheticSecret));
    }

    [Fact]
    public void Index_RedactsSecretsBeforeIndexing_AndSecretValuesAreNeverFindable()
    {
        var filter = new SensitiveDataFilter();
        var index = new EventSearchIndex(filter.Redact);

        const string apiKey = "sk-live-1234567890abcdef";
        const string password = "SuperSecretPassw0rd!";
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N";

        var activityEvent = new ActivityEvent(
            "event-secret",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Running,
            ActivityEventSource.Native,
            $"Deploy with {apiKey}",
            $"password={password}; Authorization: Bearer {jwt}");

        // Precondition: the raw event text really does contain sensitive material.
        Assert.True(filter.ContainsSensitiveData(activityEvent.Title));
        Assert.True(filter.ContainsSensitiveData(activityEvent.Description));

        index.Index(activityEvent);

        // The secret values must not be findable, in any casing.
        Assert.Empty(index.Search(apiKey));
        Assert.Empty(index.Search("1234567890abcdef"));
        Assert.Empty(index.Search(password.ToUpperInvariant()));
        Assert.Empty(index.Search("eyJhbGciOiJIUzI1NiJ9"));

        // The document itself is indexed, but only with the redaction placeholder visible.
        Assert.Single(index.Search("Deploy"));
        Assert.NotEmpty(index.Search(SensitiveDataFilter.Placeholder));
    }

    [Fact]
    public void Index_WithoutRedactionConfiguredByCaller_RunsTheSuppliedRedactorOnEveryField()
    {
        var seen = new List<string>();
        var index = new EventSearchIndex(
            text =>
            {
                seen.Add(text);
                return text.Replace("token-123", SensitiveDataFilter.Placeholder, StringComparison.Ordinal);
            });

        index.Index(Event("event-1", "Token token-123 revealed", description: "The body carries token-123."));

        Assert.Contains("Token token-123 revealed", seen);
        Assert.Contains("The body carries token-123.", seen);
        Assert.Empty(index.Search("token-123"));
        Assert.Single(index.Search(SensitiveDataFilter.Placeholder));
    }

    [Fact]
    public void Search_IsCaseInsensitiveTokenMatching_AndRequiresEveryToken()
    {
        var index = CreateIndex();

        index.Index(Event("event-1", "Architecture document delivered", description: "Reviewer accepted the plan."));
        index.Index(Event("event-2", "Implementation diff delivered", description: "Coder pushed the patch."));

        Assert.Single(index.Search("architecture"));
        Assert.Single(index.Search("ARCHITECTURE"));

        // A partial word is not a word. The in-memory index used to accept "arch" because the query token
        // was a substring of the document text; the durable FTS5 index does not, and two search engines
        // with different match sets is exactly how search and paging came to disagree about the retained
        // history. One rule, both engines: tokens only.
        Assert.Empty(index.Search("arch"));

        Assert.Equal(2, index.Search("delivered").Count);
        Assert.Single(index.Search("delivered diff"));
        Assert.Empty(index.Search("architecture patch"));
        Assert.Empty(index.Search("   "));

        // Punctuation splits tokens rather than becoming syntax, so the operator's own query is what
        // decides the match: "Architecture document" finds the same row as its two tokens alone.
        Assert.Single(index.Search("Architecture document"));
        Assert.Single(index.Search("event-1 architecture"));
        Assert.Empty(index.Search("()"));
    }

    [Fact]
    public void Search_ReturnsNewestFirst_AndRespectsTheLimit()
    {
        var index = CreateIndex();

        index.Index(Event("event-old", "alpha marker", Now.AddMinutes(-30)));
        index.Index(Event("event-middle", "alpha marker", Now.AddMinutes(-20)));
        index.Index(Event("event-new", "alpha marker", Now.AddMinutes(-10)));

        var results = index.Search("alpha marker");

        Assert.Equal(new[] { "event-new", "event-middle", "event-old" }, results.Select(result => result.Id).ToArray());

        var limited = index.Search("alpha marker", limit: 2);

        Assert.Equal(new[] { "event-new", "event-middle" }, limited.Select(result => result.Id).ToArray());
        Assert.Empty(index.Search("alpha", limit: 0));
    }

    [Fact]
    public void Index_ReplacesADocumentWithTheSameId()
    {
        var index = CreateIndex();

        index.Index(Event("event-1", "first wording", Now.AddMinutes(-10)));
        index.Index(Event("event-1", "second wording", Now));

        Assert.Equal(1, index.Count);
        Assert.Single(index.Search("second"));
        Assert.Empty(index.Search("first"));
    }

    [Fact]
    public void Index_EvictsTheOldestDocumentsBeyondCapacity()
    {
        var index = new EventSearchIndex(Redact, capacity: 3);

        for (var i = 0; i < 5; i++)
        {
            index.Index(Event($"event-{i}", $"marker number {i}", Now.AddMinutes(i)));
        }

        Assert.Equal(3, index.Count);
        Assert.Equal(3, index.Capacity);
        Assert.Empty(index.Search("number 0"));
        Assert.Empty(index.Search("number 1"));
        Assert.Single(index.Search("number 2"));
        Assert.Single(index.Search("number 4"));
    }

    [Fact]
    public void Remove_AndClear_DeleteIndexedDocuments()
    {
        var index = CreateIndex();

        index.IndexRange(new[]
        {
            Event("event-1", "alpha", Now.AddMinutes(-2)),
            Event("event-2", "beta", Now.AddMinutes(-1))
        });

        Assert.True(index.Remove("event-1"));
        Assert.False(index.Remove("event-1"));
        Assert.Equal(1, index.Count);
        Assert.Empty(index.Search("alpha"));
        Assert.Single(index.Search("beta"));

        index.Clear();

        Assert.Equal(0, index.Count);
        Assert.Empty(index.Search("beta"));
    }

    [Fact]
    public void Constructor_RejectsMissingRedactorAndInvalidCapacity()
    {
        Assert.Throws<ArgumentNullException>(() => new EventSearchIndex(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventSearchIndex(Redact, capacity: 0));
        Assert.Throws<ArgumentNullException>(() => CreateIndex().Index(null!));
        Assert.Throws<ArgumentNullException>(() => CreateIndex().IndexRange(null!));
    }

    [Fact]
    public void Search_AcrossOneHundredThousandEvents_StaysWithinTheP95LatencyBudget()
    {
        const int eventCount = 100_000;
        const int measuredQueries = 60;

        var filter = new SensitiveDataFilter();
        var index = new EventSearchIndex(filter.Redact, eventCount);
        var roles = ActivityRoleNames.All;
        var states = Enum.GetValues<ActivityEventState>();

        var events = new List<ActivityEvent>(eventCount);

        for (var i = 0; i < eventCount; i++)
        {
            events.Add(new ActivityEvent(
                $"event-{i}",
                Now.AddSeconds(-i),
                ActivityEventKind.Execution,
                roles[i % roles.Count],
                states[i % states.Length],
                i % 4 == 0 ? ActivityEventSource.Synthetic : ActivityEventSource.Native,
                $"Execution event-{i}",
                $"pipeline stage {i % 97} produced artifact chunk {i}"));
        }

        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var indexWatch = Stopwatch.StartNew();
        index.IndexRange(events);
        indexWatch.Stop();
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        var retainedMiB = (memoryAfter - memoryBefore) / (1024.0 * 1024.0);

        Assert.Equal(eventCount, index.Count);

        // Warm up the JIT and the ordinal search paths before measuring.
        for (var i = 0; i < 5; i++)
        {
            _ = index.Search($"unmatched-{i}");
        }

        var latencies = new double[measuredQueries];

        for (var i = 0; i < measuredQueries; i++)
        {
            var query = $"unmatched token {i}";

            var watch = Stopwatch.StartNew();
            _ = index.Search(query);
            watch.Stop();

            latencies[i] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(latencies);
        var p95 = latencies[(int)Math.Ceiling(0.95 * measuredQueries) - 1];
        var median = latencies[measuredQueries / 2];

        _output.WriteLine(
            "EventSearchIndex: indexed {0:N0} events in {1:N0} ms; retained {2:N1} MiB; search median {3:N2} ms, p95 {4:N2} ms, max {5:N2} ms.",
            eventCount,
            indexWatch.Elapsed.TotalMilliseconds,
            retainedMiB,
            median,
            p95,
            latencies[^1]);

        Assert.True(
            p95 <= 50.0,
            $"The p95 search latency must stay at or below 50 ms on 100,000 events; observed {p95:N2} ms.");

        // The measured queries were deliberately non-matching full scans; matching queries still work.
        Assert.NotEmpty(index.Search("artifact chunk"));
        Assert.Single(index.Search("chunk 99999"));
    }

    private static EventSearchIndex CreateIndex() => new(Redact);

    private static string Redact(string? text) => new SensitiveDataFilter().Redact(text);

    private static ActivityEvent Event(
        string id,
        string title,
        DateTimeOffset? occurredAt = null,
        string description = "") =>
        new(
            id,
            occurredAt ?? Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Running,
            ActivityEventSource.Native,
            title,
            description);
}
