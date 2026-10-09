using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class RoleTransferRecordIdentityRegressionTests
{
    [Fact]
    public void DistinctReviewerExecutionsWithIdenticalEvidenceKeepBothRecordedTexts()
    {
        var stage = new WorkflowStageDefinition("review", "Review", "Reviewer", WorkflowStageKind.Custom,
            [], false, null, null, null);
        var run = WorkflowRun.Start("run", "project", "package", "version", "session", stage, DateTimeOffset.UnixEpoch);
        run.RecordReviewerVerdict(Verdict("execution-one", 1));
        run.RecordReviewerVerdict(Verdict("execution-two", 2));

        var evidence = Assert.Single(new RoleTransferEvidenceProjector().Project(run, []));

        Assert.Equal("Same recorded evidence" + Environment.NewLine + "Same recorded evidence", evidence.WorkText);
        Assert.Equal(2, run.Verdicts.Count);
    }

    [Fact]
    public void SameVerdictCopiedIntoTransitionSnapshotIsRenderedOnlyOnce()
    {
        var verdict = Verdict("execution-one", 1);
        var copy = Verdict("execution-one", 1);
        var transition = new WorkflowTransitionRecord("transition", "review", "next",
            DateTimeOffset.UnixEpoch.AddSeconds(3), "Stage transfer", [copy], null);
        var run = new WorkflowRun("run", "project", "package", "version", "session", WorkflowRunState.Running,
            "next", "Reviewer", DateTimeOffset.UnixEpoch, null, WorkflowTerminalOutcome.None, null,
            [transition], [verdict], []);

        var evidence = Assert.Single(new RoleTransferEvidenceProjector().Project(run, []));

        Assert.Equal("Stage transfer" + Environment.NewLine + "Same recorded evidence", evidence.WorkText);
    }

    private static ReviewerVerdictRecord Verdict(string executionId, int second) => new("Reviewer", "route",
        "sha256:" + new string('a', 64), WorkflowReviewVerdict.Approve, "Same recorded evidence",
        DateTimeOffset.UnixEpoch.AddSeconds(second), executionId, "review", "artifact");
}
