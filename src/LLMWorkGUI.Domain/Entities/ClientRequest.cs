namespace LLMWorkGUI.Domain.Entities;

public sealed class ClientRequest
{
    public ClientRequest(
        string id,
        string sessionId,
        string executionId,
        string promptHash,
        string requestedRouteId,
        string? observedRouteId,
        string? nativeRequestId,
        DateTimeOffset createdAt)
    {
        Id = DomainGuard.NotBlank(id, nameof(id));
        SessionId = DomainGuard.NotBlank(sessionId, nameof(sessionId));
        ExecutionId = DomainGuard.NotBlank(executionId, nameof(executionId));
        PromptHash = DomainGuard.NotBlank(promptHash, nameof(promptHash));
        RequestedRouteId = DomainGuard.NotBlank(requestedRouteId, nameof(requestedRouteId));
        ObservedRouteId = DomainGuard.OptionalNotBlank(observedRouteId, nameof(observedRouteId));
        NativeRequestId = DomainGuard.OptionalNotBlank(nativeRequestId, nameof(nativeRequestId));
        CreatedAt = createdAt;
    }

    public string Id { get; }

    public string SessionId { get; }

    public string ExecutionId { get; }

    public string PromptHash { get; }

    public string RequestedRouteId { get; }

    public string? ObservedRouteId { get; }

    public string? NativeRequestId { get; }

    public DateTimeOffset CreatedAt { get; }
}
