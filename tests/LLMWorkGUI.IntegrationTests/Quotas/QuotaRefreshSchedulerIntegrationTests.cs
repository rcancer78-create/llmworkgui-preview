using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Quotas;

public sealed class QuotaRefreshSchedulerIntegrationTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task QuotaRefreshScheduler_SavesRealSnapshotsToSqliteDatabase()
    {
        await _database.InitializeAsync();

        var providerRepo = new SqliteProviderProfileRepository(_database.Factory);
        var accountRepo = new SqliteAccountRepository(_database.Factory);
        var snapshotRepo = new SqliteQuotaSnapshotRepository(_database.Factory);

        // 1. Setup Provider & Account in real SQLite
        var provider = new ProviderProfile("prov-sqlite", "SQLite Provider", BackendType.OpenCode, null, null, DataClassification.PrivateSource, true);
        await providerRepo.UpsertAsync(provider);

        var account = new Account("acc-sqlite", "prov-sqlite", "Real Account", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        await accountRepo.SaveAsync(account);

        var adapter = new MockQuotaSourceAdapter();
        var now = DateTimeOffset.UtcNow;
        var bucket = new QuotaBucket("requests_daily", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 5000, 1000, 4000, now.AddHours(12), 200);

        adapter.SetNextSnapshot(new QuotaSnapshot(
            "snap-sql-1",
            "acc-sqlite",
            QuotaProvenance.ExactProviderReported,
            now,
            new[] { bucket },
            "prov-sqlite",
            expiresAt: now.AddMinutes(5)));

        var options = new QuotaSchedulerOptions
        {
            DefaultTtl = TimeSpan.FromMinutes(5),
            JitterRatio = 0.0
        };

        var scheduler = new QuotaRefreshScheduler(
            adapter,
            snapshotRepo,
            accountRepo,
            Options.Create(options),
            TimeProvider.System);

        // 2. Perform refresh
        var snapshot = await scheduler.RefreshAccountNowAsync("prov-sqlite", "acc-sqlite");
        Assert.Equal("snap-sql-1", snapshot.Id);

        // 3. Verify real persistence in SQLite table
        var dbSnapshot = await snapshotRepo.GetByIdAsync("snap-sql-1");
        Assert.NotNull(dbSnapshot);
        Assert.Equal("acc-sqlite", dbSnapshot!.AccountId);
        Assert.Equal(QuotaProvenance.ExactProviderReported, dbSnapshot.Provenance);
        Assert.Single(dbSnapshot.Buckets);
        Assert.Equal(4000, dbSnapshot.Buckets[0].RemainingValue);

        // 4. Verify latest query from SQLite
        var latest = await snapshotRepo.GetLatestForAccountAsync("acc-sqlite");
        Assert.NotNull(latest);
        Assert.Equal("snap-sql-1", latest!.Id);
    }

    [Fact]
    public void QuotaRefreshScheduler_ResolvesCleanlyFromDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_database.Root);

        using var provider = services.BuildServiceProvider();

        var scheduler = provider.GetService<IQuotaRefreshScheduler>();
        Assert.NotNull(scheduler);
        Assert.IsType<QuotaRefreshScheduler>(scheduler);
    }
}
