using System.Collections;
using System.Text.Json.Nodes;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class WorkflowSchemeIntegrityTests
{
    [Theory]
    [InlineData("requiredReviewerRoles", false)]
    [InlineData("requiredReviewerRoles", true)]
    [InlineData("requiresUserApproval", false)]
    [InlineData("stageKind", false)]
    public void Snapshot_MissingGateFieldsDoesNotWeakenScheme(string field, bool setNull)
    {
        var snapshot = WorkflowSchemeSnapshot.CreateFrom(WorkflowScheme.CreateStandardDevelopmentScheme());
        var json = JsonNode.Parse(snapshot.Json)!;
        var stage = json["stages"]![0]!.AsObject();
        if (setNull) stage[field] = null;
        else stage.Remove(field);
        Assert.Throws<WorkflowValidationException>(() => WorkflowSchemeSnapshot.Deserialize(json.ToJsonString(), "run"));
    }

    [Fact]
    public void Aggregate_EvidenceCollectionsCannotBeMutatedThroughCast()
    {
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
        var run = WorkflowRun.Start("run", "project", "package", "version", null,
            scheme.GetRequiredStage(scheme.InitialStageId), DateTimeOffset.UtcNow);
        foreach (var evidence in new object[] { run.Verdicts, run.Approvals, run.Artifacts, run.Transitions })
        {
            var list = Assert.IsAssignableFrom<IList>(evidence);
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
    }

    [Fact]
    public void Aggregate_CallerCannotReplacePinnedStageWithUngatedDefinition()
    {
        var guarded = new WorkflowStageDefinition("review", "Review", "Reviewer", WorkflowStageKind.Custom,
            ["Reviewer"], true, "Document", "end", null);
        var end = new WorkflowStageDefinition("end", "End", "Coordinator", WorkflowStageKind.Custom,
            [], false, null, null, null);
        var snapshot = new WorkflowSchemeSnapshot("review", [guarded, end]);
        var run = WorkflowRun.StartPinnedToTemplate("run", "project", "package", "version", null, guarded,
            DateTimeOffset.UtcNow, "template", 1, "{}", snapshot.Json);
        var forged = new WorkflowStageDefinition("review", "Review", "Reviewer", WorkflowStageKind.Custom,
            [], false, null, "end", null);
        Assert.Throws<InvalidOperationException>(() => run.AdvanceTo(forged, end, "bypass", DateTimeOffset.UtcNow));
        Assert.Equal("review", run.CurrentStageId);
    }
}
