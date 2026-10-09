using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Infrastructure.Lifecycle;

/// <summary>
/// Recovery after an interrupted application lifetime. Native outcomes are quarantined without
/// manufacturing terminal evidence; sessions retain unresolved execution identity and checkout locks
/// until a confirmed terminal execution permits release. Workflow orchestration and health audit are
/// recovered independently
/// (ROADMAP Phase 12, ТЗ §9.4).
/// </summary>
public sealed partial class AppCrashRecoveryService : IAppCrashRecoveryService
{
    public const string AuditEventKind = "CrashRecovery";

    public const string ExecutionTerminationReason = "InterruptedByRestart";

    public const string ExecutionUncertaintyReason = "NativeOutcomeUnconfirmedAfterRecovery";

    public const string SessionRecoveryReason = "Interrupted by application restart; reconciliation required.";

    public const string WorkflowRunFailureReason =
        "Interrupted by application restart; crash recovery marked the run failed.";

    public const string LockReleaseReason = "Crash recovery released the checkout lock of an interrupted execution.";

    private const int HealthRehydrationLimit = 200;

    private static readonly ExecutionState[] InterruptedExecutionStates =
    {
        ExecutionState.Queued,
        ExecutionState.Starting,
        ExecutionState.SessionConfirmed,
        ExecutionState.Running,
        ExecutionState.WaitingApproval,
        ExecutionState.Cancelling
    };

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IProjectRepository _projectRepository;
    private readonly IWorkflowRunRepository _workflowRunRepository;
    private readonly IApplicationInstanceGuard? _instanceGuard;
    private readonly IHealthEventRepository? _healthEventRepository;
    private readonly IHealthCenterService? _healthCenterService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AppCrashRecoveryService> _logger;
    private readonly IOpenCodeJournalRecoveryService? _openCodeRecovery;
    private readonly INativeGatewayJournalRecoveryService? _nativeGatewayRecovery;

    public AppCrashRecoveryService(
        ISqliteConnectionFactory connectionFactory,
        IExecutionRepository executionRepository,
        ISessionRepository sessionRepository,
        IProjectRepository projectRepository,
        IWorkflowRunRepository workflowRunRepository,
        IProjectLockRepository projectLockRepository,
        IApplicationInstanceGuard? instanceGuard = null,
        IHealthEventRepository? healthEventRepository = null,
        IHealthCenterService? healthCenterService = null,
        TimeProvider? timeProvider = null,
        ILogger<AppCrashRecoveryService>? logger = null,
        IOpenCodeJournalRecoveryService? openCodeRecovery = null,
        INativeGatewayJournalRecoveryService? nativeGatewayRecovery = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(executionRepository);
        ArgumentNullException.ThrowIfNull(sessionRepository);
        ArgumentNullException.ThrowIfNull(projectRepository);
        ArgumentNullException.ThrowIfNull(workflowRunRepository);
        ArgumentNullException.ThrowIfNull(projectLockRepository);

        _connectionFactory = connectionFactory;
        _projectRepository = projectRepository;
        _workflowRunRepository = workflowRunRepository;
        _instanceGuard = instanceGuard;
        _healthEventRepository = healthEventRepository;
        _healthCenterService = healthCenterService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AppCrashRecoveryService>.Instance;
        _openCodeRecovery = openCodeRecovery;
        _nativeGatewayRecovery = nativeGatewayRecovery;
    }

    public async Task<AppCrashRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var instanceId = _instanceGuard?.InstanceId ?? "standalone";
        var warnings = new List<string>();

        if (_instanceGuard is { IsViewOnly: true })
        {
            warnings.Add("Crash recovery was skipped: this instance is view-only.");

            return BuildReport(now, instanceId, skippedAsViewOnly: true, warnings);
        }

        var interruptedExecutions = new List<string>();
        var interruptedSessions = new List<string>();
        var interruptedRuns = new List<string>();
        var releasedLocks = new List<string>();
        var retainedLocks = new List<string>();

        // Backend journals preserve their own admission and native ownership before generic quarantine.
        if (_openCodeRecovery is not null)
            await _openCodeRecovery.QuarantineInterruptedAsync(cancellationToken).ConfigureAwait(false);
        if (_nativeGatewayRecovery is not null)
            await _nativeGatewayRecovery.QuarantineInterruptedAsync(cancellationToken).ConfigureAwait(false);

        await RecoverExecutionsAsync(interruptedExecutions, warnings, now, cancellationToken)
            .ConfigureAwait(false);

