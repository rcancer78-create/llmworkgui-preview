using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using ReconciliationOutcome = LLMWorkGUI.Application.Reconciliation.ReconciliationOutcome;

namespace LLMWorkGUI.Infrastructure.OpenCode;

public sealed class SqliteOpenCodeJournalRecoveryService(ISqliteConnectionFactory factory,
    IApplicationInstanceGuard guard, TimeProvider clock, IActivityCenterService? activity = null) : IOpenCodeJournalRecoveryService
{
    // Match the local journal, including rows written before durable event markers were introduced.
    private const string OwnsSession = """
        (EXISTS (SELECT 1 FROM Executions e JOIN ClientRequests c ON c.ExecutionId=e.Id
            WHERE e.SessionId=s.Id AND c.SessionId=s.Id AND c.Id=e.ClientRequestId AND c.RequestedRouteId=e.RequestedRouteId)
         OR EXISTS (SELECT 1 FROM WorkflowAdaptationNativeBindings b JOIN Executions e ON e.Id=b.ExecutionId
            WHERE b.SessionId=s.Id AND e.SessionId=s.Id AND s.ActiveExecutionId=e.Id)
         OR EXISTS (SELECT 1 FROM WorkflowAdaptationRuntimeOwners o JOIN Executions e ON e.Id=o.ExecutionId
            WHERE o.SessionId=s.Id AND e.SessionId=s.Id AND o.State!='Terminated' AND s.ActiveExecutionId=e.Id))
        """;
    private const string Pending = """
        (e.EndedAtUtc IS NULL OR e.State NOT IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch')
            OR s.ActiveExecutionId=e.Id OR EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=e.Id AND l.ReleasedAtUtc IS NULL))
        """;

    public async Task<IReadOnlyList<ReconciliationEvidence>> QuarantineInterruptedAsync(CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        var ids = new List<string>();
        await using (var connection = await factory.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT s.Id FROM Sessions s WHERE s.Backend='OpenCode' AND {OwnsSession}
                    AND (s.State IN ('Starting','Active','Ambiguous','Orphaned')
                        OR EXISTS (SELECT 1 FROM Executions e WHERE e.SessionId=s.Id AND {Pending}))
                ORDER BY s.CreatedAtUtc,s.Id
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        }
        var results = new List<ReconciliationEvidence>();
        foreach (var id in ids)
            if (await TryReconcileAsync(id, cancellationToken) is { } result) results.Add(result);
        return results;
    }

    public async Task<ReconciliationEvidence?> TryReconcileAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = await ReadOwnedSession(connection, transaction, sessionId, cancellationToken);
        if (session is null) return null;
        var pending = await ReadPending(connection, transaction, sessionId, cancellationToken);
        var now = clock.GetUtcNow();
        var events = new List<OpenCodeJournalEvent>();
        foreach (var execution in pending)
        {
            // Idempotent quarantine: preserve existing ambiguous facts and do not add another event.
            if (execution.State == "Ambiguous" && execution.EndedAt is null || execution.IsConfirmedTerminal) continue;
            await using var update = Command(connection, transaction, """
                UPDATE Executions SET State='Ambiguous',EndedAtUtc=NULL,TerminationReason='NativeOutcomeUnconfirmedAfterRecovery'
                WHERE Id=$execution AND SessionId=$session
                """);
            update.Parameters.AddWithValue("$execution", execution.Id); update.Parameters.AddWithValue("$session", sessionId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            events.Add(await OpenCodeJournalEvents.AppendAsync(connection, transaction, execution.Id, sessionId, execution.RouteId,
                "OpenCodeInterrupted", ExecutionState.Ambiguous, "InterruptedOutcomeUnconfirmed", now, cancellationToken));
        }
        // A stale ownership signal retains the slot/lock, but must not invent an active turn or
        // erase the terminal fact of an execution that already has a confirmed end timestamp.
        var activeId = pending.FirstOrDefault(e => e.Id == session.ActiveId)?.Id
            ?? pending.FirstOrDefault(e => !e.IsConfirmedTerminal)?.Id;
        var outcome = pending.Count > 0 ? ReconciliationOutcome.Ambiguous : ReconciliationOutcome.Orphaned;
        await using (var update = Command(connection, transaction, """
            UPDATE Sessions SET State=CASE WHEN State='Closed' THEN State ELSE $state END,
                ReconciliationOutcome=$outcome,ActiveExecutionId=$active,LastEventAtUtc=$now WHERE Id=$session
            """))
        {
            update.Parameters.AddWithValue("$state", pending.Count > 0 ? "Ambiguous" : "Orphaned");
            update.Parameters.AddWithValue("$outcome", outcome.ToString()); update.Parameters.AddWithValue("$active", (object?)activeId ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now.ToString("O")); update.Parameters.AddWithValue("$session", sessionId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        guard.EnsureSupervisorPermitted();
        transaction.Commit(); foreach (var item in events) OpenCodeJournalEvents.Publish(activity, item);
        return new ReconciliationEvidence
        {
            LocalSessionId = sessionId, ExecutionId = activeId, NativeSessionId = session.NativeId,
            Outcome = outcome, ProcessAlive = false, BindingMatched = false, ReconciledAtUtc = now,
            Details = pending.Count > 0 && pending.All(e => e.IsConfirmedTerminal)
                ? "Локальные выполнения завершены, но остались признаки владения слотом или writer lock. Terminal evidence сохранено; автоматическое освобождение и повтор не выполнялись."
                : pending.Count > 0
                ? "Нативный исход OpenCode не подтверждён. Незавершённые выполнения, слоты и writer locks сохранены; повтор не разрешён. PID не подтверждает восстановление сессии."
                : "Незавершённых локальных выполнений нет. Нативное восстановление сессии не подтверждено; создайте новую сессию."
        };
    }

    public async Task<ReconciliationRecoveryResult?> TryApplyActionAsync(string sessionId, RecoveryAction action, CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        if (action is not (RecoveryAction.ResetSession or RecoveryAction.CloseSession or RecoveryAction.AcknowledgeAmbiguous))
            throw new ArgumentOutOfRangeException(nameof(action));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var session = await ReadOwnedSession(connection, transaction, sessionId, cancellationToken);
        if (session is null) return null;
        if (session.State is "Draft" or "Closed") throw new InvalidOperationException("Эта сессия не принимает действия восстановления.");
        if ((await ReadPending(connection, transaction, sessionId, cancellationToken)).Count > 0)
            throw new InvalidOperationException("Нативный исход OpenCode не подтверждён. Reset, закрытие и acknowledge не освобождают slot или writer lock. Сначала требуется доказанное завершение нативного выполнения.");
        if (action == RecoveryAction.AcknowledgeAmbiguous && session.State != "Ambiguous")
            throw new InvalidOperationException("Acknowledge применяется только к неопределённой сессии.");
        var target = action == RecoveryAction.AcknowledgeAmbiguous ? session.State : "Closed";
        var reason = action switch { RecoveryAction.CloseSession => "UserClose", RecoveryAction.ResetSession => "UserReset", _ => session.CloseReason };
        var now = clock.GetUtcNow();
        await using (var update = Command(connection, transaction, "UPDATE Sessions SET State=$state,CloseReason=$reason,ActiveExecutionId=NULL,LastEventAtUtc=$now WHERE Id=$session"))
        {
            update.Parameters.AddWithValue("$state", target); update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$now", now.ToString("O")); update.Parameters.AddWithValue("$session", sessionId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        OpenCodeJournalEvent? audit = null;
        await using (var command = Command(connection, transaction, "SELECT Id,RequestedRouteId,State FROM Executions WHERE SessionId=$session ORDER BY CreatedAtUtc DESC,Id DESC LIMIT 1"))
        {
            command.Parameters.AddWithValue("$session", sessionId);
            string? executionId = null, routeId = null; ExecutionState state = default;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                if (await reader.ReadAsync(cancellationToken))
                { executionId = reader.GetString(0); routeId = reader.GetString(1); state = Enum.Parse<ExecutionState>(reader.GetString(2)); }
            if (executionId is not null)
                audit = await OpenCodeJournalEvents.AppendAsync(connection, transaction, executionId, sessionId, routeId!,
                    "OpenCodeRecoveryAction", state, action.ToString(), now, cancellationToken);
        }
        guard.EnsureSupervisorPermitted();
        transaction.Commit(); if (audit is not null) OpenCodeJournalEvents.Publish(activity, audit);
        return new ReconciliationRecoveryResult
        {
            LocalSessionId = sessionId, Action = action, SessionState = Enum.Parse<SessionState>(target), CloseReason = Enum.Parse<CloseReason>(reason),
            ReleasedLockId = null, AuditDetails = action == RecoveryAction.AcknowledgeAmbiguous
                ? "Неопределённость подтверждена пользователем. Локальная сессия сохраняет неопределённое состояние; блокировки не освобождались. Нативные команды и повторная отправка не выполнялись."
                : "Закрыта только локальная сессия с подтверждённо завершёнными выполнениями. Нативные команды и повторная отправка не выполнялись."
        };
    }

    public async Task<int> ReplayActivityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        return await OpenCodeJournalEvents.ReplayAsync(connection, activity, cancellationToken);
    }

    private static async Task<OwnedSession?> ReadOwnedSession(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        await using var command = Command(connection, transaction, $"SELECT s.NativeSessionId,s.State,s.ActiveExecutionId,s.CloseReason FROM Sessions s WHERE s.Id=$id AND s.Backend='OpenCode' AND {OwnsSession}");
        command.Parameters.AddWithValue("$id", id); await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)) : null;
    }
    private static async Task<List<PendingExecution>> ReadPending(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken token)
    {
        var result = new List<PendingExecution>();
        await using var command = Command(connection, transaction, $"SELECT e.Id,e.RequestedRouteId,e.State,e.EndedAtUtc FROM Executions e JOIN Sessions s ON s.Id=e.SessionId WHERE s.Id=$id AND {Pending} ORDER BY e.CreatedAtUtc DESC,e.Id DESC");
        command.Parameters.AddWithValue("$id", id); await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return result;
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private sealed record OwnedSession(string? NativeId, string State, string? ActiveId, string CloseReason);
    private sealed record PendingExecution(string Id, string RouteId, string State, string? EndedAt)
    {
        public bool IsConfirmedTerminal => EndedAt is not null && State is "Succeeded" or "Failed" or "TimedOut" or "Cancelled" or "RouteMismatch";
    }
}
