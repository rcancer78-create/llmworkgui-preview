using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Observability;

/// <summary>
/// Composes the Phase 11 Activity Center stack: the redacting search index, the durable journal, the
/// bounded write queue and the ingestion boundary.
/// <para>
/// These registrations used to live inside the shell's own extension method, which meant the only way to
/// compose a real Activity Center was to compose the whole WPF shell. That is not a composition detail -
/// it is why the restart claim could only be tested by reading SQLite: there was no other way to build the
/// product's Activity Center outside a UI graph. The registrations now live with the services they
/// compose, so the production shell and a headless restart test build exactly the same object graph.
/// </para>
/// <para>
/// The journal and the write queue are registered only when the application database is present. A
/// UI-only graph keeps working without one, and the ingestion boundary then runs memory-only and reports
/// that through its statistics rather than pretending the history is saved.
/// </para>
/// </summary>
public static class ActivityCenterServiceCollectionExtensions
{
    /// <summary>Register before producers so reverse host stop ordering drains the journal last.</summary>
    public static IServiceCollection AddActivityJournalLifetime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ActivityJournalLifetime>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, ActivityJournalHostDrain>());
        return services;
    }

    public static IServiceCollection AddActivityCenter(
        this IServiceCollection services,
        int capacity = ActivityCenterService.DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        services.TryAddSingleton<SensitiveDataFilter>();

        var hasDatabase = services.Any(descriptor => descriptor.ServiceType == typeof(ISqliteConnectionFactory));

        if (hasDatabase)
        {
            services.AddActivityJournalLifetime();
            services.TryAddSingleton<IActivityEventJournal>(serviceProvider =>
                new SqliteActivityEventJournal(
                    serviceProvider.GetRequiredService<ISqliteConnectionFactory>(),
                    serviceProvider.GetService<TimeProvider>()));

            services.TryAddSingleton(serviceProvider =>
            {
                var queue = new ActivityJournalWriteQueue(serviceProvider.GetRequiredService<IActivityEventJournal>(),
                    logger: serviceProvider.GetService<ILogger<ActivityJournalWriteQueue>>());
                serviceProvider.GetRequiredService<ActivityJournalLifetime>().Attach(queue);
                return queue;
            });

            services.TryAddSingleton(_ => new ActivityJournalOptions());
        }

        // The in-memory index and the ingestion boundary share one bound, named once here rather than left
        // to two independent defaults. They used to default to the same 100 000 by coincidence of two
        // separate constant defaults, so raising only one of them silently left 90 000 searchable events
        // outside the index while the screen still reported them as retained.
        services.TryAddSingleton<IEventSearchIndex>(serviceProvider => new EventSearchIndex(
            serviceProvider.GetRequiredService<SensitiveDataFilter>().RedactDiagnostic,
            capacity));

        services.TryAddSingleton<IActivityCenterService>(serviceProvider =>
        {
            var queue = serviceProvider.GetService<ActivityJournalWriteQueue>();
            if (queue is not null) serviceProvider.GetService<ActivityJournalLifetime>()?.Attach(queue);
            return new ActivityCenterService(
            serviceProvider.GetRequiredService<SensitiveDataFilter>().RedactDiagnostic,
            serviceProvider.GetService<TimeProvider>(),
            serviceProvider.GetService<IEventSearchIndex>(),
            capacity,
            queue,
            serviceProvider.GetService<IActivityEventJournal>(),
            serviceProvider.GetService<ActivityJournalOptions>()?.RetentionLimit
                ?? ActivityJournalOptions.DefaultRetentionLimit,
            serviceProvider.GetService<ILogger<ActivityCenterService>>());
        });

        return services;
    }
}
