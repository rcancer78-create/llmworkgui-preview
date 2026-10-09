using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;

namespace LLMWorkGUI.Application.Repositories;

/// <summary>Read-only access to redacted, hash-checked response content committed with reviewer completion.</summary>
public interface IWorkflowReviewResponseRepository
{
    Task<StoredWorkflowReviewResponse?> GetByExecutionIdAsync(string executionId, CancellationToken cancellationToken = default);
}

/// <summary>Transport evidence only. Neither text nor a matching hash proves native response origin.</summary>
public sealed record StoredWorkflowReviewResponse(string ExecutionId, string? NativeSessionId,
    WorkflowModelResponse Response, string ReceivedSha256, DateTimeOffset CapturedAtUtc)
{
    public bool WasRedacted => Response.Sha256 != ReceivedSha256;
    public ParsedWorkflowReviewResponse? Parsed { get; init; }
}
