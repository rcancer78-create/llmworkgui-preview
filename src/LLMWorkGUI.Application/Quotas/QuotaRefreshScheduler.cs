using System.Collections.Concurrent;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Application.Quotas;

/// <summary>
/// Quota refresh scheduler with freshness TTL, jitter, exponential backoff, per-provider rate limiting,
/// and rapid-refresh protection (ТЗ §6.6, ADR-0004 §5).
/// </summary>
public sealed class QuotaRefreshScheduler : IQuotaRefreshScheduler
{
    private readonly IQuotaSourceAdapter _adapter;
    private readonly IQuotaSnapshotRepository _snapshotRepository;
    private readonly IAccountRepository? _accountRepository;
    private readonly QuotaSchedulerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QuotaRefreshScheduler>? _logger;
    private readonly Random _random;

    private readonly object _syncRoot = new();
    private readonly ConcurrentDictionary<string, AccountQuotaRefreshStatus> _statuses = new();
    private readonly ConcurrentDictionary<RefreshIdentity, RefreshCacheState> _refreshCache = new();
    private readonly ConcurrentDictionary<RefreshIdentity, Task<QuotaSnapshot>> _inFlightRefreshes = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _providerRateLimiters = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _providerLastCallTimes = new();

    private PollingEpoch? _backgroundEpoch;
    private readonly List<Task> _stoppingEpochs = new();
    private bool _isDisposed;

    private readonly record struct RefreshIdentity(string ProviderProfileId, string AccountId, string? ModelId);
    private sealed record RefreshCacheState(DateTimeOffset LastAttemptAt, string? LatestSnapshotId);

    public event EventHandler<QuotaRefreshedEventArgs>? QuotaRefreshed;

    public QuotaRefreshScheduler(
        IQuotaSourceAdapter adapter,
        IQuotaSnapshotRepository snapshotRepository,
        IAccountRepository? accountRepository = null,
        IOptions<QuotaSchedulerOptions>? options = null,
        TimeProvider? timeProvider = null,
        ILogger<QuotaRefreshScheduler>? logger = null,
        int? randomSeed = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _snapshotRepository = snapshotRepository ?? throw new ArgumentNullException(nameof(snapshotRepository));
        _accountRepository = accountRepository;
        _options = options?.Value ?? new QuotaSchedulerOptions();
        var validation = new QuotaSchedulerOptionsValidator().Validate(null, _options);
        if (validation.Failed)
            throw new OptionsValidationException(Options.DefaultName,
                typeof(QuotaSchedulerOptions), validation.Failures!);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();

        if (_options.AutoStartBackgroundPolling)
        {
            _ = StartAsync(CancellationToken.None);
        }
    }

    public async Task<QuotaSnapshot> RefreshAccountNowAsync(
        string providerProfileId,
        string accountId,
        string? modelId = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot) { ObjectDisposedException.ThrowIf(_isDisposed, this); }

        var now = _timeProvider.GetUtcNow();
        var identity = new RefreshIdentity(providerProfileId, accountId, modelId);

