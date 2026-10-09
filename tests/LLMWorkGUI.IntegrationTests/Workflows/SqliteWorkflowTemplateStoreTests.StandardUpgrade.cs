using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class SqliteWorkflowTemplateStoreTests
{
    [Fact]
    public async Task ExactRetiredStandardVersionRemainsImmutableWhileCurrentVersionCanBeExplicitlySelected()
    {
        var factory = await CreateMigratedFactoryAsync();
        var current = WorkflowStudioService.CreateStandardTemplate();
        var legacy = CreateHistoricalStandardVersion();
        var historical = new SqliteWorkflowTemplateStore(factory, new HistoricalGraphFixtureValidator(legacy.Graph));
        await historical.SaveAsync(legacy);
        await historical.SaveAssignmentAsync(new WorkflowTemplateAssignment("old-assignment", "old-project",
            legacy.TemplateId, 1, Now));
        string before;
        await using (var connection = await factory.OpenConnectionAsync())
            before = await ReadSingleAsync(connection,
                "SELECT GraphJson FROM WorkflowTemplateVersions WHERE TemplateId='workflow-standard-development' AND Version=1;");
        Assert.Equal(ActualRc9GraphJson, before);

        var upgraded = new SqliteWorkflowTemplateStore(factory, seedVersions: new[] { current });
        var listed = await upgraded.ListAsync();
        Assert.Equal(current.Version, Assert.Single(listed).Version);
        Assert.Equal(current.Version, (await upgraded.GetLatestAsync(current.TemplateId))!.Version);
        Assert.Contains("назначения и запуски сохранены", Assert.Single(listed).Description);
        await Assert.ThrowsAsync<InvalidDataException>(() => upgraded.GetAsync(legacy.TemplateId, 1));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new WorkflowTemplateExecutionPlanResolver(upgraded).ResolveForProjectAsync("old-project"));
        Assert.Equal(1, (await upgraded.GetAssignmentAsync("old-project"))!.TemplateVersion);
        await using (var connection = await factory.OpenConnectionAsync())
            Assert.Equal(before, await ReadSingleAsync(connection,
                "SELECT GraphJson FROM WorkflowTemplateVersions WHERE TemplateId='workflow-standard-development' AND Version=1;"));

        await upgraded.SaveAssignmentAsync(new WorkflowTemplateAssignment("explicit-upgrade", "old-project",
            current.TemplateId, current.Version, Now));
        var plan = await new WorkflowTemplateExecutionPlanResolver(upgraded).ResolveForProjectAsync("old-project");
        Assert.Equal(current.Version, plan.TemplateVersion);
        Assert.Equal(11, plan.Scheme.Scheme.Stages.Count);
        Assert.Equal(7, current.RequiredDocumentTemplates.Count);
    }

    [Fact]
    public async Task LegitimateCustomVersionTwoUnderOldIdentityRemainsListedAndResolvable()
    {
        var factory = await CreateMigratedFactoryAsync();
        var current = WorkflowStudioService.CreateStandardTemplate();
        var legacy = CreateHistoricalStandardVersion();
        var historical = new SqliteWorkflowTemplateStore(factory, new HistoricalGraphFixtureValidator(legacy.Graph));
        await historical.SaveAsync(legacy);
        var userVersion = new WorkflowTemplateDefinition(WorkflowStudioService.LegacyStandardTemplateId, 2,
            "User's edited linear template", "User-owned version", current.Graph,
            current.RoleBindings, current.RequiredDocumentTemplates, isBuiltIn: false, Now);
        var original = new SqliteWorkflowTemplateStore(factory);
        await original.SaveAsync(userVersion);
        await original.SaveAssignmentAsync(new WorkflowTemplateAssignment("user-assignment", "user-project",
            userVersion.TemplateId, 2, Now));
        var upgraded = new SqliteWorkflowTemplateStore(factory, seedVersions: new[] { current });

        var listed = await upgraded.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, template => template.TemplateId == userVersion.TemplateId
            && template.Version == 2 && !template.IsBuiltIn && template.DisplayName == userVersion.DisplayName);
        Assert.Contains(listed, template => template.TemplateId == current.TemplateId && template.IsBuiltIn);
        var plan = await new WorkflowTemplateExecutionPlanResolver(upgraded).ResolveForProjectAsync("user-project");
        Assert.Equal(userVersion.TemplateId, plan.TemplateId);
        Assert.Equal(2, plan.TemplateVersion);
        Assert.Equal(WorkflowGraphSnapshot.Serialize(userVersion.Graph), plan.GraphSnapshotJson);
    }

    [Fact]
    public async Task CurrentStandardVersionCollisionIsReportedWithoutOverwritingOrHidingExistingVersions()
    {
        var factory = await CreateMigratedFactoryAsync();
        var current = WorkflowStudioService.CreateStandardTemplate();
        var collision = new WorkflowTemplateDefinition(current.TemplateId, current.Version, "Existing user version",
            "Existing user content", current.Graph, current.RoleBindings, current.RequiredDocumentTemplates,
            isBuiltIn: false, Now);
        var original = new SqliteWorkflowTemplateStore(factory);
        await original.SaveAsync(collision);
        var upgraded = new SqliteWorkflowTemplateStore(factory, seedVersions: new[] { current });

        var error = await Assert.ThrowsAsync<WorkflowValidationException>(() => upgraded.ListAsync());
        Assert.Contains("built-in-version-conflict", error.Message);
        var retained = await original.GetAsync(current.TemplateId, current.Version);
        Assert.False(retained!.IsBuiltIn);
        Assert.Equal(collision.DisplayName, retained.DisplayName);
        Assert.Equal(WorkflowGraphSnapshot.Serialize(collision.Graph), WorkflowGraphSnapshot.Serialize(retained.Graph));
    }

    [Fact]
    public async Task AChangedRetiredStandardGraphIsNotSilentlyQuarantined()
    {
        var factory = await CreateMigratedFactoryAsync();
        var legacy = CreateHistoricalStandardVersion(alterDisplayName: true);
        var historical = new SqliteWorkflowTemplateStore(factory, new HistoricalGraphFixtureValidator(legacy.Graph));
        await historical.SaveAsync(legacy);
        var upgraded = new SqliteWorkflowTemplateStore(factory,
            seedVersions: new[] { WorkflowStudioService.CreateStandardTemplate() });
        await Assert.ThrowsAsync<InvalidDataException>(() => upgraded.ListAsync());
    }

    [Fact]
    public async Task HistoricalGraphWithoutAVerifiedCurrentSeedStillFailsClosedOnListing()
    {
        var factory = await CreateMigratedFactoryAsync();
        var legacy = CreateHistoricalStandardVersion();
        var historical = new SqliteWorkflowTemplateStore(factory, new HistoricalGraphFixtureValidator(legacy.Graph));
        await historical.SaveAsync(legacy);
        var unseeded = new SqliteWorkflowTemplateStore(factory);

        await Assert.ThrowsAsync<InvalidDataException>(() => unseeded.ListAsync());
        await using var connection = await factory.OpenConnectionAsync();
        Assert.Equal(WorkflowGraphSnapshot.Serialize(legacy.Graph), await ReadSingleAsync(connection,
            "SELECT GraphJson FROM WorkflowTemplateVersions WHERE TemplateId='workflow-standard-development' AND Version=1;"));
    }

    private static WorkflowTemplateDefinition CreateHistoricalStandardVersion(bool alterDisplayName = false)
    {
        var canonical = WorkflowStudioService.CreateStandardTemplate();
        Assert.Equal("5C6F1A61C870818A4AA6568B2551164227044297493FE3C0C65DFE23FE4DF377",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(ActualRc9GraphJson))));
        var graph = WorkflowGraphSnapshot.ReadStoredTemplateGraph(ActualRc9GraphJson,
            WorkflowStudioService.LegacyStandardTemplateId, 1);
        if (alterDisplayName)
        {
            graph = new WorkflowGraph(graph.EntryNodeId, graph.Nodes.Select(node =>
                new WorkflowNodeDefinition(node.NodeId, node.Kind,
                    node.NodeId == WorkflowScheme.CodeAndUiStageId ? "Altered legacy node" : node.DisplayName,
                    node.RoleBinding, node.RequiredCapabilities, node.PrimaryRouteId, node.FallbackRouteIds, node.Timeout,
                    node.RetryBudget, node.SuccessTargetNodeId, node.FailureTargetNodeId,
                    node.ConditionExpression, node.ArtifactContract, node.PermissionIntent, node.GateMetadata)).ToArray());
        }
        return new WorkflowTemplateDefinition(WorkflowStudioService.LegacyStandardTemplateId, 1, canonical.DisplayName,
            "Задача → архитектура → ТЗ → roadmap → review → утверждение → код/UI → тесты → приёмка.",
            graph, canonical.RoleBindings, canonical.RequiredDocumentTemplates, isBuiltIn: true, DateTimeOffset.UnixEpoch);
    }

    // Exact UTF-8 snapshot independently extracted from archived RC9 Application/Domain DLLs.
    // RC9 commit: 1e6c25d11d9f14847c6923670c043e021791a004; extraction proof retained in historical-rc9.
    // GraphJson UTF-8 SHA256: 5C6F1A61C870818A4AA6568B2551164227044297493FE3C0C65DFE23FE4DF377.
    private const string ActualRc9GraphJson = """
        {"entryNodeId":"stage-task-specification","nodes":[{"nodeId":"stage-task-specification","kind":"Prompt","displayName":"\u041F\u043E\u0441\u0442\u0430\u043D\u043E\u0432\u043A\u0430 \u0437\u0430\u0434\u0430\u0447\u0438","roleBinding":"Coordinator","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,"successTargetNodeId":"stage-architecture","failureTargetNodeId":null,"conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"TaskSpecification","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":"TaskSpecificationDocument"}},{"nodeId":"stage-architecture","kind":"Prompt","displayName":"\u0410\u0440\u0445\u0438\u0442\u0435\u043A\u0442\u0443\u0440\u0430","roleBinding":"Architect","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,"successTargetNodeId":"stage-technical-specification","failureTargetNodeId":null,"conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"Architecture","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":"ArchitectureDocument"}},{"nodeId":"stage-technical-specification","kind":"Prompt","displayName":"\u0422\u0417","roleBinding":"TechnicalWriter","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,"successTargetNodeId":"stage-roadmap","failureTargetNodeId":null,"conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"TechnicalSpecification","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":"TechnicalSpecificationDocument"}},{"nodeId":"stage-roadmap","kind":"Prompt","displayName":"\u0414\u043E\u0440\u043E\u0436\u043D\u0430\u044F \u043A\u0430\u0440\u0442\u0430","roleBinding":"Coordinator","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,"successTargetNodeId":"stage-document-review","failureTargetNodeId":null,"conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"Roadmap","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":"RoadmapDocument"}},{"nodeId":"stage-document-review","kind":"Review","displayName":"\u041F\u0440\u043E\u0432\u0435\u0440\u043A\u0430 \u0434\u043E\u043A\u0443\u043C\u0435\u043D\u0442\u043E\u0432","roleBinding":"Reviewer","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,"successTargetNodeId":"stage-user-approval","failureTargetNodeId":null,"conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"DocumentReview","requiredReviewerRoles":["Reviewer","Architect"],"requiresUserApproval":false,"artifactRequirement":"DocumentBundle"}},{"nodeId":"stage-user-approval","kind":"ApprovalGate","displayName":"\u0423\u0442\u0432\u0435\u0440\u0436\u0434\u0435\u043D\u0438\u0435","roleBinding":"Approver","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,"successTargetNodeId":"stage-implementation-packages","failureTargetNodeId":null,"conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"UserApproval","requiredReviewerRoles":[],"requiresUserApproval":true,"artifactRequirement":"ApprovedDocument"}},{"nodeId":"stage-implementation-packages","kind":"Writer","displayName":"\u041F\u0430\u043A\u0435\u0442\u044B \u0440\u0435\u0430\u043B\u0438\u0437\u0430\u0446\u0438\u0438","roleBinding":"Coordinator","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,"successTargetNodeId":"stage-code-and-ui","failureTargetNodeId":null,"conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"Implementation","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":"TaskPacketBundle"}},{"nodeId":"stage-code-and-ui","kind":"Writer","displayName":"\u041A\u043E\u0434/UI","roleBinding":"Implementer","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":1,"successTargetNodeId":"stage-multi-level-review","failureTargetNodeId":null,"conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"Implementation","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":"ImplementationDiff"}},{"nodeId":"stage-multi-level-review","kind":"Review","displayName":"\u041C\u043D\u043E\u0433\u043E\u0443\u0440\u043E\u0432\u043D\u0435\u0432\u043E\u0435 \u0440\u0435\u0432\u044C\u044E","roleBinding":"Reviewer","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,"successTargetNodeId":"stage-tests-and-ui-acceptance","failureTargetNodeId":"stage-code-and-ui","conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"DocumentReview","requiredReviewerRoles":["Reviewer","UiReviewer"],"requiresUserApproval":false,"artifactRequirement":"ReviewedImplementationDiff"}},{"nodeId":"stage-tests-and-ui-acceptance","kind":"ValidationCommand","displayName":"\u0422\u0435\u0441\u0442\u044B/\u0432\u0438\u0437\u0443\u0430\u043B\u044C\u043D\u0430\u044F \u043F\u0440\u0438\u0451\u043C\u043A\u0430","roleBinding":"Tester","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,"successTargetNodeId":"stage-final-outcome","failureTargetNodeId":"stage-code-and-ui","conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"UiAcceptance","requiredReviewerRoles":["Tester"],"requiresUserApproval":true,"artifactRequirement":"UiAcceptanceEvidence"}},{"nodeId":"stage-final-outcome","kind":"TerminalOutcome","displayName":"\u0418\u0442\u043E\u0433","roleBinding":"Coordinator","requiredCapabilities":[],"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,"successTargetNodeId":null,"failureTargetNodeId":null,"conditionExpression":null,"artifactContract":null,"permissionIntent":null,"gateMetadata":{"stageKind":"FinalVerification","requiredReviewerRoles":[],"requiresUserApproval":false,"artifactRequirement":null}}]}
        """;

    // Writes only the exact test-owned historical graph through the real immutable SQLite store.
    // This simulates the earlier validator's stored bytes, never execution permission in current code.
    private sealed class HistoricalGraphFixtureValidator(WorkflowGraph graph) : IWorkflowGraphValidator
    {
        private readonly string _expected = WorkflowGraphSnapshot.Serialize(graph);
        public void Validate(WorkflowGraph candidate) =>
            Assert.Equal(_expected, WorkflowGraphSnapshot.Serialize(candidate));
    }
}
