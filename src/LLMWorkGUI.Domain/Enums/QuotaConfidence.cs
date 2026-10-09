namespace LLMWorkGUI.Domain.Enums;

/// <summary>
/// Confidence level of the quota calculation or reported snapshot (ТЗ §6.6).
/// </summary>
public enum QuotaConfidence
{
    Exact,
    High,
    Medium,
    Low,
    None
}
