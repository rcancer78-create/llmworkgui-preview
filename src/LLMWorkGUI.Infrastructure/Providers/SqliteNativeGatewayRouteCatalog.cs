using LLMGateway.Core;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Security;

namespace LLMWorkGUI.Infrastructure.Providers;

public sealed class SqliteNativeGatewayRouteCatalog(ISqliteConnectionFactory factory, SensitiveDataFilter filter,
    TimeProvider clock) : INativeGatewayRouteCatalog
{
    public async Task<IReadOnlyList<NativeGatewayRouteOption>> ListAsync(string projectId, string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var root = ProjectLock.CanonicalizeRoot(rootPath);
        if (!Directory.Exists(root)) return [];
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        await using (var project = connection.CreateCommand())
        {
            project.CommandText = "SELECT RootPath FROM Projects WHERE Id=$project";
            project.Transaction = transaction;
            project.Parameters.AddWithValue("$project", projectId);
            if (await project.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string storedRoot
                || !string.Equals(ProjectLock.CanonicalizeRoot(storedRoot), root, StringComparison.OrdinalIgnoreCase)) return [];
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var evidence = await SqliteModelCapabilityEvidenceStore.ReadAsync(connection, transaction, filter, cancellationToken).ConfigureAwait(false);
        command.CommandText = NativeGatewayRouteQuery.PreviewCandidatesSql + " ORDER BY r.ManualPriority,r.Id";
        command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToString("O"));
        var result = new List<NativeGatewayRouteOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(3)) continue;
            var binding = new NativeGatewayRouteBinding(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(9) ? null : reader.GetString(9));
            if (!NativeGatewayRouteQuery.HasValidBinding(binding)) continue;
            if (!NativeGatewayRouteOptions.IsSupported(evidence, binding, clock.GetUtcNow(),
                    NativeGatewayRouteOptions.RequiresChatDeclaration(binding.ProviderProfileId, reader.GetString(10)))) continue;
            result.Add(new(reader.GetString(5), binding, Label(reader.GetString(6)), Label(reader.GetString(7)), Label(reader.GetString(8))));
        }
        return result;
    }

    private string Label(string value)
    {
        var text = filter.Redact(value);
        return text.Length <= 160 ? text : text[..160];
    }
}

internal static class NativeGatewayRouteQuery
{
    // Selection only; execution still requires an authority-consumed proof and trusted transport.
    internal static string PreviewCandidatesSql => Sql.Replace("AND pr.DataClassification!='Restricted'",
        "AND (pr.DataClassification!='Restricted' OR (pr.DataClassification='Restricted' " +
        $"AND p.Id='{LLMWorkGUI.Application.Providers.GrokBotRestrictions.ProviderProfileId}' " +
        "AND r.MaxDataClass='Restricted' AND p.MaxDataClass='Restricted'))", StringComparison.Ordinal);
    internal static bool HasValidBinding(NativeGatewayRouteBinding binding) => Enum.GetValues<ProviderKind>().Where(kind => kind != ProviderKind.Unknown).Any(kind =>
        GatewayCatalogMapper.ProviderId(kind) == binding.ProviderProfileId
        && GatewayCatalogMapper.AccountId(kind, binding.NativeAccountId) == binding.AccountId
        && GatewayCatalogMapper.ModelId(kind, binding.NativeModelId) == binding.ModelId)
        && ModelRouter.IsValidModelName(binding.NativeModelId)
        && !binding.NativeModelId.Equals("auto", StringComparison.OrdinalIgnoreCase);

    internal static readonly string Sql = $"""
        SELECT r.ProviderProfileId,r.AccountId,r.ModelId,a.ProviderNativeId,m.ProviderModelId,
               r.Id,p.DisplayName,a.DisplayName,m.DisplayName,r.ReasoningEffort,m.Provenance
        FROM Routes r
        JOIN ProviderProfiles p ON p.Id=r.ProviderProfileId AND p.Backend=r.Backend
        JOIN Accounts a ON a.Id=r.AccountId AND a.ProviderProfileId=p.Id
        JOIN Models m ON m.Id=r.ModelId AND m.ProviderProfileId=p.Id AND m.Backend=r.Backend
        JOIN Projects pr ON pr.Id=$project
        WHERE r.Backend='NativeGateway'
          AND r.IsEnabled=1 AND p.IsEnabled=1 AND a.IsEnabled=1 AND m.IsEnabled=1
          AND a.AuthState='Valid' AND m.CapabilityState='Supported'
          AND r.Health IN ('Healthy','Degraded','ForcedEnabled')
          AND a.Health IN ('Healthy','Degraded','ForcedEnabled')
          AND m.Health IN ('Healthy','Degraded','ForcedEnabled')
          AND (a.CooldownUntilUtc IS NULL OR julianday(a.CooldownUntilUtc)<=julianday($now))
          AND (a.DisabledUntilUtc IS NULL OR julianday(a.DisabledUntilUtc)<=julianday($now))
          AND r.SpeedMode IS NULL AND r.ExecutionMode IS NULL
          AND (r.ReasoningEffort IS NULL OR p.Id='{GatewayCatalogMapper.ProviderId(ProviderKind.Codex)}')
          AND pr.DataClassification!='Restricted'
          AND (pr.DataClassification='PublicSource' OR (r.MaxDataClass!='PublicSource' AND p.MaxDataClass!='PublicSource'))
          AND NOT EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.CanonicalRootPath=$root COLLATE NOCASE AND l.ReleasedAtUtc IS NULL)
          AND NOT EXISTS (SELECT 1 FROM HealthAuthenticationFanout f WHERE
              EXISTS(SELECT 1 FROM HealthAuthenticationFanoutAccounts b WHERE b.ProjectionId=f.Id AND b.AccountId=a.Id) OR
              f.AccountId=a.Id OR (f.ScopeType='account' AND f.ScopeId=a.Id) OR
              (f.ScopeType='route' AND f.ScopeId=r.Id) OR
              (f.ScopeType='model-route' AND f.ScopeId='v1:'||hex(a.Id)||':'||hex(m.Id)) OR
              (f.ScopeType='backend' AND f.ScopeId IN ('NativeGateway',p.Id)))
          AND NOT EXISTS (SELECT 1 FROM HealthStates h WHERE h.State NOT IN ('Healthy','Degraded','ForcedEnabled') AND
              ((h.ScopeType='account' AND h.ScopeId=a.Id) OR
               (h.ScopeType='route' AND h.ScopeId=r.Id) OR
               (h.ScopeType='model-route' AND h.ScopeId='v1:'||hex(a.Id)||':'||hex(m.Id)) OR
               (h.ScopeType='backend' AND h.ScopeId IN ('NativeGateway',p.Id))))
          AND (SELECT COUNT(*) FROM Executions e JOIN Sessions s ON s.Id=e.SessionId
               WHERE s.AccountId=a.Id AND (e.EndedAtUtc IS NULL OR s.ActiveExecutionId=e.Id OR
                 e.State NOT IN ('Succeeded','Failed','TimedOut','Cancelled','RouteMismatch') OR
                 EXISTS (SELECT 1 FROM ProjectLocks l WHERE l.ExecutionId=e.Id AND l.ReleasedAtUtc IS NULL))) < a.MaxConcurrentExecutions
        """;
}
