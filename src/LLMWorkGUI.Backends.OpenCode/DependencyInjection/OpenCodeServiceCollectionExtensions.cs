using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Health;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Routing;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Discovery;
using LLMWorkGUI.Backends.OpenCode.Events;
using LLMWorkGUI.Backends.OpenCode.Health;
using LLMWorkGUI.Backends.OpenCode.Routing;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Backends.OpenCode.DependencyInjection;

public static class OpenCodeServiceCollectionExtensions
{
    public static IServiceCollection AddOpenCodeBackend(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddOptions<OpenCodeServerOptions>()
            .Validate(
                IsValidOptions,
                "OpenCode server options are invalid: the hostname must be loopback, the port must be >= 0, and timeouts must be positive.");

        services
            .AddOptions<OpenCodeStreamOptions>()
            .Validate(
                IsValidStreamOptions,
                "OpenCode stream options are invalid: MaxInMemoryEvents and reconnect delays must be positive, and SpoolDirectory must not be empty when specified.");

        services
            .AddOptions<OpenCodeSessionLifecycleOptions>()
            .Validate(
                IsValidSessionLifecycleOptions,
                "OpenCode session lifecycle options are invalid: TurnTimeout and CancellationTimeout must be positive.");

        services
            .AddOptions<OpenCodeCapabilityCacheOptions>()
            .Validate(
                IsValidCapabilityCacheOptions,
                "OpenCode capability cache options are invalid: the TTL must be positive.");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OpenCodeManagedServerCredential>();
        services.TryAddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan });
        services.TryAddSingleton<IProcessIdResolver>(WindowsChildProcessIdResolver.Instance);
        services.TryAddSingleton<IOpenCodeDiscoveryService, OpenCodeDiscoveryService>();
        services.TryAddSingleton<IOpenCodeServerManager, OpenCodeServerManager>();
        services.TryAddSingleton<OpenCodeServerConnection>();
        services.TryAddSingleton<IOpenCodeSseParser, OpenCodeSseParser>();
        services.TryAddSingleton<IOpenCodeEventStreamService>(CreateEventStreamService);
        services.TryAddSingleton<IOpenCodeClient>(CreateClient);
        services.TryAddSingleton<IOpenCodeSessionLifecycleService>(CreateSessionLifecycleService);
        services.TryAddSingleton<IOpenCodeHealthEventSink>(CreateHealthEventCollector);
        services.TryAddSingleton<IRouteVerificationService>(CreateRouteVerificationService);
        services.TryAddSingleton<IOpenCodeCapabilityCacheService>(CreateCapabilityCacheService);

        return services;
    }

    private static bool IsValidOptions(OpenCodeServerOptions options)
    {
        try
        {
            options.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsValidStreamOptions(OpenCodeStreamOptions options)
    {
        try
        {
            options.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsValidSessionLifecycleOptions(OpenCodeSessionLifecycleOptions options)
    {
        try
        {
            options.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsValidCapabilityCacheOptions(OpenCodeCapabilityCacheOptions options)
    {
        try
        {
            options.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IOpenCodeEventStreamService CreateEventStreamService(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<OpenCodeStreamOptions>>().Value;
        var baseUrl = ResolveBaseUrl(serviceProvider);

        return new OpenCodeEventStreamService(
            serviceProvider.GetRequiredService<HttpClient>(),
            baseUrl,
            serviceProvider.GetRequiredService<IOpenCodeSseParser>(),
            options,
            serviceProvider.GetService<ILogger<OpenCodeEventStreamService>>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            ResolveManagedBaseUrl(serviceProvider),
            serviceProvider.GetRequiredService<OpenCodeManagedServerCredential>());
    }

    private static IOpenCodeClient CreateClient(IServiceProvider serviceProvider)
    {
        var baseUrl = ResolveBaseUrl(serviceProvider);

        return new OpenCodeClient(
            serviceProvider.GetRequiredService<HttpClient>(),
            baseUrl,
            eventStreamService: serviceProvider.GetRequiredService<IOpenCodeEventStreamService>(),
            resolveBaseUrl: ResolveManagedBaseUrl(serviceProvider),
            adaptationTransportPolicy: serviceProvider.GetService<LLMWorkGUI.Application.Workflows.IAdaptationTransportPolicy>(),
            managedCredential: serviceProvider.GetRequiredService<OpenCodeManagedServerCredential>(),
            resolveProfileBaseUrl: ResolveProfileBaseUrl(serviceProvider));
    }

    private static IOpenCodeSessionLifecycleService CreateSessionLifecycleService(IServiceProvider serviceProvider)
    {
        return new OpenCodeSessionLifecycleService(
            serviceProvider.GetRequiredService<IOpenCodeClient>(),
            serviceProvider.GetService<IProjectLockRepository>(),
            serviceProvider.GetService<IOptions<OpenCodeSessionLifecycleOptions>>()?.Value,
            serviceProvider.GetRequiredService<TimeProvider>());
    }

    private static IOpenCodeHealthEventSink CreateHealthEventCollector(IServiceProvider serviceProvider)
    {
        // The Health Center is preferred: it applies the normative transition table and writes the
        // recovery audit. The raw repository stays as the fallback for graphs without one.
        return new OpenCodeHealthEventCollector(
            serviceProvider.GetService<IHealthStateRepository>(),
            serviceProvider.GetService<ILogger<OpenCodeHealthEventCollector>>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetService<LLMWorkGUI.Application.Health.IHealthCenterService>(),
            serviceProvider.GetService<IAccountRepository>(),
            serviceProvider.GetService<IProviderProfileRepository>(),
            serviceProvider.GetService<IOptions<OpenCodeServerOptions>>(),
            serviceProvider.GetService<IOpenCodeClient>());
    }

    private static IRouteVerificationService CreateRouteVerificationService(IServiceProvider serviceProvider)
    {
        return new OpenCodeRouteVerificationService(serviceProvider.GetRequiredService<TimeProvider>());
    }

    private static IOpenCodeCapabilityCacheService CreateCapabilityCacheService(IServiceProvider serviceProvider)
    {
        return new OpenCodeCapabilityCacheService(
            serviceProvider.GetRequiredService<IOpenCodeClient>(),
            serviceProvider.GetService<IOptions<OpenCodeCapabilityCacheOptions>>()?.Value,
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetService<IOpenCodeHealthEventSink>());
    }

    private static Uri ResolveBaseUrl(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<IOptions<OpenCodeServerOptions>>().Value;

        return new Uri($"http://{options.Hostname}:{options.Port}", UriKind.Absolute);
    }

    private static Func<CancellationToken, Task<Uri>>? ResolveManagedBaseUrl(IServiceProvider serviceProvider)
    {
        // A configured nonzero port remains an explicit external server endpoint.
        var connection = serviceProvider.GetRequiredService<OpenCodeServerConnection>();
        return serviceProvider.GetRequiredService<IOptions<OpenCodeServerOptions>>().Value.Port == 0
            ? token => connection.GetBaseUrlAsync(token)
            : null;
    }

    private static Func<string, CancellationToken, Task<Uri>>? ResolveProfileBaseUrl(IServiceProvider serviceProvider)
    {
        var connection = serviceProvider.GetRequiredService<OpenCodeServerConnection>();
        return serviceProvider.GetRequiredService<IOptions<OpenCodeServerOptions>>().Value.Port == 0
            ? (profile, token) => connection.GetBaseUrlAsync(profile, token)
            : null;
    }
}
