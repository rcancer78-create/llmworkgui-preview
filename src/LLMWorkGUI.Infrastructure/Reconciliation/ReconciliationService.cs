using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ApplicationReconciliationOutcome = LLMWorkGUI.Application.Reconciliation.ReconciliationOutcome;
using CloseReason = LLMWorkGUI.Domain.Enums.CloseReason;
using DomainReconciliationOutcome = LLMWorkGUI.Domain.Enums.ReconciliationOutcome;
using ExecutionFailureReason = LLMWorkGUI.Domain.Enums.ExecutionFailureReason;
using ExecutionState = LLMWorkGUI.Domain.Enums.ExecutionState;
using SessionState = LLMWorkGUI.Domain.Enums.SessionState;

namespace LLMWorkGUI.Infrastructure.Reconciliation;

public sealed partial class ReconciliationService : IReconciliationService
{
    public const string ReconciliationEventKind = "ReconciliationOutcome";

    public const string RecoveryActionEventKind = "RecoveryAction";

    private readonly ISessionRepository _sessionRepository;
    private readonly IExecutionRepository _executionRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly IProjectLockRepository _projectLockRepository;
    private readonly IReconciliationProbe _probe;
    private readonly RecoveryMatrix _recoveryMatrix;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReconciliationService> _logger;
    private readonly IOpenCodeJournalRecoveryService? _openCodeRecovery;
    private readonly INativeGatewayJournalRecoveryService? _nativeGatewayRecovery;
    private readonly ISqliteConnectionFactory? _connectionFactory;
    private readonly IApplicationInstanceGuard? _instanceGuard;

    public ReconciliationService(
        ISessionRepository sessionRepository,
        IExecutionRepository executionRepository,
        IProjectRepository projectRepository,
        IProjectLockRepository projectLockRepository,
        IReconciliationProbe probe,
        RecoveryMatrix recoveryMatrix,
        TimeProvider? timeProvider = null,
        ILogger<ReconciliationService>? logger = null,
        IOpenCodeJournalRecoveryService? openCodeRecovery = null,
        INativeGatewayJournalRecoveryService? nativeGatewayRecovery = null,
        ISqliteConnectionFactory? connectionFactory = null,
        IApplicationInstanceGuard? instanceGuard = null)
    {
        ArgumentNullException.ThrowIfNull(sessionRepository);
        ArgumentNullException.ThrowIfNull(executionRepository);
        ArgumentNullException.ThrowIfNull(projectRepository);
        ArgumentNullException.ThrowIfNull(projectLockRepository);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(recoveryMatrix);

        _sessionRepository = sessionRepository;
        _executionRepository = executionRepository;
        _projectRepository = projectRepository;
        _projectLockRepository = projectLockRepository;
        _probe = probe;
        _recoveryMatrix = recoveryMatrix;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ReconciliationService>.Instance;
        _openCodeRecovery = openCodeRecovery;
        _nativeGatewayRecovery = nativeGatewayRecovery;
        _connectionFactory = connectionFactory;
        _instanceGuard = instanceGuard;
    }

    public async Task<ReconciliationEvidence> ReconcileSessionAsync(
        string localSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localSessionId);
        EnsureSupervisorPermitted();

        var session = await _sessionRepository
            .GetByIdAsync(localSessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{localSessionId}' was not found.");

        return await ReconcileAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReconciliationEvidence>> ReconcileAllActiveAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureSupervisorPermitted();
        var projects = await _projectRepository
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        var evidence = new List<ReconciliationEvidence>();

        foreach (var project in projects)
        {
            var sessions = await _sessionRepository
                .ListByProjectAsync(project.Id, cancellationToken)
                .ConfigureAwait(false);

            foreach (var session in sessions)
            {
                if (session.State is not (SessionState.Starting or SessionState.Active))
                {
                    continue;
                }

                evidence.Add(await ReconcileAsync(session, cancellationToken).ConfigureAwait(false));
            }
        }

        return evidence;
    }

    public async Task<ReconciliationRecoveryResult> ApplyRecoveryActionAsync(
        string localSessionId,
        RecoveryAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localSessionId);
        EnsureSupervisorPermitted();

