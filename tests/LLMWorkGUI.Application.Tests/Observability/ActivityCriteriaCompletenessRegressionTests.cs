using LLMWorkGUI.ActivityLoadDriver;
using LLMWorkGUI.Application.Observability;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class ActivityCriteriaCompletenessRegressionTests
{
    [Theory]
    [InlineData(5d)]
    [InlineData(1000d)]
    public void IncompleteVisibilityMeasurementCannotDecideTheLatencyCriterion(double retainedLatency)
    {
        var report = new ActivityLoadReport(ActivityLoadOptions.Parse([]), DateTimeOffset.UnixEpoch)
        {
            VisibilityLatency = ActivityLatencyStatistics.FromSamples([retainedLatency], observedCount: 2,
                discardedSamples: 1)
        };

        ActivityLoadCriteria.Evaluate(report, []);

        var criterion = Assert.Single(report.ToMarkdown().Split('\n')
            .Where(line => line.StartsWith("| ui-event-latency |", StringComparison.Ordinal)));
        Assert.Contains("**NOT_TESTED**", criterion, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", criterion, StringComparison.Ordinal);
    }
}
