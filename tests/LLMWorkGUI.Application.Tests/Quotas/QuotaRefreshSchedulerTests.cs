using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Quotas;

public sealed class QuotaRefreshSchedulerTests
{
    private readonly TestTimeProvider _timeProvider = new();
    private readonly InMemoryQuotaSnapshotRepository _snapshotRepo = new();
    private readonly InMemoryAccountRepository _accountRepo = new();

    [Fact]
    public void ApplyJitter_WithZeroJitterRatio_ReturnsExactInterval()
    {
        var options = new QuotaSchedulerOptions { JitterRatio = 0.0 };
        var scheduler = new QuotaRefreshScheduler(
            new MockQuotaSourceAdapter(),
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider);

        var baseInterval = TimeSpan.FromMinutes(5);
        var jittered = scheduler.ApplyJitter(baseInterval);

        Assert.Equal(baseInterval, jittered);
    }

    [Fact]
    public void ApplyJitter_WithDefaultJitterRatio_ProducesValuesWithinBounds()
    {
        var options = new QuotaSchedulerOptions { JitterRatio = 0.1 };
        var scheduler = new QuotaRefreshScheduler(
            new MockQuotaSourceAdapter(),
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider,
            randomSeed: 42);

        var baseInterval = TimeSpan.FromSeconds(100);
        for (int i = 0; i < 20; i++)
        {
            var jittered = scheduler.ApplyJitter(baseInterval);
            Assert.True(jittered >= TimeSpan.FromSeconds(90), $"Jittered interval {jittered} is below min 90s");
            Assert.True(jittered <= TimeSpan.FromSeconds(110), $"Jittered interval {jittered} is above max 110s");
        }
    }