        await RecoverSessionsAsync(interruptedSessions, warnings, now, cancellationToken)
            .ConfigureAwait(false);

        await RecoverWorkflowRunsAsync(interruptedRuns, warnings, now, cancellationToken)
            .ConfigureAwait(false);

        await RecoverLocksAsync(releasedLocks, retainedLocks, warnings, now, cancellationToken)
            .ConfigureAwait(false);

        var rehydratedHealthScopes = await RehydrateHealthAsync(warnings, cancellationToken)
            .ConfigureAwait(false);

        var report = new AppCrashRecoveryReport
        {
            RunAtUtc = now,
            ApplicationInstanceId = instanceId,
            SkippedAsViewOnly = false,
            InterruptedExecutionIds = interruptedExecutions,
            InterruptedSessionIds = interruptedSessions,
            InterruptedWorkflowRunIds = interruptedRuns,
            ReleasedLockIds = releasedLocks,
            RetainedLockIds = retainedLocks,
            RehydratedHealthScopeCount = rehydratedHealthScopes,
            Warnings = warnings
        };

        _logger.LogInformation(
            "Crash recovery finished: executions={ExecutionCount}, sessions={SessionCount}, runs={RunCount}, "
            + "releasedLocks={ReleasedLockCount}, retainedLocks={RetainedLockCount}, healthScopes={HealthScopeCount}.",
            interruptedExecutions.Count,
            interruptedSessions.Count,
            interruptedRuns.Count,
            releasedLocks.Count,
            retainedLocks.Count,
            rehydratedHealthScopes);

