using LLMGateway.Core;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Providers;

public sealed partial class NativeGatewayRouteActivationService
{
    private async Task<Snapshot> CaptureAsync(string routeId, CancellationToken token)
    {
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        return await CaptureAsync(connection, transaction, routeId, token).ConfigureAwait(false);
    }

    private async Task<Snapshot> CaptureAsync(SqliteConnection connection, SqliteTransaction transaction, string routeId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.Id,a.Id,m.Id,a.ProviderNativeId,m.ProviderModelId,
                   r.IsEnabled,p.IsEnabled,a.IsEnabled,m.IsEnabled,r.Health,a.Health,m.Health,
                   r.MaxDataClass,p.MaxDataClass,a.AuthState,m.CapabilityState,m.Provenance,
                   r.UpdatedAtUtc,p.UpdatedAtUtc,a.UpdatedAtUtc,a.CapabilityRevision,m.CapabilityRevision,
                   a.MaxConcurrentExecutions,m.SupportsTools,m.SupportsAttachments
            FROM Routes r JOIN ProviderProfiles p ON p.Id=r.ProviderProfileId AND p.Backend=r.Backend
            JOIN Accounts a ON a.Id=r.AccountId AND a.ProviderProfileId=p.Id
            JOIN Models m ON m.Id=r.ModelId AND m.ProviderProfileId=p.Id AND m.Backend=r.Backend
            WHERE r.Id=$route AND r.Backend='NativeGateway' AND p.Id=$profile
              AND r.ReasoningEffort IS NULL AND r.SpeedMode IS NULL AND r.ExecutionMode IS NULL
              AND r.Health IN ('ProbeRequired','Healthy','Degraded','ForcedEnabled')
              AND a.Health IN ('ProbeRequired','Healthy','Degraded','ForcedEnabled')
              AND m.Health IN ('ProbeRequired','Healthy','Degraded','ForcedEnabled')
              AND a.CooldownUntilUtc IS NULL AND a.DisabledUntilUtc IS NULL
              AND a.AuthState IN ('Unknown','Valid') AND m.CapabilityState IN ('Unknown','Supported')
              AND NOT EXISTS (SELECT 1 FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
                  WHERE s.AccountId=a.Id AND (e.EndedAtUtc IS NULL OR s.ActiveExecutionId=e.Id
                    OR e.State NOT IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch')
                    OR EXISTS(SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=e.Id AND l.ReleasedAtUtc IS NULL)))
              AND NOT EXISTS (SELECT 1 FROM HealthAuthenticationFanout f WHERE
                  EXISTS(SELECT 1 FROM HealthAuthenticationFanoutAccounts b WHERE b.ProjectionId=f.Id AND b.AccountId=a.Id)
                  OR f.AccountId=a.Id OR (f.ScopeType='account' AND f.ScopeId=a.Id)
                  OR (f.ScopeType='route' AND f.ScopeId=r.Id)
                  OR (f.ScopeType='model-route' AND f.ScopeId='v1:'||hex(a.Id)||':'||hex(m.Id))
                  OR (f.ScopeType='backend' AND f.ScopeId IN ('NativeGateway',p.Id)))
              AND NOT EXISTS (SELECT 1 FROM HealthStates h WHERE
                  ((h.ScopeType='backend' AND h.ScopeId IN ('NativeGateway',p.Id)
                     AND h.State NOT IN ('Healthy','Degraded','ForcedEnabled'))
                   OR ((h.ScopeType='account' AND h.ScopeId=a.Id)
                       OR (h.ScopeType='route' AND h.ScopeId=r.Id)
                       OR (h.ScopeType='model-route' AND h.ScopeId='v1:'||hex(a.Id)||':'||hex(m.Id)))
                     AND (h.State NOT IN ('ProbeRequired','Healthy','Degraded','ForcedEnabled')
                       OR h.CooldownUntilUtc IS NOT NULL OR h.ErrorClass IS NOT NULL OR h.FailureCount>0)))
            """;
        command.Parameters.AddWithValue("$route", routeId);
        command.Parameters.AddWithValue("$profile", GatewayCatalogMapper.ProviderId(ProviderKind.Cursor));
        object?[] fields;
        NativeGatewayRouteBinding binding;
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.IsDBNull(3)) throw Refused();
            binding = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            if (!NativeGatewayRouteQuery.HasValidBinding(binding)) throw Refused();
            fields = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray();
        }
        command.Parameters.Clear();
        command.CommandText = """
            SELECT ScopeType,ScopeId,State,ErrorClass,FailureCount,CooldownUntilUtc,UpdatedAtUtc
            FROM HealthStates WHERE (ScopeType='account' AND ScopeId=$account)
               OR (ScopeType='route' AND ScopeId=$route)
               OR (ScopeType='model-route' AND ScopeId='v1:'||hex($account)||':'||hex($model))
               OR (ScopeType='backend' AND ScopeId IN ('NativeGateway',$profile))
            ORDER BY ScopeType,ScopeId
            """;
        command.Parameters.AddWithValue("$account", binding.AccountId);
        command.Parameters.AddWithValue("$model", binding.ModelId);
        command.Parameters.AddWithValue("$route", routeId);
        command.Parameters.AddWithValue("$profile", binding.ProviderProfileId);
        var health = new List<object?[]>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                health.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        command.Parameters.Clear();
        command.CommandText = "SELECT CapabilityKey,CapabilityValue,State,Provenance,UpdatedAtUtc FROM ModelCapabilities WHERE ModelId=$model ORDER BY CapabilityKey";
        command.Parameters.AddWithValue("$model", binding.ModelId);
        var capabilities = new List<object?[]>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                capabilities.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        var evidence = await SqliteModelCapabilityEvidenceStore.ReadAsync(connection, transaction,
            new LLMWorkGUI.Infrastructure.Security.SensitiveDataFilter(), token).ConfigureAwait(false);
        var key = "llmworkgui.evidence.v1/" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(binding.AccountId));
        var existingRows = capabilities.Where(row => Equals(row[0], key)).ToArray();
        ModelCapabilityEvidence? existing = null;
        if (existingRows.Length != 0)
        {
            var declaration = evidence.SingleOrDefault(item => item.AccountId == binding.AccountId && item.ModelId == binding.ModelId);
            if (declaration is null || declaration.State == CapabilityState.Unsupported || declaration.ObservedAtUtc > clock.GetUtcNow())
                throw Refused();
            existing = declaration;
        }
        command.Parameters.Clear();
        command.CommandText = """
            SELECT Id,ProviderProfileId,AccountId,ModelId,IsEnabled,Health,MaxDataClass,UpdatedAtUtc
            FROM Routes WHERE Id!=$route AND IsEnabled=1
              AND (ProviderProfileId=$profile OR AccountId=$account OR ModelId=$model) ORDER BY Id
            """;
        command.Parameters.AddWithValue("$route", routeId);
        command.Parameters.AddWithValue("$profile", binding.ProviderProfileId);
        command.Parameters.AddWithValue("$account", binding.AccountId);
        command.Parameters.AddWithValue("$model", binding.ModelId);
        var siblings = new List<object?[]>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                siblings.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray());
        static bool ReadyHealth(object? state) => state is "Healthy" or "Degraded" or "ForcedEnabled";
        var accountScopeReady = !health.Any(row => Equals(row[0], "account") && Equals(row[1], binding.AccountId) && !ReadyHealth(row[2]));
        var modelScope = "v1:" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(binding.AccountId)) + ":"
            + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(binding.ModelId));
        var modelScopeReady = !health.Any(row => Equals(row[0], "model-route") && Equals(row[1], modelScope) && !ReadyHealth(row[2]));
        var shared = new SharedState(Convert.ToInt64(fields[6]) == 1,
            Convert.ToInt64(fields[7]) == 1 && Equals(fields[14], "Valid") && ReadyHealth(fields[10]) && accountScopeReady,
            Convert.ToInt64(fields[8]) == 1 && Equals(fields[15], "Supported") && ReadyHealth(fields[11]),
            Enum.Parse<DataClassification>((string)fields[13]!),
            siblings.Any(row => Equals(row[1], binding.ProviderProfileId)),
            siblings.Any(row => Equals(row[2], binding.AccountId)), siblings.Any(row => Equals(row[3], binding.ModelId)),
            existing is not null && existing.IsCurrent(clock.GetUtcNow()) && existing.Flags.HasFlag(ModelCapabilityFlags.Chat) && modelScopeReady,
            siblings.Any(row => Equals(row[2], binding.AccountId) && Equals(row[3], binding.ModelId)));
        return new(binding, Hash(new { fields, health, capabilities, siblings }), shared);
    }

    private async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, string routeId,
        NativeGatewayRouteBinding binding, DataClassification dataClass, CancellationToken token)
    {
        var observed = clock.GetUtcNow().ToUniversalTime();
        var now = observed.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ProviderProfiles SET IsEnabled=1,MaxDataClass=$class,UpdatedAtUtc=$now WHERE Id=$profile;
            UPDATE Accounts SET IsEnabled=1,AuthState='Valid',Health=CASE WHEN Health='ProbeRequired' THEN 'ForcedEnabled' ELSE Health END,UpdatedAtUtc=$now WHERE Id=$account;
            UPDATE Models SET IsEnabled=1,CapabilityState='Supported',Provenance='UserDefined',Health=CASE WHEN Health='ProbeRequired' THEN 'ForcedEnabled' ELSE Health END WHERE Id=$model;
            UPDATE Routes SET IsEnabled=1,Health=CASE WHEN Health='ProbeRequired' THEN 'ForcedEnabled' ELSE Health END,MaxDataClass=$class,UpdatedAtUtc=$now WHERE Id=$route;
            """;
        command.Parameters.AddWithValue("$class", dataClass.ToString());
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$profile", binding.ProviderProfileId);
        command.Parameters.AddWithValue("$account", binding.AccountId);
        command.Parameters.AddWithValue("$model", binding.ModelId);
        command.Parameters.AddWithValue("$route", routeId);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        // This declaration grants only text chat for this account/model, never inventory-derived tools/vision/options.
        var declaration = new ModelCapabilityEvidence(binding.ModelId, binding.AccountId, CapabilityState.Supported,
            ModelProvenance.UserDefined, observed, observed.AddHours(24), ModelCapabilityFlags.Chat, [], [], []);
        if (!declaration.IsWellFormed) throw Refused();
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO ModelCapabilities(Id,ModelId,CapabilityKey,CapabilityValue,State,Provenance,UpdatedAtUtc)
            VALUES($id,$model,$key,$value,'Supported','UserDefined',$now)
            ON CONFLICT(ModelId,CapabilityKey) DO UPDATE SET CapabilityValue=excluded.CapabilityValue,
              State=excluded.State,Provenance=excluded.Provenance,UpdatedAtUtc=excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$model", binding.ModelId);
        command.Parameters.AddWithValue("$key", "llmworkgui.evidence.v1/" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(binding.AccountId)));
        command.Parameters.AddWithValue("$value", System.Text.Json.JsonSerializer.Serialize(declaration));
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        foreach (var (type, id) in new[] { ("account", binding.AccountId), ("route", routeId),
            ("model-route", "v1:" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(binding.AccountId)) + ":" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(binding.ModelId))) })
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO HealthEvents(Id,ScopeType,ScopeId,PreviousState,NewState,Reason,OccurredAtUtc)
                VALUES($event,$type,$scope,(SELECT State FROM HealthStates WHERE ScopeType=$type AND ScopeId=$scope),
                    CASE WHEN (SELECT State FROM HealthStates WHERE ScopeType=$type AND ScopeId=$scope) IN ('Healthy','Degraded','ForcedEnabled')
                      THEN (SELECT State FROM HealthStates WHERE ScopeType=$type AND ScopeId=$scope) ELSE 'ForcedEnabled' END,
                    'Explicit initial Cursor activation: native login and exact catalog rechecked; model chat support USERDECLARED; no passing model probe; global provider/account policy consent.',$now);
                INSERT INTO HealthStates(Id,ScopeType,ScopeId,State,UpdatedAtUtc)
                VALUES($id,$type,$scope,'ForcedEnabled',$now)
                ON CONFLICT(ScopeType,ScopeId) DO UPDATE SET
                  State=CASE WHEN HealthStates.State='ProbeRequired' THEN 'ForcedEnabled' ELSE HealthStates.State END,
                  UpdatedAtUtc=CASE WHEN HealthStates.State='ProbeRequired' THEN $now ELSE HealthStates.UpdatedAtUtc END;
                """;
            command.Parameters.AddWithValue("$event", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$id", type + ":" + id);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$scope", id);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }
}
