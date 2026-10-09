namespace LLMWorkGUI.Domain.Enums;

/// <summary>
/// Routing policies for selecting accounts and routes in multi-account environments (ТЗ §6.5, ADR-0004 §6).
/// </summary>
public enum RoutingPolicy
{
    Pinned,
    SessionSticky,
    QuotaFirst,
    PriorityFirst,
    Balanced,
    ManualOnly
}