        return report;
    }

    public async Task<AppCrashRecoveryReport> RecoverWorkflowRunsAfterRestartAsync(
        IReadOnlyList<ReconciliationEvidence> reconciliationEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliationEvidence);
        var now = _timeProvider.GetUtcNow();
        var instanceId = _instanceGuard?.InstanceId ?? "standalone";
        var warnings = new List<string>();
        if (_instanceGuard is { IsViewOnly: true })
        {
            warnings.Add("Workflow startup recovery was skipped: this instance is view-only.");
            return BuildReport(now, instanceId, skippedAsViewOnly: true, warnings);
        }

        var reattachedSessions = reconciliationEvidence
            .Where(evidence => evidence.Outcome == LLMWorkGUI.Application.Reconciliation.ReconciliationOutcome.Reattached
                && evidence.ProcessAlive && evidence.BindingMatched
                && !string.IsNullOrWhiteSpace(evidence.NativeSessionId))
            .Select(evidence => evidence.LocalSessionId)
            .ToHashSet(StringComparer.Ordinal);
        var interruptedRuns = new List<string>();
        await RecoverWorkflowRunsAsync(interruptedRuns, warnings, now, cancellationToken, reattachedSessions)
            .ConfigureAwait(false);
        return BuildReport(now, instanceId, skippedAsViewOnly: false, warnings) with
        {
            InterruptedWorkflowRunIds = interruptedRuns
        };
    }

    private async Task RecoverExecutionsAsync(
        List<string> interruptedExecutions,
        List<string> warnings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var executionIds = await QueryIdsAsync(
                "SELECT Id FROM Executions WHERE State IN ({0}) AND SessionId NOT IN (SELECT Id FROM Sessions WHERE Backend='NativeGateway') ORDER BY CreatedAtUtc, Id;",
                InterruptedExecutionStates.Select(state => state.ToString()).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var executionId in executionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await QuarantineExecutionAsync(executionId, now, cancellationToken).ConfigureAwait(false))
                    interruptedExecutions.Add(executionId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Execution '{executionId}' could not be quarantined: {exception.GetType().Name}.");
            }
        }
    }

    private async Task RecoverSessionsAsync(
        List<string> interruptedSessions,
        List<string> warnings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sessionIds = await QueryIdsAsync(
                "SELECT Id FROM Sessions WHERE State IN ({0}) AND Backend!='NativeGateway' ORDER BY CreatedAtUtc, Id;",
                new[] { SessionState.Starting.ToString(), SessionState.Active.ToString() },
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var sessionId in sessionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await QuarantineSessionAsync(sessionId, now, cancellationToken).ConfigureAwait(false))
                    interruptedSessions.Add(sessionId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                warnings.Add($"Session '{sessionId}' could not be recovered: {exception.GetType().Name}.");
            }
        }
    }

    private async Task RecoverWorkflowRunsAsync(
        List<string> interruptedRuns,
        List<string> warnings,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? reattachedSessions = null)
    {
        var projects = await _projectRepository.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<WorkflowRun> runs;

            try
            {
                runs = await _workflowRunRepository
                    .GetByProjectIdAsync(project.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Workflow runs of project '{project.Id}' could not be inspected: {exception.GetType().Name}.");
                continue;
            }

            foreach (var run in runs.Where(run => !run.IsTerminal))
            {
                if (run.SessionId is not null && reattachedSessions?.Contains(run.SessionId) == true)
                {
                    continue;
                }

                try
                {
                    run.Fail(WorkflowRunFailureReason, now);

                    await _workflowRunRepository
                        .SaveAsync(run, cancellationToken)
                        .ConfigureAwait(false);

                    interruptedRuns.Add(run.Id);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    warnings.Add($"Workflow run '{run.Id}' could not be failed: {exception.GetType().Name}.");
                }
            }
        }
    }

    private async Task RecoverLocksAsync(
        List<string> releasedLocks,
        List<string> retainedLocks,
        List<string> warnings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var lockIds = await QueryActiveLockIdsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var lockId in lockIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var released = await ReleaseConfirmedLockAsync(lockId, now, cancellationToken).ConfigureAwait(false);

                if (released)
                {
                    releasedLocks.Add(lockId);
                }
                else
                {
                    retainedLocks.Add(lockId);
                    warnings.Add($"Checkout lock '{lockId}' was retained: terminal execution and ownership release are not confirmed.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProjectLockConflictException exception)
            {
                retainedLocks.Add(lockId);
                warnings.Add($"Checkout lock '{lockId}' was retained: {exception.Message}");
            }
            catch (Exception exception)
            {
                retainedLocks.Add(lockId);
                warnings.Add($"Checkout lock '{lockId}' could not be released: {exception.GetType().Name}.");
            }
        }
    }

    private async Task<int> RehydrateHealthAsync(List<string> warnings, CancellationToken cancellationToken)
    {
        if (_healthEventRepository is null || _healthCenterService is null)
        {
            return 0;
        }

        var events = await _healthEventRepository
            .ListRecentAsync(HealthRehydrationLimit, cancellationToken)
            .ConfigureAwait(false);

        var scopes = events
            .Select(healthEvent => new HealthScope(healthEvent.ScopeType, healthEvent.ScopeId))
            .Distinct()
            .ToArray();

        var rehydrated = 0;

        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _healthCenterService
                    .GetSnapshotAsync(scope, cancellationToken)
                    .ConfigureAwait(false);

                rehydrated++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                warnings.Add(
                    $"Health scope '{scope.ScopeType}:{scope.ScopeId}' could not be rehydrated: "
                    + $"{exception.GetType().Name}.");
            }
        }

        return rehydrated;
    }

    private async Task<IReadOnlyList<string>> QueryActiveLockIdsAsync(CancellationToken cancellationToken)
    {
        var ids = new List<string>();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id FROM ProjectLocks WHERE ReleasedAtUtc IS NULL ORDER BY AcquiredAtUtc, Id;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task<IReadOnlyList<string>> QueryIdsAsync(
        string commandTextTemplate,
        IReadOnlyList<string> stateValues,
        CancellationToken cancellationToken)
    {
        var parameterNames = new string[stateValues.Count];

        for (var index = 0; index < stateValues.Count; index++)
        {
            parameterNames[index] = "$state" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var commandText = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            commandTextTemplate,
            string.Join(", ", parameterNames));

        var ids = new List<string>();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        for (var index = 0; index < stateValues.Count; index++)
        {
            command.Parameters.AddWithValue(parameterNames[index], stateValues[index]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static AppCrashRecoveryReport BuildReport(
        DateTimeOffset now,
        string instanceId,
        bool skippedAsViewOnly,
        IReadOnlyList<string> warnings)
    {
        return new AppCrashRecoveryReport
        {
            RunAtUtc = now,
            ApplicationInstanceId = instanceId,
            SkippedAsViewOnly = skippedAsViewOnly,
            InterruptedExecutionIds = Array.Empty<string>(),
            InterruptedSessionIds = Array.Empty<string>(),
            InterruptedWorkflowRunIds = Array.Empty<string>(),
            ReleasedLockIds = Array.Empty<string>(),
            RetainedLockIds = Array.Empty<string>(),
            RehydratedHealthScopeCount = 0,
            Warnings = warnings
        };
    }
}
