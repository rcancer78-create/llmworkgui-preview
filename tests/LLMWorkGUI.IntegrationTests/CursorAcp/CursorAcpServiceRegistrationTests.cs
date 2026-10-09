using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

public sealed class CursorAcpServiceRegistrationTests
{
    [Fact]
    public void AddInfrastructure_RegistersCursorAcpServicesAsSingletons()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<WindowsCursorAgentExecutableResolver>(
            provider.GetRequiredService<ICursorExecutableResolver>());
        Assert.IsType<JsonRpcStdioTransportFactory>(
            provider.GetRequiredService<IJsonRpcTransportFactory>());
        Assert.IsType<CursorAcpProcessManager>(
            provider.GetRequiredService<ICursorAcpProcessManager>());

        Assert.Same(
            provider.GetRequiredService<ICursorAcpProcessManager>(),
            provider.GetRequiredService<ICursorAcpProcessManager>());
        Assert.Same(
            provider.GetRequiredService<ICursorExecutableResolver>(),
            provider.GetRequiredService<ICursorExecutableResolver>());
    }

    [Fact]
    public void AddInfrastructure_RegistersCursorAcpStatelessServices()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<CursorAcpModelCatalogService>(
            provider.GetRequiredService<ICursorAcpModelCatalogService>());
        Assert.IsType<CursorAcpModelSelector>(
            provider.GetRequiredService<ICursorAcpModelSelector>());
        Assert.IsType<CursorAcpModePolicy>(
            provider.GetRequiredService<ICursorAcpModePolicy>());

        Assert.Same(
            provider.GetRequiredService<ICursorAcpModelCatalogService>(),
            provider.GetRequiredService<ICursorAcpModelCatalogService>());
        Assert.Same(
            provider.GetRequiredService<ICursorAcpModelSelector>(),
            provider.GetRequiredService<ICursorAcpModelSelector>());
        Assert.Same(
            provider.GetRequiredService<ICursorAcpModePolicy>(),
            provider.GetRequiredService<ICursorAcpModePolicy>());
    }

    [Fact]
    public void AddInfrastructure_DoesNotRegisterStatefulCursorAcpClient()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<ICursorAcpClient>());
    }

    [Fact]
    public async Task AddInfrastructure_RegistersCursorAcpClientFactoryAndLifecycleService()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        // The lifecycle service owns a managed process, so it is IAsyncDisposable only and the
        // container must be disposed asynchronously.
        await using var provider = services.BuildServiceProvider();

        // The stateful protocol client is created through the factory, never resolved directly.
        var factory = provider.GetRequiredService<ICursorAcpClientFactory>();
        Assert.Same(factory, provider.GetRequiredService<ICursorAcpClientFactory>());

        var lifecycle = provider.GetRequiredService<ICursorAcpSessionLifecycleService>();
        Assert.Same(lifecycle, provider.GetRequiredService<ICursorAcpSessionLifecycleService>());

        // No backend is started until the caller asks for it explicitly.
        Assert.Null(lifecycle.Current);
        Assert.Null(lifecycle.NativeSessionId);
        Assert.Empty(lifecycle.Ancestry);
    }

    [Fact]
    public void AddInfrastructure_CursorAcpOptions_HaveSafeDefaults()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<CursorAcpOptions>>().Value;

        Assert.Equal(CursorAcpOptions.DefaultClientName, options.ClientName);
        Assert.Equal(CursorAcpOptions.DefaultClientVersion, options.ClientVersion);
        Assert.True(options.VersionProbeTimeout > TimeSpan.Zero);
        Assert.True(options.HandshakeTimeout > TimeSpan.Zero);
        Assert.True(options.OutboundQueueCapacity > 0);
    }

    [Fact]
    public void AddInfrastructure_InvalidCursorAcpOptions_FailValidation()
    {
        using var dataDirectory = new TestDirectory();
        var services = new ServiceCollection();
        services.AddInfrastructure(dataDirectory.Root);
        services.Configure<CursorAcpOptions>(options => options.HandshakeTimeout = TimeSpan.Zero);

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CursorAcpOptions>>().Value);
    }
}
