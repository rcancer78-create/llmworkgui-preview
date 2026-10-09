namespace LLMWorkGUI.Domain.Enums;

public enum SessionState
{
    Draft,
    Starting,
    Active,
    Idle,
    Ambiguous,
    Orphaned,
    Closed
}
