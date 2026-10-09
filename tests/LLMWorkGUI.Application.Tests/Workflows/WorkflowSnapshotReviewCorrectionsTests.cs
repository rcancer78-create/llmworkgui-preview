using System.Text.Json;
using System.Text.Json.Nodes;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class WorkflowSnapshotReviewCorrectionsTests
{
    [Fact]
    public void UnknownGateFieldCannotBeSilentlyDiscardedFromPinnedPolicy()
    {
        var document = JsonNode.Parse(GraphJson())!;
        document["nodes"]![0]!["gateMetadata"]!["requiresDualControl"] = true;

        Assert.Throws<WorkflowValidationException>(() =>
            WorkflowGraphSnapshot.Deserialize(document.ToJsonString(), "run"));
    }

    [Theory]
    [InlineData("stageKind", "\"Custom\"")]
    [InlineData("requiredReviewerRoles", "[]")]
    [InlineData("requiresUserApproval", "true")]
    [InlineData("artifactRequirement", "\"StrongerEvidence\"")]
    public void DuplicateGateFieldCannotHaveItsFirstPolicyOverwritten(string field, string firstValue)
    {
        var json = GraphJson().Replace("\"gateMetadata\":{",
            "\"gateMetadata\":{\"" + field + "\":" + firstValue + ",", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var gate = document.RootElement.GetProperty("nodes")[0].GetProperty("gateMetadata");
        Assert.Equal(2, gate.EnumerateObject().Count(property => property.Name == field));

        Assert.Throws<WorkflowValidationException>(() => WorkflowGraphSnapshot.Deserialize(json, "run"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("prompt")]
    public void GraphNodeKindMustBeAnExactDeclaredName(string token)
    {
        var document = JsonNode.Parse(GraphJson())!;
        document["nodes"]![0]!["kind"] = token;

        Assert.Throws<WorkflowValidationException>(() =>
            WorkflowGraphSnapshot.Deserialize(document.ToJsonString(), "run"));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("\"9\"")]
    [InlineData("\"custom\"")]
    public void SchemeStageKindMustBeAnExactDeclaredName(string tokenJson)
    {
        var json = SchemeJson().Replace("\"stageKind\":\"Custom\"", "\"stageKind\":" + tokenJson,
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(tokenJson, document.RootElement.GetProperty("stages")[0].GetProperty("stageKind").GetRawText());

        Assert.Throws<WorkflowValidationException>(() => WorkflowSchemeSnapshot.Deserialize(json, "run"));
    }

    [Fact]
    public void CanonicalSnapshotsKeepEveryDeclaredGateValue()
    {
        var graph = WorkflowGraphSnapshot.Deserialize(GraphJson(), "run");
        Assert.Equal(GraphJson(), WorkflowGraphSnapshot.Serialize(graph));
        Assert.Equal(SchemeJson(), WorkflowSchemeSnapshot.Deserialize(SchemeJson(), "run").Json);
    }

    private static string GraphJson() => WorkflowGraphSnapshot.Serialize(new WorkflowGraph("start",
        new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeKind.Prompt, "Start", "Worker",
                successTargetNodeId: "done", gateMetadata: new WorkflowNodeGateMetadata(
                    WorkflowStageKind.Custom, Array.Empty<string>(), false, "Evidence")),
            new WorkflowNodeDefinition("done", WorkflowNodeKind.TerminalOutcome, "Done", "Worker")
        }));

    private static string SchemeJson() => new WorkflowSchemeSnapshot("stage", new[]
    {
        new WorkflowStageDefinition("stage", "Stage", "Worker", WorkflowStageKind.Custom,
            Array.Empty<string>(), false, "Evidence", null, null)
    }).Json;
}
