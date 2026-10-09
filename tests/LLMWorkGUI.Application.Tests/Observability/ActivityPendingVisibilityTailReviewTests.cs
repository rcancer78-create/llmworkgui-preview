using LLMWorkGUI.ActivityLoadDriver;
using LLMWorkGUI.Application.Observability;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class ActivityPendingVisibilityTailReviewTests
{
    [Fact]
    public void AcceptedButNotYetVisibleTailPreventsCompleteMeasurement()
    {
        var (recorder, _) = AcceptTwoAndShowFirst();

        Assert.Equal(1, recorder.ObservedCount);
        Assert.Equal(1, recorder.SampleCount);
        Assert.Equal(0, recorder.DroppedPendingCount);
        Assert.False(recorder.IsComplete);
    }

    [Fact]
    public void AcceptedPendingTailIsReportedAsPendingRatherThanLostOrVisible()
    {
        var (recorder, _) = AcceptTwoAndShowFirst();

        var facts = recorder.ToFacts();
        Assert.True(facts.TryGetValue("pending", out var pending), "The unresolved accepted tail must appear in measurement facts.");
        Assert.Equal("1", pending);
        Assert.Equal("1", facts["observed"]);
        Assert.Equal("0", facts["droppedPending"]);
    }

    [Fact]
    public void PendingTailMakesActualLatencyCriterionNotTestedEvenWhenVisiblePrefixIsFast()
    {
        var (recorder, _) = AcceptTwoAndShowFirst();
        var measured = ActivityLatencyStatistics.FromSamples(
            recorder.DrainSamples().Select(sample => sample.Milliseconds).ToArray(), recorder.ObservedCount,
            recorder.DiscardedSampleCount, recorder.DroppedPendingCount);
        // Exercise the existing public statistics completeness handoff and actual linked criterion.
        // This is not an end-to-end test of the WPF driver's private HarvestLatency method.
        var report = new ActivityLoadReport(ActivityLoadOptions.Parse([]), DateTimeOffset.UnixEpoch)
        {
            VisibilityLatency = new ActivityLatencyStatistics(measured.SampleCount, measured.ObservedCount,
                measured.P50Milliseconds, measured.P95Milliseconds, measured.MaxMilliseconds,
                measured.MinMilliseconds, measured.MeanMilliseconds, measured.DiscardedSamples,
                measured.DroppedPending, recorder.IsComplete)
        };

        ActivityLoadCriteria.Evaluate(report, []);

        var criterion = Assert.Single(report.ToMarkdown().Split('\n'),
            line => line.StartsWith("| ui-event-latency |", StringComparison.Ordinal));
        Assert.Contains("**NOT_TESTED**", criterion, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", criterion, StringComparison.Ordinal);
    }

    [Fact]
    public void LateVisibilityCompletesTailAndResetSeparatesPendingAndWorkerMeasurementWindows()
    {
        var (recorder, clock) = AcceptTwoAndShowFirst();
        recorder.AddQueryWorkSample(12);
        recorder.AddDispatcherWorkSample(3);
        clock.Milliseconds = 20;
        recorder.MarkVisible("owned-second");

        Assert.True(recorder.IsComplete);
        Assert.Equal(2, recorder.ObservedCount);
        Assert.Equal(new[] { 10d, 20d }, recorder.DrainSamples().Select(sample => sample.Milliseconds));
        Assert.Equal(new[] { 12d }, recorder.DrainQueryWorkSamples());
        Assert.Equal(new[] { 3d }, recorder.DrainDispatcherWorkSamples());

        recorder.RecordIngested("owned-before-reset", recorder.Stamp());
        recorder.AddQueryWorkSample(99);
        recorder.AddDispatcherWorkSample(98);
        recorder.Reset();
        recorder.MarkVisible("owned-before-reset");
        Assert.True(recorder.IsComplete);
        Assert.Equal(0, recorder.ObservedCount);
        Assert.Empty(recorder.DrainSamples());
        Assert.Empty(recorder.DrainQueryWorkSamples());
        Assert.Empty(recorder.DrainDispatcherWorkSamples());
        Assert.Equal(0, recorder.QueryObservedCount);
        Assert.Equal(0, recorder.DispatcherObservedCount);
    }

    private static (ActivityVisibilityLatencyRecorder Recorder, ManualClock Clock) AcceptTwoAndShowFirst()
    {
        var clock = new ManualClock();
        var recorder = new ActivityVisibilityLatencyRecorder(clock);
        var service = new ActivityCenterService(text => text, clock);
        service.Appended += (_, notice) => recorder.RecordIngested(notice.EventId, notice.IngestedTimestamp);
        service.Append(Event("owned-first"));
        service.Append(Event("owned-second"));
        Assert.Equal(2, service.Statistics.Offered);
        clock.Milliseconds = 10;
        recorder.MarkVisible("owned-first");
        return (recorder, clock);
    }

    private static ActivityEvent Event(string id) => new(id, DateTimeOffset.UnixEpoch,
        ActivityEventKind.System, ActivityRoleNames.System, ActivityEventState.Completed,
        ActivityEventSource.Synthetic, "Owned measurement event", "Synthetic recorder boundary fixture");

    private sealed class ManualClock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Milliseconds);
    }
}
