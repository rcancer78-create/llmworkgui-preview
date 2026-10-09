using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.Mirasim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LLMWorkGUI.App.DependencyInjection;

public static class AppServiceCollectionExtensions
{
    public static IServiceCollection AddAppUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Mirasim is composed as its own backend: the loopback client and the session lifecycle feed
        // the Workspace Mirasim panel and never replace an existing backend (ADR-0008 §1, §2).
        services.AddMirasimBackend();

        services.TryAddSingleton<IThemeResourceApplier, ThemeResourceApplier>();
        services.TryAddSingleton<ISystemThemeProvider, SystemThemeProvider>();
        services.TryAddSingleton<IThemeService, ThemeService>();

        services.TryAddSingleton<CliStatusViewModel>();

        // The status bar consumes the Health Center service when the composition provides one, so the
        // observed breaker state is visible outside the Health Center screen (ТЗ §7.3).
        services.TryAddSingleton(serviceProvider => new StatusBarViewModel(
            serviceProvider.GetRequiredService<CliStatusViewModel>(),
            serviceProvider.GetService<IApplicationInstanceGuard>(),
            serviceProvider.GetService<IHealthCenterService>()));

        services.TryAddSingleton<ThemeSelectorViewModel>();

        services.AddUnifiedWorkspaceShell();

        // The workspace screens resolve the project repository and the data classification gate from the
        // composition so the stored project classification reaches the fail-closed send path (ТЗ §6.5).
        services.TryAddSingleton(serviceProvider => new WorkspaceViewModel(
            serviceProvider.GetRequiredService<IActivityTimelineService>(),
            serviceProvider.GetService<IOpenCodeSessionLifecycleService>(),
            serviceProvider.GetService<IOpenCodeClient>(),
            serviceProvider.GetService<IOpenCodeCapabilityCacheService>(),
            serviceProvider.GetService<TimeProvider>(),
            serviceProvider.GetService<StatusBarViewModel>(),
            serviceProvider.GetService<CursorWorkspaceViewModel>(),
            serviceProvider.GetService<LLMWorkGUI.Application.Health.IStickyRouteTurnGate>(),
            serviceProvider.GetService<MirasimWorkspaceViewModel>(),
            serviceProvider.GetService<LLMWorkGUI.Application.Security.IDataClassificationGate>(),
            serviceProvider.GetService<IProjectRepository>(),
            ResolveOpenedProjectResolver(serviceProvider),
            serviceProvider.GetService<LLMWorkGUI.Backends.OpenCode.Sessions.IOpenCodeExecutionJournal>(),
            serviceProvider.GetService<ICheckoutLockService>(),
            serviceProvider.GetService<IApplicationInstanceGuard>(),
            serviceProvider.GetService<NativeGatewayWorkspaceViewModel>(),
            serviceProvider.GetService<LLMWorkGUI.Backends.OpenCode.OpenCodeServerConnection>()));

        services.TryAddSingleton<IEgressPreviewPresenter, LLMWorkGUI.App.Views.EgressPreviewDialogPresenter>();
        services.TryAddSingleton<NativeGatewayComposerDrain>();
        services.AddHostedService<NativeGatewayComposerDrain>(serviceProvider => serviceProvider.GetRequiredService<NativeGatewayComposerDrain>());
        services.TryAddSingleton(serviceProvider =>
        {
            var composer = new NativeGatewayWorkspaceViewModel(
            serviceProvider.GetService<LLMWorkGUI.Application.Providers.INativeGatewayTurnService>(),
            serviceProvider.GetService<LLMWorkGUI.Application.Providers.INativeGatewayRouteCatalog>(),
            ResolveOpenedProjectResolver(serviceProvider), serviceProvider.GetService<IApplicationInstanceGuard>(),
            serviceProvider.GetService<LLMWorkGUI.Infrastructure.Security.SensitiveDataFilter>(),
            serviceProvider.GetService<LLMWorkGUI.Application.Providers.INativeGatewayEgressService>(),
            serviceProvider.GetService<IEgressPreviewPresenter>(),
            serviceProvider.GetService<IHostApplicationLifetime>());
            serviceProvider.GetRequiredService<NativeGatewayComposerDrain>().Attach(composer);
            return composer;
        });

        services.TryAddSingleton(serviceProvider => new CursorWorkspaceViewModel(
            serviceProvider.GetService<ICursorAcpSessionLifecycleService>(),
            serviceProvider.GetService<ICursorAcpModePolicy>(),
            serviceProvider.GetService<ICheckoutLockService>(),
            serviceProvider.GetService<LLMWorkGUI.Application.Security.IDataClassificationGate>(),
            serviceProvider.GetService<IProjectRepository>(),
            ResolveOpenedProjectResolver(serviceProvider),
            serviceProvider.GetService<ICursorAcpExecutionJournal>()));

