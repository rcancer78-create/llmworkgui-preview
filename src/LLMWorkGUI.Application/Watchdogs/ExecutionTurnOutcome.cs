namespace LLMWorkGUI.Application.Watchdogs;

/// <summary>
/// Terminal classification of a single supervised turn.
/// </summary>
public enum ExecutionTurnOutcome
{
    Succeeded,
    Failed,
    TimedOut,
    UserCancelled,
    Ambiguous,
    InteractiveInputWait,
    OrchestrationFailure,
    StartupTimeout,
    BufferOverflow
}
