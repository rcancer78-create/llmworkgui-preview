using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Quotas;

/// <summary>
/// Event arguments for quota refreshed events raised by the scheduler.
/// </summary>
public sealed class QuotaRefreshedEventArgs : EventArgs
{
    public QuotaRefreshedEventArgs(
        string accountId,
        string providerProfileId,
        QuotaSnapshot snapshot,
        bool isSuccess,
        string? error = null)
    {
        AccountId = accountId;
        ProviderProfileId = providerProfileId;
        Snapshot = snapshot;
        IsSuccess = isSuccess;
        Error = error;
    }

    public string AccountId { get; }
    public string ProviderProfileId { get; }
    public QuotaSnapshot Snapshot { get; }
    public bool IsSuccess { get; }
    public string? Error { get; }
}
