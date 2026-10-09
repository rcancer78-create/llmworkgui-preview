using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests.Workflows;

public sealed class WorkflowGraphTests
{
    private const string ReviewerLevelOne = "ReviewerLevel1";
    private const string ReviewerLevelTwo = "ReviewerLevel2";

    private static readonly string DocumentHash = "sha256:" + new string('a', 64);
    private static readonly string UiEvidenceHash = "sha256:" + new string('b', 64);
    private static readonly DateTimeOffset RecordedAt = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WorkflowNodeKind_ExposesEveryPrimitiveNode()
    {
        Assert.Equal(
            new[]
            {
                "Prompt",
                "Review",
                "Writer",
                "ApprovalGate",
                "Condition",
                "Retry",
                "Escalation",
                "UserDecision",
                "ValidationCommand",
                "ArtifactCollection",
                "TerminalOutcome"
            },
            Enum.GetNames<WorkflowNodeKind>());
    }

    [Fact]
    public void NodeDefinition_CapturesTheDeclaredRoutingData()
    {
        var timeout = TimeSpan.FromMinutes(5);
        var node = new WorkflowNodeDefinition(
            "writer",
            WorkflowNodeKind.Writer,
            "Implementation writer",
            "Coder",
            new[] { "writer.lock", "patch" },
            "route-opencode",
            new[] { "route-cursor", "route-mirasim" },
            timeout,
            retryBudget: 2,
            successTargetNodeId: "review",
            failureTargetNodeId: "retry",
            artifactContract: "ImplementationDiff",
            permissionIntent: "write");

        Assert.Equal("writer", node.NodeId);
        Assert.Equal(WorkflowNodeKind.Writer, node.Kind);
        Assert.Equal("Implementation writer", node.DisplayName);
        Assert.Equal("Coder", node.RoleBinding);
        Assert.Equal(new[] { "writer.lock", "patch" }, node.RequiredCapabilities);
        Assert.Equal("route-opencode", node.PrimaryRouteId);
        Assert.Equal(new[] { "route-cursor", "route-mirasim" }, node.FallbackRouteIds);
        Assert.Equal(timeout, node.Timeout);
        Assert.Equal(2, node.RetryBudget);
        Assert.Equal("review", node.SuccessTargetNodeId);
        Assert.Equal("retry", node.FailureTargetNodeId);
        Assert.Equal("ImplementationDiff", node.ArtifactContract);
        Assert.Equal("write", node.PermissionIntent);
        Assert.True(node.HasOutgoingTransitions);
    }

