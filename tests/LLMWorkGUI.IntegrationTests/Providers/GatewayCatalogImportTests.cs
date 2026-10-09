using LLMGateway.Core;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class GatewayCatalogImportTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FakeGateway _gateway = new();
    private ApplicationInstanceGuard? _guard;
    public void Dispose() { _guard?.Dispose(); _db.Dispose(); }

    private async Task<SqliteGatewayCatalogImportService> Ready()
    {
        await _db.InitializeAsync();
        _guard = new ApplicationInstanceGuard(_db.Root);
        return Service(_guard);
    }

    private SqliteGatewayCatalogImportService Service(IApplicationInstanceGuard guard) =>
        new(() => _gateway, new GatewayCatalogMapper(new SensitiveDataFilter()), _db.Factory, guard, TimeProvider.System);

    private async Task<object?> Sql(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [Theory]
    [InlineData(ProviderKind.Codex)]
    [InlineData(ProviderKind.Grok)]
    [InlineData(ProviderKind.GrokBot)]
    [InlineData(ProviderKind.Unknown)]
    public async Task CatalogProviderIdentityIsRequiredBeforeAnyNativeCatalogRowsAreWritten(ProviderKind provider)
    {
        _gateway.Providers[0] = _gateway.Providers[0] with { Provider = provider };
        _gateway.Accounts[0] = _gateway.Accounts[0] with { Provider = provider };
        _gateway.Models[0] = _gateway.Models[0] with { Provider = provider };
        _gateway.Quotas[0] = _gateway.Quotas[0] with { Provider = provider };
        var service = await Ready();
        if (provider == ProviderKind.Unknown)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync());
            foreach (var table in new[] { "ProviderProfiles", "Accounts", "Models", "Routes", "QuotaSnapshots" })
                Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM " + table));
        }
        else
        {
            var snapshot = await service.ImportAsync();
            Assert.Single(snapshot.Providers);
            Assert.Single(snapshot.Accounts);
            Assert.Single(snapshot.Models);
            Assert.False(Assert.Single(snapshot.Routes).IsEnabled);
            Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM ProviderProfiles"));
        }
    }

    [Fact]
    public async Task Import_PersistsDisabledRoutesWithoutElevatingReadyOrCatalogIdentity()
    {
        var snapshot = await (await Ready()).ImportAsync();
        var account = Assert.Single(await new SqliteAccountRepository(_db.Factory).ListAllAsync());
        Assert.Equal(AuthState.Unknown, account.AuthState);
        Assert.False(account.IsEnabled);
        Assert.False(account.IsEligibleForRouting(DateTimeOffset.UtcNow));
        Assert.Equal("work", account.ProviderNativeId);
        Assert.Null(account.GatewayNativeId);
        Assert.Null(account.SecretReference);
        var route = await new SqliteRouteRepository(_db.Factory).GetAssignmentAsync(Assert.Single(snapshot.Routes).Id);
        Assert.NotNull(route);
        Assert.True(route.HasEveryIdentity);
        Assert.Equal(BackendType.NativeGateway, route.Route.Binding.Backend);
        Assert.False(route.Route.IsEnabled);
        Assert.Null(route.Route.GatewayRouteKey);
        Assert.Equal("Unknown", await Sql("SELECT CapabilityState FROM Models"));
        Assert.Equal(DBNull.Value, await Sql("SELECT GatewayNativeId FROM Models"));
        Assert.Equal(DBNull.Value, await Sql("SELECT GatewayNativeId FROM ProviderProfiles"));
        Assert.Equal(DBNull.Value, await Sql("SELECT ExecutablePath FROM ProviderProfiles"));
        var quota = await new SqliteQuotaSnapshotRepository(_db.Factory).GetLatestForAccountAsync(account.Id);
        Assert.NotNull(quota);
        Assert.Equal(QuotaProvenance.Unknown, quota.Provenance);
        Assert.False(quota.CanCalculateNumericScore(DateTimeOffset.UtcNow));
        var bucket = Assert.Single(quota.Buckets.Where(b => b.Unit == QuotaLimitUnit.Requests));
        Assert.Equal(75d, bucket.RemainingValue);
        Assert.Equal(QuotaConfidence.None, bucket.Confidence);
        var percent = Assert.Single(quota.Buckets.Where(b => b.Unit == QuotaLimitUnit.Percent));
        Assert.Equal(100d, percent.LimitValue);
        Assert.Equal(70d, percent.RemainingValue);
    }

    [Fact]
    public async Task Reimport_PreservesUserSettingsAndDoesNotDuplicateRows()
    {
        var service = await Ready();
        var first = await service.ImportAsync();
        await Sql("UPDATE Accounts SET DisplayName='Local name',ManualPriority=7,IsEnabled=1,AuthState='Invalid',Health='DisabledManual'; UPDATE Routes SET ManualPriority=9; UPDATE Models SET DisplayName='Local model'");
        var second = await service.ImportAsync();
        Assert.Equal(first.Routes.Select(r => r.Id), second.Routes.Select(r => r.Id));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Accounts"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Models"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Routes"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM QuotaSnapshots"));
        Assert.Equal("Local name", await Sql("SELECT DisplayName FROM Accounts"));
        Assert.Equal(7L, await Sql("SELECT ManualPriority FROM Accounts"));
        Assert.Equal("Invalid", await Sql("SELECT AuthState FROM Accounts"));
        Assert.Equal("DisabledManual", await Sql("SELECT Health FROM Accounts"));
        Assert.Equal(9L, await Sql("SELECT ManualPriority FROM Routes"));
        Assert.Equal("Local model", await Sql("SELECT DisplayName FROM Models"));
    }

    [Fact]
    public async Task TwoAccounts_ShareModelButKeepSeparateRoutesAndStableIds()
    {
        _gateway.Accounts.Add(_gateway.Accounts[0] with { Id = "personal" });
        _gateway.Models.Add(_gateway.Models[0] with { AccountId = "personal", Id = "codex/personal/model" });
        var service = await Ready();
        var first = await service.ImportAsync();
        Assert.Equal(2, Assert.Single(first.Models).AvailableAccountIds.Count);
        Assert.Equal(2, first.Routes.Count);
        _gateway.Accounts.Reverse(); _gateway.Models.Reverse();
        var next = await service.ImportAsync();
        Assert.Equal(first.Routes.Select(r => r.Id).Order(), next.Routes.Select(r => r.Id).Order());
        Assert.Equal(2L, await Sql("SELECT COUNT(*) FROM Routes"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Models"));
    }

    [Fact]
    public async Task BrokenForeignIdentity_IsRefusedBeforeAnyWrites()
    {
        var service = await Ready();
        _gateway.Models[0] = _gateway.Models[0] with { AccountId = "missing" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync());
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProviderProfiles"));
    }

    [Fact]
    public async Task LateDatabaseFailure_RollsBackEntireImport()
    {
        var service = await Ready();
        await Sql("CREATE TRIGGER RejectGatewayQuota BEFORE INSERT ON QuotaSnapshots BEGIN SELECT RAISE(ABORT,'fixture'); END");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => service.ImportAsync());
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProviderProfiles"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Accounts"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Models"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Routes"));
    }

    [Fact]
    public async Task PersistedBindingConflict_IsRefusedAndNewRowsAreRolledBack()
    {
        var service = await Ready();
        await service.ImportAsync();
        await Sql("UPDATE Accounts SET ProviderNativeId='repointed'");
        _gateway.Providers.Add(_gateway.Providers[0] with { Provider = ProviderKind.Claude });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync());
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM ProviderProfiles"));
    }

    [Fact]
    public async Task SecondaryInstance_CannotStartDiscovery()
    {
        await Ready();
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => Service(new ViewOnlyGuard()).ImportAsync());
        Assert.Equal(0, _gateway.Calls);
    }

    [Fact]
    public async Task CancelledImport_DoesNotQueryGatewayOrWrite()
    {
        var service = await Ready();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(new CancellationToken(true)));
        Assert.Equal(0, _gateway.Calls);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProviderProfiles"));
    }

    [Fact]
    public async Task ConcurrentImports_DoNotDuplicateCatalogRows()
    {
        var service = await Ready();
        await Task.WhenAll(Task.Run(() => service.ImportAsync()), Task.Run(() => service.ImportAsync()));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Accounts"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Routes"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM QuotaSnapshots"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    public async Task InvalidQuotaNumber_IsRejectedBeforeWriting(double value)
    {
        var service = await Ready();
        _gateway.Quotas[0] = _gateway.Quotas[0] with { Buckets = [new("daily", "Daily", null, value, 100, "requests")] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync());
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProviderProfiles"));
    }

    [Fact]
    public async Task CachedQuotaBecomingStale_UpdatesPersistedFreshness()
    {
        var service = await Ready();
        var first = await service.ImportAsync();
        _gateway.Quotas[0] = _gateway.Quotas[0] with { IsStale = true };
        await service.ImportAsync();
        var stored = await new SqliteQuotaSnapshotRepository(_db.Factory).GetByIdAsync(Assert.Single(first.Quotas).Id);
        Assert.NotNull(stored);
        Assert.Equal(QuotaProvenance.Stale, stored.Provenance);
        Assert.False(stored.IsFresh(DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("../credentials")]
    [InlineData("C:\\profile")]
    [InlineData(" model ")]
    public async Task UnsafeNativeModel_IsRefused(string model)
    {
        var service = await Ready();
        _gateway.Models[0] = _gateway.Models[0] with { NativeModel = model };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync());
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Models"));
    }

    [Theory]
    [InlineData(true, true, AccountAvailability.Ready, QuotaProvenance.Stale)]
    [InlineData(false, false, AccountAvailability.Ready, QuotaProvenance.Unsupported)]
    [InlineData(false, true, AccountAvailability.Error, QuotaProvenance.Error)]
    public async Task QuotaStates_RemainUntrusted(bool stale, bool supported, AccountAvailability availability, QuotaProvenance expected)
    {
        _gateway.Quotas[0] = _gateway.Quotas[0] with { IsStale = stale, Supported = supported, Availability = availability };
        var snapshot = await (await Ready()).ImportAsync();
        var quota = await new SqliteQuotaSnapshotRepository(_db.Factory).GetByIdAsync(Assert.Single(snapshot.Quotas).Id);
        Assert.NotNull(quota);
        Assert.Equal(expected, quota.Provenance);
        Assert.False(quota.CanCalculateNumericScore(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Composition_DoesNotPerformDiscovery()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_db.Root);
        var constructed = false;
        services.AddSingleton<ILlmGateway>(_ => { constructed = true; return _gateway; });
        using var provider = services.BuildServiceProvider(validateScopes: true);
        Assert.IsType<SqliteGatewayCatalogImportService>(provider.GetRequiredService<IGatewayCatalogImportService>());
        Assert.False(constructed);
        Assert.Equal(0, _gateway.Calls);
    }

    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "view-only-fixture";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new SecondaryInstanceReadOnlyException("fixture");
        public void Dispose() { }
    }

    private sealed class FakeGateway : ILlmGateway
    {
        public int Calls { get; private set; }
        public List<ProviderInfo> Providers { get; } = [new(ProviderKind.Codex, "Codex", "codex", true, "C:\\native\\codex.exe",
            new(true, "native", MultiAccountSupport.Isolated, "", "CODEX_HOME", null, true, 1000))];
        public List<AccountInfo> Accounts { get; } = [new("work", "Work", ProviderKind.Codex, true, true,
            AccountAvailability.Ready, new("fixture@example.invalid", "test", "claimed-identity"), null, "C:\\private-profile",
            AccountAuthMode.NativeLogin, null, null, null, "diagnostic not imported", DateTimeOffset.UtcNow)];
        public List<GatewayModel> Models { get; } = [new("codex/work/model", "model", "Model", ProviderKind.Codex, "work", true)];
        public List<QuotaSnapshot> Quotas { get; } = [new("work", ProviderKind.Codex, DateTimeOffset.UtcNow,
            AccountAvailability.Ready, true, null, [new("daily", "Daily", 25, 25, 100, "requests", 1440),
                new("percentage", "Percent only", 30)], "native", null)];
        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<ProviderInfo>>(Providers); }
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<AccountInfo>>(Accounts); }
        public Task<IReadOnlyList<GatewayModel>> GetModelsAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<GatewayModel>>(Models); }
        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotasAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<QuotaSnapshot>>(Quotas); }
        public Task<AccountInfo> AddAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountInfo> UpdateAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountInfo> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountInfo> CheckAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartNativeLoginAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatUpdate> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
