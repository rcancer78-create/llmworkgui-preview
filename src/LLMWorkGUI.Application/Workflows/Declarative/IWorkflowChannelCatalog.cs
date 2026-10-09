namespace LLMWorkGUI.Application.Workflows.Declarative;

public interface IWorkflowChannelCatalog
{
    IWorkflowNodeChannel ResolveChannel(string routeId, IReadOnlyList<string> requiredCapabilities);
}
