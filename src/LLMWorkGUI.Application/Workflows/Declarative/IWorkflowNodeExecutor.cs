namespace LLMWorkGUI.Application.Workflows.Declarative;

public interface IWorkflowNodeExecutor
{
    Task<WorkflowNodeExecutionResult> ExecuteAsync(
        WorkflowNodeExecutionRequest request,
        CancellationToken cancellationToken = default);
}
