using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Logging;

public static class RedactingLoggingExtensions
{
    public static ILoggingBuilder AddRedactingLogging(
        this ILoggingBuilder builder,
        Action<ILoggingBuilder>? configureInner = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ClearProviders();
        builder.Services.TryAddSingleton<SensitiveDataFilter>();

        builder.Services.AddSingleton<ILoggerProvider>(serviceProvider =>
        {
            var sensitiveDataFilter = serviceProvider.GetRequiredService<SensitiveDataFilter>();

            var innerLoggerFactory = LoggerFactory.Create(inner =>
            {
                inner.AddDebug();
                inner.AddConsole();

                configureInner?.Invoke(inner);
            });

            return new RedactingLoggerProvider(
                sensitiveDataFilter,
                new LoggerFactoryLoggerProvider(innerLoggerFactory));
        });

        return builder;
    }
}
