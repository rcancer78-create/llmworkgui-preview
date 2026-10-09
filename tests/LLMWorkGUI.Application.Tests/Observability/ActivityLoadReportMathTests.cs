using System;
using System.Globalization;
using System.Linq;
using LLMWorkGUI.Application.Observability;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

/// <summary>
/// The arithmetic a measurement report is built on: the normative profile constants, the size
/// distribution, and the percentile statistics.
/// <para>
/// These are the numbers a Phase 11 exit decision is read off, so they are pinned here rather than only
/// observed in a run. A percentile implementation that quietly interpolated, or a distribution that
/// quietly stopped spreading its large messages across the stream, would both produce a plausible-looking
/// report; these tests make either change fail loudly.
/// </para>
/// </summary>
public sealed class ActivityLoadReportMathTests
{
    [Fact]
    public void TheNormativeProfileIsExactlyTheProfileOfTheSpecification()
    {
        var profile = new ActivityLoadProfile();

        Assert.Equal(100_000, profile.SeedEvents);
        Assert.Equal(50, profile.EventsPerSecond);
        Assert.Equal(8, profile.ExecutionIdentities);
        Assert.Equal(TimeSpan.FromMinutes(30), profile.Duration);
        Assert.Equal(90_000, profile.OfferedEvents);
        Assert.Equal(256 * 1024, ActivityLoadProfile.MaxMessageBytes);
        Assert.Equal(200, ActivityLoadProfile.UiEventLatencyBudgetMilliseconds);
        Assert.True(profile.IsNormativeFullProfile);
        Assert.True(profile.IsFullDuration);
    }

    [Fact]
    public void TheCapacityIsSizedForTheSeedPlusTheWholeStream()
    {
        var profile = new ActivityLoadProfile();

        // A conforming run must not evict, otherwise the overflow accounting would be reporting a
        // self-inflicted failure rather than a product property. One spare slot keeps the capacity+1
        // negative control meaningful.
        Assert.Equal(profile.SeedEvents + profile.OfferedEvents + 1, profile.Capacity);
        Assert.True(profile.Capacity > profile.SeedEvents + profile.OfferedEvents);
    }

    [Fact]
    public void AShortProfileIsNotTheNormativeOne()
    {
        var profile = new ActivityLoadProfile(seedEvents: 2_000, duration: TimeSpan.FromSeconds(20));

        Assert.False(profile.IsNormativeFullProfile);
        Assert.False(profile.IsFullDuration);
        Assert.Equal(1_000, profile.OfferedEvents);
    }

    [Fact]
    public void ProfileArgumentsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityLoadProfile(seedEvents: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityLoadProfile(eventsPerSecond: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityLoadProfile(executionIdentities: 0));
    }

    [Fact]
    public void TheNormativeSizeDistributionMeetsTheStatedShape()
    {
        var distribution = ActivityMessageSizeDistribution.ForNormativeStream(90_000);

        Assert.Equal(8, distribution.MaximumMessageCount);
        Assert.Equal(12, distribution.LargeMessageCount);
        Assert.Equal(10, distribution.MediumMessageCount);
        Assert.Equal(30, distribution.OverSmallKiBCount);
        Assert.Equal(89_970, distribution.SmallMessageCount);

        // "Several 256 KiB cases" and "at least 99.9% at or below 1 KiB".
        Assert.True(distribution.HasSeveralMaximumMessages);
        Assert.True(distribution.MeetsSmallShareCriterion);
        Assert.True(distribution.SmallShare >= 0.999);
    }

    [Fact]
    public void TheMaximumMessageSizeIsTheSupportedLimit()
    {
        var distribution = ActivityMessageSizeDistribution.ForNormativeStream(90_000);

        for (var index = 0; index < distribution.MaximumMessageCount; index++)
        {
            Assert.Equal(ActivityLoadProfile.MaxMessageBytes, distribution.SizeOfLargeMessage(index));
        }
    }

