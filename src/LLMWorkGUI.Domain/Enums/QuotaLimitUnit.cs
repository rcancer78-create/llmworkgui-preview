namespace LLMWorkGUI.Domain.Enums;

/// <summary>
/// Unit of measurement for quota buckets (ТЗ §6.6).
/// </summary>
public enum QuotaLimitUnit
{
    Requests,
    Tokens,
    Credits,
    Currency,
    Percent
}
