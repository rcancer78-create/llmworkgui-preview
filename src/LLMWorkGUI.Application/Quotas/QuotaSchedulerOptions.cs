namespace LLMWorkGUI.Application.Quotas;

/// <summary>
/// Options for quota polling, jitter, backoff, and rate limiting (ТЗ §6.6, ADR-0004 §5).
/// </summary>
public sealed class QuotaSchedulerOptions
{
    public const string SectionName = "QuotaScheduler";

    /// <summary>
    /// Default TTL for quota snapshots if provider does not report custom expiration (default: 5 minutes).
    /// </summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum interval between manual/rapid refreshes for the same account (default: 10 seconds).
    /// </summary>
    public TimeSpan MinRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Jitter ratio applied to refresh intervals to prevent synchronized traffic spikes (default: 0.1 for +/-10%).
    /// </summary>
    public double JitterRatio { get; set; } = 0.1;

    /// <summary>
    /// Initial backoff interval on failure (default: 30 seconds).
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum backoff interval on consecutive failures (default: 15 minutes).
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Exponential backoff multiplier on consecutive failures (default: 2.0).
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Minimum delay between requests to the same provider profile (default: 2 seconds).
    /// </summary>
    public TimeSpan ProviderRateLimitDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Interval for the background timer checking due account refreshes (default: 30 seconds).
    /// </summary>
    public TimeSpan BackgroundPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether background polling starts automatically on scheduler construction (default: false).
    /// </summary>
    public bool AutoStartBackgroundPolling { get; set; } = false;
}
