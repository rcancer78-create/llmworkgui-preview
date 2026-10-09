using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed partial class HealthAuthenticationFanoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeAuthFailureAdmitsAccountBarrierBeforeWritingProbeCompletion(bool modelProbe)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await Execute(database, "UPDATE ProviderProfiles SET BaseUrl='https://synthetic.invalid';");
        using var provider = Build(database);
        var health = provider.GetRequiredService<IHealthCenterService>();
        var scope = HealthScope.ForModelRoute("account-1", "model-1");
        await health.DisableManuallyAsync(scope, "prepare probe");
        await health.EnableAsync(scope, "explicit probe required");
        var executor = new AuthenticationFailureExecutor(database);
        var probe = new HealthProbeService(health, provider.GetRequiredService<IProviderProfileRepository>(),
            provider.GetRequiredService<IAccountRepository>(), connectionTest: executor, modelProbe: executor);
        var invoked = false;
        try
        {
            if (modelProbe) await probe.ProbeModelAsync(scope, HealthProbeConfirmation.ForModel("model-1", costPreviewAcknowledged: true));
            else await probe.ProbeConnectionAsync(scope);
        }
        catch (Exception error) when (error is SqliteException or InvalidOperationException) { invoked = true; }
        Assert.True(invoked);
        Assert.False((await health.GetSnapshotAsync(HealthScope.ForAccount("account-1"))).IsRoutable);
        Assert.True((await health.GetSnapshotAsync(scope)).AuthenticationFanoutPending);
        var pending = await ((IHealthAuthenticationFanoutStore)provider.GetRequiredService<IHealthTransitionStore>()).ListPendingAsync(default);
        Assert.Contains("probeAttemptId", Assert.Single(pending).EvidenceRedactedJson!);
    }

    private sealed class AuthenticationFailureExecutor(TestDatabase database) : IModelProbeExecutor, IProviderConnectionTestService
    {
        public bool CanExecute(ModelProbeRequest request) => true;
        public async Task<ModelProbeResult> ExecuteAsync(ModelProbeRequest request, CancellationToken cancellationToken = default)
        {
            await FailNextAudit();
            return ModelProbeResult.Failed(HealthErrorClass.AuthenticationOrRefresh, "https://synthetic.invalid", 1, "Synthetic401");
        }
        public async Task<ProviderConnectionTestResult> TestConnectionAsync(CustomProviderSettings settings,
            string? explicitApiKey = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            await FailNextAudit();
            return ProviderConnectionTestResult.CreateFailure(ProviderConnectionStatus.AuthenticationFailed,
                "https://synthetic.invalid", "Synthetic401", 401);
        }
        private Task FailNextAudit() => Execute(database, """
                CREATE TRIGGER RejectProbeCompletion BEFORE INSERT ON HealthEvents WHEN NEW.ScopeType='model-route'
                BEGIN SELECT RAISE(ABORT, 'injected probe completion failure'); END;
                """);
    }
}