    [Fact]
    public void TheAggregateOfLargeMessagesStaysUnderItsCap()
    {
        var distribution = ActivityMessageSizeDistribution.ForNormativeStream(90_000);

        Assert.True(distribution.RespectsByteCap);
        Assert.True(
            distribution.TotalLargePayloadBytes() <= distribution.LargePayloadByteCap,
            "the documented distribution exceeds its own aggregate byte cap.");
    }

    [Fact]
    public void LargeMessagesAreSpreadAcrossTheStream_NotClusteredAtItsStart()
    {
        var distribution = ActivityMessageSizeDistribution.ForNormativeStream(90_000);

        // Exactly the declared number of over-small messages, and one in each region of the run.
        var positions = Enumerable.Range(0, 90_000)
            .Where(sequence => distribution.ClassOf(sequence) != ActivityMessageSizeClass.Small)
            .ToArray();

        Assert.Equal(distribution.OverSmallKiBCount, positions.Length);
        Assert.Contains(positions, position => position < 1_000);
        Assert.Contains(positions, position => position > 40_000 && position < 50_000);
        Assert.Contains(positions, position => position > 80_000);
    }

    [Fact]
    public void AShortStreamDoesNotTurnIntoADownloadOfLargeMessages()
    {
        // The regression this pins: a fixed count of over-small classes applied to a fifty-event run put
        // a third of it at 256 KiB, so a diagnostic run measured the redactor instead of the UI.
        var distribution = ActivityMessageSizeDistribution.ForStream(50);

        var overSmall = Enumerable.Range(0, 50)
            .Count(sequence => distribution.ClassOf(sequence) != ActivityMessageSizeClass.Small);

        Assert.Equal(distribution.OverSmallKiBCount, overSmall);
        Assert.True(overSmall <= 4, $"a 50-event stream offered {overSmall} large messages.");
    }

