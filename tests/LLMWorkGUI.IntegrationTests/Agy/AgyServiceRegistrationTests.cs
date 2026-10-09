using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Agy;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Agy;

public sealed class AgyServiceRegistrationTests
{
    [Fact]
    public void AddInfrastructure_RegistersOnlyAccountContextAgyServices()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        // Only the account-context dependencies required by IAccountContextManager stay in product DI.
        Assert.NotNull(provider.GetService<IAgyProfileExecutableResolver>());
        Assert.NotNull(provider.GetService<IAgyProcessInspector>());
        Assert.NotNull(provider.GetService<IAgyProfileService>());

        // The direct AGY runner and direct AGY bridge are migration-only: resolving them would
        // bypass the AccountContextManager serialized lock (ADR-0007 §3), so product DI must not
        // expose them.
        Assert.Null(provider.GetService<IAgyProcessRunner>());
        Assert.Null(provider.GetService<AgyProfileAccountBridge>());

        var bridges = provider.GetServices<IAccountBridge>().ToArray();

        Assert.DoesNotContain(bridges, bridge => bridge is AgyProfileAccountBridge);
        Assert.DoesNotContain(bridges, bridge => bridge.BackendId == "agy");
        Assert.Contains(bridges, bridge => bridge is OpenCodePluginAccountBridge);
        Assert.Contains(bridges, bridge => bridge is StarCliProxyAccountBridge && bridge.BackendId == "star-cliproxy");
    }

    [Fact]
    public void AgyProfileService_IsResolvedAsSingleton()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IAgyProfileService>();
        var second = provider.GetRequiredService<IAgyProfileService>();

        Assert.Same(first, second);
    }
}
