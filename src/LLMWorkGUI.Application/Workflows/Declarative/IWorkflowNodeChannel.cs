namespace LLMWorkGUI.Application.Workflows.Declarative;

public interface IWorkflowNodeChannel
{
    string ChannelId { get; }

    IReadOnlyList<string> Capabilities { get; }

    bool SupportsRoute(string routeId);

    Task<WorkflowChannelTurnResult> ExecuteTurnAsync(
        WorkflowChannelTurnRequest request,
        CancellationToken cancellationToken = default);
}
