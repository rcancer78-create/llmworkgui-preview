using System.Text.Json;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Lifecycle;

public sealed partial class AppCrashRecoveryService
{
    // Ambiguous is an enum terminal state but does not prove native completion. A missing or
    // invalid end timestamp likewise retains ownership, including historical Failed/Succeeded rows.
    private static string ConfirmedTerminalSql(string alias) => $"""
        ({alias}.State IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch')
            AND julianday({alias}.EndedAtUtc) >= julianday({alias}.CreatedAtUtc)
            AND ({alias}.StartedAtUtc IS NULL OR (julianday({alias}.StartedAtUtc) >= julianday({alias}.CreatedAtUtc)
                AND julianday({alias}.EndedAtUtc) >= julianday({alias}.StartedAtUtc))))
        """;

    private async Task<bool> QuarantineExecutionAsync(string executionId, DateTimeOffset now, CancellationToken token)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        await using var connection = await _connectionFactory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        string? previousState;
        await using (var read = RuntimeCommand(connection, transaction, """
            SELECT e.State FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
            WHERE e.Id=$id AND s.Backend!='NativeGateway';
            """))
        {
            read.Parameters.AddWithValue("$id", executionId);
            previousState = await read.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        }
        if (previousState is null || !InterruptedExecutionStates.Any(state => state.ToString() == previousState))
            return false;

        // The immediate transaction re-reads the current state. A success committed after the initial
        // inventory is preserved; quarantine and its event either commit together or both roll back.
        await using (var update = RuntimeCommand(connection, transaction, """
            UPDATE Executions SET State='Ambiguous',EndedAtUtc=NULL,TerminationReason=$reason
            WHERE Id=$id AND State=$previous;
            """))
        {
            update.Parameters.AddWithValue("$id", executionId);
            update.Parameters.AddWithValue("$previous", previousState);
            update.Parameters.AddWithValue("$reason", ExecutionUncertaintyReason);
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return false;
        }
        await using (var audit = RuntimeCommand(connection, transaction, """
            INSERT INTO ExecutionEvents
                (Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
            SELECT $event,$id,COALESCE(MAX(Sequence)+1,0),$kind,$payload,'PrivateSource',$now
            FROM ExecutionEvents WHERE ExecutionId=$id;
            """))
        {
            audit.Parameters.AddWithValue("$event", Guid.NewGuid().ToString("D"));
            audit.Parameters.AddWithValue("$id", executionId);
            audit.Parameters.AddWithValue("$kind", AuditEventKind);
            audit.Parameters.AddWithValue("$now", now.ToString("O"));
            audit.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new
            {
                executionId, previousState, recoveryState = ExecutionState.Ambiguous.ToString(),
                reason = ExecutionUncertaintyReason, recoveredAtUtc = now
            }));
            await audit.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> QuarantineSessionAsync(string sessionId, DateTimeOffset now, CancellationToken token)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        await using var connection = await _connectionFactory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        string? activeId;
        await using (var read = RuntimeCommand(connection, transaction, """
            SELECT ActiveExecutionId FROM Sessions WHERE Id=$id
                AND Backend!='NativeGateway' AND State IN ('Starting','Active');
            """))
        {
            read.Parameters.AddWithValue("$id", sessionId);
            await using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return false;
            activeId = reader.IsDBNull(0) ? null : reader.GetString(0);
        }
        var executions = new List<(string Id, bool ConfirmedTerminal)>();
        await using (var read = RuntimeCommand(connection, transaction, $"""
            SELECT e.Id,CASE WHEN {ConfirmedTerminalSql("e")} THEN 1 ELSE 0 END
            FROM Executions e WHERE e.SessionId=$id ORDER BY e.CreatedAtUtc DESC,e.Id;
            """))
        {
            read.Parameters.AddWithValue("$id", sessionId);
            await using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                executions.Add((reader.GetString(0), reader.GetInt64(1) == 1));
        }
        // A missing pointed row is unknown ownership, not proof that the old turn ended.
        var pointerIsConfirmed = activeId is null || executions.Any(e => e.Id == activeId && e.ConfirmedTerminal);
        var retainedId = pointerIsConfirmed ? executions.Where(e => !e.ConfirmedTerminal).Select(e => e.Id).FirstOrDefault() : activeId;
        await using (var update = RuntimeCommand(connection, transaction, """
            UPDATE Sessions SET State='Ambiguous',ReconciliationOutcome='Ambiguous',ActiveExecutionId=$active,LastEventAtUtc=$now
            WHERE Id=$id AND State IN ('Starting','Active') AND Backend!='NativeGateway';
            """))
        {
            update.Parameters.AddWithValue("$id", sessionId);
            update.Parameters.AddWithValue("$active", (object?)retainedId ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return false;
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ReleaseConfirmedLockAsync(string lockId, DateTimeOffset now, CancellationToken token)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        await using var connection = await _connectionFactory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var release = RuntimeCommand(connection, transaction, $"""
            UPDATE ProjectLocks SET ReleasedAtUtc=$now,ReleaseReason=$reason
            WHERE Id=$id AND ReleasedAtUtc IS NULL AND EXISTS (
                SELECT 1 FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
                WHERE e.Id=ProjectLocks.ExecutionId AND s.ProjectId=ProjectLocks.ProjectId
                    AND {ConfirmedTerminalSql("e")} AND s.State!='Orphaned' AND s.ActiveExecutionId IS NULL
                    AND NOT EXISTS (SELECT 1 FROM Executions pending WHERE pending.SessionId=s.Id
                        AND CASE WHEN {ConfirmedTerminalSql("pending")} THEN 0 ELSE 1 END = 1));
            """);
        release.Parameters.AddWithValue("$id", lockId);
        release.Parameters.AddWithValue("$now", now.ToString("O"));
        release.Parameters.AddWithValue("$reason", LockReleaseReason);
        var changed = await release.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return changed == 1;
    }

    private static SqliteCommand RuntimeCommand(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