    [Fact]
    public void NodeDefinition_DefaultsToEmptyCollectionsAndNoTransitions()
    {
        var node = new WorkflowNodeDefinition(
            "terminal",
            WorkflowNodeKind.TerminalOutcome,
            "Final outcome",
            "Coordinator");

        Assert.Empty(node.RequiredCapabilities);
        Assert.Empty(node.FallbackRouteIds);
        Assert.Null(node.PrimaryRouteId);
        Assert.Null(node.Timeout);
        Assert.Equal(0, node.RetryBudget);
        Assert.False(node.HasOutgoingTransitions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NodeDefinition_RejectsBlankIdentifiers(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowNodeDefinition(
            invalidValue,
            WorkflowNodeKind.Prompt,
            "Display",
            "Architect"));

        Assert.Throws<ArgumentException>(() => new WorkflowNodeDefinition(
            "prompt",
            WorkflowNodeKind.Prompt,
            invalidValue,
            "Architect"));

        Assert.Throws<ArgumentException>(() => new WorkflowNodeDefinition(
            "prompt",
            WorkflowNodeKind.Prompt,
            "Display",
            invalidValue));
    }

    [Fact]
    public void NodeDefinition_RejectsNegativeRetryBudgetAndNonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowNodeDefinition(
            "retry",
            WorkflowNodeKind.Retry,
            "Retry",
            "Coder",
            retryBudget: -1));

        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowNodeDefinition(
            "prompt",
            WorkflowNodeKind.Prompt,
            "Prompt",
            "Architect",
            timeout: TimeSpan.Zero));
    }

    [Fact]
    public void Graph_RejectsNullNodesAndDuplicateNodeIds()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkflowGraph("entry", null!));

        var duplicate = new[] { Node("a", WorkflowNodeKind.Prompt), Node("a", WorkflowNodeKind.Review) };

        Assert.Throws<InvalidOperationException>(() => new WorkflowGraph("a", duplicate));
    }

    [Fact]
    public void Validate_AcceptsACompleteGraphWithEveryPrimitiveNode()
    {
        var graph = CreateCompleteGraph();

        graph.Validate();
        graph.Validate();
    }

    [Fact]
    public void Validate_RejectsAnUnknownEntryNode()
    {
        var graph = new WorkflowGraph(
            "missing",
            new[] { Node("terminal", WorkflowNodeKind.TerminalOutcome) });

        var exception = Assert.Throws<InvalidOperationException>(graph.Validate);

        Assert.Contains("entry", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsUnknownTransitionTargets()
    {
        var unknownSuccess = new WorkflowGraph(
            "a",
            new[]
            {
                Node("a", WorkflowNodeKind.Prompt, success: "missing"),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

        var successException = Assert.Throws<InvalidOperationException>(unknownSuccess.Validate);
        Assert.Contains("success target", successException.Message, StringComparison.Ordinal);

        var unknownFailure = new WorkflowGraph(
            "a",
            new[]
            {
                Node("a", WorkflowNodeKind.Prompt, failure: "missing"),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

        var failureException = Assert.Throws<InvalidOperationException>(unknownFailure.Validate);
        Assert.Contains("failure target", failureException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsANodeThatIsNotReachableFromTheEntry()
    {
        var graph = new WorkflowGraph(
            "entry",
            new[]
            {
                Node("entry", WorkflowNodeKind.Prompt, success: "terminal"),
                Node("orphan", WorkflowNodeKind.Prompt, success: "terminal"),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

        var exception = Assert.Throws<InvalidOperationException>(graph.Validate);

        Assert.Contains("not reachable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orphan", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsADeadEndNodeThatIsNotATerminalOutcome()
    {
        var graph = new WorkflowGraph(
            "entry",
            new[]
            {
                Node("entry", WorkflowNodeKind.Prompt, success: "leaf", failure: "terminal"),
                Node("leaf", WorkflowNodeKind.Prompt),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

        var exception = Assert.Throws<InvalidOperationException>(graph.Validate);

        Assert.Contains("dead end", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsAGraphWithoutAReachableTerminalOutcome()
    {
        var graph = new WorkflowGraph(
            "a",
            new[]
            {
                Node("a", WorkflowNodeKind.Prompt, success: "b"),
                Node("b", WorkflowNodeKind.Prompt, success: "a")
            });

        var exception = Assert.Throws<InvalidOperationException>(graph.Validate);

        Assert.Contains("terminal outcome", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsAnUnjustifiedCycle()
    {
        var graph = new WorkflowGraph(
            "a",
            new[]
            {
                Node("a", WorkflowNodeKind.Prompt, success: "b"),
                Node("b", WorkflowNodeKind.Prompt, success: "a", failure: "terminal"),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

        var exception = Assert.Throws<InvalidOperationException>(graph.Validate);

        Assert.Contains("unjustified cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AcceptsACycleJustifiedByARetryBudget()
    {
        var graph = new WorkflowGraph(
            "a",
            new[]
            {
                Node("a", WorkflowNodeKind.Prompt, success: "retry"),
                Node("retry", WorkflowNodeKind.Retry, success: "a", failure: "terminal", retryBudget: 2),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

        graph.Validate();
    }

    [Fact]
    public void Validate_RejectsAConditionCycleWithoutAPositiveBudget()
    {
        var graph = new WorkflowGraph(
            "a",
            new[]
            {
                Node("a", WorkflowNodeKind.Prompt, success: "gate"),
                Node("gate", WorkflowNodeKind.Condition, success: "terminal", failure: "a", conditionExpression: "testsPassed"),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

        Assert.Throws<InvalidOperationException>(() => graph.Validate());
    }

    [Fact]
    public void Validate_RejectsARetryNodeWithoutABudget()
    {
        var graph = new WorkflowGraph(
            "retry",
            new[]
            {
                Node("retry", WorkflowNodeKind.Retry, success: "terminal"),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

        var exception = Assert.Throws<InvalidOperationException>(graph.Validate);

        Assert.Contains("positive retry budget", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsAConditionNodeWithoutAnExpression()
    {
        var graph = new WorkflowGraph(
            "gate",
            new[]
            {
                Node("gate", WorkflowNodeKind.Condition, success: "terminal", failure: "terminal"),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

        var exception = Assert.Throws<InvalidOperationException>(graph.Validate);

        Assert.Contains("condition expression", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsATerminalOutcomeWithOutgoingTransitions()
    {
        var graph = new WorkflowGraph(
            "terminal",
            new[] { Node("terminal", WorkflowNodeKind.TerminalOutcome, success: "terminal") });

        var exception = Assert.Throws<InvalidOperationException>(graph.Validate);

        Assert.Contains("terminal outcome", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoleBinding_CombinesPrimaryAndFallbackRoutesWithoutDuplicates()
    {
        var binding = new RoleBindingDefinition(
            "Coder",
            "route-opencode",
            new[] { "route-cursor", "route-opencode", "route-mirasim" },
            new[] { "writer.lock" });

        Assert.Equal("Coder", binding.RoleId);
        Assert.Equal(
            new[] { "route-opencode", "route-cursor", "route-mirasim" },
            binding.AllowedRouteIds);
        Assert.True(binding.AllowsRoute("route-cursor"));
        Assert.False(binding.AllowsRoute("route-unknown"));
        Assert.False(binding.AllowsRoute(" "));
    }

    [Fact]
    public void RoleBinding_ResolveRoutePrefersTheRequestThenPrimaryThenFallback()
    {
        var binding = new RoleBindingDefinition(
            "Coder",
            "route-opencode",
            new[] { "route-cursor" });

        Assert.Equal("route-requested", binding.ResolveRoute("route-requested"));
        Assert.Equal("route-opencode", binding.ResolveRoute());
    }

    [Fact]
    public void RoleBinding_ResolveRouteFallsBackToTheFirstFallbackRoute()
    {
        var binding = new RoleBindingDefinition(
            "Tester",
            primaryRouteId: null,
            fallbackRouteIds: new[] { "route-mirasim", "route-opencode" });

        Assert.Equal("route-mirasim", binding.ResolveRoute());
    }

    [Fact]
    public void RoleBinding_ResolveRouteThrowsWhenNoRouteIsDeclared()
    {
        var binding = new RoleBindingDefinition("Approver");

        Assert.Throws<InvalidOperationException>(() => binding.ResolveRoute());
    }

    [Fact]
    public void RoleBinding_EvaluateRouteChange_KeepsTheSessionWhenTheRouteIsUnchanged()
    {
        var binding = new RoleBindingDefinition("Coder", "route-opencode", new[] { "route-cursor" });

        var decision = binding.EvaluateRouteChange("route-opencode", "route-opencode", reason: null);

        Assert.False(decision.IsRouteChanged);
        Assert.False(decision.RequiresNewNativeSession);
        Assert.False(decision.IsRouteMismatch);
        Assert.True(decision.CarriesNativeSession);
    }

    [Fact]
    public void RoleBinding_EvaluateRouteChange_RequiresAnExplicitReasonForAChange()
    {
        var binding = new RoleBindingDefinition("Coder", "route-opencode", new[] { "route-cursor" });

        var exception = Assert.Throws<ArgumentException>(
            () => binding.EvaluateRouteChange("route-opencode", "route-cursor", reason: " "));

        Assert.Contains("explicit reason", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoleBinding_EvaluateRouteChange_CreatesANewSessionForAReasonedChange()
    {
        var binding = new RoleBindingDefinition("Coder", "route-opencode", new[] { "route-cursor" });

        var decision = binding.EvaluateRouteChange(
            "route-opencode",
            "route-cursor",
            "The primary route hit its quota; the fallback route is used.");

        Assert.True(decision.IsRouteChanged);
        Assert.True(decision.RequiresNewNativeSession);
        Assert.False(decision.IsRouteMismatch);
        Assert.False(decision.CarriesNativeSession);
        Assert.Equal("route-cursor", decision.RequestedRouteId);
        Assert.Equal("route-opencode", decision.BoundRouteId);
    }

    [Fact]
    public void RoleBinding_EvaluateRouteChange_ReportsAnUnknownRouteAsAMismatch()
    {
        var binding = new RoleBindingDefinition("Coder", "route-opencode", new[] { "route-cursor" });

        var decision = binding.EvaluateRouteChange("route-opencode", "route-unknown", reason: null);

        Assert.True(decision.IsRouteMismatch);
        Assert.False(decision.IsRouteChanged);
        Assert.False(decision.RequiresNewNativeSession);
    }

    [Fact]
    public void RoleBinding_EvaluateRouteChange_StartsANewSessionForTheFirstRoute()
    {
        var binding = new RoleBindingDefinition("Coder", "route-opencode");

        var decision = binding.EvaluateRouteChange(boundRouteId: null, "route-opencode", reason: null);

        Assert.False(decision.IsRouteChanged);
        Assert.True(decision.RequiresNewNativeSession);
        Assert.False(decision.IsRouteMismatch);
    }

    [Fact]
    public void CodingRule_AllowsWhenEveryDocumentVerdictDiffAndEvidencePasses()
    {
        var rule = CreateCodingRule();

        var decision = rule.Evaluate(CreatePassingEvidence());

        Assert.True(decision.IsAllowed);
        Assert.False(decision.RequiresEscalation);
        Assert.Empty(decision.BlockingReasons);
    }

    [Fact]
    public void CodingRule_BlocksOnAMissingRequiredDocument()
    {
        var rule = CreateCodingRule();

        var incomplete = new CodingStageTransitionEvidence(
            new[] { "TechnicalSpecification", "Roadmap" },
            DocumentHash,
            CreateApprovingVerdicts(),
            diffWithinScope: true,
            testEvidencePresent: true,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: UiEvidenceHash);

        var decision = rule.Evaluate(incomplete);

        Assert.False(decision.IsAllowed);
        Assert.Contains(
            decision.BlockingReasons,
            reason => reason.Contains("TaskPacket", StringComparison.Ordinal));
    }

    [Fact]
    public void CodingRule_BlocksWhenTheDiffScopeCheckDidNotPass()
    {
        var rule = CreateCodingRule();

        var evidence = new CodingStageTransitionEvidence(
            RequiredDocuments,
            DocumentHash,
            CreateApprovingVerdicts(),
            diffWithinScope: false,
            testEvidencePresent: true,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: UiEvidenceHash);

        var decision = rule.Evaluate(evidence);

        Assert.False(decision.IsAllowed);
        Assert.Contains(
            decision.BlockingReasons,
            reason => reason.Contains("diff/scope", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(WorkflowReviewVerdict.Reject)]
    [InlineData(WorkflowReviewVerdict.RequestChanges)]
    [InlineData(WorkflowReviewVerdict.Missing)]
    public void CodingRule_BlocksOnAnyNonUnanimousVerdict(WorkflowReviewVerdict verdict)
    {
        var rule = CreateCodingRule();

        var evidence = new CodingStageTransitionEvidence(
            RequiredDocuments,
            DocumentHash,
            new[]
            {
                CreateVerdict(ReviewerLevelOne, verdict),
                CreateVerdict(ReviewerLevelTwo, WorkflowReviewVerdict.Approve)
            },
            diffWithinScope: true,
            testEvidencePresent: true,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: UiEvidenceHash);

        var decision = rule.Evaluate(evidence);

        Assert.False(decision.IsAllowed);
        Assert.Contains(
            decision.BlockingReasons,
            reason => reason.Contains("unanimous", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CodingRule_BlocksWhenARequiredReviewerHasNoVerdictOnTheCurrentHash()
    {
        var rule = CreateCodingRule();

        var evidence = new CodingStageTransitionEvidence(
            RequiredDocuments,
            DocumentHash,
            new[] { CreateVerdict(ReviewerLevelOne, WorkflowReviewVerdict.Approve) },
            diffWithinScope: true,
            testEvidencePresent: true,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: UiEvidenceHash);

        var decision = rule.Evaluate(evidence);

        Assert.False(decision.IsAllowed);
        Assert.Contains(
            decision.BlockingReasons,
            reason => reason.Contains(ReviewerLevelTwo, StringComparison.Ordinal));
    }

    [Fact]
    public void CodingRule_BlocksWhenTheReviewedHashIsNotReported()
    {
        var rule = CreateCodingRule();

        var evidence = new CodingStageTransitionEvidence(
            RequiredDocuments,
            documentHash: null,
            CreateApprovingVerdicts(),
            diffWithinScope: true,
            testEvidencePresent: true,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: UiEvidenceHash);

        var decision = rule.Evaluate(evidence);

        Assert.False(decision.IsAllowed);
        Assert.Contains(
            decision.BlockingReasons,
            reason => reason.Contains("hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CodingRule_BlocksWhenTestOrUiEvidenceIsMissing()
    {
        var rule = CreateCodingRule();

        var withoutTests = new CodingStageTransitionEvidence(
            RequiredDocuments,
            DocumentHash,
            CreateApprovingVerdicts(),
            diffWithinScope: true,
            testEvidencePresent: false,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: UiEvidenceHash);

        Assert.False(rule.Evaluate(withoutTests).IsAllowed);

        var withoutUiHash = new CodingStageTransitionEvidence(
            RequiredDocuments,
            DocumentHash,
            CreateApprovingVerdicts(),
            diffWithinScope: true,
            testEvidencePresent: true,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: null);

        var decision = rule.Evaluate(withoutUiHash);

        Assert.False(decision.IsAllowed);
        Assert.Contains(
            decision.BlockingReasons,
            reason => reason.Contains("hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CodingRule_RequiresEscalationWhenTheFixLimitIsExceeded()
    {
        var rule = CreateCodingRule(fixLimit: 2);

        var evidence = new CodingStageTransitionEvidence(
            RequiredDocuments,
            DocumentHash,
            CreateApprovingVerdicts(),
            diffWithinScope: true,
            testEvidencePresent: true,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: UiEvidenceHash,
            fixIterations: 3);

        var decision = rule.Evaluate(evidence);

        Assert.False(decision.IsAllowed);
        Assert.True(decision.RequiresEscalation);
        Assert.Equal("escalation", decision.EscalationTargetNodeId);
    }

    [Fact]
    public void CodingRule_AllowsAtTheFixLimitBoundary()
    {
        var rule = CreateCodingRule(fixLimit: 2);

        var evidence = new CodingStageTransitionEvidence(
            RequiredDocuments,
            DocumentHash,
            CreateApprovingVerdicts(),
            diffWithinScope: true,
            testEvidencePresent: true,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: UiEvidenceHash,
            fixIterations: 2);

        Assert.True(rule.Evaluate(evidence).IsAllowed);
    }

    [Fact]
    public void CodingRule_RejectsNegativeFixLimitsAndNegativeIterationCounters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodingStageTransitionRule("stage", fixLimit: -1));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CodingStageTransitionEvidence(fixIterations: -1));
    }

    private static readonly string[] RequiredDocuments =
    {
        "TechnicalSpecification",
        "Roadmap",
        "TaskPacket"
    };

    private static CodingStageTransitionRule CreateCodingRule(int fixLimit = 1) =>
        new(
            "code-and-ui",
            RequiredDocuments,
            new[] { ReviewerLevelOne, ReviewerLevelTwo },
            requiresDiffScopeCheck: true,
            requiresTestEvidence: true,
            requiresUiEvidence: true,
            fixLimit: fixLimit,
            escalationTargetNodeId: "escalation");

    private static CodingStageTransitionEvidence CreatePassingEvidence() =>
        new(
            RequiredDocuments,
            DocumentHash,
            CreateApprovingVerdicts(),
            diffWithinScope: true,
            testEvidencePresent: true,
            uiEvidencePresent: true,
            uiEvidenceArtifactHash: UiEvidenceHash);

    private static ReviewerVerdictRecord[] CreateApprovingVerdicts() =>
        new[]
        {
            CreateVerdict(ReviewerLevelOne, WorkflowReviewVerdict.Approve),
            CreateVerdict(ReviewerLevelTwo, WorkflowReviewVerdict.Approve)
        };

    private static ReviewerVerdictRecord CreateVerdict(
        string reviewerRole,
        WorkflowReviewVerdict verdict) =>
        new(
            reviewerRole,
            "route-opencode",
            DocumentHash,
            verdict,
            $"{reviewerRole} returned {verdict}.",
            RecordedAt);

    private static WorkflowGraph CreateCompleteGraph() =>
        new(
            "prompt",
            new[]
            {
                Node("prompt", WorkflowNodeKind.Prompt, success: "review"),
                Node("review", WorkflowNodeKind.Review, success: "writer", failure: "retry"),
                Node("writer", WorkflowNodeKind.Writer, success: "condition", failure: "retry"),
                Node(
                    "condition",
                    WorkflowNodeKind.Condition,
                    success: "validation",
                    failure: "retry",
                    conditionExpression: "testsPassed"),
                Node("validation", WorkflowNodeKind.ValidationCommand, success: "artifacts", failure: "retry"),
                Node("artifacts", WorkflowNodeKind.ArtifactCollection, success: "approval"),
                Node("approval", WorkflowNodeKind.ApprovalGate, success: "decision"),
                Node("decision", WorkflowNodeKind.UserDecision, success: "terminal"),
                Node("retry", WorkflowNodeKind.Retry, success: "writer", failure: "escalation", retryBudget: 2),
                Node("escalation", WorkflowNodeKind.Escalation, success: "terminal", failure: "terminal"),
                Node("terminal", WorkflowNodeKind.TerminalOutcome)
            });

    private static WorkflowNodeDefinition Node(
        string nodeId,
        WorkflowNodeKind kind,
        string? success = null,
        string? failure = null,
        int retryBudget = 0,
        string? conditionExpression = null) =>
        new(
            nodeId,
            kind,
            $"Display {nodeId}",
            "Coordinator",
            successTargetNodeId: success,
            failureTargetNodeId: failure,
            retryBudget: retryBudget,
            conditionExpression: conditionExpression);
}