        if (action is not (RecoveryAction.ResetSession
            or RecoveryAction.CloseSession
            or RecoveryAction.AcknowledgeAmbiguous))
        {
            throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "An actionable recovery action is required.");
        }

        var session = await _sessionRepository
            .GetByIdAsync(localSessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{localSessionId}' was not found.");

        if (session.Binding.Backend == LLMWorkGUI.Domain.Enums.BackendType.NativeGateway)
            return await (_nativeGatewayRecovery ?? throw new InvalidOperationException("Восстановление LLMGateway недоступно."))
                .TryApplyActionAsync(localSessionId, action, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Сессия LLMGateway изменилась.");
        if (_openCodeRecovery is not null && session.Binding.Backend == LLMWorkGUI.Domain.Enums.BackendType.OpenCode)
            return await _openCodeRecovery.TryApplyActionAsync(localSessionId, action, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("OpenCode journal ownership is not confirmed; generic recovery cannot release this session.");

        if (session.State is SessionState.Draft or SessionState.Closed)
        {
            throw new InvalidOperationException(
                $"Session '{session.Id}' is in state '{session.State}' and does not accept recovery actions.");
        }

        if (action == RecoveryAction.AcknowledgeAmbiguous && session.State != SessionState.Ambiguous)
        {
            throw new InvalidOperationException(
                $"AcknowledgeAmbiguous requires an Ambiguous session; session '{session.Id}' is in state '{session.State}'.");
        }

        var executions = await _executionRepository
            .ListBySessionAsync(session.Id, cancellationToken)
            .ConfigureAwait(false);

        // A local recovery command cannot prove that a remote writer has stopped.
        if ((!string.IsNullOrWhiteSpace(session.ActiveExecutionId)
                && !executions.Any(execution => execution.Id == session.ActiveExecutionId))
            || executions.Any(execution => !ReconciliationExecutionEvidence.IsConfirmedTerminal(execution)
                && !ReconciliationExecutionEvidence.IsPreDispatch(execution)))
        {
            throw new InvalidOperationException(
                "Backend terminal evidence is required before closing this session or releasing its checkout lock.");
        }

        var heldLock = await FindHeldLockAsync(session, executions, cancellationToken).ConfigureAwait(false);
        return await CommitRecoveryActionAsync(session, executions, heldLock, action,
            _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ReconciliationEvidence> ReconcileAsync(
        Session session,
        CancellationToken cancellationToken)
    {
        EnsureSupervisorPermitted();
        if (session.Binding.Backend == LLMWorkGUI.Domain.Enums.BackendType.NativeGateway)
            return await (_nativeGatewayRecovery ?? throw new InvalidOperationException("Восстановление LLMGateway недоступно."))
                .TryReconcileAsync(session.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Сессия LLMGateway изменилась.");
        if (_openCodeRecovery is not null && await _openCodeRecovery.TryReconcileAsync(session.Id, cancellationToken).ConfigureAwait(false) is { } managedEvidence)
            return managedEvidence;

        if (session.State is SessionState.Draft or SessionState.Closed)
        {
            throw new InvalidOperationException(
                $"Session '{session.Id}' is in state '{session.State}' and has no runtime state to reconcile.");
        }

        var executions = await _executionRepository
            .ListBySessionAsync(session.Id, cancellationToken)
            .ConfigureAwait(false);

        var activeExecution = ResolveActiveExecution(session, executions);

        var executionEvents = activeExecution is null
            ? Array.Empty<ExecutionEventRecord>()
            : await _executionRepository
                .ListEventsAsync(activeExecution.Id, cancellationToken)
                .ConfigureAwait(false);

        var probeResult = await _probe
            .ProbeAsync(
                new ReconciliationProbeRequest
                {
                    Session = session,
                    ActiveExecution = activeExecution,
                    ExecutionEvents = executionEvents
                },
                cancellationToken)
            .ConfigureAwait(false);

        var outcome = _recoveryMatrix.Classify(session, activeExecution, probeResult);
        var terminalTurnEvidence = activeExecution is null
            ? string.IsNullOrWhiteSpace(session.ActiveExecutionId)
            : ReconciliationExecutionEvidence.IsConfirmedTerminal(activeExecution);
        var promptDeliveryExcluded = RecoveryMatrix.IsPromptDeliveryExcluded(session, activeExecution);
        var bindingMatched = probeResult.ObservedBinding is not null
            && session.Binding.Equals(probeResult.ObservedBinding);

        var targetSessionState = ReconciliationStateTransitions.ResolveSessionState(
            session.State,
            outcome,
            terminalTurnEvidence);

        var now = _timeProvider.GetUtcNow();
        var reconciledExecution = activeExecution;

        if (activeExecution is not null)
        {
            var targetExecutionState = ReconciliationStateTransitions.ResolveExecutionState(
                activeExecution.State,
                outcome,
                promptDeliveryExcluded);

            if (targetExecutionState != activeExecution.State)
            {
                reconciledExecution = CreateExecution(activeExecution, targetExecutionState, now);
            }
        }

        var activeExecutionId = reconciledExecution is not null
            && !ReconciliationExecutionEvidence.IsConfirmedTerminal(reconciledExecution)
                ? reconciledExecution.Id
                : activeExecution is null ? session.ActiveExecutionId : null;

        var reconciledSession = CreateSession(
            session,
            targetSessionState,
            outcome,
            activeExecutionId,
            now);

        var details = BuildReconciliationDetails(
            probeResult,
            bindingMatched,
            terminalTurnEvidence,
            promptDeliveryExcluded);

        await CommitAutomaticReconciliationAsync(
                session,
                executions,
                activeExecution,
                executionEvents,
                reconciledExecution,
                reconciledSession,
                JsonSerializer.Serialize(new
                {
                    sessionId = session.Id,
                    executionId = reconciledExecution?.Id,
                    outcome = outcome.ToString(),
                    previousSessionState = session.State.ToString(),
                    sessionState = targetSessionState.ToString(),
                    processAlive = probeResult.ProcessAlive,
                    bindingMatched,
                    terminalTurnEvidence,
                    promptDeliveryExcluded
                }),
                now,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Reconciled session {SessionId} as {Outcome} ({SessionState}).",
            session.Id,
            outcome,
            targetSessionState);

        return new ReconciliationEvidence
        {
            LocalSessionId = session.Id,
            ExecutionId = reconciledExecution?.Id,
            NativeSessionId = session.NativeSessionId,
            Outcome = outcome,
            Details = details,
            ProcessAlive = probeResult.ProcessAlive,
            BindingMatched = bindingMatched,
            ReconciledAtUtc = now
        };
    }

    private async Task<ProjectLock?> FindHeldLockAsync(
        Session session,
        IReadOnlyList<Execution> executions,
        CancellationToken cancellationToken)
    {
        var heldLock = await _projectLockRepository
            .GetActiveByRootPathAsync(session.WorkspaceRootPath, cancellationToken)
            .ConfigureAwait(false);

        if (heldLock is null
            || !string.Equals(heldLock.ProjectId, session.ProjectId, StringComparison.Ordinal))
        {
            return null;
        }

        return executions.Any(execution => string.Equals(
            execution.Id,
            heldLock.ExecutionId,
            StringComparison.Ordinal))
            ? heldLock
            : null;
    }

    private void EnsureSupervisorPermitted()
    {
        var guard = _instanceGuard ?? throw new SecondaryInstanceReadOnlyException(
            "Application instance authority is unavailable; reconciliation cannot modify runtime ownership.");
        guard.EnsureSupervisorPermitted();
    }

    private static Execution? ResolveActiveExecution(
        Session session,
        IReadOnlyList<Execution> executions)
    {
        var nonTerminal = executions
            .Where(execution => !ReconciliationExecutionEvidence.IsConfirmedTerminal(execution))
            .OrderByDescending(execution => execution.State == ExecutionState.Ambiguous)
            .ThenByDescending(execution => execution.CreatedAt)
            .ThenBy(execution => execution.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (nonTerminal is not null)
        {
            return nonTerminal;
        }

        if (!string.IsNullOrWhiteSpace(session.ActiveExecutionId))
        {
            var pointed = executions.FirstOrDefault(execution => string.Equals(
                execution.Id,
                session.ActiveExecutionId,
                StringComparison.Ordinal));

            if (pointed is not null)
            {
                return pointed;
            }
        }

        return executions
            .OrderByDescending(execution => execution.CreatedAt)
            .ThenBy(execution => execution.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static Session CreateSession(
        Session session,
        SessionState state,
        ApplicationReconciliationOutcome outcome,
        string? activeExecutionId,
        DateTimeOffset now)
    {
        return new Session(
            session.Id,
            session.Binding,
            session.ProjectId,
            session.WorkspaceRootPath,
            session.NativeSessionId,
            state,
            ToDomainOutcome(outcome),
            session.CloseReason,
            session.ContinuationOfSessionId,
            session.ForkedFromSessionId,
            session.WorkflowRunId,
            session.Role,
            activeExecutionId,
            session.CreatedAt,
            now);
    }

    private static Session CreateClosedSession(Session session, CloseReason closeReason, DateTimeOffset now)
    {
        return new Session(
            session.Id,
            session.Binding,
            session.ProjectId,
            session.WorkspaceRootPath,
            session.NativeSessionId,
            SessionState.Closed,
            session.ReconciliationOutcome,
            closeReason,
            session.ContinuationOfSessionId,
            session.ForkedFromSessionId,
            session.WorkflowRunId,
            session.Role,
            activeExecutionId: null,
            session.CreatedAt,
            now);
    }

    private static Execution CreateExecution(
        Execution execution,
        ExecutionState targetState,
        DateTimeOffset now)
    {
        var failureReason = targetState == ExecutionState.Failed
            ? ExecutionFailureReason.StartupFailure
            : execution.FailureReason;

        return CreateExecution(execution, targetState, failureReason, now);
    }

    private static Execution CreateFailedExecution(
        Execution execution,
        ExecutionFailureReason failureReason,
        DateTimeOffset now)
    {
        return CreateExecution(execution, ExecutionState.Failed, failureReason, now);
    }

    private static Execution CreateExecution(
        Execution execution,
        ExecutionState targetState,
        ExecutionFailureReason failureReason,
        DateTimeOffset now)
    {
        return new Execution(
            execution.Id,
            execution.SessionId,
            execution.ClientRequestId,
            targetState,
            failureReason,
            execution.RequestedRouteId,
            execution.ObservedRouteId,
            execution.RetryOfExecutionId,
            execution.ProcessState,
            execution.ExitCode,
            execution.TerminationReason,
            execution.Artifacts,
            execution.SourceHashBefore,
            execution.SourceHashAfter,
            execution.CreatedAt,
            execution.StartedAt,
            targetState == ExecutionState.Ambiguous ? null : now);
    }

    private static DomainReconciliationOutcome ToDomainOutcome(ApplicationReconciliationOutcome outcome)
    {
        return outcome switch
        {
            ApplicationReconciliationOutcome.Reattached => DomainReconciliationOutcome.Reattached,
            ApplicationReconciliationOutcome.Orphaned => DomainReconciliationOutcome.Orphaned,
            ApplicationReconciliationOutcome.Ambiguous => DomainReconciliationOutcome.Ambiguous,
            ApplicationReconciliationOutcome.BackendMissing => DomainReconciliationOutcome.BackendMissing,
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "An actionable reconciliation outcome is required.")
        };
    }

    private static string BuildReconciliationDetails(
        ReconciliationProbeResult probeResult,
        bool bindingMatched,
        bool terminalTurnEvidence,
        bool promptDeliveryExcluded)
    {
        return string.Concat(
            probeResult.Details,
            " bindingMatched=",
            bindingMatched ? "true" : "false",
            "; terminalTurnEvidence=",
            terminalTurnEvidence ? "true" : "false",
            "; promptDeliveryExcluded=",
            promptDeliveryExcluded ? "true" : "false",
            ".");
    }
}
