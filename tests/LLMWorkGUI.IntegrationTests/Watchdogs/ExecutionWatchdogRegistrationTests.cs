using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Watchdogs;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Watchdogs;

public sealed class ExecutionWatchdogRegistrationTests
{
    [Fact]
    public void AddApplication_RegistersExecutionWatchdogAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddApplication();

        Assert.Contains(services, IsExecutionWatchdogDescriptor);
    }

    [Fact]
    public void AddInfrastructure_RegistersExecutionWatchdogAsSingleton()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();

        services.AddInfrastructure(dataDirectory.Root);

        Assert.Contains(services, IsExecutionWatchdogDescriptor);
    }

    [Fact]
    public void AddApplicationAndInfrastructure_ResolveSingleExecutionWatchdog()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddApplication();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        var watchdog = provider.GetRequiredService<IExecutionWatchdog>();

        Assert.IsType<ExecutionWatchdog>(watchdog);
        Assert.Same(watchdog, provider.GetRequiredService<IExecutionWatchdog>());
    }

    private static bool IsExecutionWatchdogDescriptor(ServiceDescriptor descriptor)
    {
        return descriptor.ServiceType == typeof(IExecutionWatchdog)
            && descriptor.ImplementationType == typeof(ExecutionWatchdog)
            && descriptor.Lifetime == ServiceLifetime.Singleton;
    }
}
