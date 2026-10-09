using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Declarative;

public sealed class WorkflowChannelTurnRequest
{
    public WorkflowChannelTurnRequest(
        string executionId,
        WorkflowNodeDefinition node,
        RoleBindingDefinition roleBinding,
        string requestedRouteId,
        bool isReadOnly,
        string? promptOrCommand = null,
        string? nativeSessionId = null,
        string? routeChangeReason = null,
        string? retryOfExecutionId = null)
    {
        ExecutionId = ApplicationGuard.NotBlank(executionId, nameof(executionId));
        Node = node ?? throw new ArgumentNullException(nameof(node));
        RoleBinding = roleBinding ?? throw new ArgumentNullException(nameof(roleBinding));
        RequestedRouteId = ApplicationGuard.NotBlank(requestedRouteId, nameof(requestedRouteId));
        IsReadOnly = isReadOnly;
        PromptOrCommand = ApplicationGuard.OptionalNotBlank(promptOrCommand, nameof(promptOrCommand));
        NativeSessionId = ApplicationGuard.OptionalNotBlank(nativeSessionId, nameof(nativeSessionId));
        RouteChangeReason = ApplicationGuard.OptionalNotBlank(
            routeChangeReason,
            nameof(routeChangeReason));
        RetryOfExecutionId = ApplicationGuard.OptionalNotBlank(
            retryOfExecutionId,
            nameof(retryOfExecutionId));
    }

    public string ExecutionId { get; }

    public WorkflowNodeDefinition Node { get; }

    public RoleBindingDefinition RoleBinding { get; }

    public string RequestedRouteId { get; }

    public bool IsReadOnly { get; }

    public string? PromptOrCommand { get; }

    public string? NativeSessionId { get; }

    public string? RouteChangeReason { get; }

    public string? RetryOfExecutionId { get; }
}
