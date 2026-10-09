namespace LLMWorkGUI.Application.Processes;

public enum ProcessTerminationReason
{
    None,
    UserCancelled,
    StartupTimeout,
    TurnTimeout,
    InactivityTimeout,
    BufferOverflow,
    UnexpectedExit,
    /// <summary>Start is still in flight; cleanup retains ownership but termination is not yet proven.</summary>
    StartupPending,
    /// <summary>Startup settled, but supervision failed without verified termination; cleanup retains ownership.</summary>
    CleanupPending
}
