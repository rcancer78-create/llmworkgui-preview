using LLMGateway.Core;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Security;

namespace LLMWorkGUI.Infrastructure.Providers;

internal sealed record NativeGatewayAdmission(string SessionId, string ExecutionId, string RequestId,
    string RouteId, string AccountId, string ModelId, GatewayExecutionContext Context);

/// <summary>Local ownership only. Requested gateway metadata never becomes observed native identity.</summary>
internal sealed class SqliteNativeGatewayJournal(ISqliteConnectionFactory factory,
    IApplicationInstanceGuard guard, TimeProvider clock, IActivityCenterService? activity = null)
{
    public async Task<NativeGatewayAdmission> BeginAsync(NativeGatewayTurnRequest request, CancellationToken token,
        NativeGatewayEgressAuthorization? authorization = null)
    {
        guard.EnsureSupervisorPermitted();
        var root = ProjectLock.CanonicalizeRoot(request.RootPath);
        if (!Directory.Exists(root)) throw new InvalidOperationException("Каталог проекта недоступен.");
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        string profileId, accountId, modelId, nativeAccount, nativeModel;
        string? reasoning;
        bool requireChatDeclaration;
        await using (var command = Command(connection, transaction,
            (authorization is null ? NativeGatewayRouteQuery.Sql : NativeGatewayRouteQuery.PreviewCandidatesSql) + " AND r.Id=$route",
            ("$route", request.RouteId), ("$project", request.ProjectId), ("$root", root), ("$now", Now())))
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.IsDBNull(3))
                throw new InvalidOperationException("Маршрут LLMGateway недоступен, занят или не подтверждён.");
            profileId = reader.GetString(0); accountId = reader.GetString(1); modelId = reader.GetString(2);
            nativeAccount = reader.GetString(3); nativeModel = reader.GetString(4);
            reasoning = reader.IsDBNull(9) ? null : reader.GetString(9);
            requireChatDeclaration = NativeGatewayRouteOptions.RequiresChatDeclaration(profileId, reader.GetString(10));
        }
        var kinds = Enum.GetValues<ProviderKind>().Where(p => p != ProviderKind.Unknown && GatewayCatalogMapper.ProviderId(p) == profileId).ToArray();
        if (profileId == GrokBotRestrictions.ProviderProfileId && authorization is null) throw new EgressApprovalException();
        if (authorization is not null)
        {
            if (authorization.Binding != new NativeGatewayRouteBinding(profileId, accountId, modelId, nativeAccount, nativeModel, reasoning))
                throw new EgressApprovalException();
            authorization.ValidateRows(await SqliteEgressPolicyReader.ReadAsync(connection, transaction,
                request.ProjectId, request.RouteId, token).ConfigureAwait(false));
        }
        if (request.ExpectedBinding is { } expected && expected != new NativeGatewayRouteBinding(profileId, accountId, modelId, nativeAccount, nativeModel, reasoning))
            throw new InvalidOperationException("Выбранная привязка LLMGateway изменилась. Обновите маршруты.");
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(nativeModel)
            || kinds.Length != 1 || GatewayCatalogMapper.AccountId(kinds[0], nativeAccount) != accountId
            || GatewayCatalogMapper.ModelId(kinds[0], nativeModel) != modelId
            || !ModelRouter.IsValidModelName(nativeModel) || nativeModel.Equals("auto", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Привязка LLMGateway изменилась или содержит неявный выбор модели.");
        await using (var command = Command(connection, transaction, "SELECT RootPath FROM Projects WHERE Id=$id", ("$id", request.ProjectId)))
        {
            var storedRoot = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            if (storedRoot is null || !string.Equals(ProjectLock.CanonicalizeRoot(storedRoot), root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Рабочий каталог проекта изменился.");
        }
        var binding = new NativeGatewayRouteBinding(profileId, accountId, modelId, nativeAccount, nativeModel, reasoning);
        if (!NativeGatewayRouteOptions.IsSupported(await SqliteModelCapabilityEvidenceStore.ReadAsync(connection, transaction,
                new SensitiveDataFilter(), token).ConfigureAwait(false), binding, clock.GetUtcNow(), requireChatDeclaration))
            throw new InvalidOperationException("Поддержка текстовых запросов или параметра reasoning не подтверждена для выбранной модели и аккаунта.");
        var entry = new NativeGatewayAdmission(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            request.ClientRequestId, request.RouteId, accountId, modelId, new(kinds[0], nativeAccount, nativeModel, root) { ReasoningEffort = reasoning });
        await using (var command = Command(connection, transaction, """
            INSERT INTO Sessions (Id,ProjectId,Backend,ProviderProfileId,AccountId,ModelId,WorkspaceRootPath,
                ReasoningEffort,State,ReconciliationOutcome,CloseReason,ActiveExecutionId,CreatedAtUtc,LastEventAtUtc)
            VALUES ($session,$project,'NativeGateway',$profile,$account,$model,$root,$reasoning,'Active','None','None',$execution,$now,$now);
            INSERT INTO Executions (Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,
                DispatchProviderProfileId,DispatchAccountId,DispatchNativeModelId,CreatedAtUtc)
            VALUES ($execution,$session,$request,'Starting','None',$route,$profile,$account,$nativeModel,$now);
            INSERT INTO ClientRequests (Id,SessionId,ExecutionId,PromptHash,RequestedRouteId,CreatedAtUtc)
            VALUES ($request,$session,$execution,$hash,$route,$now);
            """, ("$session", entry.SessionId), ("$project", request.ProjectId), ("$profile", profileId),
            ("$account", accountId), ("$model", modelId), ("$root", root), ("$execution", entry.ExecutionId),
            ("$request", request.ClientRequestId), ("$route", request.RouteId), ("$nativeModel", nativeModel), ("$now", Now()),
            ("$reasoning", (object?)reasoning ?? DBNull.Value),
            ("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt))))))
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (authorization is not null)
            await AppendEgressAsync(connection, transaction, entry, authorization, "NativeEgressConsentReserved", token).ConfigureAwait(false);
        var journalEvent = await AppendAsync(connection, transaction, entry, ExecutionState.Starting, ExecutionFailureReason.None, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        guard.EnsureSupervisorPermitted();
        transaction.Commit();
        NativeGatewayJournalEvents.Publish(activity, journalEvent);
        return entry;
    }

    public Task MarkRunningAsync(NativeGatewayAdmission entry, CancellationToken token,
        NativeGatewayEgressAuthorization? authorization = null, long processGeneration = 0) =>
        ValidateAdmissionAsync(entry, token, authorization, markRunning: true, processGeneration: processGeneration);

    internal Task BindProcessAsync(NativeGatewayAdmission entry, long processGeneration, CancellationToken token)
    {
        if (processGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(processGeneration));
        return ValidateAdmissionAsync(entry, token, authorization: null, markRunning: false, bindGeneration: processGeneration);
    }

    internal Task ValidateDispatchAsync(NativeGatewayAdmission entry, CancellationToken token) =>
        ValidateAdmissionAsync(entry, token, authorization: null, markRunning: false);

    internal void EnsureSupervisorPermitted() => guard.EnsureSupervisorPermitted();

    private async Task ValidateAdmissionAsync(NativeGatewayAdmission entry, CancellationToken token,
        NativeGatewayEgressAuthorization? authorization, bool markRunning, long processGeneration = 0, long bindGeneration = 0)
    {
        guard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (authorization is not null)
            authorization.ValidateRows(await ReadEgressRowsAsync(connection, transaction, entry, token).ConfigureAwait(false));
        var optionBinding = new NativeGatewayRouteBinding(GatewayCatalogMapper.ProviderId(entry.Context.Provider), entry.AccountId,
            entry.ModelId, entry.Context.AccountId, entry.Context.NativeModel, entry.Context.ReasoningEffort);
        string? modelProvenance;
        await using (var modelQuery = Command(connection, transaction,
            "SELECT Provenance FROM Models WHERE Id=$model AND ProviderProfileId=$profile AND Backend='NativeGateway'",
            ("$model", entry.ModelId), ("$profile", optionBinding.ProviderProfileId)))
            modelProvenance = await modelQuery.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        if (!NativeGatewayRouteOptions.IsSupported(await SqliteModelCapabilityEvidenceStore.ReadAsync(connection, transaction,
                new SensitiveDataFilter(), token).ConfigureAwait(false), optionBinding, clock.GetUtcNow(),
                NativeGatewayRouteOptions.RequiresChatDeclaration(optionBinding.ProviderProfileId, modelProvenance ?? "")))
            throw new InvalidOperationException("Подтверждение текстовых запросов или reasoning изменилось или истекло до отправки.");
        await using (var rootQuery = Command(connection, transaction,
            "SELECT p.RootPath FROM Projects p JOIN Sessions s ON s.ProjectId=p.Id WHERE s.Id=$session", ("$session", entry.SessionId)))
        {
            var root = await rootQuery.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            if (root is null || !Directory.Exists(root)
                || !string.Equals(ProjectLock.CanonicalizeRoot(root), entry.Context.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Рабочий каталог проекта изменился.");
        }
        var admissionPredicate = """
            WHERE Id=$execution AND SessionId=$session AND State='Starting'
              AND EXISTS (SELECT 1 FROM ExecutionEvents ev WHERE ev.ExecutionId=$execution
                  AND ev.EventKind='NativeGatewayLifecycle'
                  AND json_extract(ev.NormalizedRedactedPayloadJson,'$.state')='Starting'
                  AND json_type(ev.NormalizedRedactedPayloadJson,'$.reasoningEffort') IS NOT NULL
                  AND json_extract(ev.NormalizedRedactedPayloadJson,'$.reasoningEffort') IS $reasoning)
              AND EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=$execution AND l.ReleasedAtUtc IS NULL
                  AND l.ApplicationInstanceId=$owner AND l.ProcessGeneration=$generation AND l.CanonicalRootPath=$root COLLATE NOCASE)
              AND ($generation=0 OR EXISTS (SELECT 1 FROM ExecutionEvents ev WHERE ev.ExecutionId=$execution
                  AND ev.EventKind='NativeProcessBound' AND json_extract(ev.NormalizedRedactedPayloadJson,'$.processGeneration')=$generation
                  AND json_extract(ev.NormalizedRedactedPayloadJson,'$.applicationInstanceId')=$owner))
              AND EXISTS (SELECT 1 FROM Sessions s WHERE s.Id=$session AND s.Backend='NativeGateway'
                  AND s.ActiveExecutionId=$execution AND s.State='Active')
              AND EXISTS (SELECT 1 FROM Routes r
                  JOIN ProviderProfiles p ON p.Id=r.ProviderProfileId AND p.Backend=r.Backend
                  JOIN Accounts a ON a.Id=r.AccountId AND a.ProviderProfileId=p.Id
                  JOIN Models m ON m.Id=r.ModelId AND m.ProviderProfileId=p.Id AND m.Backend=r.Backend
                  JOIN Sessions s ON s.Id=$session JOIN Projects pr ON pr.Id=s.ProjectId
                  WHERE r.Id=$route AND r.Backend='NativeGateway' AND r.AccountId=$account AND r.ModelId=$model
                    AND r.ProviderProfileId=s.ProviderProfileId AND a.ProviderNativeId=$nativeAccount AND m.ProviderModelId=$nativeModel
                    AND r.IsEnabled=1 AND p.IsEnabled=1 AND a.IsEnabled=1 AND m.IsEnabled=1
                    AND a.AuthState='Valid' AND m.CapabilityState='Supported'
                    AND r.Health IN ('Healthy','Degraded','ForcedEnabled') AND a.Health IN ('Healthy','Degraded','ForcedEnabled')
                    AND m.Health IN ('Healthy','Degraded','ForcedEnabled')
                    AND (a.CooldownUntilUtc IS NULL OR julianday(a.CooldownUntilUtc)<=julianday($now))
                    AND (a.DisabledUntilUtc IS NULL OR julianday(a.DisabledUntilUtc)<=julianday($now))
                    AND r.ReasoningEffort IS $reasoning AND s.ReasoningEffort IS $reasoning
                    AND r.SpeedMode IS NULL AND r.ExecutionMode IS NULL
                    AND (SELECT COUNT(*) FROM Executions e JOIN Sessions es ON es.Id=e.SessionId
                        WHERE e.Id!=$execution AND es.AccountId=a.Id AND (e.EndedAtUtc IS NULL OR es.ActiveExecutionId=e.Id OR
                            e.State NOT IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch') OR
                            EXISTS (SELECT 1 FROM ProjectLocks el WHERE el.ExecutionId=e.Id AND el.ReleasedAtUtc IS NULL))) < a.MaxConcurrentExecutions
                    AND (pr.DataClassification!='Restricted' OR ($restrictedConsent=1
                        AND pr.DataClassification='Restricted' AND r.MaxDataClass='Restricted' AND p.MaxDataClass='Restricted'
                        AND p.Id=$restrictedProfile))
                    AND (pr.DataClassification='PublicSource' OR (r.MaxDataClass!='PublicSource' AND p.MaxDataClass!='PublicSource'))
                    AND NOT EXISTS (SELECT 1 FROM HealthAuthenticationFanout f WHERE
              EXISTS(SELECT 1 FROM HealthAuthenticationFanoutAccounts b WHERE b.ProjectionId=f.Id AND b.AccountId=a.Id) OR
              f.AccountId=a.Id OR (f.ScopeType='account' AND f.ScopeId=a.Id) OR
              (f.ScopeType='route' AND f.ScopeId=r.Id) OR
              (f.ScopeType='model-route' AND f.ScopeId='v1:'||hex(a.Id)||':'||hex(m.Id)) OR
              (f.ScopeType='backend' AND f.ScopeId IN ('NativeGateway',p.Id)))
                    AND NOT EXISTS (SELECT 1 FROM HealthStates h WHERE h.State NOT IN ('Healthy','Degraded','ForcedEnabled') AND
                       ((h.ScopeType='account' AND h.ScopeId=a.Id) OR (h.ScopeType='route' AND h.ScopeId=r.Id) OR
               (h.ScopeType='model-route' AND h.ScopeId='v1:'||hex(a.Id)||':'||hex(m.Id)) OR
                        (h.ScopeType='backend' AND h.ScopeId IN ('NativeGateway',p.Id)))))
            """;
        await using var command = Command(connection, transaction,
            (markRunning ? "UPDATE Executions SET State='Running',StartedAtUtc=$now " : "SELECT COUNT(*) FROM Executions ") + admissionPredicate,
            ("$execution", entry.ExecutionId), ("$session", entry.SessionId), ("$now", Now()),
            ("$owner", guard.InstanceId), ("$root", entry.Context.WorkingDirectory),
            ("$generation", processGeneration),
            ("$reasoning", (object?)entry.Context.ReasoningEffort ?? DBNull.Value),
            ("$route", entry.RouteId), ("$account", entry.AccountId), ("$model", entry.ModelId),
            ("$nativeAccount", entry.Context.AccountId), ("$nativeModel", entry.Context.NativeModel),
            ("$restrictedConsent", authorization is null ? 0 : 1), ("$restrictedProfile", GrokBotRestrictions.ProviderProfileId));
        var admitted = markRunning ? await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)
            : Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
        if (admitted != 1)
            throw new InvalidOperationException("Выполнение или блокировка проекта изменились.");
        if (!markRunning)
        {
            if (bindGeneration > 0)
            {
                await using var binding = Command(connection, transaction, """
                    UPDATE ProjectLocks SET ProcessGeneration=$generation WHERE ExecutionId=$execution
                        AND ApplicationInstanceId=$owner AND ProcessGeneration=0 AND ReleasedAtUtc IS NULL
                        AND CanonicalRootPath=$root COLLATE NOCASE;
                    """, ("$generation", bindGeneration), ("$execution", entry.ExecutionId),
                    ("$owner", guard.InstanceId), ("$root", entry.Context.WorkingDirectory));
                if (await binding.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Владелец native process уже изменился.");
                await using var proof = Command(connection, transaction, """
                    INSERT INTO ExecutionEvents (Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
                    VALUES ($id,$execution,(SELECT COALESCE(MAX(Sequence),-1)+1 FROM ExecutionEvents WHERE ExecutionId=$execution),
                        'NativeProcessBound',$payload,'PublicSource',$now);
                    """, ("$id", Guid.NewGuid().ToString("D")), ("$execution", entry.ExecutionId), ("$now", Now()),
                    ("$payload", JsonSerializer.Serialize(new { processGeneration = bindGeneration, applicationInstanceId = guard.InstanceId,
                        nativeIdentityConfirmed = false })));
                await proof.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested(); guard.EnsureSupervisorPermitted(); transaction.Commit(); return;
        }
        var journalEvent = await AppendAsync(connection, transaction, entry, ExecutionState.Running, ExecutionFailureReason.None, token).ConfigureAwait(false);
        if (authorization is not null)
            await AppendEgressAsync(connection, transaction, entry, authorization, "NativeEgressTransportAuthorized", token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        guard.EnsureSupervisorPermitted();
        transaction.Commit();
        NativeGatewayJournalEvents.Publish(activity, journalEvent);
    }

    internal async Task ValidateEgressAsync(NativeGatewayAdmission entry, NativeGatewayEgressAuthorization authorization, CancellationToken token)
    {
        guard.EnsureSupervisorPermitted();
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        authorization.ValidateRows(await ReadEgressRowsAsync(connection, transaction, entry, token).ConfigureAwait(false));
        token.ThrowIfCancellationRequested(); guard.EnsureSupervisorPermitted(); transaction.Commit();
    }

    private static async Task<EgressPolicyRows> ReadEgressRowsAsync(SqliteConnection connection, SqliteTransaction transaction,
        NativeGatewayAdmission entry, CancellationToken token)
    {
        await using var command = Command(connection, transaction,
            "SELECT ProjectId FROM Sessions WHERE Id=$session AND ActiveExecutionId=$execution AND State='Active' AND Backend='NativeGateway'",
            ("$session", entry.SessionId), ("$execution", entry.ExecutionId));
        var project = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string ?? throw new EgressApprovalException();
        return await SqliteEgressPolicyReader.ReadAsync(connection, transaction, project, entry.RouteId, token).ConfigureAwait(false);
    }

    private async Task AppendEgressAsync(SqliteConnection connection, SqliteTransaction transaction, NativeGatewayAdmission entry,
        NativeGatewayEgressAuthorization authorization, string kind, CancellationToken token)
    {
        var proof = authorization.Payload;
        await using var command = Command(connection, transaction, """
            INSERT INTO ExecutionEvents (Id,ExecutionId,Sequence,EventKind,NormalizedRedactedPayloadJson,DataClassification,OccurredAtUtc)
            VALUES ($id,$execution,(SELECT COALESCE(MAX(Sequence),-1)+1 FROM ExecutionEvents WHERE ExecutionId=$execution),
                $kind,$payload,$class,$now)
            """, ("$id", Guid.NewGuid().ToString("D")), ("$execution", entry.ExecutionId), ("$kind", kind),
            ("$payload", JsonSerializer.Serialize(new { consentId = proof.PreviewId, actor = "PrimaryLocalInteractiveUser",
                authorityInstanceId = authorization.ActorInstanceId, payloadSha256 = proof.PayloadSha256,
                wireSha256 = authorization.WireSha256, policyFingerprint = proof.PolicyFingerprint, expiresAtUtc = proof.ExpiresAtUtc,
                fragments = proof.Fragments.Select(f => new { id = f.Id, sha256 = f.ContentSha256, classification = f.Classification.ToString() }),
                providerProfileId = authorization.Binding.ProviderProfileId, accountId = entry.AccountId, modelId = entry.ModelId,
                policy = "ManualOnly", nativeIdentityConfirmed = false })), ("$class", proof.Classification.ToString()), ("$now", Now()));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public async Task CompleteAsync(NativeGatewayAdmission entry, ExecutionState state, ExecutionFailureReason failure)
    {
        guard.EnsureSupervisorPermitted();
        if (state is not (ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.Cancelled or ExecutionState.TimedOut or ExecutionState.Ambiguous))
            throw new ArgumentOutOfRangeException(nameof(state));
        var uncertain = state == ExecutionState.Ambiguous;
        await using var connection = await factory.OpenConnectionAsync().ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        // Writer ownership deliberately remains held through this transaction.
        // The turn service releases it only after this commit succeeds.
        await using (var command = Command(connection, transaction, """
            UPDATE Executions SET State=$state,FailureReason=$failure,EndedAtUtc=$ended,
                TerminationReason=$reason WHERE Id=$execution AND SessionId=$session AND ClientRequestId=$request
                AND State IN ('Starting','Running') AND EXISTS (SELECT 1 FROM Sessions s WHERE s.Id=$session
                    AND s.Backend='NativeGateway' AND s.State='Active' AND s.ActiveExecutionId=$execution);
            """, ("$state", state.ToString()), ("$failure", failure.ToString()), ("$ended", uncertain ? DBNull.Value : Now()),
            ("$reason", uncertain ? "NativeOutcomeUnconfirmed" : "GatewayStreamEnded"),
            ("$execution", entry.ExecutionId), ("$session", entry.SessionId), ("$request", entry.RequestId)))
            if (await command.ExecuteNonQueryAsync().ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Выполнение уже изменилось.");
        await using (var command = Command(connection, transaction, """
            UPDATE Sessions SET State=$state,ActiveExecutionId=$active,LastEventAtUtc=$now
            WHERE Id=$session AND State='Active' AND ActiveExecutionId=$execution
            """, ("$state", uncertain ? "Ambiguous" : "Closed"), ("$active", uncertain ? entry.ExecutionId : DBNull.Value),
            ("$now", Now()), ("$session", entry.SessionId), ("$execution", entry.ExecutionId)))
            if (await command.ExecuteNonQueryAsync().ConfigureAwait(false) != 1) throw new InvalidOperationException("Сессия изменилась.");
        var journalEvent = await AppendAsync(connection, transaction, entry, state, failure, CancellationToken.None).ConfigureAwait(false);
        guard.EnsureSupervisorPermitted();
        transaction.Commit();
        NativeGatewayJournalEvents.Publish(activity, journalEvent);
    }

    private Task<NativeGatewayJournalEvent> AppendAsync(SqliteConnection connection, SqliteTransaction transaction, NativeGatewayAdmission entry,
        ExecutionState state, ExecutionFailureReason failure, CancellationToken token) =>
        NativeGatewayJournalEvents.AppendAsync(connection, transaction, entry.ExecutionId, entry.SessionId,
            state, failure, false, clock.GetUtcNow(), token, entry.Context.ReasoningEffort);

    private string Now() => clock.GetUtcNow().ToString("O");
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Key, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return command;
    }
}
