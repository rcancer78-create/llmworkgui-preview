using LLMWorkGUI.ActivityLoadDriver;
using LLMWorkGUI.Application.Observability;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class ActivityMeasuredProfileRegressionTests
{
    [Theory]
    [InlineData(0d, 50d, ActivityLoadProfile.NormativeFullOfferedEvents)]
    [InlineData(3600d, 25d, ActivityLoadProfile.NormativeFullOfferedEvents)]
    [InlineData(1800d, 0d, ActivityLoadProfile.NormativeFullOfferedEvents)]
    [InlineData(1800d, 50d, 1)]
    public void ConfiguringFullDoesNotProveTheMeasuredProfileWasExecuted(double seconds, double rate, long offered)
    {
        var report = Report(seconds, rate, offered);
        ActivityLoadCriteria.Evaluate(report, []);

        var criterion = Criterion(report, "profile-conformance");
        Assert.DoesNotContain("**PASS**", criterion, StringComparison.Ordinal);
        if (seconds == 0) Assert.False(report.ExecutedFullProfile);
    }

    [Fact]
    public void AnActuallyMeasuredCompleteNormativeProfileCanPass()
    {
        var report = Report(1800, 50, ActivityLoadProfile.NormativeFullOfferedEvents);
        ActivityLoadCriteria.Evaluate(report, []);

        Assert.True(report.ExecutedFullProfile);
        Assert.Contains("**PASS**", Criterion(report, "profile-conformance"), StringComparison.Ordinal);
    }

    private static ActivityLoadReport Report(double seconds, double rate, long offered) =>
        new(ActivityLoadOptions.Parse(["--mode", "full"]), DateTimeOffset.UnixEpoch)
        {
            ActualStreamDuration = TimeSpan.FromSeconds(seconds),
            OfferedRatePerSecond = rate,
            OfferedEvents = offered,
            AcceptedEvents = offered,
            DistinctExecutionIdentities = ActivityLoadProfile.NormativeExecutionIdentities
        };

    private static string Criterion(ActivityLoadReport report, string id) => Assert.Single(report.ToMarkdown()
        .Split('\n').Where(line => line.StartsWith($"| {id} |", StringComparison.Ordinal)));
}
