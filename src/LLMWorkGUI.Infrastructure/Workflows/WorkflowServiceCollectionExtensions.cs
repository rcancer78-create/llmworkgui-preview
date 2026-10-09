using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Legacy;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Workflows;

public static class WorkflowServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IWorkflowPackageRepository, SqliteWorkflowPackageRepository>();
        services.TryAddSingleton<IWorkflowVersionRepository, SqliteWorkflowVersionRepository>();
        services.TryAddSingleton<IWorkflowBindingRepository, SqliteWorkflowBindingRepository>();
        services.TryAddSingleton<IWorkflowBindingService, WorkflowBindingService>();
        services.TryAddSingleton<IWorkflowArchiveValidator, SafeArchiveValidator>();
        services.TryAddSingleton<IWorkflowImportService, WorkflowImportService>();
        services.TryAddSingleton<IWorkflowExportService, WorkflowExportService>();
        services.TryAddSingleton<IWorkflowPreviewService, WorkflowPreviewService>();
        services.TryAddSingleton<IWorkflowManifestParser, WorkflowManifestParser>();
        services.TryAddSingleton<IScratchWorkspaceManager, ScratchWorkspaceManager>();
        services.TryAddSingleton<ISanitizedCatalogProvider>(provider => new SanitizedCatalogProvider(
            provider.GetRequiredService<IProviderProfileRepository>(),
            provider.GetRequiredService<IAccountRepository>(),
            provider.GetRequiredService<IHealthStateRepository>(),
            provider.GetService<LLMWorkGUI.Application.Providers.IProviderModelCatalogSource>(),
            timeProvider: provider.GetService<TimeProvider>(),
            configuration: provider.GetService<LLMWorkGUI.Application.Providers.IModelRouteConfigurationService>()));
        services.TryAddSingleton<IWorkflowSecretScanner, WorkflowSecretScanner>();
        services.TryAddSingleton<IWorkflowMaterialPolicyStore, SqliteWorkflowMaterialPolicyStore>();
        services.TryAddSingleton<IAdaptationEgressPolicy, SqliteAdaptationEgressPolicy>();
        services.TryAddSingleton<OpenCodeAdaptationRuntimeRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService,
            AdaptationRuntimeHostedService>());
        services.TryAddSingleton<LLMWorkGUI.Application.Accounts.IAdaptationAccountConfigurationService>(
            provider => provider.GetRequiredService<OpenCodeAdaptationRuntimeRegistry>());
        services.TryAddSingleton<SqliteAdaptationTransportPolicy>();
        services.TryAddSingleton<IAdaptationTransportPolicy>(provider => provider.GetRequiredService<SqliteAdaptationTransportPolicy>());
        services.TryAddSingleton<IAdaptationPromptBuilder, WorkflowAdaptationPromptBuilder>();
        services.TryAddSingleton<IAdaptationResponseParser, AdaptationResponseParser>();
        services.TryAddSingleton<IAdaptationReferenceValidator, AdaptationReferenceValidator>();
        services.TryAddSingleton<ISemanticDiffEngine, SemanticDiffEngine>();
        services.TryAddSingleton<IWorkflowDiffService, WorkflowDiffService>();
        services.TryAddSingleton<IAdaptationModelInvoker, BackendAdaptationModelInvoker>();
        services.TryAddSingleton<IWorkflowAdaptationService, WorkflowAdaptationService>();
        services.TryAddSingleton<IWorkflowActivationService, WorkflowActivationService>();
        services.TryAddSingleton<ISupervisedLegacyWorkflowRunner, SupervisedLegacyWorkflowRunner>();
        services.TryAddSingleton<IWorkflowRunRepository, SqliteWorkflowRunRepository>();

        // The artifact a gated transition is authorized by is bytes on disk, not a row that says so. The run
        // service therefore re-hashes the committed blob before every advance, and it can only do that
        // through the application abstraction - the infrastructure blob store stays behind it.
        services.TryAddSingleton<IWorkflowArtifactBlobStore>(provider =>
            new WorkflowArtifactBlobStore(provider.GetRequiredService<WorkflowBlobStore>()));

        // Phase 10F: a run is pinned to the template version currently assigned to its project. The run
        // service therefore needs the template store, not just the run repository, and the composition is
        // written out explicitly so the dependencies that decide a run's two identities and its pinned
        // execution scheme are visible instead of implied by a constructor the container guesses at.
        //
        // The approver identity is composed here too, and deliberately not optional: the run service stamps
        // every user approval with whatever this source resolves, so a composition without it could not
        // record a decision at all rather than recording one under a name nobody supplied.
        services.TryAddSingleton<IUserApprovalIdentity, WindowsLogonUserApprovalIdentity>();
        services.TryAddSingleton<IWorkflowReviewEvidenceRepository>(provider =>
            new SqliteWorkflowReviewEvidenceRepository(
                provider.GetRequiredService<ISqliteConnectionFactory>(),
                provider.GetService<TimeProvider>()));
        services.TryAddSingleton<IWorkflowRunService>(provider => new WorkflowRunService(
            provider.GetRequiredService<IWorkflowRunRepository>(),
            provider.GetRequiredService<WorkflowScheme>(),
            provider.GetRequiredService<IWorkflowTemplateStore>(),
            provider.GetRequiredService<IWorkflowArtifactBlobStore>(),
            provider.GetRequiredService<IUserApprovalIdentity>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<IWorkflowReviewEvidenceRepository>()));
        services.TryAddSingleton(WorkflowScheme.CreateStandardDevelopmentScheme());
        services.TryAddSingleton<IWorkflowRunTimelineService, WorkflowRunTimelineService>();
        services.TryAddSingleton<RoleTransferEvidenceProjector>();
        services.TryAddSingleton<IWorkflowGraphValidator, WorkflowGraphValidator>();

        // The channel catalog is composed from the channels the container actually registered, and the one
        // channel it gets is the read-only star-cliproxy reviewer. It is a real channel on the real loopback
        // client, and it refuses every route: the gateway reports native provider, account, model and
        // session identifiers, and none of them reverse-maps to a unique persisted Routes.Id in this build.
        // A channel that echoed the requested route back would turn that refusal into a fabricated
        // observation, and an empty catalog would hide a missing fact behind a missing component - so the
        // channel is registered and says no, by name, with the identities it would need in the message.
        services.TryAddSingleton<IReviewChannelGatewayLocator, UnaddressableReviewChannelGatewayLocator>();
        services.TryAddSingleton<StarCliProxyReviewReadOnlyChannel>(provider => new StarCliProxyReviewReadOnlyChannel(
            provider.GetRequiredService<IRouteRepository>(),
            provider.GetRequiredService<IReviewChannelGatewayLocator>(),
            provider.GetService<IStarCliProxyClient>(),
            provider.GetService<ILogger<StarCliProxyReviewReadOnlyChannel>>(),
            provider.GetService<LLMWorkGUI.Application.Security.IWorkflowReviewEgressPolicy>()));
        services.TryAddSingleton<IWorkflowNodeChannel>(provider =>
            provider.GetRequiredService<StarCliProxyReviewReadOnlyChannel>());
        services.TryAddSingleton<IWorkflowChannelCatalog>(provider => new WorkflowChannelCatalog(
            provider.GetServices<IWorkflowNodeChannel>()));
        services.TryAddSingleton<IWorkflowNodeExecutor, WorkflowNodeExecutor>();
        services.TryAddSingleton<IWorkflowExecutionPlanService, WorkflowExecutionPlanService>();

        // Phase 10 model review: the product action that requests the assigned review of a run's current
        // stage artifact. Its collaborators are written out explicitly, because each of them decides whether
        // the request can be dispatched truthfully at all - the pinned template supplies the role-to-route
        // binding, the route store proves that binding is a real persisted route, and the blob store
        // re-hashes and re-reads the exact bytes under a bound.
        services.TryAddSingleton<IRouteRepository, SqliteRouteRepository>();
        services.TryAddSingleton<IWorkflowReviewDispatchStore, SqliteWorkflowReviewDispatchStore>();
        services.TryAddSingleton<IWorkflowReviewResponseRepository, SqliteWorkflowReviewResponseRepository>();
        services.TryAddSingleton<IWorkflowReviewRequestService>(provider => new WorkflowReviewRequestService(
            provider.GetRequiredService<IWorkflowRunRepository>(),
            provider.GetRequiredService<IWorkflowTemplateStore>(),
            provider.GetRequiredService<IWorkflowReviewEvidenceRepository>(),
            provider.GetRequiredService<IRouteRepository>(),
            provider.GetRequiredService<IProjectRepository>(),
            provider.GetRequiredService<ISessionRepository>(),
            provider.GetRequiredService<IExecutionRepository>(),
            provider.GetRequiredService<IWorkflowChannelCatalog>(),
            provider.GetRequiredService<IWorkflowArtifactBlobStore>(),
            provider.GetService<TimeProvider>(),
            provider.GetRequiredService<IWorkflowReviewDispatchStore>(),
            provider.GetService<LLMWorkGUI.Application.Security.IDataClassificationGate>(),
            provider.GetService<IWorkflowSecretScanner>()));

        // Phase 10E Workflow Studio: immutable template versions, document drafts, the pre-coder gate and
        // the account-context switch. The template store is durable: a saved version and the
        // project-to-template pointer survive a restart, and a save of an existing version is refused
        // instead of overwriting the graph a run may already depend on. The shipped standard template is
        // the store's seed, which is the same version the service owns in memory. The studio takes no
        // session id factory: a native session id has to come from the backend, never from this process.
        services.TryAddSingleton<IWorkflowTemplateStore>(provider => new SqliteWorkflowTemplateStore(
            provider.GetRequiredService<ISqliteConnectionFactory>(),
            provider.GetService<IWorkflowGraphValidator>(),
            new[] { WorkflowStudioService.CreateStandardTemplate() }));
        services.TryAddSingleton<IDocumentTemplateService>(provider => new DocumentTemplateService(
            provider.GetService<IWorkflowSecretScanner>(),
            provider.GetService<TimeProvider>(),
            userApprovalIdentity: provider.GetRequiredService<IUserApprovalIdentity>()));
        services.TryAddSingleton<IPreCoderGateValidator, PreCoderGateValidator>();
        services.TryAddSingleton<IWorkflowStudioService>(provider => new WorkflowStudioService(
            provider.GetService<IWorkflowGraphValidator>(),
            provider.GetService<IWorkflowTemplateStore>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<IWorkflowRunRepository>(),
            provider.GetService<IWorkflowPackageRepository>()));

        // Phase 12 Milestone 12B: the deterministic end-to-end acceptance scenario coordinator. Every
        // collaborator is optional so a reduced composition still resolves the runner and gets an
        // explicit not-composed report instead of a composition failure.
        services.TryAddSingleton<IEndToEndWorkflowScenarioRunner>(provider => new EndToEndWorkflowScenarioRunner(
            provider.GetService<IPreCoderGateValidator>(),
            provider.GetService<IWorkflowStudioService>(),
            provider.GetService<IWorkflowRunService>(),
            provider.GetService<WorkflowScheme>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<IWorkflowArtifactBlobStore>()));

        return services;
    }
}
