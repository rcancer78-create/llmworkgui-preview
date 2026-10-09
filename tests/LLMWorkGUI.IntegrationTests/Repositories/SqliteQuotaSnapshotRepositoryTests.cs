using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Repositories;

public sealed class SqliteQuotaSnapshotRepositoryTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task QuotaSnapshotRepository_PerformsFullCrudLifecycle()
    {
        await _database.InitializeAsync();

        var providerRepo = new SqliteProviderProfileRepository(_database.Factory);
        var accountRepo = new SqliteAccountRepository(_database.Factory);
        var snapshotRepo = new SqliteQuotaSnapshotRepository(_database.Factory);

        // 1. Setup Provider & Account
        var provider = new ProviderProfile("prov-1", "Provider 1", BackendType.OpenCode, null, null, DataClassification.PrivateSource, true);
        await providerRepo.UpsertAsync(provider);

        var account = new Account("acc-1", "prov-1", "Dev Account", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        await accountRepo.SaveAsync(account);

        await _database.InsertModelAsync("gpt-4o", providerProfileId: "prov-1", displayName: "GPT-4o");

        var now = DateTimeOffset.UtcNow;
        var bucket1 = new QuotaBucket(
            "requests_per_day",
            QuotaLimitUnit.Requests,
            QuotaLimitWindow.PerDay,
            limitValue: 5000,
            usedValue: 1200,
            remainingValue: 3800,
            resetAt: now.AddHours(8),
            hardReserve: 500,
            confidence: QuotaConfidence.Exact);

        var bucket2 = new QuotaBucket(
            "tokens_per_minute",
            QuotaLimitUnit.Tokens,
            QuotaLimitWindow.PerMinute,
            limitValue: 100000,
            usedValue: 25000,
            remainingValue: 75000,
            hardReserve: 10000,
            confidence: QuotaConfidence.High);

        var snapshot1 = new QuotaSnapshot(
            "snap-101",
            "acc-1",
            QuotaProvenance.ExactProviderReported,
            capturedAt: now.AddMinutes(-10),
            buckets: new[] { bucket1, bucket2 },
            providerProfileId: "prov-1",
            modelId: "gpt-4o",
            expiresAt: now.AddMinutes(-5));

        var snapshot2 = new QuotaSnapshot(
            "snap-102",
            "acc-1",
            QuotaProvenance.ExactProviderReported,
            capturedAt: now.AddMinutes(-2),
            buckets: new[] { bucket1 },
            providerProfileId: "prov-1",
            modelId: "gpt-4o",
            expiresAt: now.AddMinutes(3));

        // 2. Save both snapshots
        await snapshotRepo.SaveAsync(snapshot1);
        await snapshotRepo.SaveAsync(snapshot2);

        // 3. GetById
        var fetched1 = await snapshotRepo.GetByIdAsync("snap-101");
        Assert.NotNull(fetched1);
        Assert.Equal("snap-101", fetched1!.Id);
        Assert.Equal("acc-1", fetched1.AccountId);
        Assert.Equal(QuotaProvenance.ExactProviderReported, fetched1.Provenance);
        Assert.Equal(2, fetched1.Buckets.Count);
        Assert.Equal("requests_per_day", fetched1.Buckets[0].BucketName);
        Assert.Equal(5000, fetched1.Buckets[0].LimitValue);
        Assert.Equal(3800, fetched1.Buckets[0].RemainingValue);
        Assert.Equal("tokens_per_minute", fetched1.Buckets[1].BucketName);

        // 4. GetLatestForAccountAsync
        var latest = await snapshotRepo.GetLatestForAccountAsync("acc-1");
        Assert.NotNull(latest);
        Assert.Equal("snap-102", latest!.Id); // snap-102 is newer (-2 min vs -10 min)

        // 5. ListLatestByAccountIdAsync
        var accountSnapshots = await snapshotRepo.ListLatestByAccountIdAsync("acc-1");
        Assert.Equal(2, accountSnapshots.Count);
        Assert.Equal("snap-102", accountSnapshots[0].Id);
        Assert.Equal("snap-101", accountSnapshots[1].Id);

        // 6. ListAllLatestAsync
        var allLatest = await snapshotRepo.ListAllLatestAsync();
        Assert.Single(allLatest);
        Assert.Equal("snap-102", allLatest[0].Id);

        // 7. DeleteOlderThanAsync
        var deletedCount = await snapshotRepo.DeleteOlderThanAsync(now.AddMinutes(-5));
        Assert.Equal(1, deletedCount); // snap-101 was captured at -10 min

        var remainingAfterDelete = await snapshotRepo.ListLatestByAccountIdAsync("acc-1");
        Assert.Single(remainingAfterDelete);
        Assert.Equal("snap-102", remainingAfterDelete[0].Id);
    }

    [Fact]
    public async Task QuotaSnapshotRepository_CascadeDeletes_WhenAccountDeleted()
    {
        await _database.InitializeAsync();

        var providerRepo = new SqliteProviderProfileRepository(_database.Factory);
        var accountRepo = new SqliteAccountRepository(_database.Factory);
        var snapshotRepo = new SqliteQuotaSnapshotRepository(_database.Factory);

        var provider = new ProviderProfile("prov-c", "Provider Cascade", BackendType.OpenCode, null, null, DataClassification.PrivateSource, true);
        await providerRepo.UpsertAsync(provider);

        var account = new Account("acc-c", "prov-c", "Account Cascade", null, AuthState.Valid, 0, true, HealthState.Healthy, null, null, 1, null);
        await accountRepo.SaveAsync(account);

        var snapshot = new QuotaSnapshot(
            "snap-c",
            "acc-c",
            QuotaProvenance.ExactProviderReported,
            DateTimeOffset.UtcNow);

        await snapshotRepo.SaveAsync(snapshot);
        Assert.NotNull(await snapshotRepo.GetByIdAsync("snap-c"));

        // Delete Account
        await accountRepo.DeleteAsync("acc-c");

        // Verify QuotaSnapshot is cascade deleted
        var afterDelete = await snapshotRepo.GetByIdAsync("snap-c");
        Assert.Null(afterDelete);
    }
}
