using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Domain.Enums;
using ReconciliationOutcome = LLMWorkGUI.Application.Reconciliation.ReconciliationOutcome;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Providers;

/// <summary>No native resume/termination evidence is available. Preserve pending ownership on restart.</summary>
public sealed class SqliteNativeGatewayRecoveryService(ISqliteConnectionFactory factory,
    IApplicationInstanceGuard guard, TimeProvider clock, IActivityCenterService? activity = null) : INativeGatewayJournalRecoveryService
{
    private const string Pending = """
        (e.EndedAtUtc IS NULL OR e.State NOT IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch')
         OR s.ActiveExecutionId=e.Id OR EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=e.Id AND l.ReleasedAtUtc IS NULL))
        """;

    public async Task<int> ReplayActivityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await NativeGatewayJournalEvents.ReplayAsync(connection, activity, cancellationToken).ConfigureAwait(false);
    }

    public async Task QuarantineInterruptedAsync(CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        var ids = new List<string>();
        await using (var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT s.Id FROM Sessions s WHERE s.Backend='NativeGateway' AND
                  (s.State IN ('Starting','Active','Ambiguous','Orphaned') OR
                   EXISTS (SELECT 1 FROM Executions e WHERE e.SessionId=s.Id AND {Pending}))
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        }
        foreach (var id in ids) await TryReconcileAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReconciliationEvidence?> TryReconcileAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var find = Command(connection, transaction, "SELECT State FROM Sessions WHERE Id=$session AND Backend='NativeGateway'", sessionId))
            if (await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string) return null;
        var pending = new List<(string Id, string State, bool Ended)>();
        await using (var find = Command(connection, transaction, $"""
            SELECT e.Id,e.State,e.EndedAtUtc FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
            WHERE s.Id=$session AND {Pending} ORDER BY e.CreatedAtUtc,e.Id
            """, sessionId))
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                pending.Add((reader.GetString(0), reader.GetString(1), !reader.IsDBNull(2)));
        var now = clock.GetUtcNow();
        var events = new List<NativeGatewayJournalEvent>();
        foreach (var execution in pending)
        {
            if (execution.State == "Ambiguous" && !execution.Ended ||
                execution.Ended && execution.State is "Succeeded" or "Failed" or "TimedOut" or "Cancelled" or "RouteMismatch") continue;
            await using var update = Command(connection, transaction, """
                UPDATE Executions SET State='Ambiguous',EndedAtUtc=NULL,TerminationReason='NativeOutcomeUnconfirmedAfterRecovery' WHERE Id=$execution;
                """, sessionId);
            update.Parameters.AddWithValue("$execution", execution.Id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            events.Add(await NativeGatewayJournalEvents.AppendAsync(connection, transaction, execution.Id, sessionId,
                ExecutionState.Ambiguous, ExecutionFailureReason.None, true, now, cancellationToken).ConfigureAwait(false));
        }
        var active = pending.FirstOrDefault(e => !e.Ended || e.State is not ("Succeeded" or "Failed" or "TimedOut" or "Cancelled" or "RouteMismatch")).Id;
        var outcome = pending.Count > 0 ? ReconciliationOutcome.Ambiguous : ReconciliationOutcome.Orphaned;
        await using (var update = Command(connection, transaction, """
            UPDATE Sessions SET State=CASE WHEN State='Closed' THEN State ELSE $state END,
                ReconciliationOutcome=$state,ActiveExecutionId=$active,LastEventAtUtc=$now WHERE Id=$session
            """, sessionId))
        {
            update.Parameters.AddWithValue("$state", outcome.ToString());
            update.Parameters.AddWithValue("$active", (object?)active ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        transaction.Commit();
        foreach (var item in events) NativeGatewayJournalEvents.Publish(activity, item);
        return new ReconciliationEvidence
        {
            LocalSessionId = sessionId, ExecutionId = active, NativeSessionId = null, Outcome = outcome,
            ProcessAlive = false, BindingMatched = false, ReconciledAtUtc = now,
            Details = "Нативное продолжение и завершение LLMGateway не подтверждены. Локальные блокировки и лимиты сохранены; запрос не повторён."
        };
    }

    public async Task<ReconciliationRecoveryResult?> TryApplyActionAsync(string sessionId, RecoveryAction action, CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        if (action is not (RecoveryAction.CloseSession or RecoveryAction.ResetSession or RecoveryAction.AcknowledgeAmbiguous))
            throw new ArgumentOutOfRangeException(nameof(action));
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = Command(connection, transaction, "SELECT State FROM Sessions WHERE Id=$session AND Backend='NativeGateway'", sessionId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string) return null;
        // Completed single-turn sessions are already closed. A generic user action is not proof
        // of termination and cannot clear executions, reservations, or locks for this backend.
        throw new InvalidOperationException("Для LLMGateway требуется подтверждённое завершение нативного выполнения. Закрытие, сброс и acknowledge не освобождают блокировки.");
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, string session)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$session", session); return command;
    }
}
