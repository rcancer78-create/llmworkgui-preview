using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Security;

public interface IWorkflowReviewEgressPolicy
{
    /// <summary>Re-reads the stored request and destination. Returns the saved backend-native model id.</summary>
    Task<string> ValidateAsync(WorkflowReviewEgressContext context, string endpoint, string prompt,
        string? reasoningEffort, string? nativeSessionId, CancellationToken cancellationToken);

    /// <summary>Atomically consumes Queued admission and audits the exact serialized body before HTTP.</summary>
    Task AuthorizeAsync(WorkflowReviewEgressContext context, string endpoint, string body,
        string? nativeSessionId, CancellationToken cancellationToken);
}

public sealed class WorkflowReviewEgressException : InvalidOperationException
{
    public WorkflowReviewEgressException() : base("Передача workflow-ревью заблокирована: сохранённое выполнение, текст или текущая policy не подтверждены.") { }
}

/// <summary>The sealed HTTP client stopped before invoking transport; no native cancellation is claimed.</summary>
public sealed class WorkflowReviewPreTransportCancellationException(CancellationToken cancellationToken)
    : OperationCanceledException("Workflow review was cancelled locally before HTTP transport.", cancellationToken);