        // A live owner is newer than any cached snapshot for this scope.
        var pending = GetInFlightRefresh(identity);
        if (pending is not null)
        {
            return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // 1. Rapid refresh guard
        if (!force && _refreshCache.TryGetValue(identity, out var currentStatus))
        {
            if ((now - currentStatus.LastAttemptAt) < _options.MinRefreshInterval)
            {
                _logger?.LogDebug(
                    "Rapid refresh guard hit for account {AccountId}. Elapsed: {Elapsed}s < Min: {Min}s",
                    accountId,
                    (now - currentStatus.LastAttemptAt).TotalSeconds,
                    _options.MinRefreshInterval.TotalSeconds);

                if (!string.IsNullOrWhiteSpace(currentStatus.LatestSnapshotId))
                {
                    var cachedSnapshot = await _snapshotRepository
                        .GetByIdAsync(currentStatus.LatestSnapshotId, cancellationToken)
                        .ConfigureAwait(false);

                    if (MatchesIdentity(cachedSnapshot, identity))
                    {
                        return await ReturnCachedOrJoinAsync(identity, cachedSnapshot!, cancellationToken).ConfigureAwait(false);
                    }
                }

                var latestDbSnapshot = modelId is null
                    ? await _snapshotRepository.GetLatestAccountWideAsync(accountId, cancellationToken).ConfigureAwait(false)
                    : await _snapshotRepository.GetLatestForAccountAsync(accountId, modelId, cancellationToken).ConfigureAwait(false);

                // Repository null means latest across models; it must not substitute a
                // model-scoped snapshot for an explicitly account-wide refresh.
                if (MatchesIdentity(latestDbSnapshot, identity))
                {
                    return await ReturnCachedOrJoinAsync(identity, latestDbSnapshot!, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // 2. In-flight request deduplication
        Task<QuotaSnapshot> taskToAwait;
        TaskCompletionSource<QuotaSnapshot>? owner = null;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_inFlightRefreshes.TryGetValue(identity, out var existingTask))
            {
                taskToAwait = existingTask;
            }
            else
            {
                owner = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                taskToAwait = owner.Task;
                _inFlightRefreshes[identity] = taskToAwait;
            }
        }

        if (owner is not null)
        {
            // Publish ownership before invoking the adapter or event callbacks, including
            // synchronous implementations. Reentrant requests join the same refresh.
            _ = CompleteOwnedRefreshAsync(owner, providerProfileId, accountId, modelId, cancellationToken);
        }

        // Only a follower cancels its wait independently. The owner reports the durable result once
        // its write is acknowledged, even if its caller token changes after that commit.
        return owner is null
            ? await taskToAwait.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await taskToAwait.ConfigureAwait(false);
    }

    private Task<QuotaSnapshot>? GetInFlightRefresh(RefreshIdentity identity)
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _inFlightRefreshes.GetValueOrDefault(identity);
        }
    }

    private async Task<QuotaSnapshot> ReturnCachedOrJoinAsync(
        RefreshIdentity identity, QuotaSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Repository reads can yield while another caller registers an owner. Recheck at
        // the cache-return decision so that caller receives the owner's newer result.
        var pending = GetInFlightRefresh(identity);
        return pending is not null
            ? await pending.WaitAsync(cancellationToken).ConfigureAwait(false)
            : RedactSnapshot(snapshot);
    }