        // WorkspaceViewModel receives the Mirasim panel through constructor injection, exactly like the
        // Cursor panel, so the shipped XAML renders it as soon as the backend is composed.
        services.TryAddSingleton(serviceProvider => new MirasimWorkspaceViewModel(
            serviceProvider.GetService<IMirasimClient>(),
            serviceProvider.GetService<IMirasimSessionLifecycleService>(),
            serviceProvider.GetService<IHealthCenterService>(),
            serviceProvider.GetService<LLMWorkGUI.Application.Security.IDataClassificationGate>(),
            serviceProvider.GetService<IProjectRepository>(),
            ResolveOpenedProjectResolver(serviceProvider)));

        services.TryAddSingleton<ProvidersAccountsViewModel>();
        services.TryAddSingleton<ModelsRoutesViewModel>();
        services.TryAddSingleton<QuotasViewModel>();
        services.TryAddSingleton(serviceProvider => new HealthCenterViewModel(
            serviceProvider.GetRequiredService<CliStatusViewModel>(),
            serviceProvider.GetService<IHealthCenterService>(),
            serviceProvider.GetService<IHealthProbeService>(),
            serviceProvider.GetService<IImpactedSessionService>(),
            serviceProvider.GetService<StatusBarViewModel>()));
        services.TryAddSingleton<SettingsDiagnosticsViewModel>();

        // The workflow library consumes the immutable workflow services when the composition provides
        // them; the screen still resolves without them and reports itself unavailable (ТЗ §7.2).
        //
        // The registered studio, document-template, gate, timeline, run, session and execution services
        // and the writer-lock policy are forwarded to the screen, so the shipped Workflow Console
        // observes the same run aggregate and the same stored executions the application registered
        // instead of privately constructed fallbacks (ROADMAP 10D, 10E).
        //
        // The composed run service is forwarded the same way: the shipped Start/Advance buttons execute
        // real, persisted runs through the registered service, and a graph that does not compose one -
        // the headless UI test host - simply leaves the commands unoffered (ROADMAP 10G).
        services.TryAddSingleton(serviceProvider => new WorkflowLibraryViewModel(
            serviceProvider.GetService<IWorkflowPackageRepository>(),
            serviceProvider.GetService<IWorkflowVersionRepository>(),
            serviceProvider.GetService<IWorkflowBindingRepository>(),
            serviceProvider.GetService<IWorkflowBindingService>(),
            serviceProvider.GetService<IWorkflowPreviewService>(),
            serviceProvider.GetService<IWorkflowImportService>(),
            serviceProvider.GetService<IWorkflowExportService>(),
            serviceProvider.GetService<IProjectRepository>(),
            serviceProvider.GetService<IWorkflowActivationService>(),
            serviceProvider.GetService<IWorkflowAdaptationService>(),
            serviceProvider.GetService<ISanitizedCatalogProvider>(),
            serviceProvider.GetService<IWorkflowRunTimelineService>(),
            serviceProvider.GetService<IWorkflowStudioService>(),
            serviceProvider.GetService<IDocumentTemplateService>(),
            serviceProvider.GetService<IPreCoderGateValidator>(),
            serviceProvider.GetService<IWorkflowRunRepository>(),
            serviceProvider.GetService<IWorkflowRunService>(),
            serviceProvider.GetService<ISessionRepository>(),
            serviceProvider.GetService<IExecutionRepository>(),
            serviceProvider.GetService<ICheckoutLockService>(),
            serviceProvider.GetService<RoleTransferEvidenceProjector>(),
            serviceProvider.GetService<TimeProvider>(),
            serviceProvider.GetService<IWorkflowArtifactBlobStore>(),
            serviceProvider.GetService<IUserApprovalIdentity>(),
            serviceProvider.GetService<IWorkflowReviewRequestService>(),
            serviceProvider.GetService<IWorkflowMaterialPolicyStore>()));

        services.TryAddSingleton<MainWindowViewModel>();

        return services;
    }

    /// <summary>
    /// Resolves the opened-project resolver for the workspace screens. A graph without the project
    /// repository must not resolve the resolver service (its factory requires the repository), so the
    /// panels receive <c>null</c> and their send path reports the missing context fail-closed (ТЗ §6.5).
    /// </summary>
    private static OpenedProjectResolver? ResolveOpenedProjectResolver(IServiceProvider serviceProvider)
    {
        var projectRepository = serviceProvider.GetService<IProjectRepository>();
        if (projectRepository is null)
        {
            return null;
        }

        return serviceProvider.GetService<OpenedProjectResolver>()
            ?? new OpenedProjectResolver(
                projectRepository,
                serviceProvider.GetService<IApplicationSettingsRepository>());
    }
}
