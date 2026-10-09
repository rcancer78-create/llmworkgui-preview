using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows.Declarative;

public sealed class WorkflowChannelTurnResult
{
    public WorkflowChannelTurnResult(
        ExecutionState state,
        string? observedRouteId = null,
        string? nativeSessionId = null,
        string? evidenceSummary = null,
        WorkflowModelResponse? response = null)
    {
        State = state;
        ObservedRouteId = ApplicationGuard.OptionalNotBlank(observedRouteId, nameof(observedRouteId));
        NativeSessionId = ApplicationGuard.OptionalNotBlank(nativeSessionId, nameof(nativeSessionId));
        EvidenceSummary = ApplicationGuard.OptionalNotBlank(evidenceSummary, nameof(evidenceSummary));
        Response = response;
    }

    public ExecutionState State { get; }

    public string? ObservedRouteId { get; }

    public string? NativeSessionId { get; }

    public string? EvidenceSummary { get; }

    public WorkflowModelResponse? Response { get; }

    public bool IsSuccess => State == ExecutionState.Succeeded;
}
