using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

/// <summary>
/// Composition-root evidence for the Health Center: the recovery audit and the health service must be
/// reachable from the production graph, otherwise no caller can record a transition.
/// </summary>
public sealed class HealthCenterServiceRegistrationTests
{
    [Fact]
    public void AddInfrastructure_RegistersTheRecoveryAuditAndTheHealthCenter()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<SqliteHealthEventRepository>(
            provider.GetRequiredService<IHealthEventRepository>());
        Assert.IsType<SqliteHealthStateRepository>(
            provider.GetRequiredService<IHealthStateRepository>());
        Assert.IsType<SqliteHealthTransitionStore>(
            provider.GetRequiredService<IHealthTransitionStore>());
        Assert.IsType<HealthCenterService>(
            provider.GetRequiredService<IHealthCenterService>());
    }

    [Fact]
    public void AddInfrastructure_RegistersTheProbeExecutorWithItsRealCollaborators()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        // Without this the Health Center UI could only ever record an operator judgement, never an
        // observed provider result.
        var probe = provider.GetRequiredService<IHealthProbeService>();

        Assert.IsType<HealthProbeService>(probe);

        // The probe needs a real connection executor; a graph without one would refuse every probe.
        Assert.NotNull(provider.GetService<IProviderConnectionTestService>());
        Assert.NotNull(provider.GetService<IProviderProfileRepository>());
        Assert.NotNull(provider.GetService<IAccountRepository>());
    }

    [Fact]
    public void AddInfrastructure_RegistersTheImpactedSessionViewWithItsSessionSource()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        var impacted = provider.GetRequiredService<IImpactedSessionService>();

        Assert.IsType<ImpactedSessionService>(impacted);

        // Without a session source the report could only ever declare itself incomplete.
        Assert.NotNull(provider.GetService<ISessionRepository>());
    }

    [Fact]
    public void AddInfrastructure_RegistersTheHealthCenterAsASingleton()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        // The breaker window is shared state, so a second instance would split the failure counts.
        Assert.Same(
            provider.GetRequiredService<IHealthCenterService>(),
            provider.GetRequiredService<IHealthCenterService>());
        Assert.Same(
            provider.GetRequiredService<IHealthEventRepository>(),
            provider.GetRequiredService<IHealthEventRepository>());
    }
}
