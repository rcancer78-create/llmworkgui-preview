using LLMGateway.Core;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class GatewayIntegrationCompositionTests : IDisposable
{
    private readonly TestDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void AddInfrastructure_ComposesVendoredGatewayWithIsolatedApplicationData()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(_directory.Root);

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var gateway = provider.GetRequiredService<ILlmGateway>();
        var options = provider.GetRequiredService<GatewayOptions>();

        Assert.Same(gateway, provider.GetRequiredService<ILlmGateway>());
        Assert.Equal(
            Path.Combine(_directory.Root, "llmgateway", "accounts.json"),
            options.ResolveAccountsFile());
        Assert.Equal(
            Path.Combine(_directory.Root, "llmgateway", "workspace"),
            options.ResolveWorkspace());
        Assert.False(options.DiscoverProfiles);
    }

    [Fact]
    public void AddInfrastructure_UsesTheEffectiveDefaultAndConfiguredStorageRoots()
    {
        var defaultServices = new ServiceCollection();
        defaultServices.AddInfrastructure();
        using var defaultProvider = defaultServices.BuildServiceProvider(validateScopes: true);
        var defaultOptions = defaultProvider.GetRequiredService<GatewayOptions>();

        Assert.Equal(
            Path.Combine(
                LLMWorkGUI.Infrastructure.Storage.AppDataPaths.DefaultRootDirectory,
                "llmgateway",
                "accounts.json"),
            defaultOptions.ResolveAccountsFile());
        Assert.False(defaultOptions.DiscoverProfiles);

        var configuredRoot = Path.Combine(_directory.Root, "configured");
        var configuredServices = new ServiceCollection();
        configuredServices.Configure<StorageOptions>(options => options.AppDataDirectory = configuredRoot);
        configuredServices.AddInfrastructure();
        using var configuredProvider = configuredServices.BuildServiceProvider(validateScopes: true);
        var configuredOptions = configuredProvider.GetRequiredService<GatewayOptions>();

        Assert.Equal(
            Path.Combine(configuredRoot, "llmgateway", "accounts.json"),
            configuredOptions.ResolveAccountsFile());
        Assert.False(configuredOptions.DiscoverProfiles);
    }
}
