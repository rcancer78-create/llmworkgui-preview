using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed class HealthNonterminalObservationTests
{
    [Fact]
    public async Task OrdinarySuccessCannotRetireOwnedModelProbeBeforeItsConfirmedResult()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE ProviderProfiles SET BaseUrl='https://synthetic.invalid';";
            await command.ExecuteNonQueryAsync();
        }
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IHealthCenterService>();
        var scope = HealthScope.ForAccount("account-1");
        await health.DisableManuallyAsync(scope, "prepare pinned model probe");
        await health.EnableAsync(scope, "model proof required");
        var executor = new OrdinarySuccessExecutor(health, scope);
        var probes = new HealthProbeService(health, provider.GetRequiredService<IProviderProfileRepository>(),
            provider.GetRequiredService<IAccountRepository>(), modelProbe: executor);

        var outcome = await probes.ProbeModelAsync(scope,
            HealthProbeConfirmation.ForModel("model-1", costPreviewAcknowledged: true));

        Assert.Equal(1, executor.Calls);
        Assert.Equal(HealthState.Recovering, executor.NonterminalSnapshot!.State);
        Assert.Equal(HealthState.Healthy, outcome.Snapshot.State);
        Assert.True(outcome.ConfirmsVerifiedRecovery);
        Assert.Equal(HealthState.Healthy, (await health.GetSnapshotAsync(scope)).State);
        Assert.Contains(await health.GetAuditAsync(scope), item => item.IsVerifiedRecovery);
    }

    private sealed class OrdinarySuccessExecutor(IHealthCenterService health, HealthScope scope) : IModelProbeExecutor
    {
        public int Calls { get; private set; }
        public HealthSnapshot? NonterminalSnapshot { get; private set; }
        public bool CanExecute(ModelProbeRequest request) => true;
        public async Task<ModelProbeResult> ExecuteAsync(ModelProbeRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            NonterminalSnapshot = await health.ReportSuccessAsync(scope,
                "ordinary successful execution observation", cancellationToken);
            return ModelProbeResult.Succeeded("https://synthetic.invalid", 1);
        }
    }
}