    [Fact]
    public void ComputeBackoff_CalculatesExponentialIntervalsClampedToMax()
    {
        var options = new QuotaSchedulerOptions
        {
            InitialBackoff = TimeSpan.FromSeconds(30),
            BackoffMultiplier = 2.0,
            MaxBackoff = TimeSpan.FromMinutes(15)
        };
        var scheduler = new QuotaRefreshScheduler(
            new MockQuotaSourceAdapter(),
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider);

        Assert.Equal(TimeSpan.Zero, scheduler.ComputeBackoff(0));
        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.ComputeBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(60), scheduler.ComputeBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(120), scheduler.ComputeBackoff(3));
        Assert.Equal(TimeSpan.FromSeconds(240), scheduler.ComputeBackoff(4));
        Assert.Equal(TimeSpan.FromMinutes(15), scheduler.ComputeBackoff(10));
    }

    [Fact]
    public async Task RefreshAccountNowAsync_SuccessfulFetch_UpdatesStatusAndResetsBackoff()
    {
        var options = new QuotaSchedulerOptions
        {
            DefaultTtl = TimeSpan.FromMinutes(5),
            JitterRatio = 0.0
        };
        var adapter = new MockQuotaSourceAdapter();
        adapter.SetNextSnapshot(new QuotaSnapshot(
            "snap-1",
            "acc-1",
            QuotaProvenance.ExactProviderReported,
            capturedAt: _timeProvider.GetUtcNow(),
            buckets: new[]
            {
                new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900)
            },
            providerProfileId: "prov-1",
            expiresAt: _timeProvider.GetUtcNow().AddMinutes(10)));

        var scheduler = new QuotaRefreshScheduler(
            adapter,
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider);

        QuotaRefreshedEventArgs? eventArgs = null;
        scheduler.QuotaRefreshed += (_, e) => eventArgs = e;

        var snapshot = await scheduler.RefreshAccountNowAsync("prov-1", "acc-1");

        Assert.Equal("snap-1", snapshot.Id);
        Assert.NotNull(eventArgs);
        Assert.True(eventArgs!.IsSuccess);
        Assert.Equal("acc-1", eventArgs.AccountId);

        var status = scheduler.GetStatus("acc-1");
        Assert.NotNull(status);
        Assert.Equal(0, status!.ConsecutiveFailures);
        Assert.Null(status.LastError);
        Assert.Equal("snap-1", status.LatestSnapshotId);
        Assert.Equal(_timeProvider.GetUtcNow(), status.LastSuccessfulRefreshAt);
        Assert.Equal(_timeProvider.GetUtcNow().AddMinutes(10), status.NextScheduledRefreshAt);

        // Verified snapshot saved to repo
        var saved = await _snapshotRepo.GetByIdAsync("snap-1");
        Assert.NotNull(saved);
    }

    [Fact]
    public async Task RefreshAccountNowAsync_FailureFetch_AppliesExponentialBackoffAndIncrementsFailures()
    {
        var options = new QuotaSchedulerOptions
        {
            InitialBackoff = TimeSpan.FromSeconds(30),
            BackoffMultiplier = 2.0,
            JitterRatio = 0.0
        };
        var adapter = new MockQuotaSourceAdapter();
        adapter.SimulateFailure(new HttpRequestException("Connection refused to provider baseUrl"));

        var scheduler = new QuotaRefreshScheduler(
            adapter,
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider);

        // 1st failure
        var snap1 = await scheduler.RefreshAccountNowAsync("prov-1", "acc-1");
        Assert.Equal(QuotaProvenance.Error, snap1.Provenance);

        var status1 = scheduler.GetStatus("acc-1");
        Assert.NotNull(status1);
        Assert.Equal(1, status1!.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(30), status1.CurrentBackoff);
        Assert.Contains("Connection refused", status1.LastError);
        Assert.Equal(_timeProvider.GetUtcNow().AddSeconds(30), status1.NextScheduledRefreshAt);

        // Advance virtual time and trigger 2nd failure
        _timeProvider.Advance(TimeSpan.FromSeconds(35));
        var snap2 = await scheduler.RefreshAccountNowAsync("prov-1", "acc-1", force: true);
        Assert.Equal(QuotaProvenance.Error, snap2.Provenance);

        var status2 = scheduler.GetStatus("acc-1");
        Assert.NotNull(status2);
        Assert.Equal(2, status2!.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(60), status2.CurrentBackoff);
        Assert.Equal(_timeProvider.GetUtcNow().AddSeconds(60), status2.NextScheduledRefreshAt);
    }

    [Fact]
    public async Task RefreshAccountNowAsync_RapidRefreshGuard_ReturnsCachedSnapshotWhenCalledWithinMinInterval()
    {
        var options = new QuotaSchedulerOptions
        {
            MinRefreshInterval = TimeSpan.FromSeconds(10),
            DefaultTtl = TimeSpan.FromMinutes(5),
            JitterRatio = 0.0
        };
        var adapter = new MockQuotaSourceAdapter();
        var scheduler = new QuotaRefreshScheduler(
            adapter,
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider);

        // First call
        var first = await scheduler.RefreshAccountNowAsync("prov-1", "acc-1");
        Assert.Equal(1, adapter.FetchCallCount);

        // Advance 3 seconds (< 10s min interval)
        _timeProvider.Advance(TimeSpan.FromSeconds(3));

        // Second call with force = false
        var second = await scheduler.RefreshAccountNowAsync("prov-1", "acc-1", force: false);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, adapter.FetchCallCount); // Did NOT hit adapter!
    }

    [Fact]
    public async Task RefreshAccountNowAsync_ForceFlag_BypassesRapidRefreshGuard()
    {
        var options = new QuotaSchedulerOptions
        {
            MinRefreshInterval = TimeSpan.FromSeconds(10),
            DefaultTtl = TimeSpan.FromMinutes(5),
            JitterRatio = 0.0
        };
        var adapter = new MockQuotaSourceAdapter();
        var scheduler = new QuotaRefreshScheduler(
            adapter,
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider);

        // First call
        await scheduler.RefreshAccountNowAsync("prov-1", "acc-1");
        Assert.Equal(1, adapter.FetchCallCount);

        // Advance 2 seconds (< 10s)
        _timeProvider.Advance(TimeSpan.FromSeconds(2));

        // Second call with force = true
        await scheduler.RefreshAccountNowAsync("prov-1", "acc-1", force: true);
        Assert.Equal(2, adapter.FetchCallCount); // Bypassed guard!
    }

    [Fact]
    public async Task RefreshAccountNowAsync_ConcurrentCallsSameAccount_DeduplicatesInFlight()
    {
        var options = new QuotaSchedulerOptions { JitterRatio = 0.0 };
        var adapter = new MockQuotaSourceAdapter
        {
            FetchDelay = TimeSpan.FromMilliseconds(50)
        };

        var scheduler = new QuotaRefreshScheduler(
            adapter,
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider);

        // Launch 5 concurrent refresh tasks for same account
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => scheduler.RefreshAccountNowAsync("prov-1", "acc-1", force: true))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // All got the same snapshot instance and adapter was only called once
        Assert.Equal(1, adapter.FetchCallCount);
        Assert.All(results, r => Assert.Equal(results[0].Id, r.Id));
    }

    [Fact]
    public async Task RefreshAllEligibleAccountsAsync_RefreshesOnlyEligibleAccounts()
    {
        var options = new QuotaSchedulerOptions { JitterRatio = 0.0 };
        var adapter = new MockQuotaSourceAdapter();

        var eligibleAcc = new Account("acc-e", "prov-1", "Eligible", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 1, null);
        var disabledAcc = new Account("acc-d", "prov-1", "Disabled", null, AuthState.Valid, 10, false, HealthState.Healthy, null, null, 1, null);
        var invalidAuthAcc = new Account("acc-i", "prov-1", "Invalid Auth", null, AuthState.Invalid, 10, true, HealthState.Healthy, null, null, 1, null);

        await _accountRepo.SaveAsync(eligibleAcc);
        await _accountRepo.SaveAsync(disabledAcc);
        await _accountRepo.SaveAsync(invalidAuthAcc);

        var scheduler = new QuotaRefreshScheduler(
            adapter,
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider);

        await scheduler.RefreshAllEligibleAccountsAsync();

        Assert.Equal(1, adapter.FetchCallCount);
        var allStatuses = scheduler.GetAllStatuses();
        Assert.Single(allStatuses);
        Assert.Equal("acc-e", allStatuses[0].AccountId);
    }

    [Fact]
    public void ScheduleNextRefresh_RegistersExplicitSchedule()
    {
        var options = new QuotaSchedulerOptions { JitterRatio = 0.0, DefaultTtl = TimeSpan.FromMinutes(5) };
        var scheduler = new QuotaRefreshScheduler(
            new MockQuotaSourceAdapter(),
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider);

        var expires = _timeProvider.GetUtcNow().AddMinutes(8);
        scheduler.ScheduleNextRefresh("acc-manual", "prov-1", expiresAt: expires);

        var status = scheduler.GetStatus("acc-manual");
        Assert.NotNull(status);
        Assert.Equal("acc-manual", status!.AccountId);
        Assert.Equal("prov-1", status.ProviderProfileId);
        Assert.Equal(expires, status.NextScheduledRefreshAt);
    }

    [Fact]
    public async Task RefreshAccountNowAsync_SanitizesSensitiveDataInErrors()
    {
        var options = new QuotaSchedulerOptions { JitterRatio = 0.0 };
        var adapter = new MockQuotaSourceAdapter();
        adapter.SimulateFailure(new InvalidOperationException("Failed auth with Bearer sk-supersecrettoken123456 and api_key=topsecretkey!"));

        var scheduler = new QuotaRefreshScheduler(
            adapter,
            _snapshotRepo,
            _accountRepo,
            Options.Create(options),
            _timeProvider);

        var snap = await scheduler.RefreshAccountNowAsync("prov-1", "acc-secret");

        Assert.Equal(QuotaProvenance.Error, snap.Provenance);
        Assert.NotNull(snap.ErrorMessage);
        Assert.DoesNotContain("sk-supersecrettoken123456", snap.ErrorMessage);
        Assert.DoesNotContain("topsecretkey!", snap.ErrorMessage);
        Assert.Contains("***REDACTED***", snap.ErrorMessage);

        var status = scheduler.GetStatus("acc-secret");
        Assert.NotNull(status);
        Assert.DoesNotContain("sk-supersecrettoken123456", status!.LastError);
        Assert.Contains("***REDACTED***", status.LastError);
    }
}

internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset? initialTime = null)
    {
        _utcNow = initialTime ?? new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration)
    {
        _utcNow = _utcNow.Add(duration);
    }
}

internal sealed class InMemoryQuotaSnapshotRepository : IQuotaSnapshotRepository
{
    private readonly List<QuotaSnapshot> _snapshots = new();

    public Task<QuotaSnapshot?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var found = _snapshots.FirstOrDefault(s => s.Id == id);
        return Task.FromResult(found);
    }

    public Task<QuotaSnapshot?> GetLatestForAccountAsync(string accountId, string? modelId = null, CancellationToken cancellationToken = default)
    {
        var found = _snapshots
            .Where(s => s.AccountId == accountId && (modelId == null || s.ModelId == modelId))
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefault();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<QuotaSnapshot>> ListLatestByAccountIdAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var list = _snapshots
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.CapturedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<QuotaSnapshot>>(list);
    }

    public Task<IReadOnlyList<QuotaSnapshot>> ListAllLatestAsync(CancellationToken cancellationToken = default)
    {
        var list = _snapshots
            .GroupBy(s => s.AccountId)
            .Select(g => g.OrderByDescending(s => s.CapturedAt).First())
            .ToList();
        return Task.FromResult<IReadOnlyList<QuotaSnapshot>>(list);
    }

    public Task SaveAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        _snapshots.RemoveAll(s => s.Id == snapshot.Id);
        _snapshots.Add(snapshot);
        return Task.CompletedTask;
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var count = _snapshots.RemoveAll(s => s.CapturedAt < cutoff);
        return Task.FromResult(count);
    }
}

