using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Quotas;

/// <summary>
/// Quota adapter for OpenCode providers and plugins (ТЗ §6.6, ADR-0004 §5).
/// When no quota endpoint is available, returns Unsupported per the architectural contract.
/// </summary>
public sealed class OpenCodePluginQuotaAdapter : IQuotaSourceAdapter
{
    private readonly bool _hasQuotaPluginContract;

    public string SourceKind => "opencode-plugin-quota";

    public OpenCodePluginQuotaAdapter(bool hasQuotaPluginContract = false)
    {
        _hasQuotaPluginContract = hasQuotaPluginContract;
    }

    public Task<QuotaSnapshot> FetchQuotaAsync(
        string providerProfileId,
        string accountId,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);
        ArgumentNullException.ThrowIfNull(accountId);

        if (!_hasQuotaPluginContract)
        {
            // OpenCode has no native /quota endpoint; returns Unsupported without fake numbers (ADR-0004 §5)
            return Task.FromResult(QuotaSnapshot.CreateUnsupported(accountId, providerProfileId, modelId));
        }

        // If a verified plugin contract is active, it would probe the plugin
        return Task.FromResult(QuotaSnapshot.CreateUnknown(accountId, providerProfileId, modelId));
    }
}
