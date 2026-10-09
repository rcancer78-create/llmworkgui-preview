using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

/// <summary>
/// The narrow slice that decides whether a template version may be pinned to a run at all, and what
/// execution scheme a pinned run then advances against.
/// </summary>
public sealed class WorkflowTemplateExecutionPlanResolverTests
{
    private const string ProjectId = "project-1";
    private const string TemplateId = "linear-template";

    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AGateFreeLinearGraphIsAcceptedAndMappedOneToOne()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await SaveAndAssignAsync(store, 1);

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        Assert.Equal(TemplateId, plan.TemplateId);
        Assert.Equal(1, plan.TemplateVersion);

        var scheme = plan.Scheme.Scheme;

        Assert.Equal("node-a", scheme.InitialStageId);
        Assert.Equal(
            new[] { "node-a", "node-b", "node-c" },
            scheme.Stages.Select(stage => stage.StageId));

        var first = scheme.GetRequiredStage("node-a");
        Assert.Equal("Node A", first.DisplayName);
        Assert.Equal("Role A", first.RequiredRole);
        Assert.Equal(WorkflowStageKind.Custom, first.StageKind);
        Assert.Empty(first.RequiredReviewerRoles);
        Assert.False(first.RequiresUserApproval);
        Assert.Null(first.ArtifactRequirement);
        Assert.Null(first.FailureStageId);
        Assert.Equal("node-b", first.NextStageId);

