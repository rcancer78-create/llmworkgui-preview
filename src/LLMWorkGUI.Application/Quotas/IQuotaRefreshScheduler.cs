using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Quotas;

/// <summary>
/// Service interface for scheduling quota refreshes, applying jitter/backoff, rate limiting, and rapid-refresh guards (ТЗ §6.6, ADR-0004 §5).
/// </summary>
public interface IQuotaRefreshScheduler : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Event raised whenever an account quota refresh completes (successfully or with error).
    /// The persisted result is available before synchronous notification; a subscriber failure
    /// cannot invalidate it or prevent remaining subscribers from being called. Ownership stays
    /// published until notifications finish, so reentry for the same provider/account/model can read
    /// the completed result.
    /// </summary>
    event EventHandler<QuotaRefreshedEventArgs>? QuotaRefreshed;

    /// <summary>
    /// Refreshes quota for a specific account immediately.
    /// If force is false, respects MinRefreshInterval guard against rapid repeated requests.
    /// If an in-flight refresh is already running for the same provider, account and model, joins
    /// the existing task. A null model means account-wide and never aliases a model-specific refresh.
    /// An already-cancelled token throws before cache lookup or joining. Otherwise the first request's
    /// token governs the shared operation; later joiners do not independently cancel that operation.
    /// Disposal rejects new requests and restart. Already owned manual requests keep their caller's
    /// cancellation contract and may finish after disposal; their provider leases remain valid.
    /// A cancelled or unpersisted attempt retains success/failure metadata and schedules a bounded
    /// retry when no future refresh exists, so the account remains eligible for background polling.
    /// </summary>
    Task<QuotaSnapshot> RefreshAccountNowAsync(
        string providerProfileId,
        string accountId,
        string? modelId = null,
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes quota for all eligible accounts in the repository sequentially with rate limiting.
    /// </summary>
    Task RefreshAllEligibleAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current refresh status for an account.
    /// Status inspection remains available after disposal.
    /// </summary>
    AccountQuotaRefreshStatus? GetStatus(string accountId);

    /// <summary>
    /// Gets the current refresh status for all tracked accounts.
    /// </summary>
    IReadOnlyList<AccountQuotaRefreshStatus> GetAllStatuses();

    /// <summary>
    /// Explicitly registers or schedules the next refresh time for an account.
    /// A disposed scheduler rejects scheduling and bulk refresh requests.
    /// </summary>
    void ScheduleNextRefresh(
        string accountId,
        string providerProfileId,
        DateTimeOffset? expiresAt = null,
        bool isFailure = false);

    /// <summary>
    /// Starts the background periodic polling loop.
    /// An already-cancelled token prevents allocation; a disposed scheduler cannot restart.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the background periodic polling loop.
    /// Detaches the current polling epoch before cancellation callbacks run, and awaits actual
    /// timer cleanup. Cleanup continues despite caller cancellation; stale epoch callbacks cannot
    /// use a restarted epoch. Manual refreshes are not cancelled by this background operation.
    /// Dispose/DisposeAsync stop background polling; limiter disposal is deferred until existing
    /// manual owners retire. They do not forcibly terminate or await arbitrary manual delegates.
    /// Timer cleanup failures propagate. Prefer asynchronous disposal from UI shutdown code;
    /// synchronous disposal can wait for cooperative cancellation callbacks and timer cleanup.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
