using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Domain.Entities;

public sealed class Session
{
    public Session(
        string id,
        SessionBinding binding,
        string projectId,
        string workspaceRootPath,
        string? nativeSessionId,
        SessionState state,
        ReconciliationOutcome reconciliationOutcome,
        CloseReason closeReason,
        string? continuationOfSessionId,
        string? forkedFromSessionId,
        string? workflowRunId,
        string? role,
        string? activeExecutionId,
        DateTimeOffset createdAt,
        DateTimeOffset lastEventAt)
    {
        Id = DomainGuard.NotBlank(id, nameof(id));
        ArgumentNullException.ThrowIfNull(binding);
        Binding = binding;
        ProjectId = DomainGuard.NotBlank(projectId, nameof(projectId));
        WorkspaceRootPath = DomainGuard.NotBlank(workspaceRootPath, nameof(workspaceRootPath));
        NativeSessionId = DomainGuard.OptionalNotBlank(nativeSessionId, nameof(nativeSessionId));
        State = state;
        ReconciliationOutcome = reconciliationOutcome;
        CloseReason = closeReason;
        ContinuationOfSessionId = DomainGuard.OptionalNotBlank(continuationOfSessionId, nameof(continuationOfSessionId));
        ForkedFromSessionId = DomainGuard.OptionalNotBlank(forkedFromSessionId, nameof(forkedFromSessionId));
        WorkflowRunId = DomainGuard.OptionalNotBlank(workflowRunId, nameof(workflowRunId));
        Role = DomainGuard.OptionalNotBlank(role, nameof(role));
        ActiveExecutionId = DomainGuard.OptionalNotBlank(activeExecutionId, nameof(activeExecutionId));
        CreatedAt = createdAt;
        LastEventAt = lastEventAt;
    }

    public string Id { get; }

    public SessionBinding Binding { get; }

    public string ProjectId { get; }

    public string WorkspaceRootPath { get; }

    public string? NativeSessionId { get; }

    public SessionState State { get; }

    public ReconciliationOutcome ReconciliationOutcome { get; }

    public CloseReason CloseReason { get; }

    public string? ContinuationOfSessionId { get; }

    public string? ForkedFromSessionId { get; }

    public string? WorkflowRunId { get; }

    public string? Role { get; }

    public string? ActiveExecutionId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastEventAt { get; }
}
