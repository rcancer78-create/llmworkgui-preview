using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Quotas;

/// <summary>
/// Adapter contract for querying quota and rate limits from providers or plugins (ТЗ §6.6, ADR-0004 §5).
/// </summary>
public interface IQuotaSourceAdapter
{
    string SourceKind { get; }

    Task<QuotaSnapshot> FetchQuotaAsync(
        string providerProfileId,
        string accountId,
        string? modelId = null,
        CancellationToken cancellationToken = default);
}