    private async Task CompleteOwnedRefreshAsync(
        TaskCompletionSource<QuotaSnapshot> owner,
        string providerProfileId,
        string accountId,
        string? modelId,
        CancellationToken cancellationToken)
    {
        var identity = new RefreshIdentity(providerProfileId, accountId, modelId);
        try
        {
            await ExecuteRefreshAccountCoreAsync(
                owner, providerProfileId, accountId, modelId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            RetireOwnedRefresh(identity, owner);
            owner.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            RetireOwnedRefresh(identity, owner);
            owner.TrySetException(exception);
        }
        finally
        {
            // Only the owner retires the entry, after notifications. A joiner's continuation
            // cannot remove ownership while a synchronous subscriber is still running.
            RetireOwnedRefresh(identity, owner);
        }
    }

    private void RetireOwnedRefresh(RefreshIdentity identity, TaskCompletionSource<QuotaSnapshot> owner)
    {
        lock (_syncRoot)
        {
            if (_inFlightRefreshes.TryGetValue(identity, out var registeredTask)
                && ReferenceEquals(registeredTask, owner.Task))
            {
                _inFlightRefreshes.TryRemove(identity, out _);
                ReleaseIdleLimitersUnderGate();
            }
        }
    }

    private async Task<QuotaSnapshot> ExecuteRefreshAccountCoreAsync(
        TaskCompletionSource<QuotaSnapshot> owner,
        string providerProfileId,
        string accountId,
        string? modelId,
        CancellationToken cancellationToken)
    {
        var attemptTime = _timeProvider.GetUtcNow();
        var identity = new RefreshIdentity(providerProfileId, accountId, modelId);
        _refreshCache.AddOrUpdate(identity,
            _ => new RefreshCacheState(attemptTime, null),
            (_, existing) => existing with { LastAttemptAt = attemptTime });

        UpdateStatusBeforeAttempt(accountId, providerProfileId, attemptTime);

        try
        {

            QuotaSnapshot snapshot;
            string? failureError = null;

            // 3. Per-provider rate limiting
            var providerLock = _providerRateLimiters.GetOrAdd(providerProfileId, _ => new SemaphoreSlim(1, 1));
            await providerLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (_providerLastCallTimes.TryGetValue(providerProfileId, out var lastCallTime))
                {
                    // Time spent waiting for another account's provider lease already counts
                    // towards the interval between actual calls.
                    var elapsedSinceLastCall = _timeProvider.GetUtcNow() - lastCallTime;
                    if (elapsedSinceLastCall < _options.ProviderRateLimitDelay)
                    {
                        var neededDelay = _options.ProviderRateLimitDelay - elapsedSinceLastCall;
                        _logger?.LogDebug(
                            "Rate-limiting provider {ProviderProfileId}: waiting {DelayMs}ms",
                            providerProfileId,
                            neededDelay.TotalMilliseconds);

                        await Task.Delay(neededDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                }

                _providerLastCallTimes[providerProfileId] = _timeProvider.GetUtcNow();

                try
                {
                    snapshot = await _adapter.FetchQuotaAsync(
                        providerProfileId,
                        accountId,
                        modelId,
                        cancellationToken).ConfigureAwait(false);
                    if (!MatchesIdentity(snapshot, identity))
                        throw new InvalidOperationException("Quota response identity does not match the requested provider/account/model.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failureError = SanitizeErrorMessage(ex.Message);
                    _logger?.LogWarning(
                        "Failed to fetch quota for account {AccountId} on provider {ProviderProfileId} with {ExceptionType}: {Error}",
                        accountId,
                        providerProfileId,
                        ex.GetType().Name,
                        failureError);

                    snapshot = new QuotaSnapshot(
                        $"snap-{Guid.NewGuid():N}",
                        accountId,
                        QuotaProvenance.Error,
                        capturedAt: attemptTime,
                        buckets: Array.Empty<QuotaBucket>(),
                        providerProfileId: providerProfileId,
                        modelId: modelId,
                        expiresAt: null,
                        rawRedactedPayloadJson: failureError,
                        errorMessage: failureError);
                }
            }
            finally
            {
                providerLock.Release();
            }

            var completionTime = _timeProvider.GetUtcNow();

            // Adapter-provided diagnostics are untrusted even when the field is named
            // RawRedactedPayloadJson. Scrub before persistence, status, callbacks and joins.
            snapshot = RedactSnapshot(snapshot);

            // 4. Update status & calculate next refresh with jitter/backoff
            var isSuccess = snapshot.Provenance != QuotaProvenance.Error;
            if (!isSuccess && string.IsNullOrWhiteSpace(failureError))
            {
                failureError = snapshot.RawRedactedPayloadJson ?? "Quota provider reported error.";
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _snapshotRepository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);

            // Successful completion of SaveAsync acknowledges the durable observation. Cancellation
            // can stop admission/read/write before it commits; it cannot erase its published result.

            UpdateStatusAfterAttempt(
                accountId,
                providerProfileId,
                snapshot,
                isSuccess,
                failureError,
                completionTime);
            _refreshCache[identity] = new RefreshCacheState(completionTime, snapshot.Id);

            // Commit the observable result before callbacks. Reentrant synchronous waiters
            // can read it, and a subscriber failure cannot invalidate persisted quota data.
            owner.TrySetResult(snapshot);
            NotifyRefreshed(new QuotaRefreshedEventArgs(
                accountId,
                providerProfileId,
                snapshot,
                isSuccess,
                failureError));

            return snapshot;
        }
        finally
        {
            // This covers cancellation while queued or rate limited, adapter cancellation,
            // and persistence failure. Do not invent a snapshot, success, failure or backoff.
            _statuses.AddOrUpdate(
                accountId,
                _ => new AccountQuotaRefreshStatus { AccountId = accountId, ProviderProfileId = providerProfileId },
                (_, existing) => existing with
                {
                    IsRefreshing = false,
                    // Cancellation/persistence failure has no published snapshot to supply a TTL.
                    // Keep polling eligible without inventing provider failure or success metadata.
                    NextScheduledRefreshAt = existing.NextScheduledRefreshAt > _timeProvider.GetUtcNow()
                        ? existing.NextScheduledRefreshAt
                        : _timeProvider.GetUtcNow() + RetryInterval()
                });
        }
    }

    private void NotifyRefreshed(QuotaRefreshedEventArgs args)
    {
        var handlers = QuotaRefreshed;
        if (handlers is null) { return; }
        foreach (EventHandler<QuotaRefreshedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception exception)
            {
                _logger?.LogWarning("Quota refresh subscriber failed with {ExceptionType}.", exception.GetType().Name);
            }
        }
    }

    public async Task RefreshAllEligibleAccountsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot) { ObjectDisposedException.ThrowIf(_isDisposed, this); }
        if (_accountRepository is null)
        {
            return;
        }

        var allAccounts = await _accountRepository.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        var eligibleAccounts = allAccounts
            .Where(a => a.IsEligibleForRouting(now))
            .ToList();

        foreach (var account in eligibleAccounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await RefreshAccountNowAsync(
                    account.ProviderProfileId,
                    account.Id,
                    modelId: null,
                    force: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError("Error during bulk refresh for account {AccountId} with {ExceptionType}: {Error}",
                    account.Id, ex.GetType().Name, SanitizeErrorMessage(ex.Message));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public AccountQuotaRefreshStatus? GetStatus(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        lock (_syncRoot)
        {
            return _statuses.TryGetValue(accountId, out var status) ? WithCurrentActivity(status) : null;
        }
    }

    public IReadOnlyList<AccountQuotaRefreshStatus> GetAllStatuses()
    {
        lock (_syncRoot)
        {
            return _statuses.Values.Select(WithCurrentActivity).ToList();
        }
    }

    private AccountQuotaRefreshStatus WithCurrentActivity(AccountQuotaRefreshStatus status) =>
        status with
        {
            // Model scopes finish independently; one completion must not hide another
            // account owner's pending result from the public aggregate status.
            IsRefreshing = _inFlightRefreshes.Any(entry =>
                entry.Key.AccountId == status.AccountId && !entry.Value.IsCompleted)
        };

    public void ScheduleNextRefresh(
        string accountId,
        string providerProfileId,
        DateTimeOffset? expiresAt = null,
        bool isFailure = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerProfileId);

        lock (_syncRoot) { ObjectDisposedException.ThrowIf(_isDisposed, this); }
        var now = _timeProvider.GetUtcNow();

        _statuses.AddOrUpdate(
            accountId,
            _ =>
            {
                var consecutiveFailures = isFailure ? 1 : 0;
                var backoff = isFailure ? ComputeBackoff(consecutiveFailures) : TimeSpan.Zero;
                var nextInterval = isFailure
                    ? ApplyJitter(backoff)
                    : CalculateSuccessInterval(expiresAt, now);

                return new AccountQuotaRefreshStatus
                {
                    AccountId = accountId,
                    ProviderProfileId = providerProfileId,
                    ConsecutiveFailures = consecutiveFailures,
                    CurrentBackoff = backoff,
                    NextScheduledRefreshAt = now + nextInterval,
                    IsRefreshing = false
                };
            },
            (_, existing) =>
            {
                var consecutiveFailures = isFailure ? existing.ConsecutiveFailures + 1 : 0;
                var backoff = isFailure ? ComputeBackoff(consecutiveFailures) : TimeSpan.Zero;
                var nextInterval = isFailure
                    ? ApplyJitter(backoff)
                    : CalculateSuccessInterval(expiresAt, now);

                return existing with
                {
                    ProviderProfileId = providerProfileId,
                    ConsecutiveFailures = consecutiveFailures,
                    CurrentBackoff = backoff,
                    NextScheduledRefreshAt = now + nextInterval,
                    IsRefreshing = false
                };
            });
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_backgroundEpoch is not null) { return Task.CompletedTask; }
            var epoch = new PollingEpoch();
            try
            {
                epoch.Timer = _timeProvider.CreateTimer(
                    OnBackgroundTimerTick, epoch, _options.BackgroundPollInterval, _options.BackgroundPollInterval);
                _backgroundEpoch = epoch;
            }
            catch
            {
                _backgroundEpoch = null;
                epoch.Source.Dispose();
                throw;
            }
        }
        _logger?.LogInformation("QuotaRefreshScheduler background timer started.");
        return Task.CompletedTask;
    }

