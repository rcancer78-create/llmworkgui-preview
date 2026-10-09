using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

public sealed class WorkflowNodeDefinition
{
    public WorkflowNodeDefinition(
        string nodeId,
        WorkflowNodeKind kind,
        string displayName,
        string roleBinding,
        IReadOnlyList<string>? requiredCapabilities = null,
        string? primaryRouteId = null,
        IReadOnlyList<string>? fallbackRouteIds = null,
        TimeSpan? timeout = null,
        int retryBudget = 0,
        string? successTargetNodeId = null,
        string? failureTargetNodeId = null,
        string? conditionExpression = null,
        string? artifactContract = null,
        string? permissionIntent = null,
        WorkflowNodeGateMetadata? gateMetadata = null)
    {
        NodeId = DomainGuard.NotBlank(nodeId, nameof(nodeId));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A defined workflow node kind is required.");
        Kind = kind;
        DisplayName = DomainGuard.NotBlank(displayName, nameof(displayName));
        RoleBinding = DomainGuard.NotBlank(roleBinding, nameof(roleBinding));
        RequiredCapabilities = CopyValues(requiredCapabilities, nameof(requiredCapabilities));
        PrimaryRouteId = DomainGuard.OptionalNotBlank(primaryRouteId, nameof(primaryRouteId));
        FallbackRouteIds = CopyValues(fallbackRouteIds, nameof(fallbackRouteIds));
        Timeout = GuardTimeout(timeout, nameof(timeout));
        RetryBudget = GuardRetryBudget(retryBudget, nameof(retryBudget));
        SuccessTargetNodeId = DomainGuard.OptionalNotBlank(successTargetNodeId, nameof(successTargetNodeId));
        FailureTargetNodeId = DomainGuard.OptionalNotBlank(failureTargetNodeId, nameof(failureTargetNodeId));
        ConditionExpression = DomainGuard.OptionalNotBlank(conditionExpression, nameof(conditionExpression));
        ArtifactContract = DomainGuard.OptionalNotBlank(artifactContract, nameof(artifactContract));
        PermissionIntent = DomainGuard.OptionalNotBlank(permissionIntent, nameof(permissionIntent));
        GateMetadata = gateMetadata;
    }

    public string NodeId { get; }

    public WorkflowNodeKind Kind { get; }

    public string DisplayName { get; }

    public string RoleBinding { get; }

    public IReadOnlyList<string> RequiredCapabilities { get; }

    public string? PrimaryRouteId { get; }

    public IReadOnlyList<string> FallbackRouteIds { get; }

    public TimeSpan? Timeout { get; }

    public int RetryBudget { get; }

    public string? SuccessTargetNodeId { get; }

    public string? FailureTargetNodeId { get; }

    public string? ConditionExpression { get; }

    public string? ArtifactContract { get; }

    public string? PermissionIntent { get; }

    /// <summary>
    /// The stage gates this node was declared with, or null when the node declares none.
    ///
    /// Null means <em>absent or unknown</em>, never <em>no gate</em>. A graph written before gates were
    /// preserved reads back as null, and a consumer that needs to know whether a node is gated has to
    /// decide what an absent declaration permits instead of reading null as approval to proceed. The
    /// gates themselves are never derived from <see cref="Kind"/>, from <see cref="RoleBinding"/> or from
    /// any name in the graph.
    /// </summary>
    public WorkflowNodeGateMetadata? GateMetadata { get; }

    public bool HasOutgoingTransitions =>
        SuccessTargetNodeId is not null || FailureTargetNodeId is not null;

    private static IReadOnlyList<string> CopyValues(
        IReadOnlyList<string>? values,
        string parameterName)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var copy = new string[values.Count];

        for (var index = 0; index < values.Count; index++)
        {
            copy[index] = DomainGuard.NotBlank(values[index], parameterName);
        }

        return Array.AsReadOnly(copy);
    }

    private static TimeSpan? GuardTimeout(TimeSpan? timeout, string parameterName)
    {
        if (timeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "A node timeout must be a positive duration when provided.");
        }

        return timeout;
    }

    private static int GuardRetryBudget(int retryBudget, string parameterName)
    {
        if (retryBudget < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                retryBudget,
                "A node retry budget cannot be negative.");
        }

        return retryBudget;
    }
}
