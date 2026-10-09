namespace LLMWorkGUI.Application.Executions;

public sealed class ExecutionDeduplicationException : InvalidOperationException
{
    public ExecutionDeduplicationException(string message)
        : base(message)
    {
    }
}
