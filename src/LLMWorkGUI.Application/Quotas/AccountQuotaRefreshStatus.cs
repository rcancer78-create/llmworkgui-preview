namespace LLMWorkGUI.Application.Quotas;

/// <summary>
/// Status record tracking scheduler state, freshness, backoff, and errors per account (ТЗ §6.6).
/// </summary>
public sealed record AccountQuotaRefreshStatus
{
    public required string AccountId { get; init; }
    public required string ProviderProfileId { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessfulRefreshAt { get; init; }
    public DateTimeOffset? NextScheduledRefreshAt { get; init; }
    public int ConsecutiveFailures { get; init; }
    public TimeSpan CurrentBackoff { get; init; }
    public string? LastError { get; init; }
    public string? LatestSnapshotId { get; init; }
    public bool IsRefreshing { get; init; }
}
