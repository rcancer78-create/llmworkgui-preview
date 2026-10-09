using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

/// <summary>
/// The one graph node document that both a pinned run and the durable template store read and write.
/// These tests fix what it does with a node's stage gates: a declared gate survives verbatim, a document
/// written before gates existed stays readable as a graph that declares none, and a gate that is present
/// but incomplete is a named refusal rather than a node that quietly loses its reviewer or approval.
/// </summary>
public sealed class WorkflowGraphSnapshotGateMetadataTests
{
    private const string RunId = "run-1";

    [Fact]
    public void ADeclaredGateSurvivesTheRoundTripVerbatim()
    {
        var graph = CreateGraph(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.DocumentReview,
                new[] { "Reviewer", "Architect" },
                requiresUserApproval: true,
                artifactRequirement: "DocumentBundle"));

        var pinned = Read(WorkflowGraphSnapshot.Serialize(graph));

        var gate = pinned.GetRequiredNode("node-a").GateMetadata;

        Assert.NotNull(gate);
        Assert.Equal(WorkflowStageKind.DocumentReview, gate!.StageKind);
        Assert.Equal(new[] { "Reviewer", "Architect" }, gate.RequiredReviewerRoles);
        Assert.True(gate.RequiresUserApproval);
        Assert.Equal("DocumentBundle", gate.ArtifactRequirement);
        Assert.False(gate.IsNoGate);

