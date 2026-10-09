using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Retention;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Watchdogs;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Backends.OpenCode.DependencyInjection;
using LLMWorkGUI.Infrastructure.Cli;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Diagnostics;
using LLMWorkGUI.Infrastructure.Logging;
using LLMWorkGUI.Infrastructure.Observability;
using LLMWorkGUI.Infrastructure.Processes;
using LLMWorkGUI.Infrastructure.Reconciliation;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Retention;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using LLMGateway.Core;
using LLMGateway.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string? appDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        services.AddLogging(logging => logging.AddRedactingLogging());
        // Hosted services stop in reverse registration order. Drain the journal after its producers.
        services.AddActivityJournalLifetime();

        services.TryAddSingleton(serviceProvider => ResolveStorageOptions(serviceProvider, appDataDirectory));

        // LLMGateway is vendored from the reviewed D:\work\LLMGateway solution and composed in
        // library mode. Resolve its root from the same effective StorageOptions as the rest of the
        // application, so the default, configured, and explicit composition paths cannot diverge.
        // Profile discovery is disabled for this first slice: resolving the service must not inspect
        // or persist the user's native profile until an explicit account-discovery operation exists.
        services.AddLlmGateway(new GatewayOptions { DiscoverProfiles = false });
        services.TryAddSingleton<LLMWorkGUI.Application.Reviews.IGrokBotReviewTransport, LLMWorkGUI.Infrastructure.GrokBot.GrokBotReviewTransport>();
        services.AddSingleton<IProviderAdapter, LLMWorkGUI.Infrastructure.GrokBot.GrokBotProviderAdapter>();
        services.RemoveAll<GatewayOptions>();
        services.TryAddSingleton<GatewayOptions>(serviceProvider =>
        {
            var root = serviceProvider.GetRequiredService<StorageOptions>().AppDataDirectory
                ?? AppDataPaths.DefaultRootDirectory;
            var gatewayRoot = Path.Combine(root, "llmgateway");
            return new GatewayOptions
            {
                AccountsFile = Path.Combine(gatewayRoot, "accounts.json"),
                WorkspaceDirectory = Path.Combine(gatewayRoot, "workspace"),
                DiscoverProfiles = false
            };
        });

        services.TryAddSingleton<ISqliteConnectionFactory>(serviceProvider =>
        {
            var storageOptions = serviceProvider.GetRequiredService<StorageOptions>();

            return new SqliteConnectionFactory(
                AppDataPaths.GetDatabasePath(storageOptions.AppDataDirectory, storageOptions.DatabaseFileName));
        });

        services.AddHardeningServices();

        services.TryAddSingleton(serviceProvider => new DatabaseMigrator(
            serviceProvider.GetRequiredService<ISqliteConnectionFactory>()));

        services.TryAddSingleton<SensitiveDataFilter>();
        services.AddSingleton<LLMWorkGUI.Application.Projects.IProjectDataPolicyService,
            LLMWorkGUI.Infrastructure.Projects.SqliteProjectDataPolicyService>();
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.INativeGatewayRouteCatalog,
            LLMWorkGUI.Infrastructure.Providers.SqliteNativeGatewayRouteCatalog>();
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.INativeGatewayRouteActivationService>(sp =>
            new LLMWorkGUI.Infrastructure.Providers.NativeGatewayRouteActivationService(
                sp.GetRequiredService<ISqliteConnectionFactory>(), () => sp.GetRequiredService<IAccountStore>(),
                () => sp.GetServices<IProviderAdapter>(), sp.GetRequiredService<IApplicationInstanceGuard>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddOptions<NativeGatewayHttpOptions>();
        services.TryAddSingleton<LLMWorkGUI.Infrastructure.Hosting.NativeGatewayHttpServer>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp =>
            sp.GetRequiredService<LLMWorkGUI.Infrastructure.Hosting.NativeGatewayHttpServer>());
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.INativeGatewayTurnService>(sp =>
            new LLMWorkGUI.Infrastructure.Providers.NativeGatewayTurnService(
                () => sp.GetRequiredService<ILlmGateway>(), sp.GetRequiredService<ISqliteConnectionFactory>(),
                sp.GetRequiredService<IApplicationInstanceGuard>(), sp.GetRequiredService<ICheckoutLockService>(),
                sp.GetRequiredService<IHealthCenterService>(), sp.GetRequiredService<SensitiveDataFilter>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetService<LLMWorkGUI.Application.Observability.IActivityCenterService>(),
                sp.GetRequiredService<LLMWorkGUI.Infrastructure.Providers.NativeGatewayEgressCoordinator>()));
        services.TryAddSingleton<LLMWorkGUI.Infrastructure.Providers.NativeGatewayEgressCoordinator>(sp => new(
            sp.GetRequiredService<ISqliteConnectionFactory>(), sp.GetRequiredService<IEgressApprovalService>(),
            sp.GetRequiredService<IApplicationInstanceGuard>(), () => sp.GetRequiredService<ILlmGateway>(),
            sp.GetServices<IProviderAdapter>().OfType<LLMWorkGUI.Infrastructure.GrokBot.GrokBotProviderAdapter>().SingleOrDefault(),
            sp.GetRequiredService<LLMWorkGUI.Application.Reviews.IGrokBotReviewTransport>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.INativeGatewayEgressService>(sp =>
            sp.GetRequiredService<LLMWorkGUI.Infrastructure.Providers.NativeGatewayEgressCoordinator>());
        services.TryAddSingleton<LLMWorkGUI.Infrastructure.Providers.GatewayCatalogMapper>();
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IGatewayCatalogImportService>(serviceProvider =>
            new LLMWorkGUI.Infrastructure.Providers.SqliteGatewayCatalogImportService(
                () => serviceProvider.GetRequiredService<ILlmGateway>(),
                serviceProvider.GetRequiredService<LLMWorkGUI.Infrastructure.Providers.GatewayCatalogMapper>(),
                serviceProvider.GetRequiredService<ISqliteConnectionFactory>(),
                serviceProvider.GetRequiredService<IApplicationInstanceGuard>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IModelRouteConfigurationService,
            LLMWorkGUI.Infrastructure.Providers.SqliteModelRouteConfigurationService>();
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IModelCapabilityEvidenceStore,
            LLMWorkGUI.Infrastructure.Providers.SqliteModelCapabilityEvidenceStore>();
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IModelCapabilityDiscoveryService>(sp => new
            LLMWorkGUI.Infrastructure.Providers.NativeGatewayModelCapabilityDiscoveryService(
                () => sp.GetRequiredService<ILlmGateway>(), sp.GetRequiredService<ISqliteConnectionFactory>(),
                sp.GetRequiredService<LLMWorkGUI.Application.Providers.IModelCapabilityEvidenceStore>(),
                sp.GetRequiredService<IApplicationInstanceGuard>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<LLMWorkGUI.Application.Accounts.IAccountConfigurationService,
            LLMWorkGUI.Infrastructure.Providers.SqliteAccountConfigurationService>();
        services.TryAddSingleton<ICursorAcpExecutionJournal, SqliteCursorAcpExecutionJournal>();
        services.TryAddSingleton<IMirasimEgressPolicy, LLMWorkGUI.Infrastructure.Mirasim.SqliteMirasimEgressPolicy>();
        services.TryAddSingleton<IMirasimExecutionJournal, LLMWorkGUI.Infrastructure.Mirasim.SqliteMirasimExecutionJournal>();
        services.TryAddSingleton<LLMWorkGUI.Backends.OpenCode.Sessions.IOpenCodeExecutionJournal,
            LLMWorkGUI.Infrastructure.OpenCode.SqliteOpenCodeExecutionJournal>();

        services.TryAddSingleton(serviceProvider => new WorkflowBlobStore(
            serviceProvider.GetRequiredService<StorageOptions>().AppDataDirectory));

        services.AddWorkflowServices();

        services.TryAddSingleton<ICredentialManagerApi>(serviceProvider =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "The Windows Credential Manager is only available on Windows.");
            }

            return new WindowsCredentialManagerApi();
        });

        // ADR-0005 §1.1: per-user generic credentials in the Windows Credential Manager are the
        // primary storage, and the DPAPI payload of an earlier release stays readable for the
        // references that were written before it. The store owns that precedence, so nothing else has
        // to know which of the two representations currently holds a value.
        services.TryAddSingleton<ISecretStore>(serviceProvider =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Windows Credential Manager secret storage is only available on Windows.");
            }

            return new CredentialManagerSecretStore(
                serviceProvider.GetRequiredService<ICredentialManagerApi>(),
                AppDataPaths.GetSecretsDirectory(
                    serviceProvider.GetRequiredService<StorageOptions>().AppDataDirectory),
                serviceProvider.GetService<ILogger<CredentialManagerSecretStore>>());
        });

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<ISecretReferenceRepository, SqliteSecretReferenceRepository>();

        // The secret lifecycle owns create/rotate/revoke and is the only component allowed to decide
        // whether a URN may be used. It is registered after the store and the metadata repository, and
        // it stays usable when a store cannot overwrite a payload in place.
        services.TryAddSingleton<ISecretLifecycleService>(serviceProvider =>
            new SecretLifecycleService(
                serviceProvider.GetRequiredService<ISecretStore>(),
                serviceProvider.GetRequiredService<ISecretReferenceRepository>(),
                serviceProvider.GetService<IProviderProfileRepository>(),
                serviceProvider.GetService<IAccountRepository>(),
                serviceProvider.GetService<TimeProvider>(),
                serviceProvider.GetService<ILogger<SecretLifecycleService>>(),
                serviceProvider.GetRequiredService<IApplicationInstanceGuard>()));

        services.TryAddSingleton<ICliExecutableLocator, PathCliExecutableLocator>();
        services.TryAddSingleton<ICliDetectionService, CliDetectionService>();

        services.TryAddSingleton<IApplicationInstanceGuard>(serviceProvider =>
            new ApplicationInstanceGuard(
                serviceProvider.GetRequiredService<StorageOptions>().AppDataDirectory
                    ?? AppDataPaths.DefaultRootDirectory));

        services.TryAddSingleton<ICheckoutLockService, CheckoutLockService>();

        services.TryAddSingleton<ProcessSupervisor>();
        services.TryAddSingleton<IProcessSupervisor>(serviceProvider =>
            new GuardedProcessSupervisor(
                serviceProvider.GetRequiredService<ProcessSupervisor>(),
                serviceProvider.GetRequiredService<IApplicationInstanceGuard>()));

        services.TryAddSingleton<IExecutionWatchdog, ExecutionWatchdog>();

        services.TryAddSingleton<RecoveryMatrix>();
        services.TryAddSingleton<IReconciliationProbe, ReconciliationProbe>();
        services.TryAddSingleton<IReconciliationService, ReconciliationService>();
        services.TryAddSingleton<INativeGatewayJournalRecoveryService, LLMWorkGUI.Infrastructure.Providers.SqliteNativeGatewayRecoveryService>();
        services.TryAddSingleton<IOpenCodeJournalRecoveryService, LLMWorkGUI.Infrastructure.OpenCode.SqliteOpenCodeJournalRecoveryService>();

        services.TryAddSingleton<IProjectRepository, SqliteProjectRepository>();
        services.TryAddSingleton<ISessionRepository, SqliteSessionRepository>();
        services.TryAddSingleton<IExecutionRepository, SqliteExecutionRepository>();
        services.TryAddSingleton<IProjectLockRepository, SqliteProjectLockRepository>();
        services.TryAddSingleton<IHealthStateRepository, SqliteHealthStateRepository>();
        services.TryAddSingleton<IHealthEventRepository, SqliteHealthEventRepository>();
        services.TryAddSingleton<IHealthTransitionStore, SqliteHealthTransitionStore>();
        services.TryAddSingleton<LLMWorkGUI.Application.Health.IHealthPolicyProvider>(serviceProvider =>
            new LLMWorkGUI.Application.Health.HealthPolicyRegistry(
                serviceProvider.GetService<IOptions<LLMWorkGUI.Application.Health.HealthPolicyOptions>>()?.Value));
        services.TryAddSingleton<IHealthCenterService, HealthCenterService>();
        services.TryAddSingleton<LLMWorkGUI.Application.Health.IHealthProbeService>(serviceProvider =>
            new LLMWorkGUI.Application.Health.HealthProbeService(
                serviceProvider.GetRequiredService<IHealthCenterService>(),
                serviceProvider.GetService<IProviderProfileRepository>(),
                serviceProvider.GetService<IAccountRepository>(),
                serviceProvider.GetService<LLMWorkGUI.Application.Providers.IProviderConnectionTestService>(),
                serviceProvider.GetService<LLMWorkGUI.Application.Health.IModelProbeExecutor>(),
                serviceProvider.GetService<ISecretLifecycleService>()));
        services.TryAddSingleton<LLMWorkGUI.Application.Health.IModelProbeExecutor>(serviceProvider =>
            new LLMWorkGUI.Infrastructure.Providers.ProviderModelProbeExecutor(
                serviceProvider.GetService<ISecretStore>(),
                httpClient: null,
                serviceProvider.GetService<ISecretLifecycleService>(),
                serviceProvider.GetRequiredService<IProviderProfileRepository>(),
                serviceProvider.GetRequiredService<IAccountRepository>()));
        services.TryAddSingleton<LLMWorkGUI.Application.Health.IImpactedSessionService, LLMWorkGUI.Application.Health.ImpactedSessionService>();
        services.TryAddSingleton<LLMWorkGUI.Application.Health.IStickyRouteTurnGate, LLMWorkGUI.Application.Health.StickyRouteTurnGate>();
        services.TryAddSingleton<LLMWorkGUI.Application.Security.IDataClassificationGate, LLMWorkGUI.Application.Security.DataClassificationGate>();
        services.TryAddSingleton<LLMWorkGUI.Application.Security.IEgressApprovalService, LLMWorkGUI.Application.Security.EgressApprovalService>();
        services.TryAddSingleton<IProviderProfileRepository, SqliteProviderProfileRepository>();
        services.TryAddSingleton<IApprovalRuleRepository, SqliteApprovalRuleRepository>();
        services.TryAddSingleton<IAccountRepository, SqliteAccountRepository>();
        services.TryAddSingleton<IQuotaSnapshotRepository, SqliteQuotaSnapshotRepository>();
        services.TryAddSingleton<IApplicationSettingsRepository, SqliteApplicationSettingsRepository>();
        services.TryAddSingleton<IRetentionService, RetentionService>();
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IOpenCodeConfigService, LLMWorkGUI.Infrastructure.Providers.OpenCodeConfigService>();
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IProviderConnectionTestService>(serviceProvider =>
            new LLMWorkGUI.Infrastructure.Providers.ProviderConnectionTestService(
                serviceProvider.GetService<ISecretStore>(),
                httpClient: null,
                serviceProvider.GetService<ISecretLifecycleService>(),
                serviceProvider.GetRequiredService<IProviderProfileRepository>(),
                serviceProvider.GetRequiredService<IAccountRepository>()));
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IModelRefreshService, LLMWorkGUI.Infrastructure.Providers.ModelRefreshService>();
        // The local, sanitized store of the backend model ids a provider profile is configured with.
        // It is the only source of a real OpenCode model id for an account whose record carries none.
        services.TryAddSingleton<LLMWorkGUI.Infrastructure.Providers.ApplicationSettingsProviderModelCatalog>();
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IProviderModelCatalogSource>(
            serviceProvider => serviceProvider.GetRequiredService<LLMWorkGUI.Infrastructure.Providers.ApplicationSettingsProviderModelCatalog>());
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IProviderModelCatalogWriter>(
            serviceProvider => serviceProvider.GetRequiredService<LLMWorkGUI.Infrastructure.Providers.ApplicationSettingsProviderModelCatalog>());
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IPluginInventoryService, LLMWorkGUI.Infrastructure.Providers.OpenCodePluginInventoryService>();
        services.TryAddSingleton<LLMWorkGUI.Application.Providers.IProviderDiagnosticExportService, LLMWorkGUI.Infrastructure.Providers.ProviderDiagnosticExportService>();
        services.TryAddSingleton<LLMWorkGUI.Application.Accounts.IAccountBridge, LLMWorkGUI.Application.Accounts.OpenCodePluginAccountBridge>();
        services.TryAddSingleton<LLMWorkGUI.Application.Agy.IAgyProfileExecutableResolver, LLMWorkGUI.Infrastructure.Agy.WindowsAgyProfileExecutableResolver>();
        services.TryAddSingleton<LLMWorkGUI.Application.Agy.IAgyProcessInspector, LLMWorkGUI.Infrastructure.Agy.DefaultAgyProcessInspector>();
        services.TryAddSingleton<LLMWorkGUI.Application.Agy.IAgyProfileService, LLMWorkGUI.Infrastructure.Agy.AgyProfileService>();
        services.TryAddSingleton<LLMWorkGUI.Application.StarCliProxy.IStarCliProxyExecutableResolver, LLMWorkGUI.Infrastructure.StarCliProxy.WindowsStarCliProxyExecutableResolver>();
        services.TryAddSingleton<LLMWorkGUI.Backends.Abstractions.StarCliProxy.IStarCliProxyClient, LLMWorkGUI.Infrastructure.StarCliProxy.StarCliProxyClient>();
        services.TryAddSingleton<LLMWorkGUI.Application.Security.IWorkflowReviewEgressPolicy, LLMWorkGUI.Infrastructure.Workflows.Channels.SqliteWorkflowReviewEgressPolicy>();
        services.TryAddSingleton<LLMWorkGUI.Infrastructure.StarCliProxy.StarCliProxyServerManager>();
        services.TryAddSingleton<LLMWorkGUI.Backends.Abstractions.StarCliProxy.IStarCliProxyServerManager>(serviceProvider =>
            serviceProvider.GetRequiredService<LLMWorkGUI.Infrastructure.StarCliProxy.StarCliProxyServerManager>());
        services.TryAddSingleton<LLMWorkGUI.Application.StarCliProxy.IStarCliProxyAvailabilityProbe>(serviceProvider =>
            serviceProvider.GetRequiredService<LLMWorkGUI.Infrastructure.StarCliProxy.StarCliProxyServerManager>());
        services.TryAddSingleton<LLMWorkGUI.Application.StarCliProxy.IAccountContextManager, LLMWorkGUI.Application.StarCliProxy.AccountContextManager>();
        services.TryAddSingleton<LLMWorkGUI.Application.StarCliProxy.StarCliProxyAccountBridge>();
        services.AddSingleton<LLMWorkGUI.Application.Accounts.IAccountBridge>(serviceProvider =>
            serviceProvider.GetRequiredService<LLMWorkGUI.Application.StarCliProxy.StarCliProxyAccountBridge>());
        services.TryAddSingleton<LLMWorkGUI.Backends.Abstractions.StarCliProxy.StarCliProxyBackendAdapter>();
        services.TryAddSingleton<LLMWorkGUI.Backends.Abstractions.IBackendAdapter>(serviceProvider =>
            serviceProvider.GetRequiredService<LLMWorkGUI.Backends.Abstractions.StarCliProxy.StarCliProxyBackendAdapter>());
        services.TryAddSingleton<LLMWorkGUI.Application.Quotas.IQuotaSourceAdapter, LLMWorkGUI.Application.Quotas.OpenCodePluginQuotaAdapter>();
        services.TryAddSingleton<LLMWorkGUI.Application.Quotas.IQuotaRefreshScheduler, LLMWorkGUI.Application.Quotas.QuotaRefreshScheduler>();

        services
            .AddOptions<CursorAcpOptions>()
            .Validate(
                options =>
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
                },
                "Cursor ACP options are invalid: client info must not be empty, timeouts must be positive, and queue capacities must be positive.");

        services.TryAddSingleton<ICursorExecutableResolver, WindowsCursorAgentExecutableResolver>();
        services.TryAddSingleton<IJsonRpcTransportFactory, JsonRpcStdioTransportFactory>();
        services.TryAddSingleton<ICursorAcpProcessManager, CursorAcpProcessManager>();

        services.TryAddSingleton<ICursorAcpModelCatalogService, CursorAcpModelCatalogService>();
        services.TryAddSingleton<ICursorAcpModelSelector, CursorAcpModelSelector>();
        services.TryAddSingleton<ICursorAcpModePolicy, CursorAcpModePolicy>();

        // The protocol client is stateful per managed process, so only its factory is shared. The
        // lifecycle service owns exactly one backend and is therefore resolved per workspace scope.
        services.TryAddSingleton<ICursorAcpClientFactory, CursorAcpClientFactory>();

        // Start and turn outcomes are reported to the Health Center when it is part of the composition
        // (ТЗ §6.10); the service stays usable without it.
        services.TryAddSingleton<ICursorAcpSessionLifecycleService>(serviceProvider =>
            new CursorAcpSessionLifecycleService(
                serviceProvider.GetRequiredService<ICursorAcpProcessManager>(),
                serviceProvider.GetRequiredService<ICursorAcpClientFactory>(),
                serviceProvider.GetRequiredService<ICursorAcpModePolicy>(),
                serviceProvider.GetService<IOptions<CursorAcpOptions>>(),
                serviceProvider.GetService<ILoggerFactory>(),
                serviceProvider.GetService<IHealthCenterService>()));

        services.AddOpenCodeBackend();

        return services;
    }

    private static StorageOptions ResolveStorageOptions(
        IServiceProvider serviceProvider,
        string? appDataDirectory)
    {
        var configured = serviceProvider.GetService<IOptions<StorageOptions>>()?.Value;

        var rootDirectory = appDataDirectory ?? configured?.AppDataDirectory;
        rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? AppDataPaths.DefaultRootDirectory
            : Path.GetFullPath(rootDirectory);

        var databaseFileName = configured?.DatabaseFileName;

        if (string.IsNullOrWhiteSpace(databaseFileName))
        {
            databaseFileName = StorageOptions.DefaultDatabaseFileName;
        }

        return new StorageOptions
        {
            AppDataDirectory = rootDirectory,
            DatabaseFileName = databaseFileName
        };
    }
}
