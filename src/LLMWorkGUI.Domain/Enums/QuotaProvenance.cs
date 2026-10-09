namespace LLMWorkGUI.Domain.Enums;

/// <summary>
/// Status and provenance of a quota value per ТЗ §6.6 and ADR-0004 §5.2.
/// Unknown, Unsupported, Stale, and Error do NOT receive numeric scores.
/// </summary>
public enum QuotaProvenance
{
    ExactProviderReported,
    PluginReported,
    LocallyCalculated,
    Estimated,
    Stale,
    Unsupported,
    Unknown,
    Error
}
