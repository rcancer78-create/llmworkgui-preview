using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Routing;

public sealed class RoutingEngineIntegrationTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task RoutingEngine_SelectsOptimalRoute_WithRealSqliteDatabase()
    {
        await _database.InitializeAsync();

        var providerRepo = new SqliteProviderProfileRepository(_database.Factory);
        var accountRepo = new SqliteAccountRepository(_database.Factory);
        var snapshotRepo = new SqliteQuotaSnapshotRepository(_database.Factory);

        // 1. Setup Provider & Models
        var provider = new ProviderProfile("prov-sql", "SQL Provider", BackendType.OpenCode, null, null, DataClassification.PrivateSource, true);
        await providerRepo.UpsertAsync(provider);
        await _database.InsertModelAsync("gpt-4o", providerProfileId: "prov-sql", displayName: "GPT-4o");

        // 2. Setup 2 Accounts: Acc1 (low remaining), Acc2 (high remaining)
        var acc1 = new Account("acc-sql-1", "prov-sql", "Account Low Quota", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        var acc2 = new Account("acc-sql-2", "prov-sql", "Account High Quota", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        await accountRepo.SaveAsync(acc1);
        await accountRepo.SaveAsync(acc2);

        var now = DateTimeOffset.UtcNow;
        var bucket1 = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 5000, 4500, 500, now.AddHours(8), 50);
        var bucket2 = new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 5000, 1000, 4000, now.AddHours(8), 50);

        await snapshotRepo.SaveAsync(new QuotaSnapshot("snap-acc1", "acc-sql-1", QuotaProvenance.ExactProviderReported, now, new[] { bucket1 }, "prov-sql", "gpt-4o", now.AddMinutes(5)));
        await snapshotRepo.SaveAsync(new QuotaSnapshot("snap-acc2", "acc-sql-2", QuotaProvenance.ExactProviderReported, now, new[] { bucket2 }, "prov-sql", "gpt-4o", now.AddMinutes(5)));

        var engine = new RoutingEngine(accountRepo, snapshotRepo, providerProfileRepository: providerRepo);

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-sql",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.QuotaFirst,
            PolicySource = "ProjectDefault"
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.True(decision.IsSuccess);
        Assert.NotNull(decision.SelectedAccount);
        Assert.Equal("acc-sql-2", decision.SelectedAccount!.Id);
        Assert.Equal("snap-acc2", decision.QuotaSnapshotId); // Linked to exact snapshot ID
        Assert.Equal(BackendType.OpenCode, decision.SelectedBinding!.Backend);
        Assert.Equal("prov-sql", decision.SelectedBinding.ProviderProfileId);
        Assert.Equal("acc-sql-2", decision.SelectedBinding.AccountId);
        Assert.Equal("gpt-4o", decision.SelectedBinding.ModelId);
    }

    [Fact]
    public void RoutingEngine_ResolvesCleanlyFromDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_database.Root);
        services.AddApplication();

        using var provider = services.BuildServiceProvider();

        var engine = provider.GetService<IRoutingEngine>();
        Assert.NotNull(engine);
        Assert.IsType<RoutingEngine>(engine);
    }

    [Fact]
    public async Task RoutingEngine_RejectsProjectDataClassAboveProviderMaxDataClass_WithRealSqliteDatabase()
    {
        await _database.InitializeAsync();

        var providerRepo = new SqliteProviderProfileRepository(_database.Factory);
        var accountRepo = new SqliteAccountRepository(_database.Factory);
        var snapshotRepo = new SqliteQuotaSnapshotRepository(_database.Factory);

        var provider = new ProviderProfile("prov-public", "Public Provider", BackendType.OpenCode, null, null, DataClassification.PublicSource, true);
        await providerRepo.UpsertAsync(provider);

        var account = new Account("acc-public", "prov-public", "Public Account", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null);
        await accountRepo.SaveAsync(account);

        var engine = new RoutingEngine(
            accountRepo,
            snapshotRepo,
            providerProfileRepository: providerRepo);

        var request = new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-public",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.PriorityFirst,
            ProjectDataClass = DataClassification.PrivateSource
        };

        var decision = await engine.SelectRouteAsync(request);

        Assert.False(decision.IsSuccess);
        Assert.Null(decision.SelectedAccount);
        Assert.Contains("Data classification violation", decision.ExplanationText);
    }

    [Fact]
    public async Task RoutingEngine_FromDependencyInjection_EnforcesOpaqueBridgeAndDataClassGates()
    {
        await _database.InitializeAsync();

        var services = new ServiceCollection();
        services.AddInfrastructure(_database.Root);
        services.AddApplication();

        using var provider = services.BuildServiceProvider();

        var providerRepo = provider.GetRequiredService<IProviderProfileRepository>();
        var accountRepo = provider.GetRequiredService<IAccountRepository>();

        await providerRepo.UpsertAsync(new ProviderProfile("prov-di-public", "DI Public Provider", BackendType.OpenCode, null, null, DataClassification.PublicSource, true));
        await accountRepo.SaveAsync(new Account("acc-di-public", "prov-di-public", "DI Public Account", null, AuthState.Valid, 10, true, HealthState.Healthy, null, null, 2, null));

        var engine = provider.GetRequiredService<IRoutingEngine>();

        // Production DI registers an opaque OpenCode bridge without a pin contract:
        // automatic routing must fail closed (multi-account-routing-contract.json §opaqueRoutePolicy).
        var automaticDecision = await engine.SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-di-public",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.Balanced
        });

        Assert.False(automaticDecision.IsSuccess);
        Assert.Contains("Fail-Closed", automaticDecision.ExplanationText);

        // ManualOnly bypasses the opaque bridge gate, but the data class gate must still apply.
        var manualDecision = await engine.SelectRouteAsync(new RouteSelectionRequest
        {
            Backend = BackendType.OpenCode,
            ProviderProfileId = "prov-di-public",
            ModelId = "gpt-4o",
            Policy = RoutingPolicy.ManualOnly,
            PinnedAccountId = "acc-di-public",
            ProjectDataClass = DataClassification.PrivateSource
        });

        Assert.False(manualDecision.IsSuccess);
        Assert.Contains("Data classification violation", manualDecision.ExplanationText);
    }
}
