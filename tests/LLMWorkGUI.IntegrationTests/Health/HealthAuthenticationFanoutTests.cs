using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed partial class HealthAuthenticationFanoutTests
{
    [Fact]
    public async Task CancellationImmediatelyAfterDurableEnqueueDoesNotAbandonAnyScope()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        using var caller = new CancellationTokenSource();
        var store = new CancelAfterEnqueue(new SqliteHealthTransitionStore(database.Factory), caller);
        var health = new HealthCenterService(new SqliteHealthStateRepository(database.Factory),
            new SqliteHealthEventRepository(database.Factory), transitionStore: store);
        var first = HealthScope.ForAccount("first");
        var second = HealthScope.ForAccount("second");
        await health.ReportAuthenticationFailuresAsync([first, second], cancellationToken: caller.Token);
        Assert.True(caller.IsCancellationRequested);
        Assert.False((await health.GetSnapshotAsync(first)).IsRoutable);
        Assert.False((await health.GetSnapshotAsync(second)).IsRoutable);
        Assert.Empty(await store.ListPendingAsync(default));
    }

    [Fact]
    public async Task FailedEnqueueRollsBackAllBarriersAndPropagatesFailureBeforeAnyProjection()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        using var provider = Build(database);
        var health = provider.GetRequiredService<IHealthCenterService>();
        await Execute(database, """
            CREATE TRIGGER RejectEnqueue BEFORE INSERT ON HealthAuthenticationFanout WHEN NEW.ScopeId='second'
            BEGIN SELECT RAISE(ABORT, 'injected enqueue failure'); END;
            """);
        var notifications = 0;
        health.TransitionRecorded += (_, _) => notifications++;
        await Assert.ThrowsAsync<SqliteException>(() => health.ReportAuthenticationFailuresAsync(
            [HealthScope.ForAccount("first"), HealthScope.ForAccount("second")]));
        Assert.Empty(await ((IHealthAuthenticationFanoutStore)provider.GetRequiredService<IHealthTransitionStore>()).ListPendingAsync(default));
        Assert.Empty(await health.ListAsync());
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task PrimaryStartupRetriesPendingWorkWhileViewOnlyKeepsItBlocked()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var store = new SqliteHealthTransitionStore(database.Factory);
        var scope = HealthScope.ForAccount("restart-account");
        await store.EnqueueAsync([new HealthAuthenticationProjection("restart", scope, scope.ScopeId, "Synthetic startup recovery", DateTimeOffset.UtcNow)], default);
        using (var secondary = HostBootstrapper.CreateHostBuilder(appDataDirectory: database.Root)
                   .ConfigureServices((_, services) => services.AddSingleton<IApplicationInstanceGuard>(new ViewOnlyGuard())).Build())
        {
            await HostBootstrapper.InitializeAsync(secondary);
            var health = secondary.Services.GetRequiredService<IHealthCenterService>();
            Assert.Equal(1, await health.RetryAuthenticationFanoutAsync());
            Assert.True((await health.GetSnapshotAsync(scope)).AuthenticationFanoutPending);
            Assert.False((await health.GetSnapshotAsync(scope)).IsRoutable);
        }
        using var primary = HostBootstrapper.BuildHost(appDataDirectory: database.Root);
        await HostBootstrapper.InitializeAsync(primary);
        Assert.Empty(await store.ListPendingAsync(default));
        var recovered = primary.Services.GetRequiredService<IHealthCenterService>();
        Assert.False((await recovered.GetSnapshotAsync(scope)).IsRoutable);
        Assert.Single(await recovered.GetAuditAsync(scope));
    }

    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "auth-fanout-view-only";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new InvalidOperationException("View-only");
        public void Dispose() { }
    }

    private sealed class CancelAfterEnqueue(SqliteHealthTransitionStore inner, CancellationTokenSource caller)
        : IHealthTransitionStore, IHealthAuthenticationFanoutStore
    {
        public Task SaveAsync(HealthStateRecord? state, HealthEventRecord? healthEvent, CancellationToken token = default)
            => inner.SaveAsync(state, healthEvent, token);
        public async Task EnqueueAsync(IReadOnlyList<HealthAuthenticationProjection> projections, CancellationToken token)
        { await inner.EnqueueAsync(projections, token); caller.Cancel(); }
        public Task<IReadOnlyList<HealthAuthenticationProjection>> ListPendingAsync(CancellationToken token) => inner.ListPendingAsync(token);
        public Task<bool> HasPendingAsync(HealthScope scope, CancellationToken token) => inner.HasPendingAsync(scope, token);
        public Task<(HealthStateRecord? State, bool Pending)> ReadSnapshotAsync(HealthScope scope, CancellationToken token) => inner.ReadSnapshotAsync(scope, token);
        public Task SaveAndCompleteAsync(HealthStateRecord state, HealthEventRecord healthEvent, string id, CancellationToken token)
            => inner.SaveAndCompleteAsync(state, healthEvent, id, token);
    }

    [Theory]
    [InlineData("account-1")]
    [InlineData("http://127.0.0.1:5678")]
    public async Task CollectorContinuesOtherAccountsAndKeepsFailedScopePending(string rejectedScope)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await Execute(database, """
            INSERT INTO Accounts(Id,ProviderProfileId,DisplayName,AuthState,Health,CreatedAtUtc,UpdatedAtUtc)
            SELECT 'account-2',ProviderProfileId,'Second','Valid','Healthy',CreatedAtUtc,UpdatedAtUtc FROM Accounts WHERE Id='account-1';
            """);
        using var provider = Build(database);
        var health = provider.GetRequiredService<IHealthCenterService>();
        await Execute(database, $"""
            CREATE TRIGGER RejectOne BEFORE INSERT ON HealthEvents WHEN NEW.ScopeId='{rejectedScope}'
            BEGIN SELECT RAISE(ABORT, 'injected account/backend audit failure'); END;
            """);
        var collector = new OpenCodeHealthEventCollector(healthCenter: health,
            accountRepository: provider.GetRequiredService<IAccountRepository>(),
            profileRepository: provider.GetRequiredService<IProviderProfileRepository>(),
            serverOptions: Options.Create(new OpenCodeServerOptions { Hostname = "127.0.0.1", Port = 5678 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.RecordHttpFailureAsync("http://127.0.0.1:5678", 401));
        Assert.False((await health.GetSnapshotAsync(HealthScope.ForAccount("account-1"))).IsRoutable);
        Assert.False((await health.GetSnapshotAsync(HealthScope.ForAccount("account-2"))).IsRoutable);
        Assert.Single(await health.GetAuditAsync(HealthScope.ForAccount("account-2")));
        var store = (IHealthAuthenticationFanoutStore)provider.GetRequiredService<IHealthTransitionStore>();
        Assert.Equal(rejectedScope, Assert.Single(await store.ListPendingAsync(default)).Scope.ScopeId);
        if (rejectedScope.StartsWith("http", StringComparison.Ordinal))
        {
            Assert.True((await health.GetSnapshotAsync(HealthScope.ForAccount("account-2"))).AuthenticationFanoutPending);
            await Assert.ThrowsAsync<InvalidOperationException>(() => health.ForceEnableAsync(HealthScope.ForAccount("account-2"), "must wait"));
        }
        await Execute(database, "DROP TRIGGER RejectOne;");
        Assert.Equal(0, await health.RetryAuthenticationFanoutAsync());
        Assert.Single(await health.GetAuditAsync(HealthScope.ForAccount("account-2")));
    }

    [Theory]
    [InlineData("account:other")]
    [InlineData("https://unrelated.invalid/account/v1")]
    public async Task CollectorDoesNotInferAccountIdentityFromSubstring(string entity)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        using var provider = Build(database);
        var health = provider.GetRequiredService<IHealthCenterService>();
        var scope = HealthScope.ForAccount("account");
        await health.ReportSuccessAsync(scope);
        await new OpenCodeHealthEventCollector(healthCenter: health).RecordHttpFailureAsync(entity, 401);
        Assert.True((await health.GetSnapshotAsync(scope)).IsRoutable);
        Assert.Empty(await health.GetAuditAsync(scope));
    }

    [Fact]
    public async Task FailedChildProjectionRemainsDurableAndBlocksRecoveryAfterRestart()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        using var provider = Build(database);
        var health = provider.GetRequiredService<IHealthCenterService>();
        var child = HealthScope.ForModelRoute("account", "model");
        await health.ReportSuccessAsync(child);
        var sibling = HealthScope.ForModelRoute("account", "disabled-sibling");
        await health.DisableManuallyAsync(sibling, "Excluded from routable child expansion");
        await Execute(database, """
            CREATE TRIGGER RejectChild BEFORE INSERT ON HealthEvents WHEN NEW.ScopeType='model-route'
            BEGIN SELECT RAISE(ABORT, 'injected child audit failure'); END;
            """);
        try { await health.ReportFailureAsync(HealthScope.ForAccount("account"), HealthErrorClass.AuthenticationOrRefresh); }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException) { }
        var store = (IHealthAuthenticationFanoutStore)provider.GetRequiredService<IHealthTransitionStore>();
        Assert.NotEmpty(await store.ListPendingAsync(default));
        Assert.True((await health.GetSnapshotAsync(sibling)).AuthenticationFanoutPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => health.EnableAsync(sibling, "must wait"));
        provider.Dispose();
        using var restarted = Build(database);
        var afterRestart = restarted.GetRequiredService<IHealthCenterService>();
        Assert.False((await afterRestart.GetSnapshotAsync(child)).IsRoutable);
        var probes = restarted.GetRequiredService<IHealthProbeService>();
        Assert.Equal(HealthProbeRefusal.AuthenticationFanoutPending,
            (await probes.ProbeConnectionAsync(child)).Refusal);
        Assert.Equal(HealthProbeRefusal.AuthenticationFanoutPending,
            (await probes.ProbeModelAsync(child, HealthProbeConfirmation.ForModel("model", costPreviewAcknowledged: true))).Refusal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => afterRestart.ForceEnableAsync(HealthScope.ForAccount("account"), "must wait"));
        await Execute(database, "DROP TRIGGER RejectChild;");
        Assert.Equal(0, await afterRestart.RetryAuthenticationFanoutAsync());
        Assert.False((await afterRestart.GetSnapshotAsync(child)).IsRoutable);
        Assert.False((await afterRestart.GetSnapshotAsync(child)).AuthenticationFanoutPending);
        Assert.Single(await afterRestart.GetAuditAsync(child));
        Assert.Equal(0, await afterRestart.RetryAuthenticationFanoutAsync());
        Assert.Single(await afterRestart.GetAuditAsync(child));
    }

    [Fact]
    public async Task CancellationFromFirstNotificationCannotInterruptRequiredChildren()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        using var provider = Build(database);
        var health = provider.GetRequiredService<IHealthCenterService>();
        var child = HealthScope.ForModelRoute("account", "model");
        await health.ReportSuccessAsync(child);
        using var caller = new CancellationTokenSource();
        health.TransitionRecorded += (_, _) => caller.Cancel();
        try { await health.ReportFailureAsync(HealthScope.ForAccount("account"), HealthErrorClass.AuthenticationOrRefresh, cancellationToken: caller.Token); }
        catch (OperationCanceledException) { }
        Assert.True(caller.IsCancellationRequested);
        Assert.False((await health.GetSnapshotAsync(child)).IsRoutable);
        Assert.Single(await health.GetAuditAsync(child));
    }

    private static ServiceProvider Build(TestDatabase database)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        return services.BuildServiceProvider();
    }

    private static async Task Execute(TestDatabase database, string sql)
    {
        await using var connection = await database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
