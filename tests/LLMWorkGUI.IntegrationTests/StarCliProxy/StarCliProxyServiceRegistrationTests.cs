using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Backends.Abstractions;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.StarCliProxy;

public sealed class StarCliProxyServiceRegistrationTests
{
    [Fact]
    public void AddInfrastructure_RegistersStarCliProxyServices()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IStarCliProxyExecutableResolver>());
        Assert.NotNull(provider.GetService<IStarCliProxyClient>());
        Assert.NotNull(provider.GetService<IStarCliProxyServerManager>());
        Assert.NotNull(provider.GetService<IStarCliProxyAvailabilityProbe>());
        Assert.NotNull(provider.GetService<IAccountContextManager>());
        Assert.NotNull(provider.GetService<StarCliProxyAccountBridge>());
        Assert.NotNull(provider.GetService<StarCliProxyBackendAdapter>());

        var serverManager = provider.GetRequiredService<StarCliProxyServerManager>();

        Assert.Same(serverManager, provider.GetRequiredService<IStarCliProxyServerManager>());
        Assert.Same(serverManager, provider.GetRequiredService<IStarCliProxyAvailabilityProbe>());
    }

    [Fact]
    public void AddInfrastructure_RegistersStarCliProxyAccountBridgeAndBackendAdapter()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        var bridges = provider.GetServices<IAccountBridge>().ToArray();

        Assert.Contains(bridges, bridge => bridge is StarCliProxyAccountBridge && bridge.BackendId == "star-cliproxy");
        Assert.Contains(bridges, bridge => bridge is OpenCodePluginAccountBridge && bridge.BackendId == "opencode");

        // The direct AGY bridge is migration-only and must never be resolvable from product DI;
        // AGY account changes go exclusively through StarCliProxyAccountBridge + IAccountContextManager.
        Assert.DoesNotContain(bridges, bridge => bridge.BackendId == "agy");

        var adapters = provider.GetServices<IBackendAdapter>().ToArray();

        var starCliProxyAdapter = Assert.Single(adapters, adapter => adapter.BackendId == "star-cliproxy");
        Assert.IsType<StarCliProxyBackendAdapter>(starCliProxyAdapter);
    }

    [Fact]
    public void AddInfrastructure_ResolvesStarCliProxyServicesAsSingletons()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IStarCliProxyClient>(), provider.GetRequiredService<IStarCliProxyClient>());
        Assert.Same(provider.GetRequiredService<IAccountContextManager>(), provider.GetRequiredService<IAccountContextManager>());
        Assert.Same(provider.GetRequiredService<StarCliProxyAccountBridge>(), provider.GetRequiredService<StarCliProxyAccountBridge>());
    }
}
