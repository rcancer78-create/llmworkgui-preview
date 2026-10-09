using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Providers;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Mirasim;

/// <summary>Stored project execution before its writer lock; terminal commit before ownership release.</summary>
public sealed class SqliteMirasimExecutionJournal(ISqliteConnectionFactory factory, IApplicationInstanceGuard guard,
    TimeProvider clock) : IMirasimExecutionJournal
{
    // Same health/auth/concurrency/fan-out gates as native admission; no native-identity inference.
    private static string RoutesSql => NativeGatewayRouteQuery.Sql.Replace("'NativeGateway'", "'Mirasim'", StringComparison.Ordinal)
        .Replace("IN ('Mirasim',p.Id)", "IN ('Mirasim','mirasim',p.Id)", StringComparison.Ordinal);

    public async Task<MirasimExecutionAdmission> BeginAsync(MirasimExecutionTarget target, string policyFingerprint, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var root = ProjectLock.CanonicalizeRoot(target.RootPath);
        string? route = null, account = null, model = null;
        await using (var command = Command(connection, transaction, RoutesSql +
            " AND p.Id=$profile AND m.ProviderModelId=$native AND ($route IS NULL OR r.Id=$route) LIMIT 2",
            ("$project", target.Context.ProjectId), ("$root", root), ("$now", Now()),
            ("$profile", target.Context.ProviderProfileId), ("$native", target.NativeModelId), ("$route", target.RequestedRouteId)))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new MirasimEgressPolicyException();
            account = reader.GetString(1); model = reader.GetString(2); route = reader.GetString(5);
            // The requested provider/native-model tuple must identify one saved route. Never choose
            // a default account, reasoning variant or failover when that tuple is ambiguous.
            if (await reader.ReadAsync(token).ConfigureAwait(false)) throw new MirasimEgressPolicyException();
        }
        var rows = await SqliteEgressPolicyReader.ReadAsync(connection, transaction, target.Context.ProjectId, route, token).ConfigureAwait(false);
        if (!Directory.Exists(root) || !string.Equals(root, ProjectLock.CanonicalizeRoot(rows.Project.RootPath), StringComparison.OrdinalIgnoreCase)
            || SqliteMirasimEgressPolicy.Fingerprint((rows.Project, rows.Profile)) != policyFingerprint) throw new MirasimEgressPolicyException();
        var entry = new MirasimExecutionAdmission(target, Guid.NewGuid().ToString("D"), route, account, model, policyFingerprint, rows.Fingerprint);
        await using (var command = Command(connection, transaction, """
            INSERT INTO Sessions (Id,ProjectId,Backend,ProviderProfileId,AccountId,ModelId,WorkspaceRootPath,NativeSessionId,
                State,ReconciliationOutcome,CloseReason,ActiveExecutionId,CreatedAtUtc,LastEventAtUtc)
            VALUES ($session,$project,'Mirasim',$profile,$account,$model,$root,$nativeSession,'Active','None','None',$execution,$now,$now);
            INSERT INTO Executions (Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,CreatedAtUtc)
            VALUES ($execution,$session,$request,'Starting','None',$route,$now);
            INSERT INTO ClientRequests (Id,SessionId,ExecutionId,PromptHash,RequestedRouteId,CreatedAtUtc)
            VALUES ($request,$session,$execution,$hash,$route,$now);
            """, ("$session", entry.SessionId), ("$project", target.Context.ProjectId), ("$profile", target.Context.ProviderProfileId),
            ("$account", account), ("$model", model), ("$root", root), ("$nativeSession", target.NativeSessionId),
            ("$execution", target.ExecutionId), ("$request", Guid.NewGuid().ToString("D")), ("$route", route), ("$hash", target.PromptSha256), ("$now", Now())))
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await AppendAsync(connection, transaction, entry, "MirasimAdmitted", null, token).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted(); token.ThrowIfCancellationRequested(); await transaction.CommitAsync(token).ConfigureAwait(false);
        return entry;
    }

    public async Task MarkRunningAsync(MirasimExecutionAdmission entry, string body, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var rows = await SqliteEgressPolicyReader.ReadAsync(connection, transaction, entry.Target.Context.ProjectId, entry.RouteId, token).ConfigureAwait(false);
        if (rows.Fingerprint != entry.RouteFingerprint) throw new MirasimEgressPolicyException();
        if (entry.Target.RequiresWriterLock)
        {
            await using var owner = Command(connection, transaction, """
                SELECT COUNT(*) FROM ProjectLocks WHERE ProjectId=$project AND CanonicalRootPath=$root COLLATE NOCASE
                  AND ExecutionId=$execution AND ApplicationInstanceId=$owner AND ProcessGeneration=$generation AND ReleasedAtUtc IS NULL
                """, ("$project", entry.Target.Context.ProjectId), ("$root", ProjectLock.CanonicalizeRoot(entry.Target.RootPath)),
                ("$execution", entry.Target.ExecutionId), ("$owner", guard.InstanceId), ("$generation", entry.Target.ProcessGeneration));
            if (Convert.ToInt64(await owner.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1) throw new MirasimEgressPolicyException();
        }
        var query = RoutesSql.Replace("AND l.ReleasedAtUtc IS NULL)", "AND l.ReleasedAtUtc IS NULL AND l.ExecutionId!=$execution)", StringComparison.Ordinal)
            .Replace("WHERE s.AccountId=a.Id AND (", "WHERE s.AccountId=a.Id AND e.Id!=$execution AND (", StringComparison.Ordinal);
        await using (var command = Command(connection, transaction, query + " AND r.Id=$route",
            ("$project", entry.Target.Context.ProjectId), ("$root", ProjectLock.CanonicalizeRoot(entry.Target.RootPath)), ("$now", Now()),
            ("$execution", entry.Target.ExecutionId), ("$route", entry.RouteId)))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(1) != entry.AccountId || reader.GetString(2) != entry.ModelId)
                throw new MirasimEgressPolicyException();
        }
        await using (var command = Command(connection, transaction, """
            UPDATE Executions SET State='Running',StartedAtUtc=$now WHERE Id=$execution AND SessionId=$session AND State='Starting'
              AND EXISTS(SELECT 1 FROM Sessions s WHERE s.Id=$session AND s.ActiveExecutionId=$execution AND s.Backend='Mirasim');
            """, ("$execution", entry.Target.ExecutionId), ("$session", entry.SessionId), ("$now", Now())))
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new MirasimEgressPolicyException();
        await AppendAsync(connection, transaction, entry, "MirasimTransportAuthorized", Hash(body), token).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted(); token.ThrowIfCancellationRequested(); await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async Task AuthorizeOwnedOperationAsync(MirasimExecutionAdmission entry, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await ValidateStoredOwnershipAsync(connection, transaction, entry, requireActiveWriter: true, token).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted(); token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async Task CompleteAsync(MirasimExecutionAdmission entry, ExecutionState state, ExecutionFailureReason reason, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        if (state is not (ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.Cancelled or ExecutionState.Ambiguous))
            throw new ArgumentOutOfRangeException(nameof(state));
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        // Cleanup uses the admitted tuple, even after the project's policy forbids another prompt.
        // Keep this check in the terminal transaction so a changed session cannot be completed or unlocked.
        await ValidateStoredOwnershipAsync(connection, transaction, entry, requireActiveWriter: false, token).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, """
            UPDATE Executions SET State=$state,FailureReason=$reason,EndedAtUtc=CASE WHEN $terminal=1 THEN $now ELSE NULL END
            WHERE Id=$execution AND SessionId=$session AND (EndedAtUtc IS NULL OR State=$state);
            """, ("$execution", entry.Target.ExecutionId), ("$session", entry.SessionId), ("$state", state.ToString()),
            ("$reason", reason.ToString()), ("$terminal", state == ExecutionState.Ambiguous ? 0 : 1), ("$now", Now())))
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidOperationException("Mirasim execution journal ownership changed.");
        await using (var command = Command(connection, transaction, """
            UPDATE Sessions SET State=CASE WHEN $terminal=1 THEN 'Closed' ELSE 'Ambiguous' END,
              ActiveExecutionId=CASE WHEN $terminal=1 THEN NULL ELSE $execution END,
              ReconciliationOutcome=CASE WHEN $terminal=1 THEN 'None' ELSE 'Pending' END,LastEventAtUtc=$now
            WHERE Id=$session AND (ActiveExecutionId=$execution OR (ActiveExecutionId IS NULL AND $terminal=1));
            """, ("$session", entry.SessionId), ("$execution", entry.Target.ExecutionId), ("$terminal", state == ExecutionState.Ambiguous ? 0 : 1), ("$now", Now())))
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidOperationException("Mirasim session journal ownership changed.");
        await AppendAsync(connection, transaction, entry, "MirasimLifecycle", null, token, state, reason).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted(); token.ThrowIfCancellationRequested(); await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private async Task ValidateStoredOwnershipAsync(SqliteConnection connection, SqliteTransaction transaction,
        MirasimExecutionAdmission entry, bool requireActiveWriter, CancellationToken token)
    {
        var root = ProjectLock.CanonicalizeRoot(entry.Target.RootPath);
        bool terminal;
        await using (var command = Command(connection, transaction, """
            SELECT e.EndedAtUtc FROM Executions e
            JOIN Sessions s ON s.Id=e.SessionId
            JOIN ClientRequests c ON c.Id=e.ClientRequestId AND c.ExecutionId=e.Id AND c.SessionId=s.Id
            WHERE e.Id=$execution AND s.Id=$session AND s.Backend='Mirasim'
              AND s.ProjectId=$project AND s.ProviderProfileId=$profile AND s.AccountId=$account AND s.ModelId=$model
              AND s.NativeSessionId=$nativeSession AND s.WorkspaceRootPath=$root COLLATE NOCASE
              AND e.RequestedRouteId=$route AND c.RequestedRouteId=$route AND c.PromptHash=$hash
              AND (s.ActiveExecutionId=$execution OR (s.ActiveExecutionId IS NULL AND e.EndedAtUtc IS NOT NULL))
            """, ("$execution", entry.Target.ExecutionId), ("$session", entry.SessionId),
            ("$project", entry.Target.Context.ProjectId), ("$profile", entry.Target.Context.ProviderProfileId),
            ("$account", entry.AccountId), ("$model", entry.ModelId), ("$nativeSession", entry.Target.NativeSessionId),
            ("$root", root), ("$route", entry.RouteId), ("$hash", entry.Target.PromptSha256)))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new MirasimEgressPolicyException();
            terminal = !reader.IsDBNull(0);
        }

        await using var writer = Command(connection, transaction, """
            SELECT COUNT(*), COALESCE(SUM(CASE WHEN ProjectId=$project AND CanonicalRootPath=$root COLLATE NOCASE
              AND ApplicationInstanceId=$owner AND ProcessGeneration=$generation THEN 1 ELSE 0 END),0)
            FROM ProjectLocks WHERE ExecutionId=$execution AND ReleasedAtUtc IS NULL
            """, ("$project", entry.Target.Context.ProjectId), ("$root", root), ("$owner", guard.InstanceId),
            ("$generation", entry.Target.ProcessGeneration), ("$execution", entry.Target.ExecutionId));
        await using var ownership = await writer.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await ownership.ReadAsync(token).ConfigureAwait(false)) throw new MirasimEgressPolicyException();
        var held = ownership.GetInt64(0); var owned = ownership.GetInt64(1);
        // Complete can precede lock acquisition (local refusal), or retry a committed terminal result.
        // A remote control request for an active writer must still have its exact writer ownership.
        if (held != owned || held > 1 || (requireActiveWriter && entry.Target.RequiresWriterLock && !terminal && owned != 1))
            throw new MirasimEgressPolicyException();
    }

    private async Task AppendAsync(SqliteConnection connection, SqliteTransaction transaction, MirasimExecutionAdmission entry,
        string kind, string? bodyHash, CancellationToken token, ExecutionState? state = null, ExecutionFailureReason? reason = null)
    {
        var payload = JsonSerializer.Serialize(new { executionId = entry.Target.ExecutionId, state, reason, bodySha256 = bodyHash,
            policySha256 = entry.PolicyFingerprint, routePolicySha256 = entry.RouteFingerprint, nativeIdentityConfirmed = false });
        await using var command = Command(connection, transaction, """
            INSERT INTO ExecutionEvents (Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
            VALUES ($id,$execution,(SELECT COALESCE(MAX(Sequence),0)+1 FROM ExecutionEvents WHERE ExecutionId=$execution),$kind,$payload,'PrivateSource',$now)
            """, ("$id", Guid.NewGuid().ToString("D")), ("$execution", entry.Target.ExecutionId), ("$kind", kind), ("$payload", payload), ("$now", Now()));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private string Now() => clock.GetUtcNow().ToString("O");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
