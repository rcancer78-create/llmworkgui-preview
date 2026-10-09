using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.OpenCode;

/// <summary>Persists admission atomically before a checkout lock can reference the execution.</summary>
public sealed partial class SqliteOpenCodeExecutionJournal(ISqliteConnectionFactory factory, TimeProvider timeProvider, IApplicationInstanceGuard instanceGuard,
    IActivityCenterService? activity = null)
    : IOpenCodeExecutionJournal
{
    private const string RoutesSql = """
        SELECT r.Id,r.ProviderProfileId,r.AccountId,r.ModelId,m.ProviderModelId,
               r.ExecutionMode,r.MaxDataClass,p.MaxDataClass
        FROM Routes r
        JOIN ProviderProfiles p ON p.Id=r.ProviderProfileId AND p.Backend=r.Backend
        JOIN Accounts a ON a.Id=r.AccountId AND a.ProviderProfileId=r.ProviderProfileId
        JOIN Models m ON m.Id=r.ModelId AND m.ProviderProfileId=r.ProviderProfileId AND m.Backend=r.Backend
        WHERE r.Backend='OpenCode' AND r.IsEnabled=1 AND p.IsEnabled=1 AND a.IsEnabled=1 AND m.IsEnabled=1
          AND a.AuthState='Valid' AND m.CapabilityState='Supported'
          AND r.Health IN ('Healthy','Degraded','ForcedEnabled')
          AND a.Health IN ('Healthy','Degraded','ForcedEnabled')
          AND m.Health IN ('Healthy','Degraded','ForcedEnabled')
          AND (a.CooldownUntilUtc IS NULL OR julianday(a.CooldownUntilUtc)<=julianday($now))
          AND (a.DisabledUntilUtc IS NULL OR julianday(a.DisabledUntilUtc)<=julianday($now))
          AND r.ReasoningEffort IS NULL AND r.SpeedMode IS NULL
          AND r.ExecutionMode IS NULL
        """;

    public async Task<IReadOnlyList<OpenCodeStoredRoute>> ListRoutesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RoutesSql + " ORDER BY r.ManualPriority,r.Id";
        command.Parameters.AddWithValue("$now", Now());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<OpenCodeStoredRoute>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadRoute(reader));
        return result;
    }

    public async Task<string> ConfirmSessionAsync(string projectId, string rootPath, string nativeSessionId,
        OpenCodeStoredRoute route, CancellationToken cancellationToken = default) =>
        (await AdmitAsync(projectId, rootPath, nativeSessionId, route, null, null, 0, cancellationToken)).SessionId;

    public Task<OpenCodeJournalEntry> BeginAsync(string projectId, string rootPath, string nativeSessionId,
        OpenCodeStoredRoute expectedRoute, string clientRequestId, string promptHash, long processGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptHash);
        if (processGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(processGeneration));
        return AdmitAsync(projectId, rootPath, nativeSessionId, expectedRoute, clientRequestId, promptHash, processGeneration, cancellationToken);
    }

    private async Task<OpenCodeJournalEntry> AdmitAsync(string projectId, string rootPath, string nativeSessionId,
        OpenCodeStoredRoute expectedRoute, string? clientRequestId, string? promptHash, long processGeneration,
        CancellationToken cancellationToken)
    {
        instanceGuard.EnsureSupervisorPermitted();
        ArgumentNullException.ThrowIfNull(expectedRoute);
        var routeId = expectedRoute.Id;
        foreach (var value in new[] { projectId, nativeSessionId, routeId })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var canonicalRoot = ProjectLock.CanonicalizeRoot(rootPath);
        var now = Now();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        // Reserve the SQLite writer before validating admission on this connection.
        using var transaction = connection.BeginTransaction(deferred: false);
        OpenCodeStoredRoute route;
        string routeClass, profileClass;
        await using (var command = Command(connection, transaction, RoutesSql + " AND r.Id=$route", ("$now", now), ("$route", routeId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Выбранный маршрут OpenCode отсутствует, отключён или требует отдельной настройки параметров модели.");
            route = ReadRoute(reader);
            routeClass = reader.GetString(6); profileClass = reader.GetString(7);
        }
        if (route != expectedRoute || string.IsNullOrWhiteSpace(route.NativeModelId))
            throw new InvalidOperationException("Маршрут изменился. Обновите список маршрутов OpenCode.");
        // Admission and reservation share the immediate writer transaction. Count all
        // sessions for this account, including other projects and uncertain executions.
        if (clientRequestId is not null)
        {
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
            FROM Sessions WHERE Backend='OpenCode' AND NativeSessionId=$native
            """, ("$native", nativeSessionId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                existing = true; sessionId = reader.GetString(0);
                if (reader.GetString(1) != projectId ||
                    !string.Equals(ProjectLock.CanonicalizeRoot(reader.GetString(2)), canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
                    reader.GetString(3) != route.ProviderProfileId || reader.GetString(4) != route.AccountId ||
                    reader.GetString(5) != route.ModelId || !reader.IsDBNull(6) ||
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
                VALUES ($session,$project,'OpenCode',$profile,$account,$model,NULL,$root,$native,'Idle','None','None',$now,$now)
                """, ("$session", sessionId), ("$project", projectId), ("$profile", route.ProviderProfileId),
                ("$account", route.AccountId), ("$model", route.ModelId), ("$root", canonicalRoot), ("$native", nativeSessionId), ("$now", now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (clientRequestId is null)
        {
            instanceGuard.EnsureSupervisorPermitted();
            transaction.Commit();
            return new(string.Empty, sessionId, nativeSessionId, string.Empty, route);
        }
        await using (var insert = Command(connection, transaction, """
            INSERT INTO Executions (Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,
                DispatchProviderProfileId,DispatchAccountId,DispatchNativeModelId,CreatedAtUtc,StartedAtUtc)
            VALUES ($execution,$session,$request,'SessionConfirmed','None',$route,$profile,$account,$native,$now,$now);
            INSERT INTO ClientRequests (Id,SessionId,ExecutionId,PromptHash,RequestedRouteId,CreatedAtUtc)
            VALUES ($request,$session,$execution,$hash,$route,$now);
            """, ("$execution", executionId), ("$session", sessionId), ("$request", clientRequestId), ("$route", route.Id),
            ("$profile", route.ProviderProfileId), ("$account", route.AccountId), ("$native", route.NativeModelId),
            ("$hash", promptHash!), ("$now", now)))
            await insert.ExecuteNonQueryAsync(cancellationToken);
        await using (var activate = Command(connection, transaction, """
            UPDATE Sessions SET State='Active',ActiveExecutionId=$execution,LastEventAtUtc=$now
            WHERE Id=$session AND State='Idle' AND ActiveExecutionId IS NULL
            """, ("$execution", executionId), ("$session", sessionId), ("$now", now)))
            if (await activate.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Сессия изменилась до регистрации выполнения.");
        var admitted = await OpenCodeJournalEvents.AppendAsync(connection, transaction, executionId, sessionId, route.Id,
            "OpenCodeAdmission", ExecutionState.SessionConfirmed, "Admitted", timeProvider.GetUtcNow(), cancellationToken, processGeneration);
        instanceGuard.EnsureSupervisorPermitted();
        transaction.Commit();
        OpenCodeJournalEvents.Publish(activity, admitted);
        return new(executionId, sessionId, nativeSessionId, clientRequestId, route) { ProcessGeneration = processGeneration };
    }

    public async Task MarkRunningAsync(OpenCodeJournalEntry entry, CancellationToken cancellationToken = default)
    {
        instanceGuard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!await ValidateDispatchPolicyAsync(connection, transaction, entry, cancellationToken))
            throw new InvalidOperationException("Политика или привязка OpenCode изменились до отправки.");
        await using var command = Command(connection, transaction, """
            UPDATE Executions SET State='Running'
            WHERE Id=$execution AND SessionId=$session AND ClientRequestId=$request AND RequestedRouteId=$route AND State='SessionConfirmed'
                AND EXISTS (SELECT 1 FROM Sessions s WHERE s.Id=$session AND s.Backend='OpenCode'
                    AND s.NativeSessionId=$native AND s.State='Active' AND s.ActiveExecutionId=$execution)
                AND EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=$execution AND l.ReleasedAtUtc IS NULL)
            """, ("$execution", entry.ExecutionId), ("$session", entry.SessionId), ("$request", entry.ClientRequestId),
            ("$route", entry.Route.Id), ("$native", entry.NativeSessionId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Выполнение или блокировка OpenCode изменились до отправки.");
        var dispatched = await OpenCodeJournalEvents.AppendAsync(connection, transaction, entry.ExecutionId, entry.SessionId, entry.Route.Id,
            "OpenCodeDispatch", ExecutionState.Running, "DispatchReserved", timeProvider.GetUtcNow(), cancellationToken);
        instanceGuard.EnsureSupervisorPermitted();
        transaction.Commit(); OpenCodeJournalEvents.Publish(activity, dispatched);
    }

    public async Task SetWaitingApprovalAsync(OpenCodeJournalEntry entry, bool waiting, CancellationToken cancellationToken = default)
    {
        instanceGuard.EnsureSupervisorPermitted();
        var desired = waiting ? "WaitingApproval" : "Running";
        var previous = waiting ? "Running" : "WaitingApproval";
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = Command(connection, transaction, """
            UPDATE Executions SET State=$state
            WHERE Id=$execution AND SessionId=$session AND ClientRequestId=$request AND RequestedRouteId=$route
                AND State=$previous AND EndedAtUtc IS NULL
                AND EXISTS (SELECT 1 FROM Sessions s WHERE s.Id=$session AND s.Backend='OpenCode'
                    AND s.NativeSessionId=$native AND s.State='Active' AND s.ActiveExecutionId=$execution)
                AND EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=$execution AND l.ReleasedAtUtc IS NULL)
            """, ("$state", desired), ("$previous", previous), ("$execution", entry.ExecutionId),
            ("$session", entry.SessionId), ("$request", entry.ClientRequestId), ("$route", entry.Route.Id), ("$native", entry.NativeSessionId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Выполнение или writer lock изменились при обработке разрешения OpenCode.");
        var marker = await OpenCodeJournalEvents.AppendAsync(connection, transaction, entry.ExecutionId, entry.SessionId, entry.Route.Id,
            "OpenCodeApproval", waiting ? ExecutionState.WaitingApproval : ExecutionState.Running,
            waiting ? "NativePermissionWaiting" : "NativePermissionResolved", timeProvider.GetUtcNow(), cancellationToken);
        instanceGuard.EnsureSupervisorPermitted();
        transaction.Commit(); OpenCodeJournalEvents.Publish(activity, marker);
    }

    public async Task CompleteAsync(OpenCodeJournalEntry entry, TurnResult result, CancellationToken cancellationToken = default)
    {
        instanceGuard.EnsureSupervisorPermitted();
        if (result.SessionId != entry.NativeSessionId)
            throw new InvalidOperationException("Результат не соответствует локальному выполнению.");
        if (result.Status is not (TurnResult.CompletedStatus or TurnResult.CancelledStatus or TurnResult.FailedStatus))
            throw new InvalidOperationException("Результат не содержит поддерживаемого исхода.");
        if (result.WasTimedOut && result.Status != TurnResult.FailedStatus)
            throw new InvalidOperationException("Timeout должен содержать неуспешный исход.");
        var uncertain = result.IsDeliveryUncertain;
        var state = uncertain ? "Ambiguous" : result.Status switch
        {
            TurnResult.CompletedStatus => "Succeeded", TurnResult.CancelledStatus => "Cancelled", _ => result.WasTimedOut ? "TimedOut" : "Failed"
        };
        var sessionState = uncertain ? "Ambiguous" : "Idle";
        var failure = result.WasTimedOut ? "NetworkTimeout" : state switch { "Succeeded" => "None", "Cancelled" => "UserCancelled", _ => "InternalError" };
        var now = Now();
        var observedMatches = !uncertain
            && !string.IsNullOrWhiteSpace(result.ObservedModelId)
            && !string.IsNullOrWhiteSpace(result.ObservedProviderId)
            && string.Equals(result.ObservedProviderId + "/" + result.ObservedModelId, entry.Route.NativeModelId, StringComparison.Ordinal);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        // Commit the terminal while checkout ownership is still held. The caller releases
        // its token afterwards; active lock rows continue reserving account capacity.
        // If this transaction fails, neither durable nor physical ownership is lost.
        await using (var update = Command(connection, transaction, """
            UPDATE Executions SET State=$state,FailureReason=$failure,EndedAtUtc=$ended,TerminationReason=$reason,
                ObservedRouteId=CASE WHEN $observed=1 THEN $route ELSE ObservedRouteId END
            WHERE Id=$execution AND SessionId=$session AND ClientRequestId=$request AND RequestedRouteId=$route
                AND State IN ('SessionConfirmed','Running','WaitingApproval')
                AND EXISTS (SELECT 1 FROM Sessions s WHERE s.Id=$session AND s.Backend='OpenCode'
                    AND s.NativeSessionId=$native AND s.State='Active' AND s.ActiveExecutionId=$execution)
            """, ("$state", state), ("$failure", failure), ("$ended", uncertain ? DBNull.Value : now), ("$reason", uncertain ? "NativeOutcomeUnconfirmed" : result.Status),
            ("$execution", entry.ExecutionId), ("$session", entry.SessionId), ("$request", entry.ClientRequestId),
            ("$route", entry.Route.Id), ("$native", entry.NativeSessionId), ("$observed", observedMatches ? 1 : 0)))
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Выполнение уже завершено или изменено reconciliation.");
        await using (var update = Command(connection, transaction, """
            UPDATE Sessions SET State=$state,ActiveExecutionId=$active,LastEventAtUtc=$now
            WHERE Id=$session AND ActiveExecutionId=$execution
            """, ("$state", sessionState), ("$active", uncertain ? entry.ExecutionId : DBNull.Value),
            ("$now", now), ("$session", entry.SessionId), ("$execution", entry.ExecutionId)))
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Активное выполнение сессии изменилось.");
        var terminal = await OpenCodeJournalEvents.AppendAsync(connection, transaction, entry.ExecutionId, entry.SessionId, entry.Route.Id,
            "OpenCodeTerminal", Enum.Parse<ExecutionState>(state), uncertain ? "DeliveryUnconfirmed" : result.WasTimedOut ? "SupervisorBudgetExpired" : "ConfirmedOutcome",
            timeProvider.GetUtcNow(), cancellationToken);
        instanceGuard.EnsureSupervisorPermitted();
        transaction.Commit(); OpenCodeJournalEvents.Publish(activity, terminal);
    }

    public async Task<OpenCodeDispatchDecision?> ReadDispatchDecisionAsync(string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RequestedRouteId, DispatchProviderProfileId, DispatchAccountId, DispatchNativeModelId
            FROM Executions WHERE Id=$id
            """;
        command.Parameters.AddWithValue("$id", executionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
            return null;
        return new OpenCodeDispatchDecision(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private string Now() => timeProvider.GetUtcNow().ToString("O");
    private static bool Allows(string limit, string value) => value == "PublicSource" && limit is "PublicSource" or "PrivateSource" or "Restricted"
        || value == "PrivateSource" && limit is "PrivateSource" or "Restricted";
    private static OpenCodeStoredRoute ReadRoute(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Key, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return command;
    }
}
