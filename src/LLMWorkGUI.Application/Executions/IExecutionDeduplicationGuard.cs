namespace LLMWorkGUI.Application.Executions;

public interface IExecutionDeduplicationGuard
{
    string ComputePromptHash(string prompt);

    Task<ExecutionAdmission> AdmitAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default);

    void Release(ExecutionAdmission admission);
}
