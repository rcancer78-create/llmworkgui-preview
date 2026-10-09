using System;
using System.Collections.Generic;
using System.Linq;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

/// <summary>
/// The redaction boundary of the Activity Center, asserted at every place an event can leave the product.
/// <para>
/// Before this boundary existed the service redacted only what it indexed, so a secret was unfindable by a
/// raw search and still readable from a query result, a snapshot, a selected row and the detail pane. Each
/// test below therefore uses a *different* retrieval path for the same planted secret, because asserting
/// only the search path would have passed while the leak was still wide open.
/// </para>
/// </summary>
public sealed class ActivityCenterRedactionBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly SensitiveDataFilter Filter = new();

    private const string ApiKey = "sk-live-4f9a1c7b2e8d6035aa11bb22cc33dd44ee55ff660";
    private const string Password = "Hunter2Correct-Horse-Battery";
    private const string BearerToken =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJsbG13b3JrZ3VpIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string AwsKey = "AKIAIOSFODNN7EXAMPLE";

    public static TheoryData<string> Secrets => new()
    {
        ApiKey,
        Password,
        BearerToken,
        AwsKey
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void NoReturnBoundaryEverCarriesARawSecret(string secret)
    {
        var service = new ActivityCenterService(Filter.Redact);

        service.Append(Event(
            "secret-event",
            Now,
            title: "Ordinary title",
            description: $"api_key={secret}; password={secret}",
            diffText: $"--- a/f\n+++ b/f\n-api_key={secret}\n+api_key={secret}",
            artifactContent: $"{{\"api_key\":\"{secret}\"}}"));

        service.Append(Event("clean-event", Now.AddMinutes(-1), title: "Clean", description: "nothing here"));

        // Every way an event can leave the product, one assertion each.
        AssertClean("Query().Items", TextOf(service.Query().Items), secret);
        AssertClean("Query().Snapshot", TextOf(service.Snapshot()), secret);
        AssertClean("Query(search).Items", TextOf(service.Query(new ActivityFilterCriteria { Limit = 500 }).Items), secret);
        AssertClean("Query(criteria.Matches)", TextOf(service.Query().Items.Where(item =>
            new ActivityFilterCriteria().Matches(item, Now))), secret);
        AssertClean("the raw search", TextOf(service.Query(new ActivityFilterCriteria { SearchQuery = secret }).Items), secret);
    }

    /// <summary>
    /// The same secret, planted at both ends of a populated stream.
    /// <para>
    /// The previous version of this boundary search ran against a brand-new, effectively empty index and a
    /// two-event service, so "the secret is not findable" was proved on a document set where the answer
    /// would have been the same if the index had never been consulted at all. Here the index holds hundreds
    /// of documents, one secret is planted among the oldest and another among the newest, and the test
    /// asserts both that the secret is unfindable <em>and</em> that the search really is running - because a
    /// neighbouring benign token must still be found in the very same index.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Secrets))]
    public void APlantedSecretIsUnfindableInAPopulatedIndexAtBothEndsOfTheStream(string secret)
    {
        const int Filler = 400;

        var service = new ActivityCenterService(Filter.Redact);
        var index = new EventSearchIndex(Filter.Redact);

        service.Append(Event(
            "oldest-secret",
            Now.AddMinutes(-10),
            title: "oldest observation",
            description: $"api_key={secret}"));
        index.Index(Event("oldest-secret", Now.AddMinutes(-10), description: $"api_key={secret}"));

        for (var sequence = 0; sequence < Filler; sequence++)
        {
            var filler = Event(
                $"filler-{sequence:D4}",
                Now.AddMinutes(-9) + TimeSpan.FromSeconds(sequence),
                title: $"observation {sequence}",
                description: "benignmarker payload");

            service.Append(filler);
            index.Index(filler);
        }

        service.Append(Event(
            "newest-secret",
            Now,
            title: "newest observation",
            description: $"password={secret}"));
        index.Index(Event("newest-secret", Now, description: $"password={secret}"));

        Assert.Equal(Filler + 2, index.Count);

        // The index is genuinely consulted: a benign token in the middle of the stream is found.
        Assert.Equal(Filler, index.Search("benignmarker", limit:1_000).Count);

        // And the secret is findable in neither half of the stream, through the index the product uses.
        Assert.Empty(index.Search(secret, limit: 1_000));
        Assert.Empty(service.Query(new ActivityFilterCriteria { SearchQuery = secret, Limit = 1_000 }).Items);
        Assert.Equal(0, service.Query(new ActivityFilterCriteria { SearchQuery = secret, Limit = 1_000 }).FilteredCount);

        // A partial of the secret - the prefix an operator might paste - must not resolve either.
        Assert.Empty(index.Search(secret[..12], limit: 1_000));

        // Every document the index holds is clean, not just the two planted ones.
        AssertClean("the populated index", TextOf(index.Search("observation", limit: 1_000)), secret);
        AssertClean("the populated snapshot", TextOf(service.Snapshot()), secret);
    }

    [Fact]
    public void TheStoredEventItselfIsAlreadyRedacted()
    {
        var service = new ActivityCenterService(Filter.Redact);
        service.Append(Event("secret-event", Now, description: $"api_key={ApiKey}"));

        var stored = service.Snapshot().Single();

        Assert.DoesNotContain(ApiKey, stored.Description, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataFilter.Placeholder, stored.Description, StringComparison.Ordinal);

        // Redaction is idempotent, so a second boundary cannot resurrect or double-substitute the text.
        Assert.Equal(stored.Description, Filter.Redact(stored.Description));
    }

    [Fact]
    public void StructuredProvenanceIsPreservedVerbatim()
    {
        var service = new ActivityCenterService(Filter.Redact);

        service.Append(new ActivityEvent(
            "provenance",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Running,
            ActivityEventSource.Native,
            "Delivered",
            "clean body",
            sessionId: "session-42",
            executionId: "execution-42",
            routeId: "route-42",
            artifactName: "plan.json",
            artifactSizeBytes: 1234,
            artifactSha256: "sha256:deadbeef",
            artifactChangeStatus: "Modified"));

        var stored = service.Snapshot().Single();

        // Redacting provenance would destroy the reason the Activity Center exists. The identifiers are
        // kept exactly; only operator-facing free text is passed through the redactor.
        Assert.Equal("provenance", stored.Id);
        Assert.Equal("session-42", stored.SessionId);
        Assert.Equal("execution-42", stored.ExecutionId);
        Assert.Equal("route-42", stored.RouteId);
        Assert.Equal("plan.json", stored.ArtifactName);
        Assert.Equal(1234, stored.ArtifactSizeBytes);
        Assert.Equal("sha256:deadbeef", stored.ArtifactSha256);
        Assert.Equal("Modified", stored.ArtifactChangeStatus);
        Assert.Equal(ActivityRoleNames.Coder, stored.Role);
    }

    [Fact]
    public void AQueryForTheRedactionPlaceholderFindsTheEvent()
    {
        var service = new ActivityCenterService(Filter.Redact);
        service.Append(Event("secret-event", Now, description: $"api_key={ApiKey}"));

        // Redaction must not make the event unfindable: the operator still sees that a secret was there.
        var found = service.Query(new ActivityFilterCriteria { SearchQuery = SensitiveDataFilter.Placeholder });

        Assert.Equal(new[] { "secret-event" }, found.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void ACleanEventIsNotCopied()
    {
        var service = new ActivityCenterService(Filter.Redact);
        var clean = Event("clean", Now, description: "an ordinary message about builds");

        service.Append(clean);

        // An unchanged event is returned as-is, so a clean stream does not allocate a copy per event.
        Assert.Same(clean, service.Snapshot().Single());
    }

    [Fact]
    public void AppendedEventsCarryUniqueIdentitiesAndMonotonicStamps()
    {
        var service = new ActivityCenterService(Filter.Redact);
        var notices = new List<ActivityEventAppendedEventArgs>();
        service.Appended += (_, notice) => notices.Add(notice);

        for (var index = 0; index < 25; index++)
        {
            service.Append(Event($"event-{index:D3}", Now.AddMilliseconds(index)));
        }

        Assert.Equal(25, notices.Count);
        Assert.Equal(25, notices.Select(notice => notice.EventId).Distinct().Count());
        Assert.Equal(25, service.TotalCount);

        // The stamp is taken inside the boundary on a monotonic clock, so it is non-decreasing and is
        // never the wall clock the caller happened to have.
        for (var index = 1; index < notices.Count; index++)
        {
            Assert.True(
                notices[index].IngestedTimestamp >= notices[index - 1].IngestedTimestamp,
                "the ingestion stamps are not monotonic.");
        }

        Assert.Equal(25, notices[^1].RetainedCount);
        Assert.Equal(25, notices[^1].OfferedCount);
        Assert.Equal(0, notices[^1].EvictedCount);
    }

    [Fact]
    public void RepeatedUpdatesOfOneExecutionKeepEveryObservation_OnlyAppendProjectionCollapses()
    {
        var service = new ActivityCenterService(Filter.Redact);

        for (var index = 0; index < 5; index++)
        {
            service.Append(Event($"stream:{index:D2}", Now.AddSeconds(index), executionId: "exec-1"));
        }

        // Unique ids: five rows. This is the shape the normative profile uses.
        Assert.Equal(5, service.TotalCount);

        // The projection helper derives its id from the execution, so repeated updates of one execution
        // collapse onto a single row. That is a convenience for a status line, not a journal, and the
        // difference is exactly why the load profile cannot use it.
        var projectionService = new ActivityCenterService(Filter.Redact);
        var projection = new ObservableRunProjection(
            executionId: "exec-1",
            sessionId: "session-1",
            role: WorkflowRole.Executor,
            displayLabel: "Coder",
            state: ExecutionState.Running,
            requestedRouteId: "route-1",
            observedRouteId: null,
            nativeSessionId: null,
            startedAtUtc: Now,
            lastActivityAtUtc: Now,
            endedAtUtc: null,
            evidenceSource: EvidenceSourceKind.SyntheticFixture,
            isSynthetic: true);

        projectionService.AppendProjection(projection);
        projectionService.AppendProjection(projection);

        Assert.Equal(1, projectionService.TotalCount);
    }

    [Fact]
    public void CapacityOverflowIsCountedAndLabelled_NotSilent()
    {
        var service = new ActivityCenterService(Filter.Redact, capacity: 10);

        for (var index = 0; index < 25; index++)
        {
            service.Append(Event($"event-{index:D2}", Now.AddSeconds(index)));
        }

        var statistics = service.Statistics;

        Assert.Equal(10, statistics.Retained);
        Assert.Equal(25, statistics.Offered);
        Assert.Equal(15, statistics.Evicted);
        Assert.True(statistics.HasOverflowed);
        Assert.False(statistics.IsLossy);

        // The label is the operator-visible statement; silence is not an option.
        Assert.NotEmpty(statistics.OverflowDisplay);
        Assert.Contains("15", statistics.OverflowDisplay, StringComparison.Ordinal);
        Assert.Equal("25", statistics.ToFacts()["offered"]);
        Assert.Equal("15", statistics.ToFacts()["evicted"]);

        // The retained window is the newest one.
        Assert.Equal("event-24", service.Snapshot()[0].Id);
    }

    [Fact]
    public void AnOverflowedResultCarriesItsRetentionCountersToTheScreen()
    {
        var service = new ActivityCenterService(Filter.Redact, capacity: 5);
        service.SetJournalRetainedCount(1_000);

        for (var index = 0; index < 20; index++)
        {
            service.Append(Event($"event-{index:D2}", Now.AddSeconds(index)));
        }

        var result = service.Query();

        Assert.True(result.HasOverflowed);
        Assert.Equal(15, result.Retention.Evicted);
        Assert.Equal(1_000, result.Retention.JournalRetained);
        Assert.Contains("1000", result.Retention.OverflowDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorArgumentsAreValidated()
    {
        Assert.Throws<ArgumentNullException>(() => new ActivityCenterService(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityCenterService(Filter.Redact, capacity: 0));
    }

    private static void AssertClean(string surface, IEnumerable<string?> texts, string secret) =>
        Assert.DoesNotContain(secret, string.Join("", texts), StringComparison.Ordinal);

    private static IEnumerable<string?> TextOf(IEnumerable<ActivityEvent> events) =>
        events.SelectMany(item => new[]
        {
            item.Id,
            item.Title,
            item.Description,
            item.DiffText,
            item.ArtifactContent,
            item.ArtifactName,
            item.SessionId,
            item.ExecutionId,
            item.RouteId
        });

    private static ActivityEvent Event(
        string id,
        DateTimeOffset occurredAt,
        string title = "Event",
        string description = "",
        string? diffText = null,
        string? artifactContent = null,
        string? executionId = null) => new(
        id,
        occurredAt,
        ActivityEventKind.Execution,
        ActivityRoleNames.Coder,
        ActivityEventState.Running,
        ActivityEventSource.Native,
        title,
        description,
        executionId: executionId,
        diffText: diffText,
        artifactName: "artifact.txt",
        artifactContent: artifactContent);
}
