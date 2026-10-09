namespace LLMWorkGUI.Application.Executions;

public sealed record ExecutionRequest(
    string LocalSessionId,
    Guid ClientRequestId,
    string Prompt,
    bool IsManualRetry = false,
    string? RetryOfExecutionId = null);
