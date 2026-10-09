using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Hosting;

public static class HostBootstrapper
{
    public static IHostBuilder CreateHostBuilder(string[]? args = null, string? appDataDirectory = null)
    {
        return Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.AddApplication(context.Configuration);
                services.AddInfrastructure(appDataDirectory);
            });
    }

    public static IHost BuildHost(string[]? args = null, string? appDataDirectory = null)
    {
        return CreateHostBuilder(args, appDataDirectory).Build();
    }

    public static async Task InitializeAsync(
        IHost host,
        CancellationToken cancellationToken = default,
        bool recoverInterruptedWorkflows = false)
    {
        ArgumentNullException.ThrowIfNull(host);

        var migrator = host.Services.GetRequiredService<DatabaseMigrator>();
        var instanceGuard = host.Services.GetService<IApplicationInstanceGuard>();

        if (instanceGuard is { IsViewOnly: true })
            await migrator.ValidateCurrentSchemaAsync(cancellationToken).ConfigureAwait(false);
        else
            await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var providerProfiles = host.Services.GetService<IProviderProfileRepository>();
        var pendingSecretCleanup = 0;
        if (instanceGuard is not { IsViewOnly: true } && host.Services.GetService<ISecretLifecycleService>() is { } secrets)
            pendingSecretCleanup = await secrets.RetryPendingSecretCleanupAsync(cancellationToken).ConfigureAwait(false);

        if (providerProfiles is not null && instanceGuard is not { IsViewOnly: true })
        {
            await BuiltInLocalProviderProfileSeeder
                .EnsureAsync(providerProfiles, cancellationToken)
                .ConfigureAwait(false);
        }

        var quotaMaintenanceFailed = false;
        var activityMaintenanceFailed = false;
        var pendingScratchCleanup = 0;
        if (instanceGuard is null || instanceGuard.IsPrimarySupervisor)
        {
            if (host.Services.GetService<IScratchWorkspaceManager>() is { } scratch)
            {
                try { pendingScratchCleanup = await scratch.RecoverAbandonedAdaptationWorkspacesAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
                    or LLMWorkGUI.Infrastructure.Security.PathTraversalException)
                { pendingScratchCleanup = -1; }
            }
            try
            {
                await using var connection = await host.Services.GetRequiredService<ISqliteConnectionFactory>()
                    .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await QuotaDiagnosticMaintenance.RedactLegacyPayloadsAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                // Repository reads and backup copies redact independently. A transient maintenance
                // failure must not block startup or put the database exception's text in diagnostics.
                quotaMaintenanceFailed = true;
                host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(HostBootstrapper).FullName!)
                    .LogWarning("Legacy quota diagnostic maintenance failed ({ExceptionType}); repository reads remain redacted.",
                        exception.GetType().Name);
            }

            try
            {
                await using var connection = await host.Services.GetRequiredService<ISqliteConnectionFactory>()
                    .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ActivityDiagnosticMaintenance.RedactLegacyRowsAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                activityMaintenanceFailed = true;
                host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(HostBootstrapper).FullName!)
                    .LogWarning("Legacy activity diagnostic maintenance failed ({ExceptionType}); returned fields remain redacted.",
                        exception.GetType().Name);
            }

            if (host.Services.GetService<IHealthCenterService>() is { } health)
                await health.RetryAuthenticationFanoutAsync(cancellationToken).ConfigureAwait(false);
            var reconciliationService = host.Services.GetService<IReconciliationService>();
            if (host.Services.GetService<INativeGatewayJournalRecoveryService>() is { } nativeGatewayRecovery)
                await nativeGatewayRecovery.QuarantineInterruptedAsync(cancellationToken).ConfigureAwait(false);
            var openCodeRecovery = host.Services.GetService<IOpenCodeJournalRecoveryService>();
            if (openCodeRecovery is not null)
                await openCodeRecovery.QuarantineInterruptedAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ReconciliationEvidence> reconciliationEvidence = Array.Empty<ReconciliationEvidence>();

            if (reconciliationService is not null)
            {
                reconciliationEvidence = await reconciliationService
                    .ReconcileAllActiveAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            // Reconciliation owns native executions, sessions and their locks. Workflow orchestration
            // also needs recovery, even when a run has no session (e.g. it stopped at a review gate).
            // Do not call the broad diagnostic recovery here: it would overwrite Reattached/Ambiguous.
            var crashRecovery = host.Services.GetService<IAppCrashRecoveryService>();
            if (recoverInterruptedWorkflows && crashRecovery is not null)
            {
                var recovery = await crashRecovery
                    .RecoverWorkflowRunsAfterRestartAsync(reconciliationEvidence, cancellationToken)
                    .ConfigureAwait(false);
                if (recovery.Warnings.Count > 0)
                {
                    host.Services.GetService<IActivityCenterService>()?.AppendSystemEvent(
                        "workflow:startup-recovery-warning",
                        "Не удалось полностью восстановить workflow после перезапуска",
                        string.Join(" | ", recovery.Warnings),
                        ActivityEventState.Warning);
                }
            }
        }

        // The saved activity history is restored here, after migrations and after any reconciliation that
        // may have terminalized interrupted executions, so the Activity Center opens on the real saved
        // state. This is the production startup path, not a load-driver convenience: App.OnStartup calls
        // InitializeAsync, so a plain start of the application reloads the journal into the shipped screen
        // exactly as the independent restart check does.
        await RestoreActivityCenterAsync(host, cancellationToken).ConfigureAwait(false);
        if (pendingScratchCleanup != 0)
            host.Services.GetService<IActivityCenterService>()?.AppendSystemEvent(
                "adaptation:pending-scratch-cleanup", "Очистка временных файлов адаптации не завершена",
                "Записи о временных каталогах сохранены. Очистка будет повторена при следующем запуске; каталоги работающего процесса не удаляются.",
                ActivityEventState.Warning);
        if (pendingSecretCleanup != 0)
            host.Services.GetService<IActivityCenterService>()?.AppendSystemEvent(
                "secrets:pending-cleanup", "Очистка удалённых секретов не завершена",
                "Доступ к секретам отозван; удаление значений из хранилища будет повторено при следующем запуске.",
                ActivityEventState.Warning);
        if (host.Services.GetService<INativeGatewayJournalRecoveryService>() is { } gatewayActivity)
        {
            try { await gatewayActivity.ReplayActivityAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(HostBootstrapper).FullName!)
                    .LogWarning("LLMGateway activity replay failed ({ExceptionType}); journal ownership remains unchanged.", exception.GetType().Name);
                try
                {
                    host.Services.GetService<IActivityCenterService>()?.AppendSystemEvent("native-gateway-journal:reload-failed",
                        "Не удалось восстановить события LLMGateway", "Журнал недоступен. Состояния выполнений и блокировки сохранены; восстановление событий будет повторено при следующем запуске.");
                }
                catch (Exception) { /* Observers cannot abort startup or change ownership. */ }
            }
        }
        if (host.Services.GetService<IOpenCodeJournalRecoveryService>() is { } openCodeActivity)
        {
            try { await openCodeActivity.ReplayActivityAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(HostBootstrapper).FullName!)
                    .LogWarning("OpenCode activity replay failed ({ExceptionType}); journal ownership remains unchanged.", exception.GetType().Name);
                try
                {
                    host.Services.GetService<IActivityCenterService>()?.AppendSystemEvent("opencode-journal:reload-failed",
                        "Не удалось восстановить события OpenCode", "Журнал недоступен. Состояния выполнений и блокировки сохранены; восстановление событий будет повторено при следующем запуске.");
                }
                catch (Exception) { /* A failing observer must not abort startup or change ownership. */ }
            }
        }
        if (quotaMaintenanceFailed)
        {
            host.Services.GetService<IActivityCenterService>()?.AppendSystemEvent(
                "quota:startup-maintenance-warning",
                "Не удалось очистить старые диагностические записи квот",
                "Данные для просмотра и новые резервные копии очищаются отдельно. Очистка старых записей будет повторена при следующем запуске.",
                ActivityEventState.Warning);
        }
        if (activityMaintenanceFailed)
        {
            host.Services.GetService<IActivityCenterService>()?.AppendSystemEvent(
                "activity:startup-maintenance-warning",
                "Не удалось очистить старые записи Activity Center",
                "Отображаемый текст очищается отдельно. В старом поисковом индексе могли остаться исходные значения. Очистка будет повторена при следующем запуске.",
                ActivityEventState.Warning);
        }
        if (instanceGuard?.IsPrimarySupervisor == true
            && host.Services.GetService<NativeGatewayHttpServer>() is { } gatewayHttp)
            await gatewayHttp.StartEnabledAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reloads the durable activity journal into the Activity Center. Runs after the migrations, because
    /// the reload queries the journal's full-text index that migration 011 creates.
    /// <para>
    /// A failure to restore is reported, never swallowed: the window is bounded, so an unavailable journal
    /// must not stop the application from starting, but it must also never look like a successful start
    /// with an empty history.
    /// </para>
    /// </summary>
    public static async Task<ActivityCenterLoadReport?> RestoreActivityCenterAsync(
        IHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var activityCenter = host.Services.GetService<IActivityCenterService>();

        if (activityCenter is null)
        {
            return null;
        }

        try
        {
            return await activityCenter.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activityCenter.AppendSystemEvent(
                "activity-center:reload-failed",
                "Saved activity history could not be restored",
                exception.Message,
                ActivityEventState.Warning);

            return null;
        }
    }
}
