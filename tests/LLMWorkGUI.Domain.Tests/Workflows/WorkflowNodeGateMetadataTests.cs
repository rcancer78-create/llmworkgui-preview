using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests.Workflows;

/// <summary>
/// The stage gates a node carries have to be the source stage's own values, kept as data rather than
/// re-derived from a kind or a role name. These tests pin that copy, the one explicit gate-free tuple,
/// and the fact that a node without metadata says nothing about gates rather than claiming to have none.
/// </summary>
public sealed class WorkflowNodeGateMetadataTests
{
    [Fact]
    public void CreateFrom_CopiesTheFourStageFieldsExactly()
    {
        var stage = new WorkflowStageDefinition(
            "stage-document-review",
            "Проверка документов",
            "Reviewer",
            WorkflowStageKind.DocumentReview,
            new[] { "Reviewer", "Architect" },
            requiresUserApproval: true,
            artifactRequirement: "DocumentBundle",
            nextStageId: "stage-user-approval",
            failureStageId: null);

        var gate = WorkflowNodeGateMetadata.CreateFrom(stage);

        Assert.Equal(WorkflowStageKind.DocumentReview, gate.StageKind);
        Assert.Equal(new[] { "Reviewer", "Architect" }, gate.RequiredReviewerRoles);
        Assert.True(gate.RequiresUserApproval);
        Assert.Equal("DocumentBundle", gate.ArtifactRequirement);
        Assert.False(gate.IsNoGate);
    }

    [Fact]
    public void CreateFrom_CopiesTheFieldsOfAGateFreeStageToo()
    {
        var stage = new WorkflowStageDefinition(
            "stage-final-outcome",
            "Итог",
            "Coordinator",
            WorkflowStageKind.FinalVerification,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null,
            nextStageId: null,
            failureStageId: null);

        var gate = WorkflowNodeGateMetadata.CreateFrom(stage);

        // A gate-free tuple is a stage kind plus three absences. A stage kind other than Custom is not
        // that tuple, even with no reviewer, no approval and no artifact.
        Assert.Equal(WorkflowStageKind.FinalVerification, gate.StageKind);
        Assert.Empty(gate.RequiredReviewerRoles);
        Assert.False(gate.RequiresUserApproval);
        Assert.Null(gate.ArtifactRequirement);
        Assert.False(gate.IsNoGate);
    }

    [Fact]
    public void TheReviewerListOfAGateIsACopy()
    {
        var roles = new List<string> { "Reviewer" };
        var gate = new WorkflowNodeGateMetadata(
            WorkflowStageKind.Custom,
            roles,
            requiresUserApproval: false,
            artifactRequirement: null);

        roles.Add("Approver");

        Assert.Equal(new[] { "Reviewer" }, gate.RequiredReviewerRoles);
    }

    [Fact]
    public void IsNoGate_IsTrueOnlyForTheExplicitTuple()
    {
        var noGate = new WorkflowNodeGateMetadata(
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null);

        Assert.True(noGate.IsNoGate);

        Assert.False(new WorkflowNodeGateMetadata(
            WorkflowStageKind.DocumentReview,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null).IsNoGate);

        Assert.False(new WorkflowNodeGateMetadata(
            WorkflowStageKind.Custom,
            new[] { "Reviewer" },
            requiresUserApproval: false,
            artifactRequirement: null).IsNoGate);

        Assert.False(new WorkflowNodeGateMetadata(
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: true,
            artifactRequirement: null).IsNoGate);

        Assert.False(new WorkflowNodeGateMetadata(
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: "doc.md").IsNoGate);
    }