        var last = scheme.GetRequiredStage("node-c");
        Assert.Null(last.NextStageId);
        Assert.Equal("Role C", last.RequiredRole);
    }

    [Fact]
    public async Task NoReviewerAndNoApprovalIsInferredFromARoleName()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await SaveAndAssignAsync(store, 1, roleSuffix: "-Reviewer-Approver");

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        Assert.All(plan.Scheme.Scheme.Stages, stage =>
        {
            Assert.Contains("-Reviewer-Approver", stage.RequiredRole);
            Assert.Empty(stage.RequiredReviewerRoles);
            Assert.False(stage.RequiresUserApproval);
        });
    }

    [Fact]
    public async Task ThePinnedGraphSnapshotIsTheExactGraphOfTheAssignedVersion()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await SaveAndAssignAsync(store, 1);

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        var pinned = WorkflowGraphSnapshot.Deserialize(plan.GraphSnapshotJson, "run-1");

        Assert.Equal("node-a", pinned.EntryNodeId);
        Assert.Equal(
            new[] { "node-a", "node-b", "node-c" },
            pinned.Nodes.Select(node => node.NodeId));
        Assert.Equal(
            new[] { WorkflowNodeKind.Prompt, WorkflowNodeKind.Prompt, WorkflowNodeKind.TerminalOutcome },
            pinned.Nodes.Select(node => node.Kind));
        Assert.Equal(
            new[] { "Node A", "Node B", "Node C" },
            pinned.Nodes.Select(node => node.DisplayName));
    }

    [Fact]
    public async Task TheSchemeSnapshotRebuildsIntoTheSameSchemeItWasDerivedFrom()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await SaveAndAssignAsync(store, 1);

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        var rebuilt = WorkflowSchemeSnapshot.Deserialize(plan.SchemeSnapshotJson, "run-1");

        Assert.Equal(plan.Scheme.Scheme.InitialStageId, rebuilt.Scheme.InitialStageId);
        Assert.Equal(
            plan.Scheme.Scheme.Stages.Select(stage => stage.StageId),
            rebuilt.Scheme.Stages.Select(stage => stage.StageId));
        Assert.Equal(plan.Scheme.Json, rebuilt.Json);
    }

    [Fact]
    public async Task AProjectWithoutAnAssignmentIsRefused()
    {
        var store = new InMemoryWorkflowTemplateStore();

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync(ProjectId));

        Assert.Equal(WorkflowTemplateExecutionBlockers.MissingAssignment, blocked.Blocker);
        Assert.Contains(WorkflowTemplateExecutionBlockers.MissingAssignment, blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAssignmentToAVersionThatDoesNotExistIsRefused()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(CreateLinearTemplate(TemplateId, 1));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 2));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync(ProjectId));

        Assert.Equal(WorkflowTemplateExecutionBlockers.MissingTemplateVersion, blocked.Blocker);
    }

    [Fact]
    public async Task TheShippedBuiltInStandardTemplateResolvesToItsWholeChain()
    {
        var store = new InMemoryWorkflowTemplateStore(
            new[] { WorkflowStudioService.CreateStandardTemplate() });
        await store.SaveAssignmentAsync(
            CreateAssignment(WorkflowStudioService.StandardTemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        Assert.Equal(WorkflowStudioService.StandardTemplateId, plan.TemplateId);
        Assert.Equal(1, plan.TemplateVersion);

        var scheme = plan.Scheme.Scheme;

        // Every stage is derived from the stored graph, one node to one stage, in the order the graph's
        // success chain walks. Nothing is dropped and nothing is invented: the chain ends on the terminal
        // outcome node, not on a stage the resolver added.
        Assert.Equal("stage-task-specification", scheme.InitialStageId);
        Assert.Equal(
            new[]
            {
                "stage-task-specification",
                "stage-architecture",
                "stage-technical-specification",
                "stage-roadmap",
                "stage-document-review",
                "stage-user-approval",
                "stage-implementation-packages",
                "stage-code-and-ui",
                "stage-multi-level-review",
                "stage-tests-and-ui-acceptance",
                "stage-final-outcome"
            },
            scheme.Stages.Select(stage => stage.StageId));
    }

    /// <summary>
    /// The built-in graph's own declared gates are what the derived stages carry, field for field, and its
    /// declared edges are what the derived stages keep. A stage kind, a reviewer list and its order, an
    /// approval flag and an artifact requirement all come from the node's preserved gate metadata and from
    /// nowhere else - never from the node kind, which is a label.
    /// </summary>
    [Fact]
    public async Task TheShippedBuiltInStandardTemplateCarriesItsOwnGatesAndEdgesIntoTheDerivedStages()
    {
        var store = new InMemoryWorkflowTemplateStore(
            new[] { WorkflowStudioService.CreateStandardTemplate() });
        await store.SaveAssignmentAsync(
            CreateAssignment(WorkflowStudioService.StandardTemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        var template = WorkflowStudioService.CreateStandardTemplate();
        var scheme = plan.Scheme.Scheme;

        foreach (var node in template.Graph.Nodes)
        {
            var stage = scheme.GetRequiredStage(node.NodeId);
            var gate = node.GateMetadata!;

            Assert.Equal(node.DisplayName, stage.DisplayName);
            Assert.Equal(node.RoleBinding, stage.RequiredRole);
            Assert.Equal(node.SuccessTargetNodeId, stage.NextStageId);
            Assert.Equal(node.FailureTargetNodeId, stage.FailureStageId);
            Assert.Equal(gate.StageKind, stage.StageKind);
            Assert.Equal(gate.RequiredReviewerRoles, stage.RequiredReviewerRoles);
            Assert.Equal(gate.RequiresUserApproval, stage.RequiresUserApproval);
            Assert.Equal(gate.ArtifactRequirement, stage.ArtifactRequirement);
        }

        // The stage runner keeps rejected gates blocked; it does not implement automatic rework.
        // The shipped linear template must not declare backward routes or an unconsumed Writer budget.
        Assert.Null(scheme.GetRequiredStage("stage-multi-level-review").FailureStageId);
        Assert.Null(scheme.GetRequiredStage("stage-tests-and-ui-acceptance").FailureStageId);
        Assert.Equal(0, template.Graph.GetRequiredNode("stage-code-and-ui").RetryBudget);

        // The review and approval stages keep the gates a run will really have to satisfy.
        var documentReview = scheme.GetRequiredStage("stage-document-review");
        Assert.Equal(new[] { "Reviewer", "Architect" }, documentReview.RequiredReviewerRoles);
        Assert.Equal("DocumentBundle", documentReview.ArtifactRequirement);

        var userApproval = scheme.GetRequiredStage("stage-user-approval");
        Assert.True(userApproval.RequiresUserApproval);
        Assert.Equal("ApprovedDocument", userApproval.ArtifactRequirement);

        // The terminal stage declares no gate at all, and completion is not something a transition decides.
        var finalOutcome = scheme.GetRequiredStage("stage-final-outcome");
        Assert.Equal(WorkflowStageKind.FinalVerification, finalOutcome.StageKind);
        Assert.Empty(finalOutcome.RequiredReviewerRoles);
        Assert.False(finalOutcome.RequiresUserApproval);
        Assert.Null(finalOutcome.ArtifactRequirement);
        Assert.Null(finalOutcome.NextStageId);
    }

    /// <summary>
    /// The built-in is pinned exactly as the studio ships it or not at all. A store row whose graph no
    /// longer is that graph refuses by name, and it refuses before any stage is derived, so the reason a
    /// caller sees is the difference itself rather than a symptom of it.
    /// </summary>
    [Theory]
    [InlineData("reviewers", "stage-document-review")]
    [InlineData("approval", "stage-user-approval")]
    [InlineData("artifact", "stage-user-approval")]
    [InlineData("failure-edge", "stage-multi-level-review")]
    [InlineData("retry-budget", "stage-code-and-ui")]
    [InlineData("node-kind", "stage-document-review")]
    [InlineData("display-name", "stage-architecture")]
    [InlineData("timeout", "stage-code-and-ui")]
    public async Task AChangedOrCorruptedBuiltInGraphIsRefusedByName(
        string corruption,
        string expectedNodeId)
    {
        var store = new InMemoryWorkflowTemplateStore(
            new[] { CreateCorruptedBuiltInTemplate(corruption) });
        await store.SaveAssignmentAsync(
            CreateAssignment(WorkflowStudioService.StandardTemplateId, 1));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync(ProjectId));

        Assert.Equal(
            WorkflowTemplateExecutionBlockers.BuiltInStandardTemplateGraphChanged,
            blocked.Blocker);
        Assert.Contains(expectedNodeId, blocked.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateStandardDevelopmentScheme", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserOwnedVersionOfTheStandardTemplateResolvesItsEditedGraph()
    {
        var original = WorkflowStudioService.CreateStandardTemplate();
        var originalGraph = WorkflowGraphSnapshot.Serialize(original.Graph);
        var store = new InMemoryWorkflowTemplateStore(new[] { original });
        var studio = new WorkflowStudioService(templateStore: store);
        var versionTwo = await studio.CreateTemplateVersionAsync(original.TemplateId, 2, sourceVersion: 1);
        var edited = ReplaceGraph(versionTwo.CreateVersion(3),
            CreateCorruptedBuiltInTemplate("display-name").Graph);
        await studio.SaveTemplateAsync(edited);
        var assignment = await studio.AssignTemplateToProjectAsync(ProjectId, edited.TemplateId, edited.Version);

        var plan = await new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync(ProjectId);

        Assert.True(assignment.IsAssigned);
        Assert.False(edited.IsBuiltIn);
        Assert.Equal(original.TemplateId, plan.TemplateId);
        Assert.Equal(3, plan.TemplateVersion);
        Assert.Equal(WorkflowGraphSnapshot.Serialize(edited.Graph), plan.GraphSnapshotJson);
        Assert.Equal("Архитектура (изменена)", plan.Scheme.Scheme.GetRequiredStage("stage-architecture").DisplayName);
        foreach (var node in edited.Graph.Nodes)
        {
            var stage = plan.Scheme.Scheme.GetRequiredStage(node.NodeId);
            var gate = node.GateMetadata!;
            Assert.Equal(node.SuccessTargetNodeId, stage.NextStageId);
            Assert.Equal(node.FailureTargetNodeId, stage.FailureStageId);
            Assert.Equal(gate.StageKind, stage.StageKind);
            Assert.Equal(gate.RequiredReviewerRoles, stage.RequiredReviewerRoles);
            Assert.Equal(gate.RequiresUserApproval, stage.RequiresUserApproval);
            Assert.Equal(gate.ArtifactRequirement, stage.ArtifactRequirement);
        }

        Assert.Equal(originalGraph, WorkflowGraphSnapshot.Serialize(
            (await store.GetAsync(original.TemplateId, original.Version))!.Graph));
        Assert.Equal(originalGraph, WorkflowGraphSnapshot.Serialize(
            (await store.GetAsync(versionTwo.TemplateId, versionTwo.Version))!.Graph));
    }

    [Fact]
    public async Task ClearingTheBuiltInFlagDoesNotAllowChangingTheShippedVersion()
    {
        var corrupted = CreateCorruptedBuiltInTemplate("reviewers")
            .CreateVersion(WorkflowStudioService.StandardTemplateVersion, isBuiltIn: false);
        var store = new InMemoryWorkflowTemplateStore(new[] { corrupted });
        await store.SaveAssignmentAsync(CreateAssignment(corrupted.TemplateId, corrupted.Version));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(() =>
            new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync(ProjectId));

        Assert.Equal(WorkflowTemplateExecutionBlockers.BuiltInStandardTemplateGraphChanged, blocked.Blocker);
        Assert.Contains("stage-document-review", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomTemplateIsNotJudgedByTheBuiltInProof()
    {
        // A non-built-in template id is resolved by the generic conversion only, so the same graph shape the
        // built-in proof would reject is a perfectly ordinary custom template here.
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(CreateLinearTemplate(TemplateId, 1));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        Assert.Equal(TemplateId, plan.TemplateId);
    }

    [Fact]
    public async Task APromptToTerminalGraphWithAbsentMetadataKeepsTheGateFreePath()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(CreateGateTemplate(CreateGateGraph(gate: null)));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        Assert.Equal(2, plan.Scheme.Scheme.Stages.Count);
        Assert.Null(
            WorkflowGraphSnapshot.Deserialize(plan.GraphSnapshotJson, "run-1")
                .GetRequiredNode("node-a")
                .GateMetadata);
    }

    [Fact]
    public async Task APromptToTerminalGraphWithTheExplicitNoGateTupleIsAccepted()
    {
        var gate = new WorkflowNodeGateMetadata(
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null);

        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(CreateGateTemplate(CreateGateGraph(gate)));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        var pinnedGate = WorkflowGraphSnapshot.Deserialize(plan.GraphSnapshotJson, "run-1")
            .GetRequiredNode("node-a")
            .GateMetadata;

        Assert.NotNull(pinnedGate);
        Assert.True(pinnedGate!.IsNoGate);
        Assert.Equal(WorkflowStageKind.Custom, plan.Scheme.Scheme.GetRequiredStage("node-a").StageKind);
    }

    public static TheoryData<WorkflowNodeGateMetadata> EnforceableGates()
    {
        // Gates a run can actually evaluate. Each one is refused by no rule, so each has to reach the
        // scheme stage with every one of its four fields intact.
        var data = new TheoryData<WorkflowNodeGateMetadata>();

        // A stage kind on its own: it names what the stage is and asks for nothing.
        data.Add(new WorkflowNodeGateMetadata(
            WorkflowStageKind.DocumentReview,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null));

        // An artifact with no human gate: the stored content is required, nobody has to sign it.
        data.Add(new WorkflowNodeGateMetadata(
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: "report.md"));

        data.Add(new WorkflowNodeGateMetadata(
            WorkflowStageKind.DocumentReview,
            new[] { "Reviewer", "Architect" },
            requiresUserApproval: true,
            artifactRequirement: "DocumentBundle"));

        return data;
    }

    [Theory]
    [MemberData(nameof(EnforceableGates))]
    public async Task ADeclaredGateReachesTheSchemeStageFieldForField(WorkflowNodeGateMetadata gate)
    {
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(CreateGateTemplate(CreateGateGraph(gate)));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        var stage = plan.Scheme.Scheme.GetRequiredStage("node-a");

        Assert.Equal(gate.StageKind, stage.StageKind);
        Assert.Equal(gate.RequiredReviewerRoles, stage.RequiredReviewerRoles);
        Assert.Equal(gate.RequiresUserApproval, stage.RequiresUserApproval);
        Assert.Equal(gate.ArtifactRequirement, stage.ArtifactRequirement);

        // The node identity and the linear link are preserved next to the gate, and nothing is added.
        Assert.Equal("node-a", stage.StageId);
        Assert.Equal("Node A", stage.DisplayName);
        Assert.Equal("Role A", stage.RequiredRole);
        Assert.Equal("node-b", stage.NextStageId);
        Assert.Null(stage.FailureStageId);
    }

    [Theory]
    [MemberData(nameof(EnforceableGates))]
    public async Task ADeclaredGateSurvivesThePinnedSnapshotsInsteadOfBeingFlattened(
        WorkflowNodeGateMetadata gate)
    {
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(CreateGateTemplate(CreateGateGraph(gate)));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        // Read the gate back the way a reopened run does, not from the object the resolver just built.
        var pinned = WorkflowGraphSnapshot.Deserialize(plan.GraphSnapshotJson, "run-1")
            .GetRequiredNode("node-a")
            .GateMetadata;
        var rebuilt = WorkflowSchemeSnapshot.Deserialize(plan.SchemeSnapshotJson, "run-1")
            .Scheme
            .GetRequiredStage("node-a");

        Assert.NotNull(pinned);
        Assert.Equal(gate.StageKind, pinned!.StageKind);
        Assert.Equal(gate.StageKind, rebuilt.StageKind);
        Assert.Equal(pinned.RequiredReviewerRoles, rebuilt.RequiredReviewerRoles);
        Assert.Equal(pinned.RequiresUserApproval, rebuilt.RequiresUserApproval);
        Assert.Equal(pinned.ArtifactRequirement, rebuilt.ArtifactRequirement);

        // A serialization that dropped the gate would leave a gate-free stage, so the document itself is
        // checked for the values that only a real gate can contribute.
        Assert.Contains(
            $"\"stageKind\":\"{gate.StageKind}\"",
            plan.SchemeSnapshotJson,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"requiresUserApproval\":{(gate.RequiresUserApproval ? "true" : "false")}",
            plan.SchemeSnapshotJson,
            StringComparison.Ordinal);
        foreach (var reviewer in gate.RequiredReviewerRoles)
        {
            Assert.Contains($"\"{reviewer}\"", plan.SchemeSnapshotJson, StringComparison.Ordinal);
        }

        if (gate.ArtifactRequirement is { } artifact)
        {
            Assert.Contains($"\"{artifact}\"", plan.SchemeSnapshotJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ATerminalNodeMayKeepAStageKindOnlyDeclaration()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(CreateGateTemplate(CreateGateGraph(
            gate: null,
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.FinalVerification,
                Array.Empty<string>(),
                requiresUserApproval: false,
                artifactRequirement: null))));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        var stage = plan.Scheme.Scheme.GetRequiredStage("node-b");

        // The terminal stage keeps the declared kind and still asks for nothing to be decided.
        Assert.Equal(WorkflowStageKind.FinalVerification, stage.StageKind);
        Assert.Empty(stage.RequiredReviewerRoles);
        Assert.False(stage.RequiresUserApproval);
        Assert.Null(stage.ArtifactRequirement);
        Assert.Null(stage.NextStageId);
    }

    public static TheoryData<WorkflowNodeGateMetadata, string> UnenforceableGates()
    {
        // One otherwise supported graph, one gate at a time, each paired with the value the refusal has to
        // name back. The refusal can only come from the field, and it has to state what the node declared
        // rather than only that something about it was refused.
        var data = new TheoryData<WorkflowNodeGateMetadata, string>();

        data.Add(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.Custom,
                new[] { "Reviewer" },
                requiresUserApproval: false,
                artifactRequirement: null),
            "1 required reviewer(s) (Reviewer)");

        data.Add(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.DocumentReview,
                new[] { "Reviewer", "Architect" },
                requiresUserApproval: true,
                artifactRequirement: null),
            "2 required reviewer(s) (Reviewer, Architect)");

        data.Add(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.Custom,
                Array.Empty<string>(),
                requiresUserApproval: true,
                artifactRequirement: null),
            "user approval required");

        return data;
    }

    [Theory]
    [MemberData(nameof(UnenforceableGates))]
    public async Task AReviewerOrApprovalGateWithNoArtifactIsRefusedByName(
        WorkflowNodeGateMetadata gate,
        string expectedDeclaration)
    {
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(CreateGateTemplate(CreateGateGraph(gate)));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync(ProjectId));

        Assert.Equal(WorkflowTemplateExecutionBlockers.UnrepresentableNodeGate, blocked.Blocker);
        Assert.Contains(
            WorkflowTemplateExecutionBlockers.UnrepresentableNodeGate,
            blocked.Message,
            StringComparison.Ordinal);
        Assert.Contains("node-a", blocked.Message, StringComparison.Ordinal);
        Assert.Contains(expectedDeclaration, blocked.Message, StringComparison.Ordinal);
    }

    public static TheoryData<WorkflowNodeGateMetadata, string> UnenforceableTerminalGates()
    {
        var data = new TheoryData<WorkflowNodeGateMetadata, string>();

        data.Add(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.FinalVerification,
                new[] { "Reviewer" },
                requiresUserApproval: false,
                artifactRequirement: "ApprovedDocument"),
            "1 required reviewer(s) (Reviewer)");

        data.Add(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.UserApproval,
                Array.Empty<string>(),
                requiresUserApproval: true,
                artifactRequirement: "ApprovedDocument"),
            "user approval required");

        // An artifact alone is refused as well: nothing would ever ask for it, because completing a run
        // does not evaluate a transition.
        data.Add(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.FinalVerification,
                Array.Empty<string>(),
                requiresUserApproval: false,
                artifactRequirement: "ApprovedDocument"),
            "artifact requirement 'ApprovedDocument'");

        return data;
    }

    [Theory]
    [MemberData(nameof(UnenforceableTerminalGates))]
    public async Task ATerminalGateNoRunCouldEvaluateIsRefusedByName(
        WorkflowNodeGateMetadata gate,
        string expectedDeclaration)
    {
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(CreateGateTemplate(CreateGateGraph(gate: null, terminalGate: gate)));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync(ProjectId));

        Assert.Equal(WorkflowTemplateExecutionBlockers.TerminalNodeGate, blocked.Blocker);
        Assert.Contains(WorkflowTemplateExecutionBlockers.TerminalNodeGate, blocked.Message, StringComparison.Ordinal);
        Assert.Contains("node-b", blocked.Message, StringComparison.Ordinal);
        Assert.Contains(expectedDeclaration, blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedGateIsNeverMappedOntoAPlainSchemeStage()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(CreateGateTemplate(CreateGateGraph(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.DocumentReview,
                new[] { "Reviewer" },
                requiresUserApproval: false,
                artifactRequirement: null))));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        // The refusal is a refusal, not a fallback: mapping a reviewer gate without an artifact onto a
        // stage would leave a reviewer requirement with no content to decide on, which is the exact loss
        // being prevented.
        await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync(ProjectId));
    }

    public static TheoryData<string, Func<WorkflowTemplateDefinition, WorkflowTemplateDefinition>> RefusedGraphs()
    {
        var data = new TheoryData<string, Func<WorkflowTemplateDefinition, WorkflowTemplateDefinition>>();

        data.Add(
            WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.UserDecision)));

        data.Add(
            WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.Escalation)));

        data.Add(
            WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.Condition)));

        data.Add(
            WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.Retry)));

        data.Add(
            WorkflowTemplateExecutionBlockers.ArtifactContract,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.ArtifactCollection)));

        data.Add(
            WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.Review)));

        data.Add(
            WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.ApprovalGate)));

        data.Add(
            WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.Writer)));

        data.Add(
            WorkflowTemplateExecutionBlockers.UnsupportedNodeKind,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.ValidationCommand)));

        // A failure target that names a node the graph does not declare is not a route anything could
        // preserve, so it is refused by name instead of being carried onto a stage that points nowhere.
        data.Add(
            WorkflowTemplateExecutionBlockers.FailureTransition,
            template => ReplaceGraph(template, CreateDanglingFailureGraph()));

        data.Add(
            WorkflowTemplateExecutionBlockers.UnreachedNode,
            template => ReplaceGraph(template, CreateDetachedIslandGraph()));

        data.Add(
            WorkflowTemplateExecutionBlockers.Cycle,
            template => ReplaceGraph(template, CreateCyclicGraph()));

        // A failure edge that points back at the success chain is a back edge, not a loop: the node is
        // reached once along the chain and its own failure target adds no stage. A node that fails into
        // itself, on the other hand, is a cycle the graph itself refuses to store.
        data.Add(
            WorkflowTemplateExecutionBlockers.InvalidGraph,
            template => ReplaceGraph(template, CreateSelfFailureGraph()));

        data.Add(
            WorkflowTemplateExecutionBlockers.SuccessTransition,
            template => ReplaceGraph(template, CreateDeadEndGraph()));

        data.Add(
            WorkflowTemplateExecutionBlockers.Condition,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.Prompt, condition: "always")));

        data.Add(
            WorkflowTemplateExecutionBlockers.FallbackRoute,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.Prompt, fallbackRoute: "route-b")));

        data.Add(
            WorkflowTemplateExecutionBlockers.ArtifactContract,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.Prompt, artifactContract: "doc.md")));

        data.Add(
            WorkflowTemplateExecutionBlockers.PermissionIntent,
            template => ReplaceGraph(template, CreateLinearGraph(WorkflowNodeKind.Prompt, permissionIntent: "write")));

        data.Add(
            WorkflowTemplateExecutionBlockers.RetryBudget,
            template => ReplaceGraph(template, CreateTerminalFieldGraph("retry-budget")));

        data.Add(
            WorkflowTemplateExecutionBlockers.Condition,
            template => ReplaceGraph(template, CreateTerminalFieldGraph("condition")));

        data.Add(
            WorkflowTemplateExecutionBlockers.FallbackRoute,
            template => ReplaceGraph(template, CreateTerminalFieldGraph("fallback-route")));

        data.Add(
            WorkflowTemplateExecutionBlockers.ArtifactContract,
            template => ReplaceGraph(template, CreateTerminalFieldGraph("artifact")));

        data.Add(
            WorkflowTemplateExecutionBlockers.PermissionIntent,
            template => ReplaceGraph(template, CreateTerminalFieldGraph("permission")));

        data.Add(
            WorkflowTemplateExecutionBlockers.TerminalTransition,
            template => ReplaceGraph(template, CreateTerminalTransitionGraph()));

        return data;
    }

    /// <summary>
    /// A non-terminal node that declares a retry budget is accepted, and the budget is preserved on the
    /// pinned graph rather than being acted on. It belongs to node execution, which is unavailable here, so
    /// the derived stage carries no trace of it and the run has nothing that could retry: a second transition
    /// attempt still has to satisfy the same gates from the same stored evidence.
    /// </summary>
    [Fact]
    public async Task ARetryBudgetIsPreservedOnThePinnedGraphAndNeverBecomesAStageTransitionRetry()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(ReplaceGraph(
            CreateLinearTemplate(TemplateId, 1),
            CreateLinearGraph(WorkflowNodeKind.Prompt, retryBudget: 3)));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        var pinned = WorkflowGraphSnapshot.Deserialize(plan.GraphSnapshotJson, "run-1");

        Assert.Equal(3, pinned.GetRequiredNode("node-a").RetryBudget);
        Assert.Equal(3, pinned.GetRequiredNode("node-b").RetryBudget);

        // The scheme stage is exactly the declared gate-free one. There is no retry field to read, because
        // a stage transition is not a node execution and must never be given a retry budget.
        foreach (var stage in plan.Scheme.Scheme.Stages)
        {
            Assert.Equal(WorkflowStageKind.Custom, stage.StageKind);
            Assert.Empty(stage.RequiredReviewerRoles);
            Assert.False(stage.RequiresUserApproval);
        }

        // The pinned scheme document itself names no budget at all, so nothing reading it back after a
        // restart can find a retry to perform.
        Assert.DoesNotContain("retry", plan.SchemeSnapshotJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A declared failure target is preserved on the derived stage, on the pinned scheme and on the pinned
    /// graph, so the route a template declared survives a restart. It is preserved, not taken: the node the
    /// edge names is a stage of the same plan, and nothing about the derivation turns the edge into a
    /// transition.
    /// </summary>
    [Fact]
    public async Task ADeclaredFailureTargetIsPreservedOnEveryStageThatCarriesIt()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await store.SaveAsync(ReplaceGraph(
            CreateLinearTemplate(TemplateId, 1),
            CreateBranchingGraph(WorkflowNodeKind.Prompt, includeFailureEdge: true)));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        var scheme = plan.Scheme.Scheme;

        Assert.Equal("node-d", scheme.GetRequiredStage("node-a").FailureStageId);
        Assert.Null(scheme.GetRequiredStage("node-b").FailureStageId);
        Assert.Null(scheme.GetRequiredStage("node-c").FailureStageId);
        Assert.Null(scheme.GetRequiredStage("node-d").FailureStageId);

        // The node the edge names is a real stage of the same plan, not a dangling reference, and the
        // success chain from the entry node is still the stage order - a back edge adds no stage.
        Assert.Equal(
            new[] { "node-a", "node-b", "node-c", "node-d" },
            scheme.Stages.Select(stage => stage.StageId));

        // The edge is still there after the snapshots are read back the way a reopened run reads them.
        Assert.Equal(
            "node-d",
            WorkflowSchemeSnapshot.Deserialize(plan.SchemeSnapshotJson, "run-1")
                .Scheme
                .GetRequiredStage("node-a")
                .FailureStageId);
        Assert.Equal(
            "node-d",
            WorkflowGraphSnapshot.Deserialize(plan.GraphSnapshotJson, "run-1")
                .GetRequiredNode("node-a")
                .FailureTargetNodeId);
    }

    /// <summary>
    /// A kind is a label, so it never supplies a gate - and it may not stand in for one the node did not
    /// declare either. Each of the four kinds the shipped built-in graph uses is refused, by name, when the
    /// node carrying it declares nothing the kind promises, so the label cannot be executed as a gate-free
    /// stage.
    /// </summary>
    public static TheoryData<WorkflowNodeKind, string> KindsThatPromiseAGateTheyDidNotDeclare()
    {
        var data = new TheoryData<WorkflowNodeKind, string>();

        data.Add(WorkflowNodeKind.Review, "no required reviewer role");
        data.Add(WorkflowNodeKind.ApprovalGate, "no user approval");
        data.Add(WorkflowNodeKind.Writer, "no artifact requirement");
        data.Add(WorkflowNodeKind.ValidationCommand, "no artifact requirement");

        return data;
    }

    [Theory]
    [MemberData(nameof(KindsThatPromiseAGateTheyDidNotDeclare))]
    public async Task AKindThatPromisesAGateTheNodeDidNotDeclareIsRefusedByName(
        WorkflowNodeKind kind,
        string expectedMissing)
    {
        var unbacked = new InMemoryWorkflowTemplateStore();
        await unbacked.SaveAsync(ReplaceGraph(
            CreateLinearTemplate(TemplateId, 1),
            CreateLinearGraph(kind)));
        await unbacked.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => new WorkflowTemplateExecutionPlanResolver(unbacked).ResolveForProjectAsync(ProjectId));

        Assert.Equal(WorkflowTemplateExecutionBlockers.UnsupportedNodeKind, blocked.Blocker);
        Assert.Contains(expectedMissing, blocked.Message, StringComparison.Ordinal);
        Assert.Contains("node-a", blocked.Message, StringComparison.Ordinal);

        // The control is the same graph with the gate the kind names: the label then agrees with the
        // declaration, and the stage is derived from the declaration alone.
        var agreeing = new InMemoryWorkflowTemplateStore();
        await agreeing.SaveAsync(ReplaceGraph(
            CreateLinearTemplate(TemplateId, 1),
            CreateLinearGraph(kind, gate: GateForKind(kind))));
        await agreeing.SaveAssignmentAsync(CreateAssignment(TemplateId, 1));

        var plan = await new WorkflowTemplateExecutionPlanResolver(agreeing)
            .ResolveForProjectAsync(ProjectId);

        var stage = plan.Scheme.Scheme.GetRequiredStage("node-a");
        Assert.Equal(GateForKind(kind).StageKind, stage.StageKind);
        Assert.Equal(GateForKind(kind).RequiredReviewerRoles, stage.RequiredReviewerRoles);
        Assert.Equal(GateForKind(kind).RequiresUserApproval, stage.RequiresUserApproval);
        Assert.Equal(GateForKind(kind).ArtifactRequirement, stage.ArtifactRequirement);
    }

    private static WorkflowNodeGateMetadata GateForKind(WorkflowNodeKind kind) => kind switch
    {
        WorkflowNodeKind.Review => new WorkflowNodeGateMetadata(
            WorkflowStageKind.DocumentReview,
            new[] { "Reviewer" },
            requiresUserApproval: false,
            artifactRequirement: "ReviewedDiff"),
        WorkflowNodeKind.ApprovalGate => new WorkflowNodeGateMetadata(
            WorkflowStageKind.UserApproval,
            Array.Empty<string>(),
            requiresUserApproval: true,
            artifactRequirement: "ApprovedDiff"),
        WorkflowNodeKind.Writer => new WorkflowNodeGateMetadata(
            WorkflowStageKind.Implementation,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: "ImplementationDiff"),
        WorkflowNodeKind.ValidationCommand => new WorkflowNodeGateMetadata(
            WorkflowStageKind.UiAcceptance,
            new[] { "Tester" },
            requiresUserApproval: true,
            artifactRequirement: "AcceptanceEvidence"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown node kind.")
    };

    [Theory]
    [InlineData("retry-budget")]
    [InlineData("condition")]
    [InlineData("fallback-route")]
    [InlineData("artifact")]
    [InlineData("permission")]
    public async Task TheRefusalNamesTheTerminalNodeThatDeclaresTheUnsupportedField(string field)
    {
        var store = new InMemoryWorkflowTemplateStore();
        var template = ReplaceGraph(CreateLinearTemplate(TemplateId, 1), CreateTerminalFieldGraph(field));
        await store.SaveAsync(template);
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, template.Version));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync(ProjectId));

        Assert.Contains("node-b", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATerminalOutcomeNodeIsNotRequiredToDeclareASuccessTarget()
    {
        var store = new InMemoryWorkflowTemplateStore();
        await SaveAndAssignAsync(store, 1);

        var plan = await new WorkflowTemplateExecutionPlanResolver(store)
            .ResolveForProjectAsync(ProjectId);

        var pinned = WorkflowGraphSnapshot.Deserialize(plan.GraphSnapshotJson, "run-1");
        var terminal = pinned.GetRequiredNode("node-c");

        Assert.Equal(WorkflowNodeKind.TerminalOutcome, terminal.Kind);
        Assert.Null(terminal.SuccessTargetNodeId);
        Assert.Null(terminal.FailureTargetNodeId);
        Assert.Equal(0, terminal.RetryBudget);
        Assert.Null(terminal.ConditionExpression);
        Assert.Empty(terminal.FallbackRouteIds);
        Assert.Null(terminal.ArtifactContract);
        Assert.Null(terminal.PermissionIntent);
        Assert.Null(plan.Scheme.Scheme.GetRequiredStage("node-c").NextStageId);
    }

    [Theory]
    [MemberData(nameof(RefusedGraphs))]
    public async Task EveryOtherGraphIsRefusedWithItsOwnNamedBlocker(
        string expectedBlocker,
        Func<WorkflowTemplateDefinition, WorkflowTemplateDefinition> graphFactory)
    {
        var store = new InMemoryWorkflowTemplateStore();
        var template = graphFactory(CreateLinearTemplate(TemplateId, 1));
        await store.SaveAsync(template);
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, template.Version));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => new WorkflowTemplateExecutionPlanResolver(store).ResolveForProjectAsync(ProjectId));

        Assert.Equal(expectedBlocker, blocked.Blocker);
        Assert.Contains(expectedBlocker, blocked.Message, StringComparison.Ordinal);
    }

    private static WorkflowTemplateAssignment CreateAssignment(string templateId, int version) =>
        new($"assignment-{templateId}-{version}", ProjectId, templateId, version, CreatedAt);

    private static async Task SaveAndAssignAsync(
        InMemoryWorkflowTemplateStore store,
        int version,
        string roleSuffix = "")
    {
        await store.SaveAsync(CreateLinearTemplate(TemplateId, version, roleSuffix));
        await store.SaveAssignmentAsync(CreateAssignment(TemplateId, version));
    }

    private static WorkflowTemplateDefinition CreateLinearTemplate(
        string templateId,
        int version,
        string roleSuffix = "") =>
        new(
            templateId,
            version,
            "Linear template",
            "A gate-free linear template.",
            CreateLinearGraph(WorkflowNodeKind.Prompt, roleSuffix: roleSuffix),
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            CreatedAt);

    private static WorkflowTemplateDefinition ReplaceGraph(
        WorkflowTemplateDefinition template,
        WorkflowGraph graph) =>
        new(
            template.TemplateId,
            template.Version,
            template.DisplayName,
            template.Description,
            graph,
            template.RoleBindings,
            template.RequiredDocumentTemplates,
            template.IsBuiltIn,
            template.CreatedAtUtc);

    private static WorkflowTemplateDefinition CreateGateTemplate(WorkflowGraph graph) =>
        new(
            TemplateId,
            1,
            "Gate template",
            "A linear template whose nodes may declare stage gates.",
            graph,
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            CreatedAt);

    /// <summary>
    /// The smallest graph this slice accepts - one Prompt node and one terminal outcome node - with the
    /// gate metadata of either node left under the test's control.
    /// </summary>
    private static WorkflowGraph CreateGateGraph(
        WorkflowNodeGateMetadata? gate,
        WorkflowNodeGateMetadata? terminalGate = null) =>
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
                    "Role B",
                    gateMetadata: terminalGate)
            });

    private static WorkflowGraph CreateLinearGraph(
        WorkflowNodeKind nonTerminalKind,
        string roleSuffix = "",
        int retryBudget = 0,
        string? condition = null,
        string? fallbackRoute = null,
        string? artifactContract = null,
        string? permissionIntent = null,
        WorkflowNodeGateMetadata? gate = null) =>
        new(
            "node-a",
            new[]
            {
                CreateNode(
                    "node-a",
                    "Node A",
                    nonTerminalKind,
                    "node-b",
                    "Role A" + roleSuffix,
                    retryBudget,
                    condition,
                    fallbackRoute,
                    artifactContract,
                    permissionIntent,
                    gate),
                CreateNode(
                    "node-b",
                    "Node B",
                    nonTerminalKind,
                    "node-c",
                    "Role B" + roleSuffix,
                    retryBudget,
                    condition,
                    fallbackRoute,
                    artifactContract,
                    permissionIntent,
                    gate),
                CreateNode("node-c", "Node C", WorkflowNodeKind.TerminalOutcome, null, "Role C" + roleSuffix)
            });

    /// <summary>
    /// A failure target that names a node the graph does not declare. The store accepts the graph - the
    /// dangling id is a string - so the refusal can only come from the run start reading the edge.
    /// </summary>
    private static WorkflowGraph CreateDanglingFailureGraph() =>
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
                    failureTargetNodeId: "node-d"),
                new WorkflowNodeDefinition("node-b", WorkflowNodeKind.TerminalOutcome, "Node B", "Role B")
            });

    /// <summary>
    /// A node whose failure target is the node itself. A back edge onto the success chain is an ordinary
    /// declared route and is preserved; a node that fails into itself is a cycle, and the stored document is
    /// not a valid workflow graph at all - which is a different refusal, and a different blocker name, from
    /// a success chain that visits a node twice.
    /// </summary>
    private static WorkflowGraph CreateSelfFailureGraph() =>
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
                    failureTargetNodeId: "node-a"),
                new WorkflowNodeDefinition("node-b", WorkflowNodeKind.TerminalOutcome, "Node B", "Role B")
            });

    /// <summary>
    /// A gate-free Prompt to Terminal chain whose terminal outcome node carries one extra declaration the
    /// derived scheme stage cannot express. Nothing else about the graph changes, so the refusal can only
    /// come from the field on the terminal node.
    /// </summary>
    private static WorkflowGraph CreateTerminalFieldGraph(string field) =>
        new(
            "node-a",
            new[]
            {
                CreateNode("node-a", "Node A", WorkflowNodeKind.Prompt, "node-b", "Role A"),
                CreateTerminalNode("node-b", field)
            });

    private static WorkflowNodeDefinition CreateTerminalNode(string nodeId, string field) =>
        field switch
        {
            "retry-budget" => new WorkflowNodeDefinition(
                nodeId,
                WorkflowNodeKind.TerminalOutcome,
                "Node B",
                "Role B",
                retryBudget: 2),
            "condition" => new WorkflowNodeDefinition(
                nodeId,
                WorkflowNodeKind.TerminalOutcome,
                "Node B",
                "Role B",
                conditionExpression: "always"),
            "fallback-route" => new WorkflowNodeDefinition(
                nodeId,
                WorkflowNodeKind.TerminalOutcome,
                "Node B",
                "Role B",
                fallbackRouteIds: new[] { "route-b" }),
            "artifact" => new WorkflowNodeDefinition(
                nodeId,
                WorkflowNodeKind.TerminalOutcome,
                "Node B",
                "Role B",
                artifactContract: "report.md"),
            "permission" => new WorkflowNodeDefinition(
                nodeId,
                WorkflowNodeKind.TerminalOutcome,
                "Node B",
                "Role B",
                permissionIntent: "write"),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown terminal field.")
        };

    /// <summary>
    /// A terminal outcome node that keeps a transition out of it. Accepting a run against it would mean
    /// executing a graph whose declared exit is not in the derived scheme, so the transition itself stays
    /// a refusal rather than being evaluated as a missing target.
    /// </summary>
    private static WorkflowGraph CreateTerminalTransitionGraph() =>
        new(
            "node-a",
            new[]
            {
                CreateNode("node-a", "Node A", WorkflowNodeKind.Prompt, "node-b", "Role A"),
                CreateNode("node-b", "Node B", WorkflowNodeKind.TerminalOutcome, "node-c", "Role B"),
                CreateNode("node-c", "Node C", WorkflowNodeKind.TerminalOutcome, null, "Role C")
            });

    private static WorkflowGraph CreateBranchingGraph(
        WorkflowNodeKind nonTerminalKind,
        bool includeFailureEdge) =>
        new(
            "node-a",
            new[]
            {
                new WorkflowNodeDefinition(
                    "node-a",
                    nonTerminalKind,
                    "Node A",
                    "Role A",
                    successTargetNodeId: "node-b",
                    failureTargetNodeId: includeFailureEdge ? "node-d" : null),
                new WorkflowNodeDefinition(
                    "node-b",
                    nonTerminalKind,
                    "Node B",
                    "Role B",
                    successTargetNodeId: "node-c"),
                new WorkflowNodeDefinition("node-c", WorkflowNodeKind.TerminalOutcome, "Node C", "Role C"),
                new WorkflowNodeDefinition(
                    "node-d",
                    WorkflowNodeKind.TerminalOutcome,
                    "Node D",
                    "Role D")
            });

    private static WorkflowGraph CreateDetachedIslandGraph() =>
        new(
            "node-a",
            new[]
            {
                new WorkflowNodeDefinition(
                    "node-a",
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: "node-b"),
                new WorkflowNodeDefinition("node-b", WorkflowNodeKind.TerminalOutcome, "Node B", "Role B"),
                new WorkflowNodeDefinition(
                    "node-c",
                    WorkflowNodeKind.Prompt,
                    "Node C",
                    "Role C",
                    successTargetNodeId: "node-d"),
                new WorkflowNodeDefinition("node-d", WorkflowNodeKind.TerminalOutcome, "Node D", "Role D")
            });

    private static WorkflowGraph CreateCyclicGraph() =>
        new(
            "node-a",
            new[]
            {
                new WorkflowNodeDefinition(
                    "node-a",
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: "node-b"),
                new WorkflowNodeDefinition(
                    "node-b",
                    WorkflowNodeKind.Prompt,
                    "Node B",
                    "Role B",
                    successTargetNodeId: "node-a")
            });

    private static WorkflowGraph CreateDeadEndGraph() =>
        new(
            "node-a",
            new[]
            {
                new WorkflowNodeDefinition(
                    "node-a",
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: null)
            });

    private static WorkflowNodeDefinition CreateNode(
        string nodeId,
        string displayName,
        WorkflowNodeKind kind,
        string? successTargetNodeId,
        string roleBinding,
        int retryBudget = 0,
        string? condition = null,
        string? fallbackRoute = null,
        string? artifactContract = null,
        string? permissionIntent = null,
        WorkflowNodeGateMetadata? gate = null) =>
        new(
            nodeId,
            kind,
            displayName,
            roleBinding,
            requiredCapabilities: null,
            primaryRouteId: null,
            fallbackRouteIds: fallbackRoute is null ? null : new[] { fallbackRoute },
            timeout: null,
            retryBudget: retryBudget,
            successTargetNodeId: successTargetNodeId,
            failureTargetNodeId: null,
            conditionExpression: condition,
            artifactContract: artifactContract,
            permissionIntent: permissionIntent,
            gateMetadata: gate);

    /// <summary>
    /// The shipped built-in template with exactly one field of exactly one node changed - the shape a
    /// half-applied edit, a restored row or a hand-patched store leaves behind. Every corruption keeps the
    /// graph valid and its gates enforceable, so a refusal can only have come from the proof that the
    /// built-in is the graph the studio produces.
    /// </summary>
    private static WorkflowTemplateDefinition CreateCorruptedBuiltInTemplate(string corruption)
    {
        var canonical = WorkflowStudioService.CreateStandardTemplate();
        var nodes = canonical.Graph.Nodes.ToDictionary(
            node => node.NodeId,
            StringComparer.Ordinal);

        WorkflowNodeDefinition Corrupt(string nodeId, Func<WorkflowNodeDefinition, WorkflowNodeDefinition> change) =>
            nodes[nodeId] = change(nodes[nodeId]);

        var corrupted = corruption switch
        {
            "timeout" => Corrupt("stage-code-and-ui", node => new WorkflowNodeDefinition(
                node.NodeId, node.Kind, node.DisplayName, node.RoleBinding,
                node.RequiredCapabilities, node.PrimaryRouteId, node.FallbackRouteIds,
                TimeSpan.FromSeconds(1), node.RetryBudget, node.SuccessTargetNodeId,
                node.FailureTargetNodeId, node.ConditionExpression, node.ArtifactContract,
                node.PermissionIntent, node.GateMetadata)),
            // One reviewer removed from a two-reviewer gate: the chain would still run, one gate weaker.
            "reviewers" => Corrupt(
                "stage-document-review",
                node => WithGate(
                    node,
                    new WorkflowNodeGateMetadata(
                        WorkflowStageKind.DocumentReview,
                        new[] { "Reviewer" },
                        requiresUserApproval: false,
                        artifactRequirement: "DocumentBundle"))),
            // The approval stage stops asking for the approval its kind names.
            "approval" => Corrupt(
                "stage-user-approval",
                node => WithGate(
                    node,
                    new WorkflowNodeGateMetadata(
                        WorkflowStageKind.UserApproval,
                        Array.Empty<string>(),
                        requiresUserApproval: false,
                        artifactRequirement: "ApprovedDocument"))),
            // The approved document the approval is decided against disappears.
            "artifact" => Corrupt(
                "stage-user-approval",
                node => WithGate(
                    node,
                    new WorkflowNodeGateMetadata(
                        WorkflowStageKind.UserApproval,
                        Array.Empty<string>(),
                        requiresUserApproval: true,
                        artifactRequirement: null))),
            // The review that fails goes somewhere else, or nowhere.
            "failure-edge" => Corrupt(
                "stage-multi-level-review",
                node => Rebuild(
                    node.NodeId,
                    node.Kind,
                    node.DisplayName,
                    node.RoleBinding,
                    node.PrimaryRouteId,
                    node.RetryBudget,
                    node.SuccessTargetNodeId,
                    failureTargetNodeId: "stage-final-outcome",
                    gate: node.GateMetadata)),
            // An unused Writer budget is added to the exact shipped definition.
            "retry-budget" => Corrupt(
                "stage-code-and-ui",
                node => Rebuild(
                    node.NodeId,
                    node.Kind,
                    node.DisplayName,
                    node.RoleBinding,
                    node.PrimaryRouteId,
                    retryBudget: 1,
                    node.SuccessTargetNodeId,
                    failureTargetNodeId: node.FailureTargetNodeId,
                    gate: node.GateMetadata)),
            // A review node is relabelled as a prompt, which is the label the derived stage is built from.
            "node-kind" => Corrupt(
                "stage-document-review",
                node => WithGate(
                    Rebuild(
                        node.NodeId,
                        WorkflowNodeKind.Prompt,
                        node.DisplayName,
                        node.RoleBinding,
                        node.PrimaryRouteId,
                        node.RetryBudget,
                        node.SuccessTargetNodeId,
                        node.FailureTargetNodeId,
                        node.GateMetadata),
                    node.GateMetadata!)),
            "display-name" => Corrupt(
                "stage-architecture",
                node => WithGate(
                    Rebuild(
                        node.NodeId,
                        node.Kind,
                        "Архитектура (изменена)",
                        node.RoleBinding,
                        node.PrimaryRouteId,
                        node.RetryBudget,
                        node.SuccessTargetNodeId,
                        node.FailureTargetNodeId,
                        node.GateMetadata),
                    node.GateMetadata!)),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown corruption.")
        };

        return new WorkflowTemplateDefinition(
            canonical.TemplateId,
            canonical.Version,
            canonical.DisplayName,
            canonical.Description,
            new WorkflowGraph(
                canonical.Graph.EntryNodeId,
                canonical.Graph.Nodes.Select(node => nodes[node.NodeId]).ToArray()),
            canonical.RoleBindings,
            canonical.RequiredDocumentTemplates,
            canonical.IsBuiltIn,
            canonical.CreatedAtUtc);
    }

    private static WorkflowNodeDefinition Rebuild(
        string nodeId,
        WorkflowNodeKind kind,
        string displayName,
        string roleBinding,
        string? primaryRouteId,
        int retryBudget,
        string? successTargetNodeId,
        string? failureTargetNodeId,
        WorkflowNodeGateMetadata? gate) =>
        new(
            nodeId,
            kind,
            displayName,
            roleBinding,
            primaryRouteId: primaryRouteId,
            retryBudget: retryBudget,
            successTargetNodeId: successTargetNodeId,
            failureTargetNodeId: failureTargetNodeId,
            gateMetadata: gate);

    private static WorkflowNodeDefinition WithGate(
        WorkflowNodeDefinition node,
        WorkflowNodeGateMetadata gate) =>
        Rebuild(
            node.NodeId,
            node.Kind,
            node.DisplayName,
            node.RoleBinding,
            node.PrimaryRouteId,
            node.RetryBudget,
            node.SuccessTargetNodeId,
            node.FailureTargetNodeId,
            gate);
}
