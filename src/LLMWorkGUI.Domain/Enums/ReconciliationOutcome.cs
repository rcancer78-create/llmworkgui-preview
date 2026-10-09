namespace LLMWorkGUI.Domain.Enums;

public enum ReconciliationOutcome
{
    None,
    Reattached,
    Orphaned,
    Ambiguous,
    BackendMissing
}
