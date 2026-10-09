using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Declarative;

public sealed class WorkflowNodeExecutionRequest
{
    public WorkflowNodeExecutionRequest(
        WorkflowNodeDefinition node,
        RoleBindingDefinition roleBinding,
        string projectId,
        string canonicalCheckoutPath,
        string? requestedRouteId = null,
        string? promptOrCommand = null,
        string? executionId = null,
        string? previousExecutionId = null,
        ExecutionState? previousExecutionState = null,
        string? nativeSessionId = null,
        string? boundRouteId = null,
        string? routeChangeReason = null,
        int? retryBudgetRemaining = null,
        long processGeneration = 0,
        IReadOnlyDictionary<string, string>? facts = null,
        WorkflowArtifactCollectionInput? artifactCollection = null)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        RoleBinding = roleBinding ?? throw new ArgumentNullException(nameof(roleBinding));
        ProjectId = ApplicationGuard.NotBlank(projectId, nameof(projectId));
        CanonicalCheckoutPath = ApplicationGuard.NotBlank(
            canonicalCheckoutPath,
            nameof(canonicalCheckoutPath));
        RequestedRouteId = ApplicationGuard.OptionalNotBlank(requestedRouteId, nameof(requestedRouteId));
        PromptOrCommand = ApplicationGuard.OptionalNotBlank(
            promptOrCommand,
            nameof(promptOrCommand));
        ExecutionId = ApplicationGuard.OptionalNotBlank(executionId, nameof(executionId));
        PreviousExecutionId = ApplicationGuard.OptionalNotBlank(
            previousExecutionId,
            nameof(previousExecutionId));
        PreviousExecutionState = previousExecutionState;
        NativeSessionId = ApplicationGuard.OptionalNotBlank(nativeSessionId, nameof(nativeSessionId));
        BoundRouteId = ApplicationGuard.OptionalNotBlank(boundRouteId, nameof(boundRouteId));
        RouteChangeReason = ApplicationGuard.OptionalNotBlank(
            routeChangeReason,
            nameof(routeChangeReason));
        RetryBudgetRemaining = retryBudgetRemaining;
        ProcessGeneration = processGeneration;
        ArtifactCollection = artifactCollection;
        Facts = facts is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(facts, StringComparer.Ordinal);
    }

    public WorkflowNodeDefinition Node { get; }

    public WorkflowArtifactCollectionInput? ArtifactCollection { get; }

    public RoleBindingDefinition RoleBinding { get; }

    public string ProjectId { get; }

    public string CanonicalCheckoutPath { get; }

    public string? RequestedRouteId { get; }

    public string? PromptOrCommand { get; }

    public string? ExecutionId { get; }

    public string? PreviousExecutionId { get; }

    public ExecutionState? PreviousExecutionState { get; }

    public string? NativeSessionId { get; }

    public string? BoundRouteId { get; }

    public string? RouteChangeReason { get; }

    public int? RetryBudgetRemaining { get; }

    public long ProcessGeneration { get; }

    public IReadOnlyDictionary<string, string> Facts { get; }
}
