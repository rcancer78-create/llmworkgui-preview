namespace LLMWorkGUI.Application.Executions;

public sealed record ExecutionAdmission(
    string LocalSessionId,
    string ClientRequestId,
    string PromptHash,
    string? RetryOfExecutionId);
