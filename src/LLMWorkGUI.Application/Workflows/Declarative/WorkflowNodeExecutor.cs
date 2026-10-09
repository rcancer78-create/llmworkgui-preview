using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows.Orchestration;

namespace LLMWorkGUI.Application.Workflows.Declarative;

public sealed class WorkflowNodeExecutor : IWorkflowNodeExecutor
{
    public const string AmbiguousRetryForbiddenMessage =
        "Automatic retry is strictly forbidden for Ambiguous execution state (ТЗ §6.15).";

    public const string WriterLockReleaseReason =
        "declarative writer node reached a terminal outcome";

    private const string WritePermissionIntent = "write";

    private readonly IWorkflowChannelCatalog _channelCatalog;
    private readonly ICheckoutLockService _checkoutLockService;
    private readonly IWorkflowRunService? _runService;
    private readonly IWorkflowRunRepository? _runRepository;

    public WorkflowNodeExecutor(
        IWorkflowChannelCatalog channelCatalog,
        ICheckoutLockService checkoutLockService,
        IWorkflowRunService? runService = null,
        IWorkflowRunRepository? runRepository = null)
    {
        ArgumentNullException.ThrowIfNull(channelCatalog);
        ArgumentNullException.ThrowIfNull(checkoutLockService);

        _channelCatalog = channelCatalog;
        _checkoutLockService = checkoutLockService;
        _runService = runService;
        _runRepository = runRepository;
    }

