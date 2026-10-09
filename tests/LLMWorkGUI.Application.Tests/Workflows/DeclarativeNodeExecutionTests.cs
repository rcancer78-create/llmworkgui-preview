using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed partial class DeclarativeNodeExecutionTests
{
    private const string ProjectId = "project-1";
    private const string CheckoutPath = @"C:\work\checkout";

    private static readonly string DocumentHash = "sha256:" + new string('a', 64);
    private static readonly DateTimeOffset RecordedAt = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly RecordingCheckoutLockService _lockService = new();
    private readonly List<IWorkflowNodeChannel> _channels = new();

    [Fact]
    public async Task PromptNode_RoutesTheTurnToAChannelWithProvenCapabilities()
    {
        var channel = AddChannel("opencode", new[] { "route-opencode" }, new[] { "prompt" });
        var executor = CreateExecutor();
        var node = Node(
            "prompt",
            WorkflowNodeKind.Prompt,
            role: "Architect",
            success: "review",
            capabilities: new[] { "prompt" });
        var roleBinding = new RoleBindingDefinition(
            "Architect",
            "route-opencode",
            requiredCapabilities: new[] { "prompt" });

        var result = await executor.ExecuteAsync(CreateRequest(node, roleBinding, promptOrCommand: "Draft the ТЗ."));

        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.True(result.IsSuccess);
        Assert.Equal("route-opencode", result.RequestedRouteId);
        Assert.Equal("route-opencode", result.ObservedRouteId);
        Assert.True(result.IsReadOnly);
        Assert.False(result.WriterLockAcquired);
        Assert.Equal("review", result.NextNodeId);

        var turn = Assert.Single(channel.Requests);
        Assert.True(turn.IsReadOnly);
        Assert.Equal("Draft the ТЗ.", turn.PromptOrCommand);
        Assert.Equal("Architect", turn.RoleBinding.RoleId);
    }

    [Fact]
    public async Task PromptNode_FailsClosedWhenNoChannelProvesTheCapabilities()
    {
        AddChannel("opencode", new[] { "route-opencode" }, new[] { "prompt.basic" });
        var executor = CreateExecutor();
        var node = Node(
            "prompt",
            WorkflowNodeKind.Prompt,
            role: "Architect",
            capabilities: new[] { "prompt" });
        var roleBinding = new RoleBindingDefinition("Architect", "route-opencode");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => executor.ExecuteAsync(CreateRequest(node, roleBinding)));

        Assert.Contains("proven capabilities", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewNode_IsStrictlyReadOnlyAndNeverTakesTheWriterLock()
    {
        var channel = AddChannel("cursor-acp", new[] { "route-cursor" }, new[] { "review.readonly" });
        var executor = CreateExecutor();
        var node = Node(
            "review",
            WorkflowNodeKind.Review,
            role: "ReviewerLevel1",
            capabilities: new[] { "review.readonly" });
        var roleBinding = new RoleBindingDefinition(
            "ReviewerLevel1",
            "route-cursor",
            requiredCapabilities: new[] { "review.readonly" });

        var result = await executor.ExecuteAsync(CreateRequest(node, roleBinding));

        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.True(result.IsReadOnly);
        Assert.False(result.WriterLockAcquired);
        Assert.Empty(_lockService.Acquisitions);
        Assert.True(Assert.Single(channel.Requests).IsReadOnly);
    }

    [Fact]
    public async Task ReviewNode_RejectsAWritePermissionIntent()
    {
        var channel = AddChannel("cursor-acp", new[] { "route-cursor" }, new[] { "review.readonly" });
        var executor = CreateExecutor();
        var node = Node(
            "review",
            WorkflowNodeKind.Review,
            role: "ReviewerLevel1",
            capabilities: new[] { "review.readonly" },
            permissionIntent: "write");
        var roleBinding = new RoleBindingDefinition("ReviewerLevel1", "route-cursor");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => executor.ExecuteAsync(CreateRequest(node, roleBinding)));

        Assert.Contains("read-only", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public async Task WriterNode_TakesTheCheckoutWriterLockAndReleasesIt()
    {
        var channel = AddChannel("opencode", new[] { "route-opencode" }, new[] { "writer.lock" });
        var executor = CreateExecutor();
        var node = Node(
            "writer",
            WorkflowNodeKind.Writer,
            role: "Coder",
            capabilities: new[] { "writer.lock" },
            permissionIntent: "write");
        var roleBinding = new RoleBindingDefinition("Coder", "route-opencode");

        var result = await executor.ExecuteAsync(
            CreateRequest(node, roleBinding, executionId: "exec-writer"));

        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.True(result.WriterLockAcquired);
        Assert.False(result.IsReadOnly);

        var acquisition = Assert.Single(_lockService.Acquisitions);
        Assert.Equal(ProjectId, acquisition.ProjectId);
        Assert.Equal(CheckoutPath, acquisition.CanonicalRootPath);
        Assert.Equal("exec-writer", acquisition.ExecutionId);
        Assert.Equal(WorkflowNodeExecutor.WriterLockReleaseReason, acquisition.ReleaseReason);
        Assert.False(Assert.Single(channel.Requests).IsReadOnly);
    }

    [Fact]
    public async Task WriterNode_PropagatesALockConflictBeforeDispatch()
    {
        var channel = AddChannel("opencode", new[] { "route-opencode" }, new[] { "writer.lock" });
        _lockService.AcquireFailure = new ProjectLockConflictException("already locked");
        var executor = CreateExecutor();
        var node = Node("writer", WorkflowNodeKind.Writer, capabilities: new[] { "writer.lock" });
        var roleBinding = new RoleBindingDefinition("Coder", "route-opencode");

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => executor.ExecuteAsync(CreateRequest(node, roleBinding)));

        Assert.Empty(channel.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriterNode_UnconfirmedDispatchFailureOrCancellationRetainsOwnership(bool cancelled)
    {
        var channel = AddChannel("opencode", new[] { "route-opencode" }, new[] { "writer.lock" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<WorkflowChannelTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Handler = async (_, token) =>
        {
            entered.TrySetResult();
            return await finish.Task.WaitAsync(token);
        };
        using var cancellation = new CancellationTokenSource();
        var executor = CreateExecutor();
        var node = Node("writer", WorkflowNodeKind.Writer, capabilities: new[] { "writer.lock" });
        var binding = new RoleBindingDefinition("Coder", "route-opencode");
        var execution = executor.ExecuteAsync(CreateRequest(node, binding, executionId: "failed-writer"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var acquired = Assert.Single(_lockService.Acquisitions);
        Assert.True(acquired.IsHeld);

        if (cancelled)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        else
        {
            finish.TrySetException(new InvalidOperationException("synthetic dispatch failure"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
        }

        Assert.True(acquired.IsHeld);
        Assert.Equal(0, acquired.ReleaseCount);
        Assert.Single(channel.Requests);
    }

    [Theory]
    [InlineData(ExecutionState.Ambiguous, true)]
    [InlineData(ExecutionState.TimedOut, true)]
    [InlineData(ExecutionState.Running, true)]
    [InlineData(ExecutionState.Failed, false)]
    [InlineData(ExecutionState.Cancelled, false)]
    public async Task WriterNode_ReleasesOnlyConfirmedTerminalOwnership(ExecutionState state, bool held)
    {
        var channel = AddChannel("opencode", new[] { "route-opencode" }, new[] { "writer.lock" });
        channel.State = state;
        await CreateExecutor().ExecuteAsync(CreateRequest(Node("writer", WorkflowNodeKind.Writer,
            capabilities: new[] { "writer.lock" }), new RoleBindingDefinition("Coder", "route-opencode")));
        Assert.Equal(held, Assert.Single(_lockService.Acquisitions).IsHeld);
    }

    [Fact]
    public async Task WriterNode_PreflightRefusalReleasesItsOwnAdmission()
    {
        await Assert.ThrowsAsync<WorkflowValidationException>(() => CreateExecutor().ExecuteAsync(
            CreateRequest(Node("writer", WorkflowNodeKind.Writer, capabilities: new[] { "unavailable" }),
                new RoleBindingDefinition("Coder", "missing-route"))));
        Assert.False(Assert.Single(_lockService.Acquisitions).IsHeld);
    }

    [Fact]
    public async Task ArtifactCollection_WithoutDurableCollectorCannotReportSuccess()
    {
        await Assert.ThrowsAsync<WorkflowValidationException>(() => CreateExecutor().ExecuteAsync(
            CreateRequest(Node("artifacts", WorkflowNodeKind.ArtifactCollection),
                new RoleBindingDefinition("Collector", "route-opencode"))));
        Assert.Empty(_lockService.Acquisitions);
    }

    [Theory]
    [InlineData(WorkflowNodeKind.Retry)]
    [InlineData(WorkflowNodeKind.Prompt)]
    [InlineData(WorkflowNodeKind.Writer)]
    public async Task AmbiguousExecutionState_ForbidsAnyAutomaticRetry(WorkflowNodeKind kind)
    {
        AddChannel("opencode", new[] { "route-opencode" }, new[] { "prompt", "writer.lock" });
        var executor = CreateExecutor();
        var node = Node(
            kind.ToString(),
            kind,
            success: "terminal",
            failure: "terminal",
            retryBudget: 2,
            capabilities: new[] { "prompt", "writer.lock" });
        var roleBinding = new RoleBindingDefinition("Coder", "route-opencode");

        var request = CreateRequest(
            node,
            roleBinding,
            previousExecutionId: "exec-ambiguous",
            previousExecutionState: ExecutionState.Ambiguous);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(request));

        Assert.Equal(WorkflowNodeExecutor.AmbiguousRetryForbiddenMessage, exception.Message);
    }

    [Fact]
    public async Task RetryNode_CreatesANewExecutionLinkedToThePreviousOneAndSpendsTheBudget()
    {
        var executor = CreateExecutor();
        var node = Node(
            "retry",
            WorkflowNodeKind.Retry,
            success: "writer",
            failure: "escalation",
            retryBudget: 3);
        var roleBinding = new RoleBindingDefinition("Coder", "route-opencode");

        var result = await executor.ExecuteAsync(CreateRequest(
            node,
            roleBinding,
            executionId: "exec-retry-2",
            previousExecutionId: "exec-retry-1",
            previousExecutionState: ExecutionState.Failed,
            retryBudgetRemaining: 3));

        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.Equal("exec-retry-1", result.RetryOfExecutionId);
        Assert.Equal(2, result.RemainingRetryBudget);
        Assert.Equal("writer", result.NextNodeId);
        Assert.False(result.EscalationRequired);
        Assert.Empty(_channels);
    }

    [Fact]
    public async Task RetryNode_ReportsEscalationRequiredWhenTheBudgetIsExhausted()
    {
        var executor = CreateExecutor();
        var node = Node(
            "retry",
            WorkflowNodeKind.Retry,
            success: "writer",
            failure: "escalation",
            retryBudget: 2);
        var roleBinding = new RoleBindingDefinition("Coder", "route-opencode");

        var result = await executor.ExecuteAsync(CreateRequest(
            node,
            roleBinding,
            previousExecutionId: "exec-retry-1",
            previousExecutionState: ExecutionState.Failed,
            retryBudgetRemaining: 0));

        Assert.Equal(ExecutionState.Failed, result.State);
        Assert.True(result.EscalationRequired);
        Assert.Equal(0, result.RemainingRetryBudget);
        Assert.Equal("escalation", result.NextNodeId);
    }

    [Fact]
    public async Task RetryNode_RequiresThePreviousExecutionLink()
    {
        var executor = CreateExecutor();
        var node = Node("retry", WorkflowNodeKind.Retry, success: "writer", failure: "escalation", retryBudget: 1);
        var roleBinding = new RoleBindingDefinition("Coder", "route-opencode");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => executor.ExecuteAsync(CreateRequest(node, roleBinding)));

        Assert.Contains("previous execution", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EscalationNode_SwitchesToTheFallbackRouteAndRequiresANewSession()
    {
        var executor = CreateExecutor();
        var node = Node("escalation", WorkflowNodeKind.Escalation, success: "retry");
        var roleBinding = new RoleBindingDefinition(
            "Coder",
            "route-opencode",
            new[] { "route-cursor" });

        var result = await executor.ExecuteAsync(CreateRequest(
            node,
            roleBinding,
            previousExecutionId: "exec-retry-1",
            nativeSessionId: "native-original",
            boundRouteId: "route-opencode",
            routeChangeReason: "Retry budget exhausted; the fallback route is used explicitly."));

        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.Equal("route-cursor", result.RequestedRouteId);
        Assert.True(result.RequiresNewNativeSession);
        Assert.Null(result.NativeSessionId);
        Assert.Equal("native-original", result.PreviousNativeSessionId);
        Assert.Contains("fallback route", result.RouteChangeReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("retry", result.NextNodeId);
    }

    [Fact]
    public async Task EscalationNode_RefusesASilentFallbackWithoutAReason()
    {
        var executor = CreateExecutor();
        var node = Node("escalation", WorkflowNodeKind.Escalation, success: "retry");
        var roleBinding = new RoleBindingDefinition(
            "Coder",
            "route-opencode",
            new[] { "route-cursor" });

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => executor.ExecuteAsync(CreateRequest(
                node,
                roleBinding,
                nativeSessionId: "native-original",
                boundRouteId: "route-opencode")));

        Assert.Contains("explicit reason", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RouteChange_CreatesANewNativeSessionWithoutCarryingTheOldOne()
    {
        var channel = AddChannel("cursor-acp", new[] { "route-cursor" }, new[] { "prompt" });
        channel.NativeSessionId = "native-new";
        var executor = CreateExecutor();
        var node = Node("prompt", WorkflowNodeKind.Prompt, capabilities: new[] { "prompt" });
        var roleBinding = new RoleBindingDefinition(
            "Coder",
            "route-opencode",
            new[] { "route-cursor" });

        var result = await executor.ExecuteAsync(CreateRequest(
            node,
            roleBinding,
            requestedRouteId: "route-cursor",
            nativeSessionId: "native-old",
            boundRouteId: "route-opencode",
            routeChangeReason: "The operator moved the role to the Cursor route."));

        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.Equal("native-new", result.NativeSessionId);
        Assert.Equal("native-old", result.PreviousNativeSessionId);
        Assert.True(result.RequiresNewNativeSession);
        Assert.Equal("The operator moved the role to the Cursor route.", result.RouteChangeReason);

        var turn = Assert.Single(channel.Requests);
        Assert.Null(turn.NativeSessionId);
        Assert.Equal("The operator moved the role to the Cursor route.", turn.RouteChangeReason);
    }

    [Fact]
    public async Task RouteChange_RefusesAnImplicitModelOrAccountSwitch()
    {
        AddChannel("cursor-acp", new[] { "route-cursor" }, new[] { "prompt" });
        var executor = CreateExecutor();
        var node = Node("prompt", WorkflowNodeKind.Prompt, capabilities: new[] { "prompt" });
        var roleBinding = new RoleBindingDefinition(
            "Coder",
            "route-opencode",
            new[] { "route-cursor" });

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => executor.ExecuteAsync(CreateRequest(
                node,
                roleBinding,
                requestedRouteId: "route-cursor",
                nativeSessionId: "native-old",
                boundRouteId: "route-opencode")));

        Assert.Contains("silent fallback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RouteMismatch_WhenTheObservedRouteDiffersFromTheRequestedRoute()
    {
        var channel = AddChannel("opencode", new[] { "route-opencode" }, new[] { "prompt" });
        channel.ObservedRouteId = "route-other";
        var executor = CreateExecutor();
        var node = Node("prompt", WorkflowNodeKind.Prompt, success: "review", failure: "retry", capabilities: new[] { "prompt" });
        var roleBinding = new RoleBindingDefinition("Coder", "route-opencode");

        var result = await executor.ExecuteAsync(CreateRequest(node, roleBinding));

        Assert.Equal(ExecutionState.RouteMismatch, result.State);
        Assert.False(result.IsSuccess);
        Assert.Equal("route-opencode", result.RequestedRouteId);
        Assert.Equal("route-other", result.ObservedRouteId);
        Assert.Equal("retry", result.NextNodeId);
    }

    [Fact]
    public async Task RouteMismatch_WhenTheRequestedRouteIsNotAllowedByTheRoleBinding()
    {
        var channel = AddChannel("opencode", new[] { "route-opencode" }, new[] { "prompt" });
        var executor = CreateExecutor();
        var node = Node("prompt", WorkflowNodeKind.Prompt, success: "review", failure: "retry", capabilities: new[] { "prompt" });
        var roleBinding = new RoleBindingDefinition("Coder", "route-opencode");

        var result = await executor.ExecuteAsync(CreateRequest(
            node,
            roleBinding,
            requestedRouteId: "route-forbidden"));

        Assert.Equal(ExecutionState.RouteMismatch, result.State);
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public async Task ApprovalGateNode_WaitsForAnExplicitUserDecision()
    {
        var executor = CreateExecutor();
        var node = Node("approval", WorkflowNodeKind.ApprovalGate, role: "Approver", success: "terminal");
        var roleBinding = new RoleBindingDefinition("Approver");

        var result = await executor.ExecuteAsync(CreateRequest(node, roleBinding));

        Assert.Equal(ExecutionState.WaitingApproval, result.State);
        Assert.True(result.RequiresUserDecision);
        Assert.Equal("terminal", result.NextNodeId);
        Assert.Empty(_channels);
    }

    [Theory]
    [InlineData("true", "terminal")]
    [InlineData("false", "escalation")]
    public async Task ConditionNode_SelectsTheTargetFromReportedFacts(string factValue, string expectedTarget)
    {
        var executor = CreateExecutor();
        var node = Node(
            "condition",
            WorkflowNodeKind.Condition,
            success: "terminal",
            failure: "escalation",
            conditionExpression: "testsPassed");
        var roleBinding = new RoleBindingDefinition("Tester", "route-opencode");

        var request = new WorkflowNodeExecutionRequest(
            node,
            roleBinding,
            ProjectId,
            CheckoutPath,
            facts: new Dictionary<string, string> { ["testsPassed"] = factValue });

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.Equal(expectedTarget, result.NextNodeId);
    }

    [Fact]
    public async Task ConditionNode_FailsClosedOnAnUnreportedFact()
    {
        var executor = CreateExecutor();
        var node = Node(
            "condition",
            WorkflowNodeKind.Condition,
            success: "terminal",
            failure: "escalation",
            conditionExpression: "testsPassed");
        var roleBinding = new RoleBindingDefinition("Tester", "route-opencode");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => executor.ExecuteAsync(CreateRequest(node, roleBinding)));

        Assert.Contains("testsPassed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalOutcomeNode_CompletesWithoutDispatchingATurn()
    {
        var executor = CreateExecutor();
        var node = Node("terminal", WorkflowNodeKind.TerminalOutcome, role: "Coordinator");
        var roleBinding = new RoleBindingDefinition("Coordinator");

        var result = await executor.ExecuteAsync(CreateRequest(node, roleBinding));

        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.Null(result.NextNodeId);
        Assert.Empty(_channels);
    }

    [Fact]
    public void GraphValidator_DelegatesToTheGraphValidation()
    {
        var validator = new WorkflowGraphValidator();
        var invalid = new WorkflowGraph(
            "entry",
            new[] { Node("entry", WorkflowNodeKind.Prompt, success: "missing") });

        Assert.Throws<InvalidOperationException>(() => validator.Validate(invalid));
        Assert.Throws<ArgumentNullException>(() => validator.Validate(null!));
    }

    [Fact]
    public void CreatePlan_StartsAtTheEntryNodeAndValidatesTheGraph()
    {
        var service = new WorkflowExecutionPlanService();
        var plan = service.CreatePlan(CreatePlanGraph());

        Assert.Equal("code", plan.CurrentNodeId);
        Assert.False(plan.IsComplete);
        Assert.Equal(WorkflowNodeKind.Prompt, plan.CurrentNode.Kind);
        Assert.Single(plan.Steps);

        var invalid = new WorkflowGraph(
            "entry",
            new[] { Node("entry", WorkflowNodeKind.Prompt, success: "missing") });

        Assert.Throws<InvalidOperationException>(() => service.CreatePlan(invalid));
    }

    [Fact]
    public void AdvanceSuccess_FollowsTheDeclaredTransitionsUntilTheTerminalOutcome()
    {
        var service = new WorkflowExecutionPlanService();
        var plan = service.CreatePlan(CreatePlanGraph());

        service.AdvanceSuccess(plan);

        Assert.Equal("terminal", plan.CurrentNodeId);
        Assert.True(plan.IsComplete);

        Assert.Throws<WorkflowValidationException>(() => service.AdvanceSuccess(plan));
    }

    [Fact]
    public void AdvanceFailure_FollowsTheDeclaredFailureTarget()
    {
        var service = new WorkflowExecutionPlanService();
        var plan = service.CreatePlan(CreatePlanGraph());

        service.AdvanceFailure(plan, "The writer failed.");

        Assert.Equal("escalation", plan.CurrentNodeId);
        Assert.False(plan.IsComplete);
        Assert.Contains(
            plan.Steps,
            step => step.NodeId == "escalation"
                && step.Reason.Contains("writer failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdvanceFailure_ThrowsWhenNoFailureTargetIsDeclared()
    {
        var service = new WorkflowExecutionPlanService();
        var plan = service.CreatePlan(CreatePlanGraph());
        service.AdvanceSuccess(plan);

        Assert.Throws<WorkflowValidationException>(() => service.AdvanceFailure(plan, "failed"));
    }

    [Fact]
    public void AdvanceSuccess_IsBlockedByCodingTransitionRulesUntilTheEvidencePasses()
    {
        var service = new WorkflowExecutionPlanService();
        var plan = service.CreatePlan(CreatePlanGraph());
        var rules = new[] { CreateCodingRule() };

        var blocked = Assert.Throws<WorkflowValidationException>(
            () => service.AdvanceSuccess(plan, rules, CreateIncompleteEvidence()));

        Assert.Contains("TechnicalSpecification", blocked.Message, StringComparison.Ordinal);
        Assert.Equal("code", plan.CurrentNodeId);

        service.AdvanceSuccess(plan, rules, CreatePassingEvidence());

        Assert.Equal("terminal", plan.CurrentNodeId);
    }

    [Fact]
    public void AdvanceSuccess_RequiresEvidenceWhenACodingRuleIsDeclared()
    {
        var service = new WorkflowExecutionPlanService();
        var plan = service.CreatePlan(CreatePlanGraph());
        var rules = new[] { CreateCodingRule() };

        var exception = Assert.Throws<WorkflowValidationException>(
            () => service.AdvanceSuccess(plan, rules));

        Assert.Contains("evidence", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("code", plan.CurrentNodeId);
    }

    [Fact]
    public void AdvanceSuccess_MovesToTheEscalationTargetWhenTheFixLimitIsExceeded()
    {
        var service = new WorkflowExecutionPlanService();
        var plan = service.CreatePlan(CreatePlanGraph());
        var rules = new[] { CreateCodingRule(fixLimit: 0) };

        service.AdvanceSuccess(plan, rules, CreatePassingEvidence(fixIterations: 1));

        Assert.Equal("escalation", plan.CurrentNodeId);
        Assert.Contains(
            plan.Steps,
            step => step.NodeId == "escalation"
                && step.Reason.Contains("escalation", StringComparison.OrdinalIgnoreCase));
    }

    private WorkflowNodeExecutor CreateExecutor() =>
        new(new WorkflowChannelCatalog(_channels), _lockService);

    private FakeNodeChannel AddChannel(
        string channelId,
        string[] routes,
        string[] capabilities)
    {
        var channel = new FakeNodeChannel(channelId, routes, capabilities);
        _channels.Add(channel);

        return channel;
    }

    private static WorkflowNodeExecutionRequest CreateRequest(
        WorkflowNodeDefinition node,
        RoleBindingDefinition roleBinding,
        string? requestedRouteId = null,
        string? promptOrCommand = null,
        string? executionId = null,
        string? previousExecutionId = null,
        ExecutionState? previousExecutionState = null,
        string? nativeSessionId = null,
        string? boundRouteId = null,
        string? routeChangeReason = null,
        int? retryBudgetRemaining = null) =>
        new(
            node,
            roleBinding,
            ProjectId,
            CheckoutPath,
            requestedRouteId,
            promptOrCommand,
            executionId,
            previousExecutionId,
            previousExecutionState,
            nativeSessionId,
            boundRouteId,
            routeChangeReason,
            retryBudgetRemaining,
            processGeneration: 42);

    private static WorkflowNodeDefinition Node(
        string nodeId,
        WorkflowNodeKind kind,
        string role = "Coder",
        string? success = null,
        string? failure = null,
        int retryBudget = 0,
        IReadOnlyList<string>? capabilities = null,
        string? conditionExpression = null,
        string? permissionIntent = null) =>
        new(
            nodeId,
            kind,
            $"Display {nodeId}",
            role,
            capabilities,
            successTargetNodeId: success,
            failureTargetNodeId: failure,
            retryBudget: retryBudget,
            conditionExpression: conditionExpression,
            permissionIntent: permissionIntent);

    private static WorkflowGraph CreatePlanGraph() =>
        new(
            "code",
            new[]
            {
                Node("code", WorkflowNodeKind.Prompt, success: "terminal", failure: "escalation"),
                Node("escalation", WorkflowNodeKind.Escalation, success: "terminal", failure: "terminal"),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

    private static CodingStageTransitionRule CreateCodingRule(int fixLimit = 1) =>
        new(
            "code",
            new[] { "TechnicalSpecification" },
            new[] { "ReviewerLevel1" },
            requiresDiffScopeCheck: true,
            requiresTestEvidence: false,
            requiresUiEvidence: false,
            fixLimit: fixLimit,
            escalationTargetNodeId: "escalation");

    private static CodingStageTransitionEvidence CreatePassingEvidence(int fixIterations = 0) =>
        new(
            new[] { "TechnicalSpecification" },
            DocumentHash,
            new[] { CreateVerdict(WorkflowReviewVerdict.Approve) },
            diffWithinScope: true,
            fixIterations: fixIterations);

    private static CodingStageTransitionEvidence CreateIncompleteEvidence() =>
        new(
            Array.Empty<string>(),
            DocumentHash,
            Array.Empty<ReviewerVerdictRecord>(),
            diffWithinScope: false);

    private static ReviewerVerdictRecord CreateVerdict(WorkflowReviewVerdict verdict) =>
        new(
            "ReviewerLevel1",
            "route-opencode",
            DocumentHash,
            verdict,
            $"ReviewerLevel1 returned {verdict}.",
            RecordedAt);

    private sealed class FakeNodeChannel : IWorkflowNodeChannel
    {
        private readonly HashSet<string> _routes;

        public FakeNodeChannel(string channelId, string[] routes, string[] capabilities)
        {
            ChannelId = channelId;
            Capabilities = capabilities;
            _routes = new HashSet<string>(routes, StringComparer.Ordinal);
        }

        public string ChannelId { get; }

        public IReadOnlyList<string> Capabilities { get; }

        public List<WorkflowChannelTurnRequest> Requests { get; } = new();

        public ExecutionState State { get; set; } = ExecutionState.Succeeded;

        public string? ObservedRouteId { get; set; }

        public string? NativeSessionId { get; set; }

        public string? EvidenceSummary { get; set; }
        public Func<WorkflowChannelTurnRequest, CancellationToken, Task<WorkflowChannelTurnResult>>? Handler { get; set; }

        public bool SupportsRoute(string routeId) => _routes.Contains(routeId);

        public Task<WorkflowChannelTurnResult> ExecuteTurnAsync(
            WorkflowChannelTurnRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (Handler is not null) return Handler(request, cancellationToken);

            return Task.FromResult(new WorkflowChannelTurnResult(
                State,
                ObservedRouteId ?? request.RequestedRouteId,
                NativeSessionId ?? request.NativeSessionId,
                EvidenceSummary ?? $"Channel '{ChannelId}' completed a turn."));
        }
    }

    private sealed class RecordingCheckoutLockService : ICheckoutLockService
    {
        public List<RecordingLockAcquisition> Acquisitions { get; } = new();

        public Exception? AcquireFailure { get; set; }

        public bool RequiresWriterLock(string? executionMode) => true;

        public bool RequiresWriterLock(WorkflowRole role, string? executionMode) => true;

        public Task<ICheckoutLockToken> AcquireWriterLockAsync(
            string projectId,
            string canonicalRootPath,
            string executionId,
            long processGeneration,
            CancellationToken cancellationToken = default)
        {
            if (AcquireFailure is not null)
            {
                throw AcquireFailure;
            }

            var acquisition = new RecordingLockAcquisition(projectId, canonicalRootPath, executionId);
            Acquisitions.Add(acquisition);

            return Task.FromResult<ICheckoutLockToken>(acquisition);
        }

        public Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(
            string projectId,
            string canonicalRootPath,
            string executionId,
            long processGeneration,
            string? executionMode,
            WorkflowRole role = WorkflowRole.Unknown,
            CancellationToken cancellationToken = default) =>
            AcquireWriterLockAsync(
                projectId,
                canonicalRootPath,
                executionId,
                processGeneration,
                cancellationToken)!;
    }

    private sealed class RecordingLockAcquisition : ICheckoutLockToken
    {
        public RecordingLockAcquisition(
            string projectId,
            string canonicalRootPath,
            string executionId)
        {
            ProjectId = projectId;
            CanonicalRootPath = canonicalRootPath;
            ExecutionId = executionId;
        }

        public string LockId { get; } = Guid.NewGuid().ToString("D");

        public string ProjectId { get; }

        public string CanonicalRootPath { get; }

        public string ExecutionId { get; }

        public string ApplicationInstanceId => "application-under-test";

        public bool IsHeld { get; private set; } = true;

        public string? ReleaseReason { get; private set; }
        public int ReleaseCount { get; private set; }
        public CancellationToken ReleaseToken { get; private set; }

        public Task ReleaseAsync(string reason, CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            ReleaseToken = cancellationToken;
            ReleaseReason = reason;
            IsHeld = false;

            return Task.CompletedTask;
        }

        public void Dispose() => IsHeld = false;

        public ValueTask DisposeAsync()
        {
            IsHeld = false;

            return ValueTask.CompletedTask;
        }
    }
}
