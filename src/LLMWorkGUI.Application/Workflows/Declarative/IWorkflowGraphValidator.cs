using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Declarative;

public interface IWorkflowGraphValidator
{
    void Validate(WorkflowGraph graph);
}
