using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace LLMWorkGUI.Infrastructure.Mirasim;

public static class MirasimServiceCollectionExtensions
{
    public static IServiceCollection AddMirasimBackend(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services
            .AddOptions<MirasimOptions>()
            .Validate(
                IsValidOptions,
                "Mirasim options are invalid: the hostname must be loopback and the port must be between 1 and 65535.");

        if (configuration is not null)
        {
            optionsBuilder.Bind(configuration.GetSection(MirasimOptions.SectionName));
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMirasimClient>(CreateClient);
        services.TryAddSingleton<IMirasimSessionLifecycleService>(CreateSessionLifecycleService);
        services.TryAddSingleton<MirasimLifecycleHostedService>();
        services.AddHostedService(provider => provider.GetRequiredService<MirasimLifecycleHostedService>());

        return services;
    }

    private static bool IsValidOptions(MirasimOptions options)
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

    private static IMirasimClient CreateClient(IServiceProvider serviceProvider)
    {
        var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            return new MirasimClient(
                httpClient,
                serviceProvider.GetRequiredService<IOptions<MirasimOptions>>(),
                serviceProvider.GetService<ILogger<MirasimClient>>(),
                serviceProvider.GetService<TimeProvider>(), ownsHttpClient: true);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    private static IMirasimSessionLifecycleService CreateSessionLifecycleService(IServiceProvider serviceProvider)
    {
        var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        try
        {
            return new MirasimSessionLifecycleService(
                httpClient,
                serviceProvider.GetRequiredService<IOptions<MirasimOptions>>(),
                serviceProvider.GetService<ICheckoutLockService>(),
                serviceProvider.GetService<ILogger<MirasimSessionLifecycleService>>(),
                serviceProvider.GetService<TimeProvider>(),
                serviceProvider.GetService<LLMWorkGUI.Application.Security.IMirasimEgressPolicy>(),
                serviceProvider.GetService<LLMWorkGUI.Application.Security.IMirasimExecutionJournal>(),
                serviceProvider.GetRequiredService<MirasimLifecycleHostedService>(), ownsHttpClient: true);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }
}
