using LLMWorkGUI.Application.Workflows;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class EndToEndScenarioReportTests
{
    [Theory]
    [InlineData("duplicate", false)]
    [InlineData("unknown", false)]
    [InlineData("case", false)]
    [InlineData("valid", true)]
    public void SuccessfulReportRequiresEachNormativeBlockerExactlyOnce(string variant, bool expected)
    {
        var ids = new[] { EndToEndScenarioBlockerIds.MissingReviewer, EndToEndScenarioBlockerIds.ConflictingVerdicts,
            EndToEndScenarioBlockerIds.HashMismatch, EndToEndScenarioBlockerIds.MissingUiArtifact };
        if (variant == "duplicate") ids[3] = ids[0];
        if (variant == "unknown") ids[3] = "unrelated-blocker";
        if (variant == "case") ids[3] = ids[3].ToUpperInvariant();
        var report = new EndToEndWorkflowScenarioReport
        {
            StartedAtUtc = DateTimeOffset.UnixEpoch, CompletedAtUtc = DateTimeOffset.UnixEpoch,
            BlockerEvidences = ids.Select(id => new EndToEndScenarioBlockerEvidence(id, id, "gate", true, "blocked")).ToArray(),
            ReworkApprovalAllowed = true, AgyProfileSwitchRefused = true, CodexHomeSwitchRefused = true,
            NativeSwitchProofRefusalNamed = true, MirasimHostUntouched = true, RunFailureRecovered = true
        };
        Assert.Equal(expected, report.IsSuccessful);
    }
}
