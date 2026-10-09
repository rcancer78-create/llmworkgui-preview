using System.Globalization;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Reconciliation;

public sealed partial class ReconciliationService
{
    private async Task CommitAutomaticReconciliationAsync(Session originalSession,
        IReadOnlyList<Execution> originalExecutions, Execution? originalActiveExecution,
        IReadOnlyList<ExecutionEventRecord> originalEvents, Execution? reconciledExecution,
        Session reconciledSession, string auditPayload, DateTimeOffset now, CancellationToken token)
    {
        EnsureSupervisorPermitted();
        var factory = _connectionFactory ?? throw new InvalidOperationException(
            "Atomic reconciliation storage is unavailable; session and execution ownership are unchanged.");
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        // The probe ran outside the write transaction. Its source facts must still be current.
        await ValidateAutomaticSessionAsync(connection, transaction, originalSession, token).ConfigureAwait(false);
        await ValidateAutomaticExecutionsAsync(connection, transaction, originalSession.Id, originalExecutions, token)
            .ConfigureAwait(false);
        if (originalActiveExecution is not null)
            await ValidateAutomaticEventsAsync(connection, transaction, originalActiveExecution.Id, originalEvents, token)
                .ConfigureAwait(false);

        EnsureSupervisorPermitted();
        if (originalActiveExecution is not null && reconciledExecution is not null
            && reconciledExecution.State != originalActiveExecution.State)
        {
            // Preserve every native field that reconciliation does not own.
            await using var update = RecoveryCommand(connection, transaction, """
                UPDATE Executions SET State=$state,FailureReason=$failure,EndedAtUtc=$ended
                WHERE Id=$execution AND SessionId=$session;
                """);
            Add(update, "$execution", reconciledExecution.Id); Add(update, "$session", originalSession.Id);
            Add(update, "$state", reconciledExecution.State.ToString()); Add(update, "$failure", reconciledExecution.FailureReason.ToString());
            Add(update, "$ended", reconciledExecution.EndedAt?.ToUniversalTime().ToString("O"));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw RecoveryChanged();
        }
        await using (var update = RecoveryCommand(connection, transaction, """
            UPDATE Sessions SET State=$state,ReconciliationOutcome=$outcome,ActiveExecutionId=$active,LastEventAtUtc=$now
            WHERE Id=$session;
            """))
        {
            Add(update, "$session", originalSession.Id); Add(update, "$state", reconciledSession.State.ToString());
            Add(update, "$outcome", reconciledSession.ReconciliationOutcome.ToString());
            Add(update, "$active", reconciledSession.ActiveExecutionId); Add(update, "$now", now.ToUniversalTime().ToString("O"));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw RecoveryChanged();
        }
        if (reconciledExecution is not null)
        {
            await using var audit = RecoveryCommand(connection, transaction, """
                INSERT INTO ExecutionEvents(Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
                SELECT $id,$execution,COALESCE(MAX(Sequence)+1,0),$kind,$payload,'PrivateSource',$now
                FROM ExecutionEvents WHERE ExecutionId=$execution;
                """);
            Add(audit, "$id", Guid.NewGuid().ToString("D")); Add(audit, "$execution", reconciledExecution.Id);
            Add(audit, "$kind", ReconciliationEventKind); Add(audit, "$payload", auditPayload);
            Add(audit, "$now", now.ToUniversalTime().ToString("O"));
            await audit.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        EnsureSupervisorPermitted();
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static async Task ValidateAutomaticSessionAsync(SqliteConnection connection, SqliteTransaction transaction,
        Session session, CancellationToken token)
    {
        await using var check = RecoveryCommand(connection, transaction, """
            SELECT CreatedAtUtc,LastEventAtUtc FROM Sessions WHERE Id=$session AND ProjectId=$project AND State=$state
                AND Backend=$backend AND ProviderProfileId=$provider AND AccountId=$account AND ModelId=$model
                AND ReasoningEffort IS $reasoning AND SpeedMode IS $speed AND ExecutionMode IS $mode
                AND WorkspaceRootPath=$root AND NativeSessionId IS $native AND ActiveExecutionId IS $active
                AND ReconciliationOutcome=$outcome AND CloseReason=$close AND ContinuationOfSessionId IS $continuation
                AND ForkedFromSessionId IS $fork AND WorkflowRunId IS $run AND Role IS $role;
            """);
        Add(check, "$session", session.Id); Add(check, "$project", session.ProjectId); Add(check, "$state", session.State.ToString());
        Add(check, "$backend", session.Binding.Backend.ToString()); Add(check, "$provider", session.Binding.ProviderProfileId);
        Add(check, "$account", session.Binding.AccountId); Add(check, "$model", session.Binding.ModelId);
        Add(check, "$reasoning", session.Binding.ReasoningEffort); Add(check, "$speed", session.Binding.SpeedMode);
        Add(check, "$mode", session.Binding.ExecutionMode); Add(check, "$root", session.WorkspaceRootPath);
        Add(check, "$native", session.NativeSessionId); Add(check, "$active", session.ActiveExecutionId);
        Add(check, "$outcome", session.ReconciliationOutcome.ToString()); Add(check, "$close", session.CloseReason.ToString());
        Add(check, "$continuation", session.ContinuationOfSessionId); Add(check, "$fork", session.ForkedFromSessionId);
        Add(check, "$run", session.WorkflowRunId); Add(check, "$role", session.Role);
        await using var reader = await check.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)
            || !AutomaticTimestampMatches(reader, 0, session.CreatedAt)
            || !AutomaticTimestampMatches(reader, 1, session.LastEventAt)) throw RecoveryChanged();
    }

    private static async Task ValidateAutomaticExecutionsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sessionId, IReadOnlyList<Execution> executions, CancellationToken token)
    {
        var expected = executions.ToDictionary(execution => execution.Id, StringComparer.Ordinal);
        await using var check = RecoveryCommand(connection, transaction, """
            SELECT Id,ClientRequestId,State,FailureReason,RequestedRouteId,ObservedRouteId,RetryOfExecutionId,ProcessState,
                ExitCode,TerminationReason,SourceHashBefore,SourceHashAfter,CreatedAtUtc,StartedAtUtc,EndedAtUtc
            FROM Executions WHERE SessionId=$session;
            """);
        Add(check, "$session", sessionId);
        await using var reader = await check.ExecuteReaderAsync(token).ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (!expected.TryGetValue(reader.GetString(0), out var execution)
                || reader.GetString(1) != execution.ClientRequestId || reader.GetString(2) != execution.State.ToString()
                || reader.GetString(3) != execution.FailureReason.ToString() || reader.GetString(4) != execution.RequestedRouteId
                || AutomaticString(reader, 5) != execution.ObservedRouteId || AutomaticString(reader, 6) != execution.RetryOfExecutionId
                || AutomaticString(reader, 7) != execution.ProcessState
                || (reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8)) != execution.ExitCode
                || AutomaticString(reader, 9) != execution.TerminationReason || AutomaticString(reader, 10) != execution.SourceHashBefore
                || AutomaticString(reader, 11) != execution.SourceHashAfter || !AutomaticTimestampMatches(reader, 12, execution.CreatedAt)
                || !AutomaticTimestampMatches(reader, 13, execution.StartedAt) || !AutomaticTimestampMatches(reader, 14, execution.EndedAt))
                throw RecoveryChanged();
            count++;
        }
        if (count != expected.Count) throw RecoveryChanged();
    }

    private static async Task ValidateAutomaticEventsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string executionId, IReadOnlyList<ExecutionEventRecord> events, CancellationToken token)
    {
        await using var check = RecoveryCommand(connection, transaction, """
            SELECT Id,Sequence,EventKind,NormalizedRedactedPayloadJson,RawRedactedPayloadText,DataClassification,OccurredAtUtc
            FROM ExecutionEvents WHERE ExecutionId=$execution ORDER BY Sequence,Id;
            """);
        Add(check, "$execution", executionId);
        await using var reader = await check.ExecuteReaderAsync(token).ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (count >= events.Count) throw RecoveryChanged();
            var expected = events[count];
            if (reader.GetString(0) != expected.Id || reader.GetInt64(1) != expected.Sequence || reader.GetString(2) != expected.EventKind
                || AutomaticString(reader, 3) != expected.NormalizedRedactedPayloadJson || AutomaticString(reader, 4) != expected.RawRedactedPayloadText
                || AutomaticString(reader, 5) != expected.DataClassification?.ToString()
                || !AutomaticTimestampMatches(reader, 6, expected.OccurredAt)) throw RecoveryChanged();
            count++;
        }
        if (count != events.Count) throw RecoveryChanged();
    }

    private static string? AutomaticString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static bool AutomaticTimestampMatches(SqliteDataReader reader, int index, DateTimeOffset? expected)
        => reader.IsDBNull(index) ? expected is null : expected is not null
            && DateTimeOffset.TryParse(reader.GetString(index), CultureInfo.InvariantCulture, DateTimeStyles.None, out var actual)
            && actual == expected.Value;
}