    [Fact]
    public void AGate_RefusesAnUndeclaredStageKind()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new WorkflowNodeGateMetadata(
                (WorkflowStageKind)99,
                Array.Empty<string>(),
                requiresUserApproval: false,
                artifactRequirement: null));

        Assert.Equal("stageKind", exception.ParamName);
    }

    [Fact]
    public void AGate_RefusesAMissingOrBlankReviewerRole()
    {
        Assert.Throws<ArgumentNullException>(
            () => new WorkflowNodeGateMetadata(
                WorkflowStageKind.Custom,
                null!,
                requiresUserApproval: false,
                artifactRequirement: null));

        var blank = Assert.Throws<ArgumentException>(
            () => new WorkflowNodeGateMetadata(
                WorkflowStageKind.Custom,
                new[] { "Reviewer", " " },
                requiresUserApproval: false,
                artifactRequirement: null));

        Assert.Equal("requiredReviewerRoles", blank.ParamName);
    }

    [Fact]
    public void AGate_RefusesAnEmptyArtifactRequirement()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new WorkflowNodeGateMetadata(
                WorkflowStageKind.Custom,
                Array.Empty<string>(),
                requiresUserApproval: false,
                artifactRequirement: " "));

        Assert.Equal("artifactRequirement", exception.ParamName);
    }

    [Fact]
    public void ANodeDefinition_KeepsItsGateAndDefaultsToNoneAtAll()
    {
        var gate = new WorkflowNodeGateMetadata(
            WorkflowStageKind.UserApproval,
            Array.Empty<string>(),
            requiresUserApproval: true,
            artifactRequirement: "ApprovedDocument");

        var gated = new WorkflowNodeDefinition(
            "node-a",
            WorkflowNodeKind.Prompt,
            "Node A",
            "Role A",
            gateMetadata: gate);

        Assert.Same(gate, gated.GateMetadata);

        // An absent gate is a node that declares none, which is a different statement from a node that
        // declares the gate-free tuple: nothing about the second follows from the first.
        var ungated = new WorkflowNodeDefinition(
            "node-b",
            WorkflowNodeKind.Prompt,
            "Node B",
            "Role B");

        Assert.Null(ungated.GateMetadata);
    }

    [Fact]
    public void EveryStageOfTheStandardDevelopmentSchemeCarriesItsOwnGate()
    {
        var nodes = new WorkflowGraph(
            "entry",
            WorkflowSchemeStages()
                .Select(stage => new WorkflowNodeDefinition(
                    stage.StageId,
                    WorkflowNodeKind.Prompt,
                    stage.DisplayName,
                    stage.RequiredRole,
                    gateMetadata: WorkflowNodeGateMetadata.CreateFrom(stage),
                    successTargetNodeId: stage.NextStageId,
                    failureTargetNodeId: stage.FailureStageId))
                .ToArray())
            .Nodes;

        Assert.Equal(WorkflowSchemeStages().Count, nodes.Count);

        foreach (var stage in WorkflowSchemeStages())
        {
            var gate = nodes.Single(node => node.NodeId == stage.StageId).GateMetadata;

            Assert.NotNull(gate);
            Assert.Equal(stage.StageKind, gate!.StageKind);
            Assert.Equal(stage.RequiredReviewerRoles, gate.RequiredReviewerRoles);
            Assert.Equal(stage.RequiresUserApproval, gate.RequiresUserApproval);
            Assert.Equal(stage.ArtifactRequirement, gate.ArtifactRequirement);
        }
    }

    private static IReadOnlyList<WorkflowStageDefinition> WorkflowSchemeStages() =>
        new[]
        {
            new WorkflowStageDefinition(
                "stage-task-specification",
                "Постановка задачи",
                "Coordinator",
                WorkflowStageKind.TaskSpecification,
                Array.Empty<string>(),
                requiresUserApproval: false,
                artifactRequirement: "TaskSpecificationDocument",
                nextStageId: "stage-document-review",
                failureStageId: null),
            new WorkflowStageDefinition(
                "stage-document-review",
                "Проверка документов",
                "Reviewer",
                WorkflowStageKind.DocumentReview,
                new[] { "Reviewer", "Architect" },
                requiresUserApproval: false,
                artifactRequirement: "DocumentBundle",
                nextStageId: "stage-user-approval",
                failureStageId: null),
            new WorkflowStageDefinition(
                "stage-user-approval",
                "Утверждение",
                "Approver",
                WorkflowStageKind.UserApproval,
                Array.Empty<string>(),
                requiresUserApproval: true,
                artifactRequirement: "ApprovedDocument",
                nextStageId: "stage-final-outcome",
                failureStageId: null),
            new WorkflowStageDefinition(
                "stage-final-outcome",
                "Итог",
                "Coordinator",
                WorkflowStageKind.FinalVerification,
                Array.Empty<string>(),
                requiresUserApproval: false,
                artifactRequirement: null,
                nextStageId: null,
                failureStageId: null)
        };
}
