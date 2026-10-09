using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

public sealed class Execution
{
    public Execution(
        string id,
        string sessionId,
        string clientRequestId,
        ExecutionState state,
        ExecutionFailureReason failureReason,
        string requestedRouteId,
        string? observedRouteId,
        string? retryOfExecutionId,
        string? processState,
        int? exitCode,
        string? terminationReason,
        IReadOnlyList<string> artifacts,
        string? sourceHashBefore,
        string? sourceHashAfter,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? endedAt)
    {
        Id = DomainGuard.NotBlank(id, nameof(id));
        SessionId = DomainGuard.NotBlank(sessionId, nameof(sessionId));
        ClientRequestId = DomainGuard.NotBlank(clientRequestId, nameof(clientRequestId));
        State = state;
        FailureReason = failureReason;
        RequestedRouteId = DomainGuard.NotBlank(requestedRouteId, nameof(requestedRouteId));
        ObservedRouteId = DomainGuard.OptionalNotBlank(observedRouteId, nameof(observedRouteId));
        RetryOfExecutionId = DomainGuard.OptionalNotBlank(retryOfExecutionId, nameof(retryOfExecutionId));
        ProcessState = DomainGuard.OptionalNotBlank(processState, nameof(processState));
        ExitCode = exitCode;
        TerminationReason = DomainGuard.OptionalNotBlank(terminationReason, nameof(terminationReason));
        Artifacts = DomainGuard.NotNullList(artifacts, nameof(artifacts));
        SourceHashBefore = DomainGuard.OptionalNotBlank(sourceHashBefore, nameof(sourceHashBefore));
        SourceHashAfter = DomainGuard.OptionalNotBlank(sourceHashAfter, nameof(sourceHashAfter));
        CreatedAt = createdAt;
        StartedAt = startedAt;
        EndedAt = endedAt;
    }

    public string Id { get; }

    public string SessionId { get; }

    public string ClientRequestId { get; }

    public ExecutionState State { get; }

    public ExecutionFailureReason FailureReason { get; }

    public string RequestedRouteId { get; }

    public string? ObservedRouteId { get; }

    public string? RetryOfExecutionId { get; }

    public string? ProcessState { get; }

    public int? ExitCode { get; }

    public string? TerminationReason { get; }

    /// <summary>All artifacts associated with this execution, including execution-only and run-scoped rows.
    /// A workflow gate must use its run's evidence, not treat this list as run-scoped authority.</summary>
    public IReadOnlyList<string> Artifacts { get; }

    public string? SourceHashBefore { get; }

    public string? SourceHashAfter { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? StartedAt { get; }

    public DateTimeOffset? EndedAt { get; }
}