        // The reader is a round trip, not a repair: re-serializing what it read yields the same document.
        Assert.Equal(WorkflowGraphSnapshot.Serialize(graph), WorkflowGraphSnapshot.Serialize(pinned));
    }

    [Fact]
    public void TheExplicitNoGateTupleSurvivesAsANoGate()
    {
        var graph = CreateGraph(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.Custom,
                Array.Empty<string>(),
                requiresUserApproval: false,
                artifactRequirement: null));

        var gate = Read(WorkflowGraphSnapshot.Serialize(graph)).GetRequiredNode("node-a").GateMetadata;

        Assert.NotNull(gate);
        Assert.True(gate!.IsNoGate);
        Assert.Equal(WorkflowStageKind.Custom, gate.StageKind);
        Assert.Empty(gate.RequiredReviewerRoles);
        Assert.False(gate.RequiresUserApproval);
        Assert.Null(gate.ArtifactRequirement);
    }

    [Fact]
    public void ANodeThatDeclaresNoGateReadsBackWithNoMetadata()
    {
        var graph = CreateGraph(gate: null);

        Assert.Null(Read(WorkflowGraphSnapshot.Serialize(graph)).GetRequiredNode("node-a").GateMetadata);
    }

    [Fact]
    public void ASnapshotWrittenBeforeGatesWerePreservedIsStillReadable()
    {
        // The exact node document the previous reader and writer produced: no gate property at all.
        const string legacySnapshot = """
            {"entryNodeId":"node-a","nodes":[
            {"nodeId":"node-a","kind":"Prompt","displayName":"Node A","roleBinding":"Role A",
             "requiredCapabilities":[],"primaryRouteId":null,"fallbackRouteIds":[],"timeoutTicks":null,
             "retryBudget":0,"successTargetNodeId":"node-b","failureTargetNodeId":null,
             "conditionExpression":null,"artifactContract":null,"permissionIntent":null},
            {"nodeId":"node-b","kind":"TerminalOutcome","displayName":"Node B","roleBinding":"Role B",
             "requiredCapabilities":[],"primaryRouteId":null,"fallbackRouteIds":[],"timeoutTicks":null,
             "retryBudget":0,"successTargetNodeId":null,"failureTargetNodeId":null,
             "conditionExpression":null,"artifactContract":null,"permissionIntent":null}]}
            """;

        var graph = WorkflowGraphSnapshot.Deserialize(legacySnapshot, RunId);

        Assert.Equal("node-a", graph.EntryNodeId);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Null(graph.GetRequiredNode("node-a").GateMetadata);
        Assert.Null(graph.GetRequiredNode("node-b").GateMetadata);

        graph.Validate();
    }

    [Fact]
    public void AGateThatIsJsonNullMeansAbsentNotApprovalFree()
    {
        var graph = Read(WithGate("null"));

        // An explicit null is an older document that declared no gate. It is read as no metadata, which
        // is what the resolver's gate-free path already treats; it is never read as a gate that was
        // considered and cleared.
        Assert.Null(graph.GetRequiredNode("node-a").GateMetadata);
    }

    [Fact]
    public void TheShippedStandardTemplateCarriesEveryStagesOwnGate()
    {
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();

        foreach (var stage in scheme.Stages)
        {
            var gate = WorkflowStudioService.CreateStandardTemplate()
                .Graph
                .GetRequiredNode(stage.StageId)
                .GateMetadata;

            Assert.NotNull(gate);
            Assert.Equal(stage.StageKind, gate!.StageKind);
            Assert.Equal(stage.RequiredReviewerRoles, gate.RequiredReviewerRoles);
            Assert.Equal(stage.RequiresUserApproval, gate.RequiresUserApproval);
            Assert.Equal(stage.ArtifactRequirement, gate.ArtifactRequirement);
        }
    }

    [Fact]
    public void TheShippedStandardTemplateKeepsItsGatesThroughTheSnapshot()
    {
        var template = WorkflowStudioService.CreateStandardTemplate();

        var pinned = Read(WorkflowGraphSnapshot.Serialize(template.Graph));

        Assert.Equal(template.Graph.Nodes.Count, pinned.Nodes.Count);

        foreach (var node in template.Graph.Nodes)
        {
            var gate = pinned.GetRequiredNode(node.NodeId).GateMetadata;

            Assert.NotNull(gate);
            Assert.Equal(node.GateMetadata!.StageKind, gate!.StageKind);
            Assert.Equal(node.GateMetadata.RequiredReviewerRoles, gate.RequiredReviewerRoles);
            Assert.Equal(node.GateMetadata.RequiresUserApproval, gate.RequiresUserApproval);
            Assert.Equal(node.GateMetadata.ArtifactRequirement, gate.ArtifactRequirement);
        }

        // The reviewer gate, the two approval stages and the artifact requirements are all still stated
        // by the pinned document, so nothing here depends on a name being read back as a gate.
        Assert.Equal(
            WorkflowStageKind.DocumentReview,
            pinned.GetRequiredNode("stage-document-review").GateMetadata!.StageKind);
        Assert.Equal(
            new[] { "Reviewer", "Architect" },
            pinned.GetRequiredNode("stage-document-review").GateMetadata!.RequiredReviewerRoles);
        Assert.True(pinned.GetRequiredNode("stage-user-approval").GateMetadata!.RequiresUserApproval);
        Assert.Equal(
            "UiAcceptanceEvidence",
            pinned.GetRequiredNode("stage-tests-and-ui-acceptance").GateMetadata!.ArtifactRequirement);
    }

    public static TheoryData<string, string> MalformedGates()
    {
        var data = new TheoryData<string, string>();

        // A present gate has to declare all four fields. Each missing field names itself.
        data.Add(
            """{"requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":null}""",
            "stageKind");

        data.Add(
            """{"stageKind":"Custom","requiresUserApproval":false,"artifactRequirement":null}""",
            "requiredReviewerRoles");

        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":[],"artifactRequirement":null}""",
            "requiresUserApproval");

        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":[],"requiresUserApproval":false}""",
            "artifactRequirement");

        // An unknown or ordinal stage kind is not a stage kind this build can execute.
        data.Add(
            """{"stageKind":"SignOff","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":null}""",
            "unknown stage kind");

        data.Add(
            """{"stageKind":"custom","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":null}""",
            "unknown stage kind");

        data.Add(
            """{"stageKind":"5","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":null}""",
            "unknown stage kind");

        data.Add(
            """{"stageKind":5,"requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":null}""",
            "stageKind");

        // An omitted reviewer list is not an empty one.
        data.Add(
            """{"stageKind":"Custom","requiresUserApproval":true,"artifactRequirement":"doc.md"}""",
            "requiredReviewerRoles");

        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":null,"requiresUserApproval":false,"artifactRequirement":null}""",
            "requiredReviewerRoles");

        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":["Reviewer",7],"requiresUserApproval":false,"artifactRequirement":null}""",
            "required reviewer role");

        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":[{"role":"Reviewer"}],"requiresUserApproval":false,"artifactRequirement":null}""",
            "required reviewer role");

        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":["Reviewer","  "],"requiresUserApproval":false,"artifactRequirement":null}""",
            "not usable");

        // An omitted approval flag is not a refused approval.
        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":[],"requiresUserApproval":"false","artifactRequirement":null}""",
            "requiresUserApproval");

        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":[],"requiresUserApproval":null,"artifactRequirement":null}""",
            "requiresUserApproval");

        // An omitted artifact requirement is not an absent one.
        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":[],"requiresUserApproval":true,"artifactRequirement":[]}""",
            "artifactRequirement");

        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":false}""",
            "artifactRequirement");

        data.Add(
            """{"stageKind":"Custom","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":""}""",
            "not usable");

        // A gate that is not an object at all.
        data.Add("\"Reviewer\"", "must be an object");
        data.Add("7", "must be an object");
        data.Add("[\"Custom\"]", "must be an object");
        data.Add("true", "must be an object");

        return data;
    }

    [Theory]
    [MemberData(nameof(MalformedGates))]
    public void AMalformedGateIsRefusedByNameAndNeverBecomesAGateFreeNode(string gateJson, string expected)
    {
        var exception = Assert.Throws<WorkflowValidationException>(
            () => WorkflowGraphSnapshot.Deserialize(WithGate(gateJson), RunId));

        Assert.Contains("invalid-node-gate-metadata", exception.Message, StringComparison.Ordinal);
        Assert.Contains("node-a", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedGateStopsTheWholeDocument()
    {
        // The reader builds no graph at all out of a document whose gate it could not read, so a partially
        // rebuilt graph is never handed to a caller as a working one.
        Assert.Throws<WorkflowValidationException>(
            () => WorkflowGraphSnapshot.Deserialize(WithGate("""{"stageKind":"Custom"}"""), RunId));
    }

    [Fact]
    public void TheStoredTemplateReaderAppliesTheSameRules()
    {
        const string TemplateId = "gate-workflow";

        var graph = CreateGraph(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.UserApproval,
                Array.Empty<string>(),
                requiresUserApproval: true,
                artifactRequirement: "ApprovedDocument"));

        var stored = WorkflowGraphSnapshot.ReadStoredTemplateGraph(
            WorkflowGraphSnapshot.Serialize(graph),
            TemplateId,
            version: 1);

        Assert.True(stored.GetRequiredNode("node-a").GateMetadata!.RequiresUserApproval);

        var legacy = WorkflowGraphSnapshot.ReadStoredTemplateGraph(
            LegacyNodeJson,
            TemplateId,
            version: 1);

        Assert.Null(legacy.GetRequiredNode("node-a").GateMetadata);

        var malformed = Assert.Throws<WorkflowValidationException>(
            () => WorkflowGraphSnapshot.ReadStoredTemplateGraph(
                LegacyNodeJson.Replace("\"permissionIntent\":null", "\"gateMetadata\":{\"stageKind\":\"Nope\"}", StringComparison.Ordinal),
                TemplateId,
                version: 1));

        Assert.Contains("invalid-node-gate-metadata", malformed.Message, StringComparison.Ordinal);
        Assert.Contains(TemplateId, malformed.Message, StringComparison.Ordinal);
    }

    private const string LegacyNodeJson = """
        {"entryNodeId":"node-a","nodes":[
        {"nodeId":"node-a","kind":"Prompt","displayName":"Node A","roleBinding":"Role A",
         "requiredCapabilities":[],"primaryRouteId":null,"fallbackRouteIds":[],"timeoutTicks":null,
         "retryBudget":0,"successTargetNodeId":"node-b","failureTargetNodeId":null,
         "conditionExpression":null,"artifactContract":null,"permissionIntent":null},
        {"nodeId":"node-b","kind":"TerminalOutcome","displayName":"Node B","roleBinding":"Role B",
         "requiredCapabilities":[],"primaryRouteId":null,"fallbackRouteIds":[],"timeoutTicks":null,
         "retryBudget":0,"successTargetNodeId":null,"failureTargetNodeId":null,
         "conditionExpression":null,"artifactContract":null,"permissionIntent":null}]}
        """;

    /// <summary>
    /// A minimal well-formed snapshot whose only node carries the given raw <c>gateMetadata</c> value, so
    /// the reader's verdict about the gate cannot come from anything else in the document.
    /// </summary>
    private static string WithGate(string gateJson) => $$"""
        {"entryNodeId":"node-a","nodes":[
        {"nodeId":"node-a","kind":"Prompt","displayName":"Node A","roleBinding":"Role A",
         "requiredCapabilities":[],"primaryRouteId":null,"fallbackRouteIds":[],"timeoutTicks":null,
         "retryBudget":0,"successTargetNodeId":"node-b","failureTargetNodeId":null,
         "conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{{gateJson}}},
        {"nodeId":"node-b","kind":"TerminalOutcome","displayName":"Node B","roleBinding":"Role B",
         "requiredCapabilities":[],"primaryRouteId":null,"fallbackRouteIds":[],"timeoutTicks":null,
         "retryBudget":0,"successTargetNodeId":null,"failureTargetNodeId":null,
         "conditionExpression":null,"artifactContract":null,"permissionIntent":null}]}
        """;

    private static WorkflowGraph CreateGraph(WorkflowNodeGateMetadata? gate) =>
        new(
            "node-a",
            new[]
            {
                new WorkflowNodeDefinition(
                    "node-a",
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: "node-b",
                    gateMetadata: gate),
                new WorkflowNodeDefinition(
                    "node-b",
                    WorkflowNodeKind.TerminalOutcome,
                    "Node B",
                    "Role B")
            });

    private static WorkflowGraph Read(string json) =>
        WorkflowGraphSnapshot.Deserialize(json, RunId);
}
