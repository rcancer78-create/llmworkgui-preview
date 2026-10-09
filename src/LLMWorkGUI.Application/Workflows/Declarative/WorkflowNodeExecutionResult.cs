using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Declarative;

public sealed class WorkflowNodeExecutionResult
{
    public WorkflowNodeExecutionResult(
        string executionId,
        string nodeId,
        WorkflowNodeKind kind,
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
        string? nextNodeId,
        IReadOnlyList<WorkflowArtifactEvidence>? collectedArtifacts = null)
    {
        ExecutionId = ApplicationGuard.NotBlank(executionId, nameof(executionId));
        NodeId = ApplicationGuard.NotBlank(nodeId, nameof(nodeId));
        Kind = kind;
        State = state;
        RequestedRouteId = ApplicationGuard.OptionalNotBlank(requestedRouteId, nameof(requestedRouteId));
        ObservedRouteId = ApplicationGuard.OptionalNotBlank(observedRouteId, nameof(observedRouteId));
        NativeSessionId = ApplicationGuard.OptionalNotBlank(nativeSessionId, nameof(nativeSessionId));
        PreviousNativeSessionId = ApplicationGuard.OptionalNotBlank(
            previousNativeSessionId,
            nameof(previousNativeSessionId));
        RequiresNewNativeSession = requiresNewNativeSession;
        RouteChangeReason = ApplicationGuard.OptionalNotBlank(
            routeChangeReason,
            nameof(routeChangeReason));
        RetryOfExecutionId = ApplicationGuard.OptionalNotBlank(
            retryOfExecutionId,
            nameof(retryOfExecutionId));
        RemainingRetryBudget = remainingRetryBudget;
        IsReadOnly = isReadOnly;
        WriterLockAcquired = writerLockAcquired;
        EscalationRequired = escalationRequired;
        RequiresUserDecision = requiresUserDecision;
        EvidenceSummary = ApplicationGuard.OptionalNotBlank(
            evidenceSummary,
            nameof(evidenceSummary));
        NextNodeId = ApplicationGuard.OptionalNotBlank(nextNodeId, nameof(nextNodeId));
        CollectedArtifacts = Array.AsReadOnly((collectedArtifacts ?? []).ToArray());
    }

    public string ExecutionId { get; }

    /// <summary>
    /// Collection is post-processing of an existing execution. Its Succeeded state reports the artifact
    /// operation only; consumers must not upsert/replace the source execution from this result.
    /// </summary>
    public bool IsExistingExecutionPostprocessing => Kind == WorkflowNodeKind.ArtifactCollection;

    public IReadOnlyList<WorkflowArtifactEvidence> CollectedArtifacts { get; }

    public string NodeId { get; }

    public WorkflowNodeKind Kind { get; }

    public ExecutionState State { get; }

    public string? RequestedRouteId { get; }

    public string? ObservedRouteId { get; }

    public string? NativeSessionId { get; }

    public string? PreviousNativeSessionId { get; }

    public bool RequiresNewNativeSession { get; }

    public string? RouteChangeReason { get; }

    public string? RetryOfExecutionId { get; }

    public int RemainingRetryBudget { get; }

    public bool IsReadOnly { get; }

    public bool WriterLockAcquired { get; }

    public bool EscalationRequired { get; }

    public bool RequiresUserDecision { get; }

    public string? EvidenceSummary { get; }

    public string? NextNodeId { get; }

    public bool IsSuccess => State == ExecutionState.Succeeded;
}