    private static bool MatchesIdentity(QuotaSnapshot? snapshot, RefreshIdentity identity) =>
        snapshot is not null
        && string.Equals(snapshot.AccountId, identity.AccountId, StringComparison.Ordinal)
        && string.Equals(snapshot.ProviderProfileId, identity.ProviderProfileId, StringComparison.Ordinal)
        && string.Equals(snapshot.ModelId, identity.ModelId, StringComparison.Ordinal);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Once requested, cleanup is not abandoned because its caller is cancelled.
        PollingEpoch? epoch;
        Task[] pending;
        lock (_syncRoot)
        {
            epoch = _backgroundEpoch;
            _backgroundEpoch = null;
            if (epoch is not null) { _stoppingEpochs.Add(epoch.Stopped.Task); }
            pending = _stoppingEpochs.ToArray();
        }
        if (epoch is not null) { _ = CompleteStopAsync(epoch); }
        return pending.Length == 0 ? Task.CompletedTask : Task.WhenAll(pending);
    }

    private async Task CompleteStopAsync(PollingEpoch epoch)
    {
        Exception? cleanupFailure = null;
        try
        {
            try { await epoch.Source.CancelAsync().ConfigureAwait(false); }
            catch (AggregateException exception)
            {
                // The token is cancelled even when a foreign callback throws. Continue owning
                // timer/source cleanup and avoid logging callback messages or secret values.
                _logger?.LogWarning("Quota polling cancellation callback failed with {ExceptionType}.", exception.GetType().Name);
            }
            finally
            {
                if (epoch.Timer is not null) { await epoch.Timer.DisposeAsync().ConfigureAwait(false); }
            }
        }
        catch (Exception exception) { cleanupFailure = exception; }
        finally
        {
            epoch.Source.Dispose();
            if (cleanupFailure is null)
            {
                try { _logger?.LogInformation("QuotaRefreshScheduler background timer stopped."); }
                catch (Exception exception) { cleanupFailure = exception; }
            }
            lock (_syncRoot) { _stoppingEpochs.Remove(epoch.Stopped.Task); }
            if (cleanupFailure is null) { epoch.Stopped.TrySetResult(); }
            else { epoch.Stopped.TrySetException(cleanupFailure); }
        }
    }

    public void Dispose()
    {
        MarkDisposed();
        try { StopAsync().GetAwaiter().GetResult(); }
        finally { lock (_syncRoot) { ReleaseIdleLimitersUnderGate(); } }
    }

    public async ValueTask DisposeAsync()
    {
        MarkDisposed();
        try { await StopAsync().ConfigureAwait(false); }
        finally { lock (_syncRoot) { ReleaseIdleLimitersUnderGate(); } }
    }

    private void MarkDisposed()
    {
        lock (_syncRoot) { _isDisposed = true; }
    }

    private void ReleaseIdleLimitersUnderGate()
    {
        // Existing manual requests retain their caller-owned cancellation contract. They may
        // finish after disposal; the last owner retires the limiter only after its lease release.
        if (!_isDisposed || !_inFlightRefreshes.IsEmpty) { return; }
        foreach (var limiter in _providerRateLimiters.Values) { limiter.Dispose(); }
        _providerRateLimiters.Clear();
    }

    private TimeSpan RetryInterval()
    {
        if (_options.MinRefreshInterval > TimeSpan.Zero) { return _options.MinRefreshInterval; }
        return _options.BackgroundPollInterval > TimeSpan.Zero
            ? _options.BackgroundPollInterval : TimeSpan.FromSeconds(1);
    }

    private void OnBackgroundTimerTick(object? state)
    {
        if (state is not PollingEpoch epoch) { return; }
        lock (_syncRoot)
        {
            if (_isDisposed || !ReferenceEquals(_backgroundEpoch, epoch)) { return; }
        }
        // Capture the token in the epoch constructor, never read Source.Token after disposal.
        var token = epoch.Token;
        if (token.IsCancellationRequested) { return; }
        var now = _timeProvider.GetUtcNow();
        var dueStatuses = _statuses.Values
            .Where(s => s.NextScheduledRefreshAt.HasValue && s.NextScheduledRefreshAt.Value <= now && !s.IsRefreshing)
            .ToList();
        foreach (var due in dueStatuses)
        {
            if (token.IsCancellationRequested) { break; }
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshAccountNowAsync(due.ProviderProfileId, due.AccountId,
                        modelId: null, force: true, cancellationToken: token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (ObjectDisposedException) { }
                catch (Exception exception)
                {
                    _logger?.LogError("Background quota refresh failed for account {AccountId} on provider {ProviderProfileId} with {ExceptionType}.",
                        due.AccountId, due.ProviderProfileId, exception.GetType().Name);
                }
            }, token);
        }
    }

    private sealed class PollingEpoch
    {
        public CancellationTokenSource Source { get; } = new();
        public CancellationToken Token { get; }
        public ITimer? Timer { get; set; }
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PollingEpoch() { Token = Source.Token; }
    }


    private void UpdateStatusBeforeAttempt(string accountId, string providerProfileId, DateTimeOffset attemptTime)
    {
        _statuses.AddOrUpdate(
            accountId,
            _ => new AccountQuotaRefreshStatus
            {
                AccountId = accountId,
                ProviderProfileId = providerProfileId,
                LastAttemptAt = attemptTime,
                IsRefreshing = true
            },
            (_, existing) => existing with
            {
                ProviderProfileId = providerProfileId,
                LastAttemptAt = attemptTime,
                IsRefreshing = true
            });
    }

    private void UpdateStatusAfterAttempt(
        string accountId,
        string providerProfileId,
        QuotaSnapshot snapshot,
        bool isSuccess,
        string? failureError,
        DateTimeOffset completionTime)
    {
        _statuses.AddOrUpdate(
            accountId,
            _ =>
            {
                var failures = isSuccess ? 0 : 1;
                var backoff = isSuccess ? TimeSpan.Zero : ComputeBackoff(failures);
                var nextInterval = isSuccess
                    ? CalculateSuccessInterval(snapshot.ExpiresAt, completionTime)
                    : ApplyJitter(backoff);

                return new AccountQuotaRefreshStatus
                {
                    AccountId = accountId,
                    ProviderProfileId = providerProfileId,
                    LastAttemptAt = completionTime,
                    LastSuccessfulRefreshAt = isSuccess ? completionTime : null,
                    NextScheduledRefreshAt = completionTime + nextInterval,
                    ConsecutiveFailures = failures,
                    CurrentBackoff = backoff,
                    LastError = failureError,
                    LatestSnapshotId = snapshot.Id,
                    IsRefreshing = false
                };
            },
            (_, existing) =>
            {
                var failures = isSuccess ? 0 : existing.ConsecutiveFailures + 1;
                var backoff = isSuccess ? TimeSpan.Zero : ComputeBackoff(failures);
                var nextInterval = isSuccess
                    ? CalculateSuccessInterval(snapshot.ExpiresAt, completionTime)
                    : ApplyJitter(backoff);

                return existing with
                {
                    ProviderProfileId = providerProfileId,
                    LastAttemptAt = completionTime,
                    LastSuccessfulRefreshAt = isSuccess ? completionTime : existing.LastSuccessfulRefreshAt,
                    NextScheduledRefreshAt = completionTime + nextInterval,
                    ConsecutiveFailures = failures,
                    CurrentBackoff = backoff,
                    LastError = failureError,
                    LatestSnapshotId = snapshot.Id,
                    IsRefreshing = false
                };
            });
    }

    public TimeSpan ComputeBackoff(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Max(0, consecutiveFailures - 1);
        var multiplier = Math.Pow(_options.BackoffMultiplier, exponent);
        var backoffMs = _options.InitialBackoff.TotalMilliseconds * multiplier;
        var clampedMs = Math.Min(_options.MaxBackoff.TotalMilliseconds, backoffMs);

        return TimeSpan.FromMilliseconds(clampedMs);
    }

    public TimeSpan ApplyJitter(TimeSpan baseInterval)
    {
        if (baseInterval <= TimeSpan.Zero || _options.JitterRatio <= 0.0)
        {
            return baseInterval;
        }

        var ratio = Math.Clamp(_options.JitterRatio, 0.0, 1.0);
        double sample;
        lock (_random)
        {
            sample = _random.NextDouble();
        }
        var factor = 1.0 + (sample * 2.0 - 1.0) * ratio;
        var jitteredTicks = baseInterval.Ticks * factor;
        return jitteredTicks >= TimeSpan.MaxValue.Ticks
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks((long)Math.Max(0, jitteredTicks));
    }

    private TimeSpan CalculateSuccessInterval(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        TimeSpan baseInterval;
        if (expiresAt.HasValue && expiresAt.Value > now)
        {
            baseInterval = expiresAt.Value - now;
        }
        else
        {
            baseInterval = _options.DefaultTtl;
        }

        var intervalWithJitter = ApplyJitter(baseInterval);

        return intervalWithJitter < _options.MinRefreshInterval
            ? _options.MinRefreshInterval
            : intervalWithJitter;
    }

    private static string SanitizeErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unknown error during quota refresh.";
        }

        var cleaned = RedactDiagnostic(message);
        if (cleaned.Length > 500)
        {
            cleaned = cleaned[..500] + "...";
        }

        return cleaned;
    }

    private static QuotaSnapshot RedactSnapshot(QuotaSnapshot snapshot)
    {
        var payload = snapshot.RawRedactedPayloadJson is null ? null
            : RedactDiagnostic(snapshot.RawRedactedPayloadJson);
        var error = snapshot.ErrorMessage is null ? null
            : RedactDiagnostic(snapshot.ErrorMessage);
        if (string.Equals(payload, snapshot.RawRedactedPayloadJson, StringComparison.Ordinal)
            && string.Equals(error, snapshot.ErrorMessage, StringComparison.Ordinal))
        {
            return snapshot;
        }
        return new QuotaSnapshot(snapshot.Id, snapshot.AccountId, snapshot.Provenance, snapshot.CapturedAt,
            snapshot.Buckets, snapshot.ProviderProfileId, snapshot.ModelId, snapshot.ExpiresAt, payload, error);
    }

    private static string RedactDiagnostic(string text) => QuotaDiagnosticRedactor.Redact(text);
}
