namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public enum MirasimTurnStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Incomplete,
    Ambiguous,
    RefusedByLock,
    Cancelled,
    UnsupportedTransport,
    RefusedByPolicy
}
