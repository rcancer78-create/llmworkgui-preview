using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;

namespace LLMWorkGUI.Application.Reconciliation;

/// <summary>Separates a local status label from durable completion or pre-dispatch evidence.</summary>
public static class ReconciliationExecutionEvidence
{
    public static bool IsConfirmedTerminal(Execution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return execution.State != ExecutionState.Ambiguous
            && ExecutionStateMachine.IsTerminal(execution.State)
            && execution.EndedAt is { } ended && ended >= execution.CreatedAt
            && (execution.StartedAt is not { } started || started >= execution.CreatedAt && ended >= started);
    }

    public static bool IsPreDispatch(Execution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return execution.State is ExecutionState.Queued or ExecutionState.Starting
            && execution.StartedAt is null && execution.ProcessState is null && execution.EndedAt is null;
    }
}
