using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using LLMWorkGUI.Application.Concurrency;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

/// <summary>Persists admission atomically before a checkout lock can reference the execution.</summary>
public sealed partial class SqliteCursorAcpExecutionJournal(ISqliteConnectionFactory factory, TimeProvider timeProvider, IApplicationInstanceGuard instanceGuard)
    : ICursorAcpExecutionJournal
{
    private const string RoutesSql = """
        SELECT r.Id,r.ProviderProfileId,r.AccountId,r.ModelId,m.ProviderModelId,
               r.ExecutionMode,r.MaxDataClass,p.MaxDataClass
        FROM Routes r
        JOIN ProviderProfiles p ON p.Id=r.ProviderProfileId AND p.Backend=r.Backend
        JOIN Accounts a ON a.Id=r.AccountId AND a.ProviderProfileId=r.ProviderProfileId
        JOIN Models m ON m.Id=r.ModelId AND m.ProviderProfileId=r.ProviderProfileId AND m.Backend=r.Backend
        WHERE r.Backend='CursorAcp' AND r.IsEnabled=1 AND p.IsEnabled=1 AND a.IsEnabled=1 AND m.IsEnabled=1
          AND a.AuthState='Valid' AND m.CapabilityState='Supported'
          AND r.Health IN ('Healthy','Degraded','ForcedEnabled')
          AND a.Health IN ('Healthy','Degraded','ForcedEnabled')
          AND m.Health IN ('Healthy','Degraded','ForcedEnabled')
          AND (a.CooldownUntilUtc IS NULL OR julianday(a.CooldownUntilUtc)<=julianday($now))
          AND (a.DisabledUntilUtc IS NULL OR julianday(a.DisabledUntilUtc)<=julianday($now))
          AND NOT EXISTS (SELECT 1 FROM HealthStates h WHERE h.State NOT IN ('Healthy','Degraded','ForcedEnabled') AND
              ((h.ScopeType='account' AND h.ScopeId=a.Id) OR
               (h.ScopeType='route' AND h.ScopeId=r.Id) OR
               (h.ScopeType='model-route' AND h.ScopeId='v1:'||hex(a.Id)||':'||hex(m.Id)) OR
               (h.ScopeType='backend' AND h.ScopeId IN ('CursorAcp','cursor-agent-acp',p.Id))))
          AND r.ReasoningEffort IS NULL AND r.SpeedMode IS NULL
        """;

    public async Task<IReadOnlyList<CursorAcpStoredRoute>> ListRoutesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RoutesSql + " ORDER BY r.ManualPriority,r.Id";
        command.Parameters.AddWithValue("$now", Now());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CursorAcpStoredRoute>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadRoute(reader));
        return result;
    }

    public async Task<CursorAcpJournalEntry> BeginAsync(string projectId, string rootPath, string nativeSessionId,
        CursorAcpStoredRoute expectedRoute, string modeId, string clientRequestId, string promptHash, CancellationToken cancellationToken = default)
    {
        instanceGuard.EnsureSupervisorPermitted();
        ArgumentNullException.ThrowIfNull(expectedRoute);
        var routeId = expectedRoute.Id;
        foreach (var value in new[] { projectId, nativeSessionId, routeId, clientRequestId, promptHash })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (modeId is not ("ask" or "plan" or "agent")) throw new InvalidOperationException("Режим Cursor не поддерживается.");
        var canonicalRoot = ProjectLock.CanonicalizeRoot(rootPath);
        var now = Now();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        // Reserve the SQLite writer before validating admission on this connection.
        using var transaction = connection.BeginTransaction(deferred: false);
        CursorAcpStoredRoute route;
        string? routeMode;
        string routeClass, profileClass;
        await using (var command = Command(connection, transaction, RoutesSql + " AND r.Id=$route", ("$now", now), ("$route", routeId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Выбранный маршрут Cursor отсутствует, отключён или требует отдельной настройки параметров модели.");
            route = ReadRoute(reader);
            routeMode = reader.IsDBNull(5) ? null : reader.GetString(5);
            routeClass = reader.GetString(6); profileClass = reader.GetString(7);
        }
        if (route != expectedRoute || string.IsNullOrWhiteSpace(route.NativeModelId))
            throw new InvalidOperationException("Маршрут изменился. Обновите список маршрутов Cursor.");
        if (routeMode is not null && routeMode != modeId)
            throw new InvalidOperationException("Режим не совпадает с сохранённым маршрутом Cursor.");
        // Admission and reservation share the immediate writer transaction. Count all
        // sessions for this account, including other projects and uncertain executions.
        await using (var command = Command(connection, transaction, """
            SELECT a.MaxConcurrentExecutions,
                (SELECT COUNT(*) FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
                 WHERE s.AccountId=a.Id AND (
                     e.EndedAtUtc IS NULL
                     OR e.State NOT IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch')
                     OR s.ActiveExecutionId=e.Id
                     OR EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=e.Id AND l.ReleasedAtUtc IS NULL)))
            FROM Accounts a WHERE a.Id=$account
            """, ("$account", route.AccountId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt64(1) >= reader.GetInt64(0))
                throw new InvalidOperationException("Достигнут лимит одновременных выполнений аккаунта. Дождитесь завершения или восстановления предыдущего выполнения.");
        }
        await using (var command = Command(connection, transaction,
            "SELECT RootPath,DataClassification FROM Projects WHERE Id=$project", ("$project", projectId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) ||
                !string.Equals(ProjectLock.CanonicalizeRoot(reader.GetString(0)), canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Рабочий каталог проекта изменился; отправка отменена.");
            var classification = reader.GetString(1);
            if (classification == "Restricted" || !Allows(routeClass, classification) || !Allows(profileClass, classification))
                throw new InvalidOperationException("Классификация проекта запрещает отправку по этому маршруту.");
        }

        var sessionId = Guid.NewGuid().ToString("D");
        var existing = false;
        await using (var command = Command(connection, transaction, """
            SELECT Id,ProjectId,WorkspaceRootPath,ProviderProfileId,AccountId,ModelId,ExecutionMode,State,ActiveExecutionId
            FROM Sessions WHERE Backend='CursorAcp' AND NativeSessionId=$native
            """, ("$native", nativeSessionId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                existing = true; sessionId = reader.GetString(0);
                if (reader.GetString(1) != projectId ||
                    !string.Equals(ProjectLock.CanonicalizeRoot(reader.GetString(2)), canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
                    reader.GetString(3) != route.ProviderProfileId || reader.GetString(4) != route.AccountId ||
                    reader.GetString(5) != route.ModelId || reader.IsDBNull(6) || reader.GetString(6) != modeId ||
                    reader.GetString(7) != "Idle" || !reader.IsDBNull(8))
                    throw new InvalidOperationException("Сессия занята, требует reconciliation или привязана к другому маршруту. Создайте новую сессию.");
                if (await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Неоднозначная локальная привязка нативной сессии.");
            }
        }
        var executionId = Guid.NewGuid().ToString("D");
        if (!existing)
        {
            await using var insert = Command(connection, transaction, """
                INSERT INTO Sessions (Id,ProjectId,Backend,ProviderProfileId,AccountId,ModelId,ExecutionMode,
                    WorkspaceRootPath,NativeSessionId,State,ReconciliationOutcome,CloseReason,CreatedAtUtc,LastEventAtUtc)
                VALUES ($session,$project,'CursorAcp',$profile,$account,$model,$mode,$root,$native,'Idle','None','None',$now,$now)
                """, ("$session", sessionId), ("$project", projectId), ("$profile", route.ProviderProfileId),
                ("$account", route.AccountId), ("$model", route.ModelId), ("$mode", modeId), ("$root", canonicalRoot), ("$native", nativeSessionId), ("$now", now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = Command(connection, transaction, """
            INSERT INTO Executions (Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,
                DispatchProviderProfileId,DispatchAccountId,DispatchNativeModelId,CreatedAtUtc,StartedAtUtc)
            VALUES ($execution,$session,$request,'SessionConfirmed','None',$route,$profile,$account,$native,$now,$now);
            INSERT INTO ClientRequests (Id,SessionId,ExecutionId,PromptHash,RequestedRouteId,CreatedAtUtc)
            VALUES ($request,$session,$execution,$hash,$route,$now);
            """, ("$execution", executionId), ("$session", sessionId), ("$request", clientRequestId), ("$route", route.Id),
            ("$profile", route.ProviderProfileId), ("$account", route.AccountId), ("$native", route.NativeModelId),
            ("$hash", promptHash), ("$now", now)))
            await insert.ExecuteNonQueryAsync(cancellationToken);
        await using (var activate = Command(connection, transaction, """
            UPDATE Sessions SET State='Active',ActiveExecutionId=$execution,LastEventAtUtc=$now
            WHERE Id=$session AND State='Idle' AND ActiveExecutionId IS NULL
            """, ("$execution", executionId), ("$session", sessionId), ("$now", now)))
            if (await activate.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Сессия изменилась до регистрации выполнения.");
        transaction.Commit();
        return new(executionId, sessionId, nativeSessionId, clientRequestId, route);
    }

    public async Task CompleteAsync(CursorAcpJournalEntry entry, CursorAcpTurnResult result, CancellationToken cancellationToken = default)
    {
        instanceGuard.EnsureSupervisorPermitted();
        if (result.SessionId != entry.NativeSessionId || result.ClientRequestId != entry.ClientRequestId)
            throw new InvalidOperationException("Результат не соответствует локальному выполнению.");
        var uncertain = result.LockRetained || result.Outcome is CursorAcpTurnOutcome.Ambiguous or CursorAcpTurnOutcome.Orphaned;
        var state = uncertain ? "Ambiguous" : result.Outcome switch
        {
            CursorAcpTurnOutcome.Succeeded => "Succeeded", CursorAcpTurnOutcome.Cancelled => "Cancelled",
            CursorAcpTurnOutcome.TimedOut => "TimedOut", _ => "Failed"
        };
        var sessionState = result.Outcome == CursorAcpTurnOutcome.Orphaned ? "Orphaned" : uncertain ? "Ambiguous" : "Idle";
        var failure = state switch { "Succeeded" => "None", "Cancelled" => "UserCancelled", "TimedOut" => "NetworkTimeout", _ => "InternalError" };
        var now = Now();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using (var update = Command(connection, transaction, """
            UPDATE Executions SET State=$state,FailureReason=$failure,EndedAtUtc=$ended,TerminationReason=$reason
            WHERE Id=$execution AND SessionId=$session AND ClientRequestId=$request AND State='SessionConfirmed'
            """, ("$state", state), ("$failure", failure), ("$ended", uncertain ? DBNull.Value : now), ("$reason", result.Outcome.ToString()),
            ("$execution", entry.ExecutionId), ("$session", entry.SessionId), ("$request", entry.ClientRequestId)))
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Выполнение уже завершено или изменено reconciliation.");
        await using (var update = Command(connection, transaction, """
            UPDATE Sessions SET State=$state,ActiveExecutionId=$active,LastEventAtUtc=$now
            WHERE Id=$session AND ActiveExecutionId=$execution
            """, ("$state", sessionState), ("$active", uncertain ? entry.ExecutionId : DBNull.Value),
            ("$now", now), ("$session", entry.SessionId), ("$execution", entry.ExecutionId)))
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Активное выполнение сессии изменилось.");
        transaction.Commit();
    }

    private string Now() => timeProvider.GetUtcNow().ToString("O");
    private static bool Allows(string limit, string value) => value == "PublicSource" && limit is "PublicSource" or "PrivateSource" or "Restricted"
        || value == "PrivateSource" && limit is "PrivateSource" or "Restricted";
    private static CursorAcpStoredRoute ReadRoute(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Key, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return command;
    }
}
