using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Executions;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class ConcurrencyRegistrationTests
{
    [Fact]
    public void AddApplicationAndInfrastructure_RegisterConcurrencyServices()
    {
        using var dataDirectory = new TestDirectory();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        var instanceGuard = provider.GetRequiredService<IApplicationInstanceGuard>();
        Assert.IsType<ApplicationInstanceGuard>(instanceGuard);
        Assert.True(instanceGuard.IsPrimarySupervisor);

        var checkoutLockService = provider.GetRequiredService<ICheckoutLockService>();
        Assert.IsType<CheckoutLockService>(checkoutLockService);

        var deduplicationGuard = provider.GetRequiredService<IExecutionDeduplicationGuard>();
        Assert.IsType<ExecutionDeduplicationGuard>(deduplicationGuard);

        var supervisor = provider.GetRequiredService<IProcessSupervisor>();
        var guarded = Assert.IsType<GuardedProcessSupervisor>(supervisor);
        Assert.IsType<ProcessSupervisor>(guarded.Inner);
    }

    [Fact]
    public void ConcurrencyServices_AreRegisteredAsSingletons()
    {
        using var dataDirectory = new TestDirectory();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<IApplicationInstanceGuard>(),
            provider.GetRequiredService<IApplicationInstanceGuard>());
        Assert.Same(
            provider.GetRequiredService<ICheckoutLockService>(),
            provider.GetRequiredService<ICheckoutLockService>());
        Assert.Same(
            provider.GetRequiredService<IExecutionDeduplicationGuard>(),
            provider.GetRequiredService<IExecutionDeduplicationGuard>());
    }
}
