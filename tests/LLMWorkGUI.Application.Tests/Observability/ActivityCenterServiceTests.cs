using System;
using System.Diagnostics;
using System.Linq;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;
using Xunit.Abstractions;
using ActivityEvent = LLMWorkGUI.Application.Observability.ActivityEvent;

namespace LLMWorkGUI.Application.Tests.Observability;

/// <summary>
/// Multi-criteria behaviour of the Activity Center aggregation service: time windows, roles, states,
/// provenance, full-text search through the redacting index, exact totals and bounded memory.
/// </summary>
public sealed class ActivityCenterServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly SensitiveDataFilter Filter = new();

    private readonly ITestOutputHelper _output;

    public ActivityCenterServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ThrowingAppendSubscriberDoesNotRejectStoredEventOrSkipOtherSubscribers()
    {
        var service = CreateService();
        var notified = new List<string>();
        service.Appended += (_, _) => throw new InvalidOperationException("synthetic subscriber failure");
        service.Appended += (_, args) => notified.Add(args.EventId);

        var failure = Record.Exception(() => service.Append(
            Event("accepted-event", Now, ActivityRoleNames.Coder, ActivityEventState.Completed)));

        Assert.Null(failure);
        Assert.Equal("accepted-event", Assert.Single(service.Snapshot()).Id);
        Assert.Equal(new[] { "accepted-event" }, notified);
        Assert.Equal(1, service.Statistics.Offered);
    }

    [Fact]
    public void Query_FiltersByTimeWindowRoleStateAndProvenance()
    {
        var service = CreateService();

        service.AppendRange(new[]
        {
            Event("event-architect", Now.AddMinutes(-5), ActivityRoleNames.Architect, ActivityEventState.Completed),
            Event("event-reviewer-running", Now.AddMinutes(-10), ActivityRoleNames.Reviewer, ActivityEventState.Running),
            Event("event-reviewer-failed", Now.AddHours(-2), ActivityRoleNames.Reviewer, ActivityEventState.Failed),
            Event("event-coder-synthetic", Now.AddHours(-30), ActivityRoleNames.Coder, ActivityEventState.Running, ActivityEventSource.Synthetic)
        });

        // Time window only.
        var lastHour = service.Query(new ActivityFilterCriteria { TimeRange = ActivityTimeRange.LastHour });
        Assert.Equal(2, lastHour.FilteredCount);
        Assert.Equal(4, lastHour.TotalCount);

        // Roles + states are combined with AND.
        var reviewerRunning = service.Query(new ActivityFilterCriteria
        {
            Roles = new[] { ActivityRoleNames.Reviewer },
            States = new[] { ActivityEventState.Running }
        });
        Assert.Equal(new[] { "event-reviewer-running" }, reviewerRunning.Items.Select(item => item.Id).ToArray());

        // Provenance filter keeps the synthetic event even though it is older than 24 hours.
        var synthetic = service.Query(new ActivityFilterCriteria { Source = ActivityEventSource.Synthetic });
        Assert.Equal(new[] { "event-coder-synthetic" }, synthetic.Items.Select(item => item.Id).ToArray());

        // All criteria combined can legitimately return nothing without inventing a count.
        var impossible = service.Query(new ActivityFilterCriteria
        {
            TimeRange = ActivityTimeRange.Last15Minutes,
            Roles = new[] { ActivityRoleNames.Coder },
            Source = ActivityEventSource.Native
        });
        Assert.Equal(0, impossible.FilteredCount);
        Assert.Equal(4, impossible.TotalCount);
        Assert.Empty(impossible.Items);
    }

    [Fact]
    public void Query_WithSearchQuery_UsesTheRedactingIndex_AndSecretValuesAreNotFindable()
    {
        var service = CreateService();

        const string apiKey = "sk-test-abcdef1234567890";
        const string password = "Hunter2Hunter2";

        service.Append(Event(
            "event-secret",
            Now,
            ActivityRoleNames.TechLead,
            ActivityEventState.Running,
            description: $"api_key={apiKey}; password={password}"));

        service.Append(Event(
            "event-clean",
            Now.AddMinutes(-1),
            ActivityRoleNames.Reviewer,
            ActivityEventState.Completed,
            description: "review completed with clean text"));

        // The operator can search ordinary text.
        Assert.Equal(new[] { "event-clean" }, service.Query(new ActivityFilterCriteria { SearchQuery = "review" }).Items.Select(item => item.Id).ToArray());

        // Secret values are never indexed, so they can never be returned by a query.
        Assert.Empty(service.Query(new ActivityFilterCriteria { SearchQuery = apiKey }).Items);
        Assert.Empty(service.Query(new ActivityFilterCriteria { SearchQuery = password }).Items);
        Assert.Empty(service.Query(new ActivityFilterCriteria { SearchQuery = "abcdef1234567890" }).Items);

        // The event is still present in the unfiltered stream and the redaction placeholder is searchable.
        Assert.Equal(2, service.Query().FilteredCount);
        Assert.Contains(
            service.Query(new ActivityFilterCriteria { SearchQuery = SensitiveDataFilter.Placeholder }).Items,
            item => item.Id == "event-secret");
    }

    [Fact]
    public void Query_TruncatesToLimitButReportsExactFilteredAndTotalCounts()
    {
        var service = CreateService();

        for (var i = 0; i < 10; i++)
        {
            service.Append(Event($"event-{i}", Now.AddMinutes(i - 10), ActivityRoleNames.Coder, ActivityEventState.Running));
        }

        var result = service.Query(new ActivityFilterCriteria { Limit = 3 });

        Assert.Equal(10, result.TotalCount);
        Assert.Equal(10, result.FilteredCount);
        Assert.Equal(3, result.Items.Count);
        Assert.True(result.IsTruncated);
        Assert.Equal(new[] { "event-9", "event-8", "event-7" }, result.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void Append_ReplacesDuplicateIds_AndEvictsTheOldestBeyondCapacity()
    {
        var service = CreateService(capacity: 3);

        service.Append(Event("event-1", Now.AddMinutes(-3), ActivityRoleNames.Coder, ActivityEventState.Running));
        service.Append(Event("event-2", Now.AddMinutes(-2), ActivityRoleNames.Coder, ActivityEventState.Running));
        service.Append(Event("event-1", Now.AddMinutes(-1), ActivityRoleNames.Coder, ActivityEventState.Completed, description: "replaced"));

        Assert.Equal(2, service.TotalCount);
        Assert.Equal("replaced", service.Query().Items.Single(item => item.Id == "event-1").Description);

        service.Append(Event("event-3", Now, ActivityRoleNames.Coder, ActivityEventState.Running));
        service.Append(Event("event-4", Now.AddMinutes(1), ActivityRoleNames.Coder, ActivityEventState.Running));

        Assert.Equal(3, service.TotalCount);
        Assert.DoesNotContain(service.Snapshot(), item => item.Id == "event-2");
        Assert.Equal(new[] { "event-4", "event-3", "event-1" }, service.Snapshot().Select(item => item.Id).ToArray());

        Assert.True(service.Remove("event-4"));
        Assert.False(service.Remove("event-4"));

        service.Clear();
        Assert.Equal(0, service.TotalCount);
        Assert.Empty(service.Query().Items);
    }

    [Fact]
    public void AppendProjection_MapsDomainStateAndSyntheticProvenance()
    {
        var service = CreateService();

        var native = new ObservableRunProjection(
            "exec-native",
            "session-1",
            WorkflowRole.Reviewer,
            "Reviewer",
            ExecutionState.Succeeded,
            "route-opencode",
            "route-opencode",
            "native-1",
            Now.AddMinutes(-20),
            Now.AddMinutes(-10),
            Now.AddMinutes(-10),
            EvidenceSourceKind.NativeProtocolEvent,
            isSynthetic: false);

        var synthetic = new ObservableRunProjection(
            "exec-synthetic",
            "session-1",
            WorkflowRole.Unknown,
            "Implementer",
            ExecutionState.RouteMismatch,
            "route-opencode",
            null,
            null,
            Now.AddMinutes(-8),
            Now.AddMinutes(-5),
            null,
            EvidenceSourceKind.SyntheticFixture,
            isSynthetic: true);

        service.AppendProjection(native);
        service.AppendProjection(synthetic);

        var events = service.Snapshot();

        Assert.Equal(ActivityEventState.Completed, events.Single(item => item.Id == "execution:exec-native").State);
        Assert.Equal(ActivityEventSource.Native, events.Single(item => item.Id == "execution:exec-native").Source);
        Assert.Equal(ActivityRoleNames.Reviewer, events.Single(item => item.Id == "execution:exec-native").Role);

        var syntheticEvent = events.Single(item => item.Id == "execution:exec-synthetic");
        Assert.Equal(ActivityEventState.Warning, syntheticEvent.State);
        Assert.Equal(ActivityEventSource.Synthetic, syntheticEvent.Source);
        Assert.Equal(ActivityRoleNames.Coder, syntheticEvent.Role);
    }

    [Fact]
    public void AppendTimelineAndWorkflowTimeline_ProjectSearchableEvents()
    {
        var service = CreateService();

        var projection = new ObservableRunProjection(
            "exec-1",
            "session-1",
            WorkflowRole.Coordinator,
            "Coordinator",
            ExecutionState.Running,
            "route-opencode",
            null,
            null,
            Now.AddMinutes(-10),
            Now.AddMinutes(-5),
            null,
            EvidenceSourceKind.NativeProtocolEvent,
            isSynthetic: false);

        service.AppendTimeline(new ActivityTimelineService().BuildTimeline(new[] { projection }));

        Assert.Equal("execution:exec-1", service.Snapshot().Single().Id);

        var transition = new WorkflowRunTimelineItem(
            sequence: 0,
            WorkflowRunTimelineItemKind.Transition,
            Now,
            "stage-review",
            "Reviewer",
            "route-opencode",
            "native-1",
            executionId: null,
            "Implementation diff delivered for review.",
            ExecutionState.Succeeded,
            EvidenceSourceKind.NativeProtocolEvent,
            Array.Empty<LLMWorkGUI.Domain.ValueObjects.ReviewerVerdictRecord>(),
            userApproval: null);

        service.AppendWorkflowTimeline(new WorkflowRunTimeline(
            LLMWorkGUI.Domain.Entities.WorkflowRun.Start(
                "run-1",
                "project-1",
                "pkg-1",
                "ver-1",
                "session-1",
                new LLMWorkGUI.Domain.ValueObjects.WorkflowStageDefinition(
                    "stage-implementation",
                    "Implementation",
                    "Implementer",
                    WorkflowStageKind.Custom,
                    Array.Empty<string>(),
                    requiresUserApproval: false,
                    artifactRequirement: null,
                    nextStageId: "stage-review",
                    failureStageId: null),
                Now.AddHours(-1)),
            new[] { transition }));

        Assert.Equal(2, service.TotalCount);
        Assert.Single(service.Query(new ActivityFilterCriteria { SearchQuery = "diff delivered" }).Items);
    }

    [Theory]
    [InlineData("Healthy", ActivityEventState.Completed)]
    [InlineData("Degraded", ActivityEventState.Warning)]
    [InlineData("CoolingDown", ActivityEventState.Warning)]
    [InlineData("QuarantinedAuto", ActivityEventState.Failed)]
    [InlineData("DisabledManual", ActivityEventState.Failed)]
    [InlineData("ProbeRequired", ActivityEventState.Warning)]
    [InlineData("Recovering", ActivityEventState.Completed)]
    [InlineData("ForcedEnabled", ActivityEventState.Completed)]
    [InlineData("Unavailable", ActivityEventState.Failed)]
    [InlineData("Probing", ActivityEventState.Warning)]
    [InlineData("Cooldown", ActivityEventState.Warning)]
    [InlineData("LegacyReadyLabel", ActivityEventState.Completed)]
    public void AppendHealthTransition_MapsCanonicalStatesAndPreservesLegacyDisplays(
        string stateDisplay, ActivityEventState expectedState)
    {
        var service = CreateService();

        service.AppendHealthTransition("route", stateDisplay, "health detail");

        Assert.Equal(expectedState, Assert.Single(service.Snapshot()).State);
    }

    [Fact]
    public void AppendHealthTransitionUserActionAndSystemEvent_UseTheInjectedClock()
    {
        var service = CreateService();

        service.AppendHealthTransition("route-gpt4o", "QuarantinedAuto", "Probe required before recovery.");
        service.AppendUserAction("reset-filters", "Filters reset", "The operator reset the activity filters.");
        service.AppendSystemEvent("retention", "Retention run completed", "No active artifacts were removed.");

        var snapshot = service.Snapshot();

        Assert.Equal(3, snapshot.Count);

        var health = snapshot.Single(item => item.Kind == ActivityEventKind.Health);
        Assert.Equal(ActivityEventState.Failed, health.State);
        Assert.Equal(ActivityRoleNames.System, health.Role);

        Assert.Equal(ActivityEventKind.UserAction, snapshot.Single(item => item.Kind == ActivityEventKind.UserAction).Kind);
        Assert.Equal(ActivityEventState.Warning, snapshot.Single(item => item.Kind == ActivityEventKind.System).State);
        Assert.All(snapshot, item => Assert.Equal(Now, item.OccurredAtUtc));
    }

    [Fact]
    public void Query_AtHundredThousandEvents_StaysWithinTheNormativeInteractionBudget()
    {
        const int eventCount = 100_000;
        const int measuredQueries = 30;

        var service = CreateService(capacity: eventCount);
        var roles = ActivityRoleNames.All;
        var states = Enum.GetValues<ActivityEventState>();

        var events = new ActivityEvent[eventCount];

        for (var i = 0; i < eventCount; i++)
        {
            events[i] = Event(
                $"event-{i}",
                Now.AddSeconds(-i),
                roles[i % roles.Count],
                states[i % states.Length],
                i % 4 == 0 ? ActivityEventSource.Synthetic : ActivityEventSource.Native);
        }

        var appendWatch = Stopwatch.StartNew();
        service.AppendRange(events);
        appendWatch.Stop();

        Assert.Equal(eventCount, service.TotalCount);

        var criteriaSet = new ActivityFilterCriteria[measuredQueries];

        for (var i = 0; i < measuredQueries; i++)
        {
            criteriaSet[i] = new ActivityFilterCriteria
            {
                TimeRange = (ActivityTimeRange)(i % 4),
                Roles = new[] { roles[i % roles.Count] },
                States = new[] { states[i % states.Length] },
                Source = i % 3 == 0 ? ActivityEventSource.Native : null
            };
        }

        _ = service.Query(criteriaSet[0]); // warm-up

        var latencies = new double[measuredQueries];

        for (var i = 0; i < measuredQueries; i++)
        {
            var watch = Stopwatch.StartNew();
            _ = service.Query(criteriaSet[i]);
            watch.Stop();

            latencies[i] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(latencies);
        var p95 = latencies[(int)Math.Ceiling(0.95 * measuredQueries) - 1];

        _output.WriteLine(
            "ActivityCenterService: appended {0:N0} events in {1:N0} ms; multi-criteria query median {2:N2} ms, p95 {3:N2} ms, max {4:N2} ms.",
            eventCount,
            appendWatch.Elapsed.TotalMilliseconds,
            latencies[measuredQueries / 2],
            p95,
            latencies[^1]);

        Assert.True(
            p95 <= 200.0,
            $"The p95 multi-criteria query latency must stay at or below the normative 200 ms; observed {p95:N2} ms.");
    }

    [Fact]
    public void Constructor_AndArgumentGuards_RejectInvalidInput()
    {
        Assert.Throws<ArgumentNullException>(() => new ActivityCenterService(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityCenterService(Filter.Redact, capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ActivityCenterService(Filter.Redact, capacity: 10, retentionLimit: -1));

        var service = CreateService();

        Assert.Throws<ArgumentNullException>(() => service.Append(null!));
        Assert.Throws<ArgumentNullException>(() => service.AppendRange(null!));
        Assert.Throws<ArgumentNullException>(() => service.AppendProjection(null!));
        Assert.Throws<ArgumentNullException>(() => service.AppendTimeline(null!));
        Assert.Throws<ArgumentNullException>(() => service.AppendWorkflowTimeline(null!));
        Assert.Throws<ArgumentNullException>(() => service.Remove(null!));
    }

    [Fact]
    public async Task AFullWriteQueueIsReportedAsQueueLossAndNeverAsJournalRetention()
    {
        var journal = new BlockingJournal();
        var queue = new ActivityJournalWriteQueue(journal, capacity: 2, batchSize: 2);
        var service = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now), capacity: 100, journalQueue: queue);

        for (var sequence = 0; sequence < 40; sequence++)
        {
            service.Append(Event(
                $"queued-{sequence:D3}",
                Now.AddSeconds(sequence),
                ActivityRoleNames.Coder,
                ActivityEventState.Running));
        }

        journal.ReleaseBlockedWrite();
        await queue.DrainAsync();

        var statistics = service.Statistics;

        // Queue loss and durable retention are different failures and must never be merged: telling an
        // operator their history was deliberately trimmed when the product in fact failed to write it is
        // the specific mislabelling this asserts against.
        Assert.True(statistics.IsDurablyLossy);
        Assert.Equal(0, statistics.JournalEvicted);
        Assert.Equal("0", statistics.ToFacts()["journalEvicted"]);
        Assert.True(statistics.QueueDropped > 0);
        Assert.Equal(statistics.QueueDropped.ToString(System.Globalization.CultureInfo.InvariantCulture), statistics.ToFacts()["queueDropped"]);
        Assert.Equal(queue.DroppedCount, statistics.QueueDropped);
        Assert.Equal(queue.FailedCount, statistics.QueueFailed);

        var label = statistics.OverflowDisplay;
        Assert.Contains(statistics.QueueDropped.ToString(System.Globalization.CultureInfo.InvariantCulture), label, StringComparison.Ordinal);
        Assert.DoesNotContain("вытеснено", label, StringComparison.Ordinal);

        await queue.DisposeAsync();
    }

    [Fact]
    public void TheWindowEvictsTheOldestOngoingRowRatherThanATerminalOutcome()
    {
        // A bounded window has to drop something. Dropping purely by age throws away the one row an
        // operator needs most - an early failure - in favour of a still-running observation that will
        // report again.
        var service = CreateService(capacity: 2);

        service.Append(Event("running-old", Now, ActivityRoleNames.Coder, ActivityEventState.Running));
        service.Append(Event("failed-early", Now.AddSeconds(1), ActivityRoleNames.Coder, ActivityEventState.Failed));
        service.Append(Event("running-new", Now.AddSeconds(2), ActivityRoleNames.Coder, ActivityEventState.Running));

        var retained = service.Snapshot().Select(item => item.Id).ToArray();

        Assert.Equal(2, retained.Length);
        Assert.Contains("failed-early", retained);
        Assert.Contains("running-new", retained);
        Assert.DoesNotContain("running-old", retained);
        Assert.Equal(1, service.Statistics.Evicted);

        // When every retained row is terminal, the oldest still goes: the bound is the bound.
        var terminal = CreateService(capacity: 2);

        terminal.Append(Event("failed-old", Now, ActivityRoleNames.Coder, ActivityEventState.Failed));
        terminal.Append(Event("failed-new", Now.AddSeconds(1), ActivityRoleNames.Coder, ActivityEventState.Failed));
        terminal.Append(Event("failed-newest", Now.AddSeconds(2), ActivityRoleNames.Coder, ActivityEventState.Cancelled));

        Assert.Equal(
            new[] { "failed-newest", "failed-new" },
            terminal.Snapshot().Select(item => item.Id).ToArray());
        Assert.Equal(1, terminal.Statistics.Evicted);
    }

    [Fact]
    public void AMemoryOnlyServiceSaysSoAndNeverClaimsADurableHistory()
    {
        var service = CreateService(capacity: 2);

        for (var sequence = 0; sequence < 3; sequence++)
        {
            service.Append(Event(
                $"memory-{sequence}",
                Now.AddSeconds(sequence),
                ActivityRoleNames.Coder,
                ActivityEventState.Completed));
        }

        var statistics = service.Statistics;

        Assert.Null(statistics.JournalRetained);
        Assert.False(service.IsDurable);
        Assert.Contains("журнал не подключён", statistics.OverflowDisplay, StringComparison.Ordinal);
        Assert.Equal("none", statistics.ToFacts()["journalRetained"]);
    }

    [Fact]
    public async Task ApplyingRetentionWithoutAJournalIsRefusedRatherThanFaked()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyJournalRetentionAsync());

        // Nothing durable is composed, so there is nothing to restore either, and the report says so
        // instead of reporting a successful load of zero rows.
        var load = await service.LoadAsync();

        Assert.Same(ActivityCenterLoadReport.NotDurable, load);
        Assert.Equal(0, load.RetainedRows);
        Assert.Equal(0, load.Reloaded);
    }

    [Fact]
    public void DurableWindow_DoesNotKeepAnUnusedSecondSearchIndex()
    {
        var index = new EventSearchIndex(Filter.Redact, capacity: 2);
        var service = new ActivityCenterService(Filter.Redact, new FixedTimeProvider(Now),
            searchIndex: index, capacity: 2, journal: new BlockingJournal());
        service.Append(Event("old", Now, ActivityRoleNames.Coder, ActivityEventState.Running));
        service.Append(Event("terminal", Now, ActivityRoleNames.Reviewer, ActivityEventState.Failed));
        service.Append(Event("new", Now, ActivityRoleNames.Coder, ActivityEventState.Completed));
        Assert.Equal(0, index.Count);
        Assert.Equal(new[] { "new", "terminal" }, service.Snapshot().Select(item => item.Id));
        service.Clear();
        service.Append(Event("after-clear", Now, ActivityRoleNames.Coder, ActivityEventState.Running));
        Assert.Equal("after-clear", Assert.Single(service.Snapshot()).Id);
    }

    private static ActivityCenterService CreateService(int capacity = ActivityCenterService.DefaultCapacity) =>
        new(Filter.Redact, new FixedTimeProvider(Now), capacity: capacity);

    private static ActivityEvent Event(
        string id,
        DateTimeOffset occurredAt,
        string role,
        ActivityEventState state,
        ActivityEventSource source = ActivityEventSource.Native,
        string description = "") =>
        new(
            id,
            occurredAt,
            ActivityEventKind.Execution,
            role,
            state,
            source,
            $"Event {id}",
            description);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    /// <summary>
    /// A journal whose durable write blocks until released, so a two-deep queue genuinely overflows. The
    /// accounting assertions below are about what the product reports, so the fault has to be real rather
    /// than simulated by a counter.
    /// </summary>
    private sealed class BlockingJournal : IActivityEventJournal
    {
        private readonly List<string> _ids = new();
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseBlockedWrite() => _release.TrySetResult(true);

        public Task AppendAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default) =>
            AppendRangeAsync(new[] { activityEvent }, cancellationToken);

        public async Task AppendRangeAsync(
            IReadOnlyList<ActivityEvent> activityEvents,
            CancellationToken cancellationToken = default)
        {
            _blocked.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_ids)
            {
                foreach (var activityEvent in activityEvents)
                {
                    _ids.Add(activityEvent.Id);
                }
            }
        }

        public Task<IReadOnlyList<ActivityEvent>> LoadNewestAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActivityEvent>>(Array.Empty<ActivityEvent>());

        public Task<long> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((long)_ids.Count);

        public Task<ActivityJournalTrimResult> TrimAsync(
            int retentionLimit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActivityJournalTrimResult(_ids.Count, _ids.Count, 0));

        public ActivityJournalPage QueryPage(ActivityJournalQuery query) =>
            throw new NotSupportedException("This double only models the write path.");
    }
}
