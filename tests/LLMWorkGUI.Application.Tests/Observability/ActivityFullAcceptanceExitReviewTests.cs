using LLMWorkGUI.ActivityLoadDriver;
using LLMWorkGUI.Application.Observability;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class ActivityFullAcceptanceExitReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullRunWithMissingAcceptanceEvidenceCannotExitSuccessfully(bool reduced)
    {
        var options = ActivityLoadOptions.Parse(reduced
            ? ["--mode", "full", "--seed", "1", "--stream-seconds", "1"]
            : ["--mode", "full"]);
        var report = new ActivityLoadReport(options, DateTimeOffset.UnixEpoch);
        report.Add(new ActivityCriterionResult("owned-not-tested", "Required measurement", ActivityCriterionOutcome.NotTested, "Missing owned evidence"));
        Assert.NotEqual(0, report.ExitCode);
        Assert.DoesNotContain("# Phase 11 normative event-load report", report.ToMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public void SmokeWithExplicitNotTestedCriteriaRemainsAUsefulDiagnostic()
    {
        var report = new ActivityLoadReport(ActivityLoadOptions.Parse([]), DateTimeOffset.UnixEpoch);
        report.Add(new ActivityCriterionResult("owned-not-tested", "Full profile not exercised", ActivityCriterionOutcome.NotTested, "Smoke only"));
        Assert.Equal(0, report.ExitCode);
    }
}
