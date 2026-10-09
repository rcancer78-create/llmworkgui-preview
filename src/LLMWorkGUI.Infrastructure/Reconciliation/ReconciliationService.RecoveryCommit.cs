using System.Text.Json;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Reconciliation;

public sealed partial class ReconciliationService
{
    private async Task<ReconciliationRecoveryResult> CommitRecoveryActionAsync(Session session,
        IReadOnlyList<Execution> executions, ProjectLock? heldLock, RecoveryAction action,
        DateTimeOffset now, CancellationToken token)
    {
        EnsureSupervisorPermitted();
        var factory = _connectionFactory ?? throw new InvalidOperationException("Atomic recovery storage is unavailable; session and ownership are unchanged.");
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var check = RecoveryCommand(connection, transaction, """
            SELECT COUNT(*) FROM Sessions WHERE Id=$session AND ProjectId=$project AND State=$state
                AND Backend=$backend AND ProviderProfileId=$provider AND AccountId=$account AND ModelId=$model
                AND ReasoningEffort IS $reasoning AND SpeedMode IS $speed AND ExecutionMode IS $mode
                AND WorkspaceRootPath=$root AND NativeSessionId IS $native AND ActiveExecutionId IS $active
                AND julianday(LastEventAtUtc)=julianday($last);
            """))
        {
            Add(check, "$session", session.Id); Add(check, "$project", session.ProjectId); Add(check, "$state", session.State.ToString());
            Add(check, "$backend", session.Binding.Backend.ToString()); Add(check, "$provider", session.Binding.ProviderProfileId);
            Add(check, "$account", session.Binding.AccountId); Add(check, "$model", session.Binding.ModelId);
            Add(check, "$reasoning", session.Binding.ReasoningEffort); Add(check, "$speed", session.Binding.SpeedMode);
            Add(check, "$mode", session.Binding.ExecutionMode); Add(check, "$root", session.WorkspaceRootPath);
            Add(check, "$native", session.NativeSessionId); Add(check, "$active", session.ActiveExecutionId);
            Add(check, "$last", session.LastEventAt.ToString("O"));
            if (Convert.ToInt64(await check.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1) throw RecoveryChanged();
        }
        var actualIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var check = RecoveryCommand(connection, transaction, """
            SELECT e.Id,CASE WHEN
                (e.State IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch')
                    AND julianday(e.EndedAtUtc)>=julianday(e.CreatedAtUtc)
                    AND (e.StartedAtUtc IS NULL OR (julianday(e.StartedAtUtc)>=julianday(e.CreatedAtUtc)
                        AND julianday(e.EndedAtUtc)>=julianday(e.StartedAtUtc))))
                OR (e.State IN ('Queued','Starting') AND e.StartedAtUtc IS NULL AND e.ProcessState IS NULL AND e.EndedAtUtc IS NULL)
                THEN 1 ELSE 0 END FROM Executions e WHERE e.SessionId=$session;
            """))
        {
            Add(check, "$session", session.Id);
            await using var reader = await check.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (reader.GetInt64(1) != 1) throw RecoveryChanged();
                actualIds.Add(reader.GetString(0));
            }
        }
        if (!actualIds.SetEquals(executions.Select(e => e.Id))
            || session.ActiveExecutionId is not null && !actualIds.Contains(session.ActiveExecutionId)) throw RecoveryChanged();

        var canonicalRoot = ProjectLock.CanonicalizeRoot(session.WorkspaceRootPath);
        if (OperatingSystem.IsWindows()) canonicalRoot = canonicalRoot.ToUpperInvariant();
        await using (var check = RecoveryCommand(connection, transaction, """
            SELECT Id,ExecutionId,ProjectId FROM ProjectLocks WHERE CanonicalRootPath=$root AND ReleasedAtUtc IS NULL;
            """))
        {
            Add(check, "$root", canonicalRoot);
            await using var reader = await check.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (heldLock is null || reader.GetString(0) != heldLock.Id || reader.GetString(1) != heldLock.ExecutionId
                    || reader.GetString(2) != session.ProjectId || !actualIds.Contains(heldLock.ExecutionId)) throw RecoveryChanged();
            }
            else if (heldLock is not null) throw RecoveryChanged();
        }
        EnsureSupervisorPermitted();
        await using (var cancel = RecoveryCommand(connection, transaction, """
            UPDATE Executions SET State='Failed',FailureReason='UserCancelled',EndedAtUtc=$now
            WHERE SessionId=$session AND State IN ('Queued','Starting')
                AND StartedAtUtc IS NULL AND ProcessState IS NULL AND EndedAtUtc IS NULL;
            """))
        {
            Add(cancel, "$session", session.Id); Add(cancel, "$now", now.ToString("O"));
            await cancel.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        var targetState = action == RecoveryAction.AcknowledgeAmbiguous ? session.State : SessionState.Closed;
        var reason = action switch { RecoveryAction.CloseSession => CloseReason.UserClose, RecoveryAction.ResetSession => CloseReason.UserReset, _ => session.CloseReason };
        await using (var close = RecoveryCommand(connection, transaction, """
            UPDATE Sessions SET State=$state,CloseReason=$reason,ActiveExecutionId=NULL,LastEventAtUtc=$now WHERE Id=$session;
            """))
        {
            Add(close, "$session", session.Id); Add(close, "$state", targetState.ToString());
            Add(close, "$reason", reason.ToString()); Add(close, "$now", now.ToString("O"));
            if (await close.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw RecoveryChanged();
        }
        if (heldLock is not null)
        {
            await using var release = RecoveryCommand(connection, transaction, """
                UPDATE ProjectLocks SET ReleasedAtUtc=$now,ReleaseReason=$reason
                WHERE Id=$lock AND ExecutionId=$execution AND ProjectId=$project AND ReleasedAtUtc IS NULL;
                """);
            Add(release, "$lock", heldLock.Id); Add(release, "$execution", heldLock.ExecutionId);
            Add(release, "$project", session.ProjectId); Add(release, "$now", now.ToString("O"));
            Add(release, "$reason", $"Recovery action '{action}' for session '{session.Id}'.");
            if (await release.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw RecoveryChanged();
        }
        var details = $"Recovery action '{action}' applied to session '{session.Id}' (state '{session.State}' -> '{targetState}').";
        var auditId = heldLock?.ExecutionId ?? ResolveActiveExecution(session, executions)?.Id;
        if (auditId is not null)
        {
            await using var audit = RecoveryCommand(connection, transaction, """
                INSERT INTO ExecutionEvents(Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
                SELECT $id,$execution,COALESCE(MAX(Sequence)+1,0),'RecoveryAction',$payload,'PrivateSource',$now
                FROM ExecutionEvents WHERE ExecutionId=$execution;
                """);
            Add(audit, "$id", Guid.NewGuid().ToString("D")); Add(audit, "$execution", auditId); Add(audit, "$now", now.ToString("O"));
            Add(audit, "$payload", JsonSerializer.Serialize(new { sessionId = session.Id, action = action.ToString(),
                previousSessionState = session.State.ToString(), sessionState = targetState.ToString(), closeReason = reason.ToString(),
                lockId = heldLock?.Id, releasedLockId = heldLock?.Id, reason = details }));
            await audit.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        EnsureSupervisorPermitted();
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new ReconciliationRecoveryResult { LocalSessionId = session.Id, Action = action, SessionState = targetState,
            CloseReason = reason, ReleasedLockId = heldLock?.Id, AuditDetails = details };
    }

    private static InvalidOperationException RecoveryChanged() => new("Session, execution or writer ownership changed; native terminal evidence must be revalidated before recovery.");
    private static SqliteCommand RecoveryCommand(SqliteConnection connection, SqliteTransaction transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
