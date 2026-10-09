using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.ViewModels.Onboarding;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Data;
using LLMWorkGUI.Application.Diagnostics;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Diagnostics;
using LLMWorkGUI.Infrastructure.Observability;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.App.Shell;

/// <summary>
/// Registers the unified workspace shell, its layout persistence and the Phase 11 Activity Center
/// stack next to the existing UI services. The composition root decides when to call this; the shell
/// does not alter the baseline main window. The search index is always built on top of
/// <see cref="SensitiveDataFilter.Redact"/>, so Activity Center queries can never reveal secret
/// material (ТЗ §9.3).
/// <para>
/// The layout store is bound to the configured application-data root rather than to the user profile,
/// so a host composed over an isolated root keeps its shell memory inside that root. An ordinary
/// production start configures no root and keeps the shipped
/// <c>%LOCALAPPDATA%\LLMWorkGUI\shell-layout.json</c>.
/// </para>
/// </summary>
public static class ShellServiceCollectionExtensions
{
    public static IServiceCollection AddUnifiedWorkspaceShell(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILayoutPersistenceService>(serviceProvider =>
            new LayoutPersistenceService(LayoutPersistenceService.ResolveFilePath(
                serviceProvider.GetService<StorageOptions>()?.AppDataDirectory)));
        services.TryAddSingleton<IClipboardService, ClipboardService>();
        services.TryAddSingleton<SensitiveDataFilter>();

        // Phase 11: the Activity Center stream is durable. The journal, the bounded write queue and the
        // ingestion boundary are composed here, next to the services that consume them, so the shell and a
        // headless restart test build exactly the same object graph.
        services.AddActivityCenter();

        services.TryAddSingleton(serviceProvider => new DiffArtifactViewerViewModel(
            serviceProvider.GetService<IClipboardService>()));

        // The production scheduler marshals the coalesced refresh onto the WPF dispatcher; the headless
        // compositions fall back to the immediate scheduler, which keeps them deterministic.
        services.TryAddSingleton<IActivityUiScheduler>(_ => new DispatcherActivityUiScheduler(
            System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher));

        // The durable query is not UI work and must not run on the dispatcher: a counted, paged FTS query
        // over the normative 190 000 retained rows measured p50 258.5 ms and p95 511.1 ms, which is a
        // quarter-second freeze per keystroke. One named background thread, reused for every query, keeps
        // that cost off the UI thread and attributes it to this component in a stack dump. The
        // compositions that have no dispatcher keep the inline executor, which runs the query on the
        // calling thread and therefore keeps their assertions synchronous.
        services.TryAddSingleton<ActivityQueryLifetime>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, ActivityQueryHostDrain>());
        services.TryAddSingleton(serviceProvider =>
        {
            var worker = new BoundedActivityQueryExecutor();
            serviceProvider.GetRequiredService<ActivityQueryLifetime>().Attach(worker);
            return worker;
        });
        services.TryAddSingleton<IActivityQueryExecutor>(
            serviceProvider => serviceProvider.GetRequiredService<BoundedActivityQueryExecutor>());

        services.TryAddSingleton(serviceProvider => new ActivityVisibilityLatencyRecorder(
            serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System));

        services.TryAddSingleton(serviceProvider => new ActivityCenterViewModel(
            serviceProvider.GetRequiredService<IActivityCenterService>(),
            serviceProvider.GetService<DiffArtifactViewerViewModel>(),
            serviceProvider.GetService<TimeProvider>(),
            serviceProvider.GetService<IClipboardService>(),
            ActivityCenterViewModel.DefaultPageSize,
            serviceProvider.GetService<IActivityUiScheduler>(),
            serviceProvider.GetService<ActivityVisibilityLatencyRecorder>(),
            minimumRefreshInterval: null,
            queryExecutor: serviceProvider.GetService<IActivityQueryExecutor>()));

        // Phase 11 R1: the product's own event sources now feed the Activity Center. Until this existed no
        // shipped code path called Append, so the shipped journal stayed empty in an ordinary session and
        // only a load driver ever produced activity events. The bridge subscribes to notification seams the
        // product already raises (health transitions, Cursor ACP turn state and permission requests,
        // stalled long-running executions, quota refreshes) and maps each one through the same ingestion
        // boundary. Resolved eagerly so the wiring happens at startup rather than the first time some
        // screen happens to ask for the bridge.
        services.TryAddSingleton(serviceProvider => new ActivityCenterEventBridge(
            serviceProvider.GetRequiredService<IActivityCenterService>(),
            new object?[]
            {
                serviceProvider.GetService<IHealthCenterService>(),
                serviceProvider.GetService<ICursorAcpSessionLifecycleService>(),
                serviceProvider.GetService<ILongRunningExecutionService>(),
                serviceProvider.GetService<IQuotaRefreshScheduler>()
            },
            serviceProvider.GetService<TimeProvider>()));

        // Frictionless onboarding (ROADMAP Phase 11): fully local, no API keys and no paid model calls.
        // The repository services are optional, so the wizard also composes in UI-only graphs.
        services.TryAddSingleton(serviceProvider => new OnboardingViewModel(
            serviceProvider.GetService<CliStatusViewModel>(),
            serviceProvider.GetService<WorkflowLibraryViewModel>(),
            serviceProvider.GetService<IApplicationSettingsRepository>(),
            serviceProvider.GetService<IProjectRepository>(),
            serviceProvider.GetService<TimeProvider>(),
            serviceProvider.GetService<IStarCliProxyExecutableResolver>(),
            serviceProvider.GetService<IOptions<MirasimOptions>>()?.Value));

        // The consolidated studio/monitor console reuses the same workflow library instance as the
        // Workflows screen, so no library state is duplicated.
        services.TryAddSingleton(serviceProvider => new WorkflowConsolidatedViewModel(
            serviceProvider.GetService<WorkflowLibraryViewModel>()));

        services.TryAddSingleton(serviceProvider => new UnifiedWorkspaceShellViewModel(
            serviceProvider.GetRequiredService<MainWindowViewModel>(),
            serviceProvider.GetService<ILayoutPersistenceService>(),
            serviceProvider.GetService<ActivityCenterViewModel>(),
            serviceProvider.GetService<OnboardingViewModel>(),
            serviceProvider.GetService<WorkflowConsolidatedViewModel>(),
            autoOpenOnboarding: true));

        // Phase 12 hardening services are composed only when the infrastructure graph (and its SQLite
        // connection factory) is present, so UI-only compositions keep working without a database.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ISqliteConnectionFactory)))
        {
            services.AddHardeningServices();
        }

        services.TryAddSingleton(serviceProvider => new HardeningDiagnosticsViewModel(
            serviceProvider.GetService<IDiagnosticBundleService>(),
            serviceProvider.GetService<IDatabaseBackupService>(),
            serviceProvider.GetService<IAppCrashRecoveryService>(),
            serviceProvider.GetService<IRetentionCleanupService>(),
            serviceProvider.GetService<IQuotaPollingSoakRunner>(),
            serviceProvider.GetService<IEndToEndWorkflowScenarioRunner>(),
            serviceProvider.GetService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()?.ApplicationStopping
                ?? CancellationToken.None));

        return services;
    }
}
