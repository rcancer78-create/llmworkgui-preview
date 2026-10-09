using System.Reflection;

namespace LLMWorkGUI.Workflows;

public static class WorkflowsAssembly
{
    public static Assembly Instance { get; } = typeof(WorkflowsAssembly).Assembly;
}