internal sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly List<Account> _accounts = new();

    public Task<Account?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_accounts.FirstOrDefault(a => a.Id == id));
    }

    public Task<IReadOnlyList<Account>> ListByProviderProfileIdAsync(string providerProfileId, CancellationToken cancellationToken = default)
    {
        var list = _accounts.Where(a => a.ProviderProfileId == providerProfileId).ToList();
        return Task.FromResult<IReadOnlyList<Account>>(list);
    }

    public Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<Account>>(_accounts.ToList());
    }

    public Task SaveAsync(Account account, CancellationToken cancellationToken = default)
    {
        _accounts.RemoveAll(a => a.Id == account.Id);
        _accounts.Add(account);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _accounts.RemoveAll(a => a.Id == id);
        return Task.CompletedTask;
    }

    public Task UpdateAuthStateAsync(string id, AuthState authState, CancellationToken cancellationToken = default)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == id);
        if (acc != null)
        {
            _accounts.Remove(acc);
            _accounts.Add(acc.WithAuthState(authState));
        }
        return Task.CompletedTask;
    }

    public Task UpdateCooldownAsync(string id, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken = default)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == id);
        if (acc != null)
        {
            _accounts.Remove(acc);
            _accounts.Add(acc.WithCooldown(cooldownUntil));
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretReferenceAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var acc = _accounts.FirstOrDefault(a => a.Id == accountId);
        return Task.FromResult(acc?.SecretReference);
    }
}
