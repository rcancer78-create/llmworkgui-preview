using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Executions;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Watchdogs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var retentionOptions = services.AddOptions<RetentionOptions>();
        var storageOptions = services.AddOptions<StorageOptions>();
        var gatewayHttpOptions = services.AddOptions<NativeGatewayHttpOptions>();
        var processSupervisorOptions = services.AddOptions<ProcessSupervisorOptions>();
        var quotaSchedulerOptions = services.AddOptions<LLMWorkGUI.Application.Quotas.QuotaSchedulerOptions>();
        var balancedWeightsOptions = services.AddOptions<LLMWorkGUI.Application.Routing.BalancedScoringWeights>();

        if (configuration is not null)
        {
            retentionOptions.Bind(configuration.GetSection(RetentionOptions.SectionName));
            storageOptions.Bind(configuration.GetSection(StorageOptions.SectionName));
            gatewayHttpOptions.Bind(configuration.GetSection(NativeGatewayHttpOptions.SectionName));
            processSupervisorOptions.Bind(configuration.GetSection(ProcessSupervisorOptions.SectionName));
            quotaSchedulerOptions.Bind(configuration.GetSection(LLMWorkGUI.Application.Quotas.QuotaSchedulerOptions.SectionName));
            balancedWeightsOptions.Bind(configuration.GetSection(LLMWorkGUI.Application.Routing.BalancedScoringWeights.SectionName));
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<RetentionOptions>, RetentionOptionsValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ProcessSupervisorOptions>, ProcessSupervisorOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<LLMWorkGUI.Application.Quotas.QuotaSchedulerOptions>,
            LLMWorkGUI.Application.Quotas.QuotaSchedulerOptionsValidator>());

        services.TryAddSingleton<IActivityTimelineService, ActivityTimelineService>();
        services.TryAddSingleton<IExecutionDeduplicationGuard, ExecutionDeduplicationGuard>();
        services.TryAddSingleton<IExecutionWatchdog, ExecutionWatchdog>();
        services.TryAddSingleton<RecoveryMatrix>();

        // Configured credentials require the lifecycle service; a composition without it blocks their
        // routes. Explicitly keyless profiles remain eligible (ADR-0005 §5.2).
        services.TryAddSingleton<LLMWorkGUI.Application.Routing.IRoutingEngine>(serviceProvider =>
            new LLMWorkGUI.Application.Routing.RoutingEngine(
                serviceProvider.GetRequiredService<IAccountRepository>(),
                serviceProvider.GetRequiredService<IQuotaSnapshotRepository>(),
                serviceProvider.GetService<IOptions<LLMWorkGUI.Application.Routing.BalancedScoringWeights>>(),
                serviceProvider.GetService<TimeProvider>(),
                serviceProvider.GetService<ILogger<LLMWorkGUI.Application.Routing.RoutingEngine>>(),
                serviceProvider.GetService<IProviderProfileRepository>(),
                serviceProvider.GetService<IEnumerable<LLMWorkGUI.Application.Accounts.IAccountBridge>>(),
                serviceProvider.GetService<LLMWorkGUI.Application.Health.IHealthCenterService>(),
                serviceProvider.GetService<LLMWorkGUI.Application.Security.ISecretLifecycleService>(),
                serviceProvider.GetService<LLMWorkGUI.Application.Providers.IModelRouteConfigurationService>() is { } modelConfiguration
                    ? new LLMWorkGUI.Application.Routing.ConfiguredRouteModelEligibility(modelConfiguration, serviceProvider.GetService<TimeProvider>())
                    : null,
                serviceProvider.GetService<IOptions<LLMWorkGUI.Application.Quotas.QuotaSchedulerOptions>>()));

        // The send path resolves the opened project through this service. The factory always returns a
        // resolver instance, so the project repository is required to resolve it; graphs without
        // repositories leave the resolver absent through the app-side composition helper instead of
        // resolving this service.
        services.TryAddSingleton(serviceProvider =>
        {
            var projectRepository = serviceProvider.GetRequiredService<IProjectRepository>();

            return new OpenedProjectResolver(
                projectRepository,
                serviceProvider.GetService<IApplicationSettingsRepository>());
        });

        return services;
    }
}
