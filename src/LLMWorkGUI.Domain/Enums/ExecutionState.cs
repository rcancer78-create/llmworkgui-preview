namespace LLMWorkGUI.Domain.Enums;

public enum ExecutionState
{
    Queued,
    Starting,
    SessionConfirmed,
    Running,
    WaitingApproval,
    Cancelling,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    Ambiguous,
    RouteMismatch
}
