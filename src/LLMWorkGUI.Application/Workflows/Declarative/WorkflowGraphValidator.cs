using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Declarative;

public sealed class WorkflowGraphValidator : IWorkflowGraphValidator
{
    public void Validate(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        graph.Validate();
    }
}
