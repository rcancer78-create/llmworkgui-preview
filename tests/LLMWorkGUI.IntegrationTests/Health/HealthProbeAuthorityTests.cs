using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed class HealthProbeAuthorityTests
{
    [Fact]
    public void ModelConfirmationFactoryWithoutExplicitAcknowledgementDoesNotAuthorizeSpending()
    {
        Assert.False(HealthProbeConfirmation.ForModel("model-1").CostPreviewAcknowledged);
        Assert.True(HealthProbeConfirmation.ForModel("model-1", costPreviewAcknowledged: true)
            .CostPreviewAcknowledged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyUncorrelatedSuccessCannotVerifyRecoveryOrRetireAdmittedAttempt(bool currentAttempt)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(database.Factory);
        services.AddInfrastructure(database.Root);
        using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<IHealthCenterService>();
        var events = provider.GetRequiredService<IHealthEventRepository>();
        var scope = HealthScope.ForAccount("legacy-probe-authority");
        await health.DisableManuallyAsync(scope, "prepare probe");
        await health.EnableAsync(scope, "probe required");
        HealthProbeAttempt? attempt = null;
        if (currentAttempt)
            attempt = await health.TryBeginProbeAttemptAsync(scope, true, "actual correlated admission");
        else await health.StartProbeAsync(scope);
        var before = await events.ListByScopeAsync(scope.ScopeType, scope.ScopeId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => health.CompleteProbeAsync(scope, true));

        Assert.Equal(HealthState.Recovering, (await health.GetSnapshotAsync(scope)).State);
        Assert.Equal(before.Count, (await events.ListByScopeAsync(scope.ScopeType, scope.ScopeId)).Count);
        if (attempt is not null)
        {
            var proper = await health.CompleteProbeAttemptAsync(attempt,
                HealthProbeCompletionKind.ModelSucceeded, "actual pinned model observation");
            Assert.True(proper.WasCurrent);
            Assert.True(proper.VerifiedRecovery);
        }
    }
}
