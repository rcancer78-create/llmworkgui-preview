using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Reconciliation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Reconciliation;

public sealed class ReconciliationRegistrationTests
{
    [Fact]
    public void AddApplication_RegistersRecoveryMatrixAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddApplication();

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(RecoveryMatrix)
            && descriptor.ImplementationType == typeof(RecoveryMatrix)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddInfrastructure_RegistersReconciliationServices()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddInfrastructure(dataDirectory.Root);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(RecoveryMatrix)
            && descriptor.ImplementationType == typeof(RecoveryMatrix)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IReconciliationProbe)
            && descriptor.ImplementationType == typeof(ReconciliationProbe)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IReconciliationService)
            && descriptor.ImplementationType == typeof(ReconciliationService)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddApplicationAndInfrastructure_ResolveSingleReconciliationService()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddApplication();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        var probe = provider.GetRequiredService<IReconciliationProbe>();
        Assert.IsType<ReconciliationProbe>(probe);

        var reconciliationService = provider.GetRequiredService<IReconciliationService>();
        Assert.IsType<ReconciliationService>(reconciliationService);
        Assert.Same(
            reconciliationService,
            provider.GetRequiredService<IReconciliationService>());

        Assert.NotNull(provider.GetRequiredService<RecoveryMatrix>());
    }
}
