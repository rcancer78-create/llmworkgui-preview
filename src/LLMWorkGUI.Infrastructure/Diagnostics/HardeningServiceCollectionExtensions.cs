using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Data;
using LLMWorkGUI.Application.Diagnostics;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Lifecycle;
using LLMWorkGUI.Infrastructure.Quotas;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LLMWorkGUI.Infrastructure.Diagnostics;

/// <summary>
/// Composition of the Phase 12 hardening services: the redacted diagnostic bundle, the SQLite
/// backup/restore service and the startup crash recovery. They are registered next to the existing
/// infrastructure graph; every optional collaborator is resolved through <c>GetService</c> so a
/// reduced composition still composes (ROADMAP Phase 12).
/// </summary>
public static class HardeningServiceCollectionExtensions
{
    public static IServiceCollection AddHardeningServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDatabaseBackupService, DatabaseBackupService>();

        services.TryAddSingleton<IDiagnosticBundleService>(serviceProvider => new DiagnosticBundleService(
            serviceProvider.GetRequiredService<ISqliteConnectionFactory>(),
            serviceProvider.GetRequiredService<IWorkflowSecretScanner>(),
            serviceProvider.GetRequiredService<SensitiveDataFilter>(),
            serviceProvider.GetRequiredService<StorageOptions>(),
            serviceProvider.GetService<IHealthEventRepository>(),
            serviceProvider.GetService<ICliDetectionService>(),
            serviceProvider.GetService<TimeProvider>()));

        services.TryAddSingleton<IAppCrashRecoveryService>(serviceProvider => new AppCrashRecoveryService(
            serviceProvider.GetRequiredService<ISqliteConnectionFactory>(),
            serviceProvider.GetRequiredService<IExecutionRepository>(),
            serviceProvider.GetRequiredService<ISessionRepository>(),
            serviceProvider.GetRequiredService<IProjectRepository>(),
            serviceProvider.GetRequiredService<IWorkflowRunRepository>(),
            serviceProvider.GetRequiredService<IProjectLockRepository>(),
            serviceProvider.GetService<IApplicationInstanceGuard>(),
            serviceProvider.GetService<IHealthEventRepository>(),
            serviceProvider.GetService<IHealthCenterService>(),
            serviceProvider.GetService<TimeProvider>(),
            nativeGatewayRecovery: serviceProvider.GetService<LLMWorkGUI.Application.Reconciliation.INativeGatewayJournalRecoveryService>()));

        // Milestone 12B: long-term stability services. Retention cleanup keeps only recent data,
        // the long-running execution supervisor enforces bounded timeouts, and the quota soak
        // runner proves polling stability over many cycles.
        services.TryAddSingleton<ILongRunningExecutionService>(serviceProvider =>
            new LongRunningExecutionService(serviceProvider.GetService<TimeProvider>()));

        services.TryAddSingleton<IRetentionCleanupService>(serviceProvider => new RetentionCleanupService(
            serviceProvider.GetRequiredService<StorageOptions>(),
            serviceProvider.GetService<TimeProvider>(),
            RetentionPolicy.Default,
            serviceProvider.GetService<ISqliteConnectionFactory>(),
            serviceProvider.GetService<IApplicationInstanceGuard>() ?? UnavailableSupervisorGuard.Instance));

        services.TryAddSingleton<IQuotaPollingSoakRunner>(serviceProvider => new QuotaPollingSoakRunner(
            serviceProvider.GetRequiredService<IQuotaRefreshScheduler>(),
            serviceProvider.GetService<TimeProvider>(),
            serviceProvider.GetService<IQuotaSoakTimerProbe>()));

        return services;
    }

    // Reduced UI compositions may display diagnostics without a supervisor graph.
    // They must remain constructible, but cannot gain destructive cleanup authority.
    private sealed class UnavailableSupervisorGuard : IApplicationInstanceGuard
    {
        public static readonly UnavailableSupervisorGuard Instance = new();
        public string InstanceId => "unavailable";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new SecondaryInstanceReadOnlyException(
            "Очистка недоступна: основной экземпляр приложения не подключён.");
        public void Dispose() { }
    }
}
