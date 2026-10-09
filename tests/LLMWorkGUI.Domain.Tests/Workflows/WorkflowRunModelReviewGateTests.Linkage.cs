using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests.Workflows;

public sealed partial class WorkflowRunModelReviewGateTests
{
    [Theory]
    [InlineData("wrong-stage", ArtifactId)]
    [InlineData(StageId, "wrong-artifact")]
    public void APinnedRunRefusesVerdictLinkageThatContradictsItsExecution(string verdictStage, string verdictArtifact)
    {
        var run = PinnedRunWithArtifact();
        run.RecordReviewerVerdict(new ReviewerVerdictRecord(ReviewerRole, RouteId, DocumentHash,
            WorkflowReviewVerdict.Approve, "Historical caller claim", StartedAt.AddMinutes(2),
            ExecutionId, verdictStage, verdictArtifact));

        Assert.Throws<InvalidOperationException>(() => Advance(run, Only(Evidence(ExecutionState.Succeeded, RouteId))));
        Assert.Equal(StageId, run.CurrentStageId);
        Assert.Empty(run.Transitions);
    }
}
