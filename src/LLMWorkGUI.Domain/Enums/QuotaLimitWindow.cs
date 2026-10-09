namespace LLMWorkGUI.Domain.Enums;

/// <summary>
/// Time window for quota limits (ТЗ §6.6).
/// </summary>
public enum QuotaLimitWindow
{
    PerMinute,
    PerHour,
    PerDay,
    PerMonth,
    Rolling,
    Total
}
