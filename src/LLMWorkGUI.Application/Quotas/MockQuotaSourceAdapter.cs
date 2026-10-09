using System.Collections.Concurrent;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Quotas;

/// <summary>
/// Deterministic test double for testing quota fetching and multi-account routing scenarios.
/// </summary>
public sealed class MockQuotaSourceAdapter : IQuotaSourceAdapter
{
    private readonly ConcurrentDictionary<string, QuotaSnapshot> _snapshots = new();
    private Exception? _simulatedFailure;
    private QuotaSnapshot? _nextSnapshot;
    private int _fetchCallCount;

    public string SourceKind => "mock-quota-source";

    public int FetchCallCount => Volatile.Read(ref _fetchCallCount);

    public TimeSpan FetchDelay { get; set; } = TimeSpan.Zero;

    public void SetSnapshot(string accountId, QuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshots[accountId] = snapshot;
    }

    public void SetNextSnapshot(QuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _nextSnapshot = snapshot;
    }

    public void SimulateFailure(Exception exception)
    {
        _simulatedFailure = exception;
    }

    public async Task<QuotaSnapshot> FetchQuotaAsync(
        string providerProfileId,
        string accountId,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerProfileId);
        ArgumentNullException.ThrowIfNull(accountId);

        Interlocked.Increment(ref _fetchCallCount);

        if (FetchDelay > TimeSpan.Zero)
        {
            await Task.Delay(FetchDelay, cancellationToken).ConfigureAwait(false);
        }

        if (_simulatedFailure is not null)
        {
            throw _simulatedFailure;
        }

        if (_nextSnapshot is not null)
        {
            var next = _nextSnapshot;
            _nextSnapshot = null;
            return next;
        }

        if (_snapshots.TryGetValue(accountId, out var snapshot))
        {
            return snapshot;
        }

        // Default behavior: create a deterministic snapshot based on accountId
        var now = DateTimeOffset.UtcNow;

        if (accountId.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return QuotaSnapshot.CreateUnsupported(accountId, providerProfileId, modelId);
        }

        if (accountId.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return QuotaSnapshot.CreateError(accountId, "Mock 429 Rate limit exceeded", providerProfileId, modelId);
        }

        if (accountId.Contains("reserve-violation", StringComparison.OrdinalIgnoreCase))
        {
            var bucket = new QuotaBucket(
                "requests_daily",
                QuotaLimitUnit.Requests,
                QuotaLimitWindow.PerDay,
                limitValue: 1000,
                usedValue: 950,
                remainingValue: 50,
                resetAt: now.AddHours(3),
                hardReserve: 100);

            return new QuotaSnapshot(
                $"snap-{Guid.NewGuid():N}",
                accountId,
                QuotaProvenance.ExactProviderReported,
                now,
                new[] { bucket },
                providerProfileId,
                modelId);
        }

        // Default valid snapshot: 80% remaining
        var defaultBucket = new QuotaBucket(
            "requests_daily",
            QuotaLimitUnit.Requests,
            QuotaLimitWindow.PerDay,
            limitValue: 1000,
            usedValue: 200,
            remainingValue: 800,
            resetAt: now.AddHours(12),
            hardReserve: 50);

        return new QuotaSnapshot(
            $"snap-{Guid.NewGuid():N}",
            accountId,
            QuotaProvenance.ExactProviderReported,
            now,
            new[] { defaultBucket },
            providerProfileId,
            modelId);
    }
}