    [Fact]
    public void TheOverSmallMappingIsStrictAndPure()
    {
        var distribution = ActivityMessageSizeDistribution.ForNormativeStream(90_000);

        for (var sequence = 0L; sequence < 90_000; sequence++)
        {
            var first = distribution.OverSmallIndexOf(sequence);
            var second = distribution.OverSmallIndexOf(sequence);

            Assert.Equal(first, second);

            if (first < 0)
            {
                Assert.Equal(ActivityMessageSizeClass.Small, distribution.ClassOf(sequence));
            }
            else
            {
                Assert.InRange(first, 0, distribution.OverSmallKiBCount - 1);
                Assert.NotEqual(ActivityMessageSizeClass.Small, distribution.ClassOf(sequence));
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => distribution.OverSmallIndexOf(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => distribution.SizeOfLargeMessage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => distribution.SizeOfLargeMessage(distribution.OverSmallKiBCount));
    }

    [Fact]
    public void DistributionArgumentsAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ActivityMessageSizeDistribution(1, 1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ActivityMessageSizeDistribution(-1, 0, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ActivityMessageSizeDistribution(5, 5, 5, 10));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ActivityMessageSizeDistribution(1, 1, 1, 10, largePayloadByteCap: 0));
    }

    [Fact]
    public void PercentilesUseNearestRank_OverObservedValues()
    {
        var ascending = new[] { 1d, 2d, 3d, 4d, 5d, 6d, 7d, 8d, 9d, 10d };

        Assert.Equal(5d, ActivityLatencyStatistics.Percentile(ascending, 0.50));
        Assert.Equal(10d, ActivityLatencyStatistics.Percentile(ascending, 0.95));
        Assert.Equal(1d, ActivityLatencyStatistics.Percentile(ascending, 0.0));
        Assert.Equal(10d, ActivityLatencyStatistics.Percentile(ascending, 1.0));
        Assert.Equal(0d, ActivityLatencyStatistics.Percentile(Array.Empty<double>(), 0.5));

        // Every reported percentile is a value that actually occurred.
        Assert.Contains(ActivityLatencyStatistics.Percentile(ascending, 0.95), ascending);
    }

    [Fact]
    public void LatencyStatisticsReportTheSampleCountBesideEveryPercentile()
    {
        var samples = Enumerable.Range(1, 100).Select(value => (double)value).ToArray();
        var statistics = ActivityLatencyStatistics.FromSamples(samples, observedCount: 100);

        Assert.Equal(100, statistics.SampleCount);
        Assert.Equal(100, statistics.ObservedCount);
        Assert.Equal(50d, statistics.P50Milliseconds);
        Assert.Equal(95d, statistics.P95Milliseconds);
        Assert.Equal(100d, statistics.MaxMilliseconds);
        Assert.Equal(1d, statistics.MinMilliseconds);
        Assert.Equal(50.5d, statistics.MeanMilliseconds);
        Assert.True(statistics.IsComplete);

        // The sample count travels with the number, so a p95 over a handful of samples cannot be read as
        // a p95 over the full stream.
        Assert.Contains("100", statistics.Describe("event-to-visible"), StringComparison.Ordinal);
    }

    [Fact]
    public void LatencyStatisticsAreIncompleteWhenSamplesWereLost()
    {
        var samples = new[] { 1d, 2d, 3d };
        var statistics = ActivityLatencyStatistics.FromSamples(
            samples,
            observedCount: 10,
            discardedSamples: 4,
            droppedPending: 3);

        Assert.Equal(3, statistics.SampleCount);
        Assert.Equal(10, statistics.ObservedCount);
        Assert.False(statistics.IsComplete);
        Assert.Contains("INCOMPLETE", statistics.Describe("event-to-visible"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySampleSetIsReportedAsEmpty_NotAsZeroLatency()
    {
        var statistics = ActivityLatencyStatistics.FromSamples(
            Array.Empty<double>(),
            observedCount: 0,
            discardedSamples: 0,
            droppedPending: 0);

        Assert.Equal(0, statistics.SampleCount);
        Assert.Contains("0 samples", statistics.Describe("event-to-visible"), StringComparison.Ordinal);
    }

    [Fact]
    public void CriterionOutcomesRenderExplicitly_AndNeverTestedIsItsOwnState()
    {
        var pass = new ActivityCriterionResult("a", "requirement", ActivityCriterionOutcome.Pass, "measured");
        var fail = new ActivityCriterionResult("b", "requirement", ActivityCriterionOutcome.Fail, "measured");
        var notTested = new ActivityCriterionResult(
            "c",
            "requirement",
            ActivityCriterionOutcome.NotTested,
            "the 30-minute profile was not executed",
            requiresFullProfile: true);

        Assert.Equal("PASS", pass.OutcomeDisplay);
        Assert.Equal("FAIL", fail.OutcomeDisplay);
        Assert.Equal("NOT_TESTED", notTested.OutcomeDisplay);
        Assert.True(notTested.RequiresFullProfile);
        Assert.Contains("was not executed", notTested.Measurement, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => new ActivityCriterionResult(" ", "r", ActivityCriterionOutcome.Pass, "m"));
        Assert.Throws<ArgumentException>(() => new ActivityCriterionResult("i", " ", ActivityCriterionOutcome.Pass, "m"));
        Assert.Throws<ArgumentException>(() => new ActivityCriterionResult("i", "r", ActivityCriterionOutcome.Pass, " "));
    }

    [Fact]
    public void TheNormativeProfileIsDescribedWithTheNumbersItWillBeJudgedBy()
    {
        var description = new ActivityLoadProfile().Describe();

        // The description is what a reader compares against the specification, so it has to carry the
        // normative numbers rather than a pointer to where they are defined.
        Assert.Contains("100,000", description, StringComparison.Ordinal);
        Assert.Contains("50 events/second", description, StringComparison.Ordinal);
        Assert.Contains("8", description, StringComparison.Ordinal);
        Assert.Contains("30", description, StringComparison.Ordinal);
        Assert.Contains("90,000", description, StringComparison.Ordinal);
        Assert.Contains("256 KiB", description, StringComparison.Ordinal);
        Assert.Contains("200 ms", description, StringComparison.Ordinal);
    }
}
