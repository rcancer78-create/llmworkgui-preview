using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class ActivityReviewRegressionTests
{
    [Fact]
    public void ProjectionReplacementKeepsOfferedCountWithoutReportingMemoryLoss()
    {
        var service = new ActivityCenterService(text => text, capacity: 2);
        service.AppendProjection(Projection(ExecutionState.Running));
        service.AppendProjection(Projection(ExecutionState.Succeeded));

        Assert.Equal(2, service.Statistics.Offered);
        Assert.Equal(1, service.Statistics.Retained);
        Assert.Equal(0, service.Statistics.Evicted);
        Assert.False(service.Statistics.IsLossy);
        Assert.False(service.Statistics.HasOverflowed);
        Assert.Equal(1, service.Statistics.Replaced);
        Assert.Equal("1", service.Statistics.ToFacts()["replaced"]);
        Assert.Equal(ActivityEventState.Completed, Assert.Single(service.Snapshot()).State);
    }

    [Fact]
    public void CoalescedProjectionLatencyUsesTheReplacementIngestionTimestamp()
    {
        var clock = new ManualClock();
        var recorder = new ActivityVisibilityLatencyRecorder(clock);
        var service = new ActivityCenterService(text => text, clock);
        service.Appended += (_, notice) => recorder.RecordIngested(notice.EventId, notice.IngestedTimestamp);
        service.AppendProjection(Projection(ExecutionState.Running));
        clock.Milliseconds = 90;
        service.AppendProjection(Projection(ExecutionState.Succeeded));
        clock.Milliseconds = 100;

        recorder.MarkVisible("execution:execution");

        Assert.Equal(10d, Assert.Single(recorder.DrainSamples()).Milliseconds);
        Assert.Equal(1, recorder.ObservedCount);
        clock.Milliseconds = 110;
        service.AppendProjection(Projection(ExecutionState.Succeeded));
        clock.Milliseconds = 115;
        recorder.MarkVisible("execution:execution");
        Assert.Equal(5d, Assert.Single(recorder.DrainSamples()).Milliseconds);
        Assert.Equal(2, recorder.ObservedCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MissingVisibleEventSamplesCannotBeClaimedComplete(int sampleCount)
    {
        var samples = Enumerable.Repeat(5d, sampleCount).ToArray();
        var statistics = ActivityLatencyStatistics.FromSamples(samples, observedCount: 2);
        Assert.False(statistics.IsComplete);
        Assert.Contains("INCOMPLETE", statistics.Describe("visibility"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidLatencyCannotEnterMeasurementReport(double invalid)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ActivityLatencyStatistics.FromSamples([invalid]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedWorkerSeriesReportsLossInsteadOfACompleteEarlyPrefix(bool query)
    {
        var recorder = new ActivityVisibilityLatencyRecorder(TimeProvider.System, sampleCapacity: 1);
        if (query) { recorder.AddQueryWorkSample(1); recorder.AddQueryWorkSample(1000); }
        else { recorder.AddDispatcherWorkSample(1); recorder.AddDispatcherWorkSample(1000); }

        Assert.False(recorder.IsComplete);
        var key = query ? "queryDiscarded" : "dispatcherDiscarded";
        Assert.True(recorder.ToFacts().TryGetValue(key, out var discarded));
        Assert.Equal("1", discarded);
        Assert.Single(query ? recorder.DrainQueryWorkSamples() : recorder.DrainDispatcherWorkSamples());
        Assert.False(recorder.IsComplete);
        recorder.Reset();
        Assert.True(recorder.IsComplete);
        Assert.Equal("0", recorder.ToFacts()[key]);
    }

    [Fact]
    public void RoundedEventCountDoesNotMakeAShortDurationNormative()
    {
        var profile = new ActivityLoadProfile(duration: TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1));
        Assert.Equal(ActivityLoadProfile.NormativeFullOfferedEvents, profile.OfferedEvents);
        Assert.False(profile.IsFullDuration);
        Assert.False(profile.IsNormativeFullProfile);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void TinyDiagnosticDistributionStillExercisesTheSupportedMaximum(int count)
    {
        var distribution = ActivityMessageSizeDistribution.ForStream(count);
        Assert.Equal(1, distribution.MaximumMessageCount);
        Assert.InRange(distribution.Stride, 1, count);
        Assert.Equal(ActivityLoadProfile.MaxMessageBytes, distribution.SizeOfLargeMessage(0));
        Assert.Equal(ActivityMessageSizeClass.Maximum, distribution.ClassOf(0));
        Assert.Equal(count, distribution.SmallMessageCount + distribution.OverSmallKiBCount);
    }

    private static ObservableRunProjection Projection(ExecutionState state) => new(
        "execution", "session", WorkflowRole.Executor, "Coder", state, "route", null, null,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, EvidenceSourceKind.SyntheticFixture, true);

    private sealed class ManualClock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Milliseconds);
    }
}
