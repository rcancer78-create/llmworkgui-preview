using System.Globalization;
using System.Text.Json;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed partial class SqliteHealthTransitionStore
{
    private const string PendingPredicate = """
        (f.ScopeType=$type AND f.ScopeId=$scope) OR
        (f.AccountId=$account AND $account IS NOT NULL) OR
        EXISTS(SELECT 1 FROM HealthAuthenticationFanoutAccounts b WHERE b.ProjectionId=f.Id AND
            (b.AccountId=$account OR ($type='route' AND b.AccountId=(SELECT AccountId FROM Routes WHERE Id=$scope))))
        """;

    private static object AccountParameter(HealthScope scope) =>
        (object?)(scope.ScopeType == HealthScope.AccountScopeType ? scope.ScopeId :
            scope.TryGetModelRoute(out var account, out _) ? account : null) ?? DBNull.Value;

    public async Task<(HealthStateRecord? State, bool Pending)> ReadSnapshotAsync(HealthScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT h.Id,h.ScopeType,h.ScopeId,h.State,h.ErrorClass,h.FailureCount,h.WindowStartedAtUtc,
                   h.CooldownUntilUtc,h.EvidenceRedactedJson,h.UpdatedAtUtc,h.FailureHistoryJson,
                   EXISTS(SELECT 1 FROM HealthAuthenticationFanout f WHERE {PendingPredicate})
            FROM (SELECT 1) singleton LEFT JOIN HealthStates h ON h.ScopeType=$type AND h.ScopeId=$scope;
            """;
        command.Parameters.AddWithValue("$type", scope.ScopeType);
        command.Parameters.AddWithValue("$scope", scope.ScopeId);
        command.Parameters.AddWithValue("$account", AccountParameter(scope));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.IsDBNull(0) ? null : SqliteHealthStateRepository.Read(reader), reader.GetBoolean(11));
    }

    public async Task EnqueueAsync(IReadOnlyList<HealthAuthenticationProjection> projections, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        foreach (var projection in projections)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO HealthAuthenticationFanout(Id,ScopeType,ScopeId,AccountId,Reason,ObservedAtUtc,EvidenceRedactedJson)
                VALUES($id,$type,$scope,$account,$reason,$observed,$evidence);
                """;
            command.Parameters.AddWithValue("$id", projection.Id);
            command.Parameters.AddWithValue("$type", projection.Scope.ScopeType);
            command.Parameters.AddWithValue("$scope", projection.Scope.ScopeId);
            command.Parameters.AddWithValue("$account", (object?)projection.AccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("$reason", projection.Reason);
            command.Parameters.AddWithValue("$observed", projection.ObservedAt.ToString("O"));
            command.Parameters.AddWithValue("$evidence", (object?)projection.EvidenceRedactedJson ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var account in projection.ProtectedAccountIds.Concat(projection.AccountId is null ? [] : new[] { projection.AccountId }).Distinct(StringComparer.Ordinal))
            {
                await using var link = connection.CreateCommand();
                link.Transaction = transaction;
                link.CommandText = "INSERT INTO HealthAuthenticationFanoutAccounts(ProjectionId,AccountId) VALUES($id,$account);";
                link.Parameters.AddWithValue("$id", projection.Id);
                link.Parameters.AddWithValue("$account", account);
                await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HealthAuthenticationProjection>> ListPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,ScopeType,ScopeId,AccountId,Reason,ObservedAtUtc,EvidenceRedactedJson,
                (SELECT json_group_array(AccountId) FROM HealthAuthenticationFanoutAccounts b WHERE b.ProjectionId=f.Id)
            FROM HealthAuthenticationFanout f ORDER BY ObservedAtUtc,Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<HealthAuthenticationProjection>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(reader.GetString(0), new(reader.GetString(1), reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture))
                { EvidenceRedactedJson = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ProtectedAccountIds = JsonSerializer.Deserialize<string[]>(reader.GetString(7))! });
        return result;
    }

    public async Task<bool> HasPendingAsync(HealthScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM HealthAuthenticationFanout f WHERE {PendingPredicate});";
        command.Parameters.AddWithValue("$type", scope.ScopeType);
        command.Parameters.AddWithValue("$scope", scope.ScopeId);
        command.Parameters.AddWithValue("$account", AccountParameter(scope));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 0;
    }

    public async Task SaveAndCompleteAsync(HealthStateRecord state, HealthEventRecord healthEvent,
        string projectionId, CancellationToken cancellationToken)
    {
        if (state.ScopeType != healthEvent.ScopeType || state.ScopeId != healthEvent.ScopeId || state.State != healthEvent.NewState)
            throw new ArgumentException("The health snapshot and audit must describe the same scope and state.");
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await using var remove = connection.CreateCommand();
        remove.Transaction = transaction;
        remove.CommandText = "DELETE FROM HealthAuthenticationFanout WHERE Id=$id AND ScopeType=$type AND ScopeId=$scope;";
        remove.Parameters.AddWithValue("$id", projectionId);
        remove.Parameters.AddWithValue("$type", state.ScopeType);
        remove.Parameters.AddWithValue("$scope", state.ScopeId);
        if (await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The authentication projection no longer belongs to this scope.");
        await SqliteHealthStateRepository.UpsertAsync(connection, transaction, state, cancellationToken).ConfigureAwait(false);
        await SqliteHealthEventRepository.AppendAsync(connection, transaction, healthEvent, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
