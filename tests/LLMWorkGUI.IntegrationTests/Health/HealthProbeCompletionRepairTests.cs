using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed class HealthProbeCompletionRepairTests
{
    [Theory]
    [InlineData("model", HealthState.Healthy)]
    [InlineData("connection", HealthState.Recovering)]
    [InlineData("interrupted", HealthState.Recovering)]
    [InlineData("mismatch", HealthState.Recovering)]
    [InlineData("committed", HealthState.Healthy)]
    [InlineData("refresh", HealthState.Recovering)]
    [InlineData("authentication", HealthState.QuarantinedAuto)]
    [InlineData("mismatchcommitted", HealthState.Recovering)]
    public async Task RetryingProbeAfterAuditRepairSavesOriginalObservationWithoutProviderReplay(
        string scenario, HealthState expected)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await Execute(database, "UPDATE ProviderProfiles SET BaseUrl='https://synthetic.invalid';");
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        using var provider = services.BuildServiceProvider();
        var committedStore = scenario is "committed" or "mismatchcommitted"
            ? new CommitThenThrowStore(provider.GetRequiredService<IHealthTransitionStore>()) : null;
        var health = committedStore is null ? provider.GetRequiredService<IHealthCenterService>()
            : new HealthCenterService(provider.GetRequiredService<IHealthStateRepository>(),
                provider.GetRequiredService<IHealthEventRepository>(), transitionStore: committedStore);
        var events = provider.GetRequiredService<IHealthEventRepository>();
        var scope = HealthScope.ForAccount("account-1");
        await health.DisableManuallyAsync(scope, "prepare probe");
        await health.EnableAsync(scope, "probe required");
        var executor = new AuditFailingExecutor(database, scenario, committedStore);
        var probe = new HealthProbeService(health, provider.GetRequiredService<IProviderProfileRepository>(),
            provider.GetRequiredService<IAccountRepository>(), executor, executor);
        var notifications = 0;
        health.TransitionRecorded += (_, _) => notifications++;
        Task<HealthProbeOutcome> Invoke() => scenario is "connection" or "refresh"
            ? probe.ProbeConnectionAsync(scope)
            : probe.ProbeModelAsync(scope, HealthProbeConfirmation.ForModel("model-1", costPreviewAcknowledged: true));

        if (scenario == "authentication") await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
        else if (committedStore is null) await Assert.ThrowsAsync<SqliteException>(Invoke);
        else await Assert.ThrowsAsync<IOException>(Invoke);
        Assert.Equal(1, executor.Calls);
        Assert.Equal(scenario == "committed" ? HealthState.Healthy : HealthState.Recovering,
            (await health.GetSnapshotAsync(scope)).State);
        var beforeRetry = await events.ListByScopeAsync(scope.ScopeType, scope.ScopeId);
        var routeScope = HealthScope.ForModelRoute("account-1", "model-1");
        var routeEventsBeforeRetry = await events.ListByScopeAsync(routeScope.ScopeType, routeScope.ScopeId);
        Assert.Null(await health.TryBeginProbeAttemptAsync(scope, true, "no duplicate admission"));
        var notificationsBeforeRetry = notifications;

        if (committedStore is null) await Execute(database, "DROP TRIGGER RejectObservedProbeAudit;");
        if (scenario == "refresh")
        {
            Assert.Equal(0, await ((IHealthProbeService)probe).RetryPendingCompletionsAsync());
            Assert.Equal(1, executor.Calls);
            Assert.Equal(beforeRetry.Count + 1,
                (await events.ListByScopeAsync(scope.ScopeType, scope.ScopeId)).Count);
            Assert.Equal(HealthState.Recovering, (await health.GetSnapshotAsync(scope)).State);
            return;
        }
        var repaired = await Invoke();

        Assert.Equal(1, executor.Calls);
        Assert.Equal(HealthProbeRefusal.None, repaired.Refusal);
        Assert.Equal(expected, repaired.Snapshot.State);
        Assert.Equal(scenario is "model" or "committed", repaired.ConfirmsVerifiedRecovery);
        Assert.Equal(beforeRetry.Count + (scenario == "committed" ? 0 : 1),
            (await events.ListByScopeAsync(scope.ScopeType, scope.ScopeId)).Count);
        Assert.Equal(routeEventsBeforeRetry.Count,
            (await events.ListByScopeAsync(routeScope.ScopeType, routeScope.ScopeId)).Count);
        if (scenario == "interrupted")
            Assert.Equal(HealthErrorClass.UnknownOrAmbiguousCompletion, repaired.ErrorClass);
        if (scenario is "mismatch" or "mismatchcommitted")
            Assert.Equal(HealthState.CoolingDown, (await health.GetSnapshotAsync(routeScope)).State);
        if (scenario == "authentication")
        {
            Assert.False((await health.GetSnapshotAsync(scope)).AuthenticationFanoutPending);
            var fanout = (IHealthAuthenticationFanoutStore)provider.GetRequiredService<IHealthTransitionStore>();
            Assert.Empty(await fanout.ListPendingAsync(default));
        }
        if (scenario == "committed")
        {
            Assert.Equal(notificationsBeforeRetry + 1, notifications);
            await ((IHealthProbeService)probe).RetryPendingCompletionsAsync();
            Assert.Equal(notificationsBeforeRetry + 1, notifications);
        }
    }

    [Fact]
    public async Task RetainedSuccessAfterNewManualDisableCannotRestoreHealthOrReplayProvider()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await Execute(database, "UPDATE ProviderProfiles SET BaseUrl='https://synthetic.invalid';");
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IHealthCenterService>();
        var scope = HealthScope.ForAccount("account-1");
        await health.DisableManuallyAsync(scope, "prepare probe");
        await health.EnableAsync(scope, "probe required");
        var executor = new AuditFailingExecutor(database, "model");
        var probe = new HealthProbeService(health, provider.GetRequiredService<IProviderProfileRepository>(),
            provider.GetRequiredService<IAccountRepository>(), executor, executor);
        await Assert.ThrowsAsync<SqliteException>(() =>
            probe.ProbeModelAsync(scope, HealthProbeConfirmation.ForModel("model-1", costPreviewAcknowledged: true)));
        await Execute(database, "DROP TRIGGER RejectObservedProbeAudit;");
        await health.DisableManuallyAsync(scope, "new authoritative operator decision");

        var repaired = await probe.ProbeModelAsync(scope, HealthProbeConfirmation.ForModel("model-1", costPreviewAcknowledged: true));

        Assert.Equal(1, executor.Calls);
        Assert.Equal(HealthProbeRefusal.None, repaired.Refusal);
        Assert.Equal(HealthState.DisabledManual, repaired.Snapshot.State);
        Assert.False(repaired.ConfirmsVerifiedRecovery);
        Assert.Contains("newer", repaired.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Execute(TestDatabase database, string sql)
    {
        await using var connection = await database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class AuditFailingExecutor(TestDatabase database, string scenario,
        CommitThenThrowStore? committedStore = null)
        : IModelProbeExecutor, IProviderConnectionTestService
    {
        public int Calls { get; private set; }
        public bool CanExecute(ModelProbeRequest request) => true;

        private async Task FailCompletion()
        {
            Calls++;
            if (committedStore is not null)
            {
                committedStore.Armed = true;
                return;
            }
            await Execute(database, """
                CREATE TRIGGER RejectObservedProbeAudit BEFORE INSERT ON HealthEvents
                WHEN NEW.ScopeType='account'
                BEGIN SELECT RAISE(ABORT, 'injected observed probe audit failure'); END;
                """);
        }

        public async Task<ModelProbeResult> ExecuteAsync(ModelProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            await FailCompletion();
            if (scenario == "interrupted") throw new IOException("synthetic provider interruption");
            if (scenario == "authentication")
                return ModelProbeResult.Failed(HealthErrorClass.AuthenticationOrRefresh,
                    "https://synthetic.invalid", 1, "synthetic authentication failure");
            return scenario is "mismatch" or "mismatchcommitted"
                ? ModelProbeResult.Failed(HealthErrorClass.ModelUnavailableOrMismatch,
                    "https://synthetic.invalid", 1, "synthetic model mismatch")
                : ModelProbeResult.Succeeded("https://synthetic.invalid", 1);
        }

        public async Task<ProviderConnectionTestResult> TestConnectionAsync(CustomProviderSettings settings,
            string? explicitApiKey = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            await FailCompletion();
            return ProviderConnectionTestResult.CreateSuccess("https://synthetic.invalid", 1, []);
        }
    }

    private sealed class CommitThenThrowStore(IHealthTransitionStore durable) : IHealthTransitionStore
    {
        public bool Armed { get; set; }
        public async Task SaveAsync(HealthStateRecord? state, HealthEventRecord? healthEvent,
            CancellationToken cancellationToken = default)
        {
            await durable.SaveAsync(state, healthEvent, cancellationToken);
            if (!Armed) return;
            Armed = false;
            throw new IOException("synthetic loss of acknowledgement after SQLite commit");
        }
    }
}