    public Task<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.PreviousExecutionState == ExecutionState.Ambiguous)
        {
            throw new InvalidOperationException(AmbiguousRetryForbiddenMessage);
        }

        var executionId = string.IsNullOrWhiteSpace(request.ExecutionId)
            ? Guid.NewGuid().ToString("N")
            : request.ExecutionId;

        return request.Node.Kind switch
        {
            WorkflowNodeKind.Prompt => ExecuteModelTurnAsync(
                request, executionId, isReadOnly: true, writerLockAcquired: false, cancellationToken),
            WorkflowNodeKind.Review => ExecuteReviewAsync(request, executionId, cancellationToken),
            WorkflowNodeKind.Writer => ExecuteWriterAsync(request, executionId, cancellationToken),
            WorkflowNodeKind.ValidationCommand => ExecuteModelTurnAsync(
                request, executionId, isReadOnly: true, writerLockAcquired: false, cancellationToken),
            WorkflowNodeKind.ApprovalGate => Task.FromResult(ExecuteUserGate(request, executionId)),
            WorkflowNodeKind.UserDecision => Task.FromResult(ExecuteUserGate(request, executionId)),
            WorkflowNodeKind.Condition => Task.FromResult(ExecuteCondition(request, executionId)),
            WorkflowNodeKind.Retry => Task.FromResult(ExecuteRetry(request, executionId)),
            WorkflowNodeKind.Escalation => Task.FromResult(ExecuteEscalation(request, executionId)),
            WorkflowNodeKind.ArtifactCollection => CollectArtifactAsync(request, cancellationToken),
            WorkflowNodeKind.TerminalOutcome => Task.FromResult(
                ExecuteTerminalOutcome(request, executionId)),
            _ => throw new WorkflowValidationException(
                $"Unsupported workflow node kind '{request.Node.Kind}'.")
        };
    }

    private async Task<WorkflowNodeExecutionResult> CollectArtifactAsync(WorkflowNodeExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (_runService is null || _runRepository is null || request.ArtifactCollection is not { } input
            || request.ExecutionId is not { } executionId)
            throw new WorkflowValidationException("Artifact collection requires a durable collector, captured bytes, run and existing execution identity.");
        var run = await _runRepository.GetByIdAsync(input.RunId, cancellationToken).ConfigureAwait(false);
        if (run is null || !run.IsTemplateBacked || run.ProjectId != request.ProjectId
            || run.CurrentStageId != request.Node.NodeId || run.IsTerminal)
            throw new WorkflowValidationException("Artifact collection requires this project's current pinned run stage.");
        var graph = WorkflowGraphSnapshot.Deserialize(run.TemplateGraphSnapshotJson!, run.Id);
        var node = graph.Nodes.SingleOrDefault(n => n.NodeId == run.CurrentStageId);
        // The caller's graph is not authority for either the collection contract or the next edge.
        if (node is null || node.Kind != WorkflowNodeKind.ArtifactCollection || node.ArtifactContract is null
            || WorkflowGraphSnapshot.Serialize(new WorkflowGraph(node.NodeId, [node]))
                != WorkflowGraphSnapshot.Serialize(new WorkflowGraph(request.Node.NodeId, [request.Node])))
            throw new WorkflowValidationException("Artifact collection node differs from the run's pinned contract.");
        var saved = await _runService.RecordExecutionArtifactAsync(run.Id, node.NodeId, node.ArtifactContract,
            executionId, input.Content, input.Classification, cancellationToken).ConfigureAwait(false);
        var artifact = saved.Artifact;
        if (artifact.StageId != node.NodeId || artifact.ExecutionId != executionId || artifact.RunId != run.Id)
            throw new WorkflowValidationException("The collector returned evidence for a different target.");
        return new WorkflowNodeExecutionResult(executionId, node.NodeId, node.Kind, ExecutionState.Succeeded,
            null, null, null, null, false, null, null, 0, true, false, false, false,
            $"Artifact '{artifact.ArtifactId}' stored for run '{run.Id}', stage '{node.NodeId}' and execution '{executionId}'.",
            node.SuccessTargetNodeId, [artifact]);
    }

    private async Task<WorkflowNodeExecutionResult> ExecuteReviewAsync(
        WorkflowNodeExecutionRequest request,
        string executionId,
        CancellationToken cancellationToken)
    {
        EnsureReviewIsReadOnly(request);

        return await ExecuteModelTurnAsync(
                request,
                executionId,
                isReadOnly: true,
                writerLockAcquired: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkflowNodeExecutionResult> ExecuteWriterAsync(
        WorkflowNodeExecutionRequest request,
        string executionId,
        CancellationToken cancellationToken)
    {
        var lockToken = await _checkoutLockService
            .AcquireWriterLockAsync(
                request.ProjectId,
                request.CanonicalCheckoutPath,
                executionId,
                request.ProcessGeneration,
                cancellationToken)
            .ConfigureAwait(false);

        var dispatched = false;
        WorkflowNodeExecutionResult? result = null;
        try
        {
            result = await ExecuteModelTurnAsync(
                    request,
                    executionId,
                    isReadOnly: false,
                    writerLockAcquired: true,
                    cancellationToken,
                    onDispatch: () => dispatched = true)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (!dispatched || result?.State is ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.Cancelled)
                await lockToken.ReleaseAsync(WriterLockReleaseReason, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<WorkflowNodeExecutionResult> ExecuteModelTurnAsync(
        WorkflowNodeExecutionRequest request,
        string executionId,
        bool isReadOnly,
        bool writerLockAcquired,
        CancellationToken cancellationToken,
        Action? onDispatch = null)
    {
        var requestedRouteId = ResolveRequestedRoute(request);
        var remainingRetryBudget = request.RetryBudgetRemaining ?? request.Node.RetryBudget;

        EnsureExplicitRouteChangeReason(request, requestedRouteId);

        var routeChange = request.RoleBinding.EvaluateRouteChange(
            request.BoundRouteId,
            requestedRouteId,
            request.RouteChangeReason);

        if (routeChange.IsRouteMismatch)
        {
            return CreateResult(
                request,
                executionId,
                ExecutionState.RouteMismatch,
                requestedRouteId,
                observedRouteId: null,
                nativeSessionId: null,
                previousNativeSessionId: request.NativeSessionId,
                requiresNewNativeSession: false,
                routeChangeReason: request.RouteChangeReason,
                retryOfExecutionId: request.PreviousExecutionId,
                remainingRetryBudget,
                isReadOnly,
                writerLockAcquired,
                escalationRequired: false,
                requiresUserDecision: false,
                evidenceSummary:
                    $"The requested route '{requestedRouteId}' is not allowed by role "
                    + $"'{request.RoleBinding.RoleId}'; the turn is refused as RouteMismatch.",
                nextNodeId: request.Node.FailureTargetNodeId);
        }

        var carriedNativeSessionId = routeChange.CarriesNativeSession
            ? request.NativeSessionId
            : null;

        var requiredCapabilities = MergeCapabilities(
            request.Node.RequiredCapabilities,
            request.RoleBinding.RequiredCapabilities);

        var channel = _channelCatalog.ResolveChannel(requestedRouteId, requiredCapabilities);

        var turnRequest = new WorkflowChannelTurnRequest(
            executionId,
            request.Node,
            request.RoleBinding,
            requestedRouteId,
            isReadOnly,
            request.PromptOrCommand,
            carriedNativeSessionId,
            routeChange.IsRouteChanged ? request.RouteChangeReason : null,
            request.PreviousExecutionId);

        cancellationToken.ThrowIfCancellationRequested();
        onDispatch?.Invoke();
        var turn = await channel
            .ExecuteTurnAsync(turnRequest, cancellationToken)
            .ConfigureAwait(false);

        var state = turn.State;

        if (state == ExecutionState.Succeeded
            && !string.Equals(turn.ObservedRouteId, requestedRouteId, StringComparison.Ordinal))
        {
            state = ExecutionState.RouteMismatch;
        }

        var evidenceSummary = state == ExecutionState.RouteMismatch
            ? $"The channel '{channel.ChannelId}' observed route '{turn.ObservedRouteId ?? "<none>"}' "
                + $"instead of the requested route '{requestedRouteId}'; the turn is not a success."
            : turn.EvidenceSummary;

        return CreateResult(
            request,
            executionId,
            state,
            requestedRouteId,
            turn.ObservedRouteId,
            turn.NativeSessionId,
            routeChange.RequiresNewNativeSession ? request.NativeSessionId : null,
            routeChange.RequiresNewNativeSession,
            routeChange.IsRouteChanged ? request.RouteChangeReason : null,
            request.PreviousExecutionId,
            remainingRetryBudget,
            isReadOnly,
            writerLockAcquired,
            escalationRequired: false,
            requiresUserDecision: false,
            evidenceSummary,
            ResolveNextNodeId(request.Node, state));
    }

    private static WorkflowNodeExecutionResult ExecuteUserGate(
        WorkflowNodeExecutionRequest request,
        string executionId) =>
        CreateResult(
            request,
            executionId,
            ExecutionState.WaitingApproval,
            request.RequestedRouteId ?? request.BoundRouteId,
            observedRouteId: null,
            request.NativeSessionId,
            previousNativeSessionId: null,
            requiresNewNativeSession: false,
            routeChangeReason: null,
            retryOfExecutionId: request.PreviousExecutionId,
            request.RetryBudgetRemaining ?? request.Node.RetryBudget,
            isReadOnly: true,
            writerLockAcquired: false,
            escalationRequired: false,
            requiresUserDecision: true,
            evidenceSummary:
                $"Node '{request.Node.NodeId}' waits for an explicit user decision; "
                + "no automatic transition is performed.",
            nextNodeId: request.Node.SuccessTargetNodeId);

    private static WorkflowNodeExecutionResult ExecuteCondition(
        WorkflowNodeExecutionRequest request,
        string executionId)
    {
        var conditionMet = EvaluateCondition(request);

        return CreateResult(
            request,
            executionId,
            ExecutionState.Succeeded,
            requestedRouteId: null,
            observedRouteId: null,
            nativeSessionId: null,
            previousNativeSessionId: null,
            requiresNewNativeSession: false,
            routeChangeReason: null,
            retryOfExecutionId: request.PreviousExecutionId,
            request.RetryBudgetRemaining ?? request.Node.RetryBudget,
            isReadOnly: true,
            writerLockAcquired: false,
            escalationRequired: false,
            requiresUserDecision: false,
            evidenceSummary:
                $"Condition '{request.Node.ConditionExpression}' evaluated to {conditionMet}.",
            nextNodeId: conditionMet
                ? request.Node.SuccessTargetNodeId
                : request.Node.FailureTargetNodeId);
    }

    private static WorkflowNodeExecutionResult ExecuteRetry(
        WorkflowNodeExecutionRequest request,
        string executionId)
    {
        var previousExecutionId = request.PreviousExecutionId
            ?? throw new WorkflowValidationException(
                $"The retry node '{request.Node.NodeId}' requires the identifier of the previous "
                + "execution so the new execution can be linked to it.");

        // Retry routes a known terminal failure to the next attempt; it does not dispatch that
        // attempt itself. Cancelled, in-flight, successful or unknown predecessors cannot spend
        // the workflow budget. Ambiguous is independently refused before node dispatch.
        if (request.PreviousExecutionState is not
            (ExecutionState.Failed or ExecutionState.TimedOut or ExecutionState.RouteMismatch))
        {
            throw new InvalidOperationException(
                $"The retry node '{request.Node.NodeId}' requires a terminal failed predecessor; "
                + "the workflow retry budget was not spent.");
        }

        var remainingRetryBudget = Math.Clamp(
            request.RetryBudgetRemaining ?? request.Node.RetryBudget, 0, request.Node.RetryBudget);

        if (remainingRetryBudget == 0)
        {
            return CreateResult(
                request,
                executionId,
                ExecutionState.Failed,
                request.RequestedRouteId ?? request.BoundRouteId,
                observedRouteId: null,
                request.NativeSessionId,
                previousNativeSessionId: null,
                requiresNewNativeSession: false,
                routeChangeReason: null,
                retryOfExecutionId: previousExecutionId,
                remainingRetryBudget: 0,
                isReadOnly: true,
                writerLockAcquired: false,
                escalationRequired: true,
                requiresUserDecision: false,
                evidenceSummary:
                    $"The retry budget of node '{request.Node.NodeId}' is exhausted; "
                    + "escalation is required instead of another automatic retry.",
                nextNodeId: request.Node.FailureTargetNodeId);
        }

        return CreateResult(
            request,
            executionId,
            ExecutionState.Succeeded,
            request.RequestedRouteId ?? request.BoundRouteId,
            observedRouteId: null,
            request.NativeSessionId,
            previousNativeSessionId: null,
            requiresNewNativeSession: false,
            routeChangeReason: null,
            retryOfExecutionId: previousExecutionId,
            remainingRetryBudget: remainingRetryBudget - 1,
            isReadOnly: true,
            writerLockAcquired: false,
            escalationRequired: false,
            requiresUserDecision: false,
            evidenceSummary:
                $"Retry execution '{executionId}' is linked to the previous execution "
                + $"'{previousExecutionId}'; {remainingRetryBudget - 1} retry attempt(s) remain.",
            nextNodeId: request.Node.SuccessTargetNodeId);
    }

    private static WorkflowNodeExecutionResult ExecuteEscalation(
        WorkflowNodeExecutionRequest request,
        string executionId)
    {
        var currentRouteId = request.BoundRouteId ?? request.RequestedRouteId;

        var fallbackRouteId = request.RoleBinding.FallbackRouteIds
            .FirstOrDefault(route =>
                !string.Equals(route, currentRouteId, StringComparison.Ordinal))
            ?? throw new WorkflowValidationException(
                $"Escalation for role '{request.RoleBinding.RoleId}' requires a fallback route "
                + "different from the current route.");

        if (string.IsNullOrWhiteSpace(request.RouteChangeReason))
        {
            throw new WorkflowValidationException(
                "An escalation route change requires an explicit reason; silent fallback is forbidden.");
        }

        return CreateResult(
            request,
            executionId,
            ExecutionState.Succeeded,
            fallbackRouteId,
            observedRouteId: null,
            nativeSessionId: null,
            previousNativeSessionId: request.NativeSessionId,
            requiresNewNativeSession: true,
            routeChangeReason: request.RouteChangeReason,
            retryOfExecutionId: request.PreviousExecutionId,
            request.RetryBudgetRemaining ?? request.Node.RetryBudget,
            isReadOnly: false,
            writerLockAcquired: false,
            escalationRequired: false,
            requiresUserDecision: false,
            evidenceSummary:
                $"Escalated role '{request.RoleBinding.RoleId}' from route "
                + $"'{currentRouteId ?? "<none>"}' to fallback route '{fallbackRouteId}'; "
                + "a new backend session is required.",
            nextNodeId: request.Node.SuccessTargetNodeId);
    }

    private static WorkflowNodeExecutionResult ExecuteTerminalOutcome(
        WorkflowNodeExecutionRequest request,
        string executionId) =>
        CreateResult(
            request,
            executionId,
            ExecutionState.Succeeded,
            requestedRouteId: null,
            observedRouteId: null,
            nativeSessionId: null,
            previousNativeSessionId: null,
            requiresNewNativeSession: false,
            routeChangeReason: null,
            retryOfExecutionId: request.PreviousExecutionId,
            request.RetryBudgetRemaining ?? request.Node.RetryBudget,
            isReadOnly: true,
            writerLockAcquired: false,
            escalationRequired: false,
            requiresUserDecision: false,
            evidenceSummary: $"Terminal outcome '{request.Node.DisplayName}'.",
            nextNodeId: null);

    private static void EnsureExplicitRouteChangeReason(
        WorkflowNodeExecutionRequest request,
        string requestedRouteId)
    {
        if (request.BoundRouteId is null
            || string.Equals(request.BoundRouteId, requestedRouteId, StringComparison.Ordinal)
            || !request.RoleBinding.AllowsRoute(requestedRouteId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.RouteChangeReason))
        {
            throw new WorkflowValidationException(
                $"Changing the route of the running native session from '{request.BoundRouteId}' "
                + $"to '{requestedRouteId}' requires an explicit reason; silent fallback is forbidden.");
        }
    }

    private static void EnsureReviewIsReadOnly(WorkflowNodeExecutionRequest request)
    {
        if (request.Node.PermissionIntent is not null
            && string.Equals(
                request.Node.PermissionIntent,
                WritePermissionIntent,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowValidationException(
                $"The review node '{request.Node.NodeId}' is strictly read-only and cannot declare "
                + "a write permission intent.");
        }
    }

    private static string ResolveRequestedRoute(WorkflowNodeExecutionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.RequestedRouteId))
        {
            return request.RequestedRouteId;
        }

        if (!string.IsNullOrWhiteSpace(request.BoundRouteId))
        {
            return request.BoundRouteId;
        }

        return request.RoleBinding.ResolveRoute();
    }

    private static bool EvaluateCondition(WorkflowNodeExecutionRequest request)
    {
        var expression = request.Node.ConditionExpression
            ?? throw new WorkflowValidationException(
                $"The condition node '{request.Node.NodeId}' has no condition expression.");

        var negated = expression.StartsWith("!", StringComparison.Ordinal);
        var term = negated ? expression[1..] : expression;

        var comparisonIndex = term.IndexOf("==", StringComparison.Ordinal);
        bool conditionMet;

        if (comparisonIndex >= 0)
        {
            var factName = term[..comparisonIndex].Trim();
            var expectedValue = term[(comparisonIndex + 2)..].Trim();
            var actualValue = ResolveFact(request, factName);

            conditionMet = string.Equals(actualValue, expectedValue, StringComparison.Ordinal);
        }
        else
        {
            conditionMet = IsTruthy(ResolveFact(request, term.Trim()));
        }

        return negated ? !conditionMet : conditionMet;
    }

    private static string ResolveFact(WorkflowNodeExecutionRequest request, string factName)
    {
        if (factName.Length == 0 || !request.Facts.TryGetValue(factName, out var value))
        {
            throw new WorkflowValidationException(
                $"Condition fact '{factName}' is not reported for node '{request.Node.NodeId}'; "
                + "the condition is refused fail-closed.");
        }

        return value;
    }

    private static bool IsTruthy(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "1" or "yes";

    private static string? ResolveNextNodeId(
        WorkflowNodeDefinition node,
        ExecutionState state) =>
        state switch
        {
            ExecutionState.Succeeded => node.SuccessTargetNodeId,
            ExecutionState.WaitingApproval => node.SuccessTargetNodeId,
            ExecutionState.Failed or ExecutionState.TimedOut or ExecutionState.Cancelled
                or ExecutionState.Ambiguous or ExecutionState.RouteMismatch =>
                node.FailureTargetNodeId,
            _ => null
        };

    private static IReadOnlyList<string> MergeCapabilities(
        IReadOnlyList<string> nodeCapabilities,
        IReadOnlyList<string> roleCapabilities)
    {
        var merged = new List<string>(nodeCapabilities.Count + roleCapabilities.Count);

        foreach (var capability in nodeCapabilities)
        {
            if (!merged.Contains(capability, StringComparer.Ordinal))
            {
                merged.Add(capability);
            }
        }

        foreach (var capability in roleCapabilities)
        {
            if (!merged.Contains(capability, StringComparer.Ordinal))
            {
                merged.Add(capability);
            }
        }

        return merged;
    }

    private static WorkflowNodeExecutionResult CreateResult(
        WorkflowNodeExecutionRequest request,
        string executionId,
        ExecutionState state,
        string? requestedRouteId,
        string? observedRouteId,
        string? nativeSessionId,
        string? previousNativeSessionId,
        bool requiresNewNativeSession,
        string? routeChangeReason,
        string? retryOfExecutionId,
        int remainingRetryBudget,
        bool isReadOnly,
        bool writerLockAcquired,
        bool escalationRequired,
        bool requiresUserDecision,
        string? evidenceSummary,
        string? nextNodeId) =>
        new(
            executionId,
            request.Node.NodeId,
            request.Node.Kind,
            state,
            requestedRouteId,
            observedRouteId,
            nativeSessionId,
            previousNativeSessionId,
            requiresNewNativeSession,
            routeChangeReason,
            retryOfExecutionId,
            remainingRetryBudget,
            isReadOnly,
            writerLockAcquired,
            escalationRequired,
            requiresUserDecision,
            evidenceSummary,
            nextNodeId);
}
