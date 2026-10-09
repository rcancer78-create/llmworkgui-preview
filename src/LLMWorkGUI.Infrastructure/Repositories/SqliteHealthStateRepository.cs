using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using System.Text.Json;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteHealthStateRepository : IHealthStateRepository
{
    private const string SelectColumns = """
        Id, ScopeType, ScopeId, State, ErrorClass, FailureCount, WindowStartedAtUtc,
        CooldownUntilUtc, EvidenceRedactedJson, UpdatedAtUtc, FailureHistoryJson
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteHealthStateRepository(ISqliteConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
    }

    public async Task UpsertAsync(HealthStateRecord healthState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(healthState);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await UpsertAsync(connection, null, healthState, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction? transaction,
        HealthStateRecord healthState, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO HealthStates
                (Id, ScopeType, ScopeId, State, ErrorClass, FailureCount, WindowStartedAtUtc,
                 CooldownUntilUtc, EvidenceRedactedJson, UpdatedAtUtc, FailureHistoryJson)
            VALUES
                ($id, $scopeType, $scopeId, $state, $errorClass, $failureCount, $windowStartedAtUtc,
                 $cooldownUntilUtc, $evidenceRedactedJson, $updatedAtUtc, $failureHistoryJson)
            ON CONFLICT (ScopeType, ScopeId) DO UPDATE SET
                State = excluded.State,
                ErrorClass = excluded.ErrorClass,
                FailureCount = excluded.FailureCount,
                WindowStartedAtUtc = excluded.WindowStartedAtUtc,
                CooldownUntilUtc = excluded.CooldownUntilUtc,
                EvidenceRedactedJson = excluded.EvidenceRedactedJson,
                UpdatedAtUtc = excluded.UpdatedAtUtc,
                FailureHistoryJson = excluded.FailureHistoryJson;
            """;

        command.Parameters.AddWithValue("$id", healthState.Id);
        command.Parameters.AddWithValue("$scopeType", healthState.ScopeType);
        command.Parameters.AddWithValue("$scopeId", healthState.ScopeId);
        command.Parameters.AddWithValue("$state", SqliteRepositorySupport.FormatEnum(healthState.State));
        SqliteRepositorySupport.AddNullable(
            command,
            "$errorClass",
            healthState.ErrorClass is null
                ? null
                : SqliteRepositorySupport.FormatEnum(healthState.ErrorClass.Value));
        command.Parameters.AddWithValue("$failureCount", healthState.FailureCount);
        SqliteRepositorySupport.AddNullable(command, "$failureHistoryJson", healthState.FailureHistory is null
            ? null : JsonSerializer.Serialize(healthState.FailureHistory));
        SqliteRepositorySupport.AddNullable(
            command,
            "$windowStartedAtUtc",
            SqliteRepositorySupport.FormatTimestamp(healthState.WindowStartedAt));
        SqliteRepositorySupport.AddNullable(
            command,
            "$cooldownUntilUtc",
            SqliteRepositorySupport.FormatTimestamp(healthState.CooldownUntil));
        SqliteRepositorySupport.AddNullable(
            command,
            "$evidenceRedactedJson",
            healthState.EvidenceRedactedJson);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            SqliteRepositorySupport.FormatTimestamp(healthState.UpdatedAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<HealthStateRecord?> GetAsync(
        string scopeType,
        string scopeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM HealthStates
            WHERE ScopeType = $scopeType AND ScopeId = $scopeId;
            """;
        command.Parameters.AddWithValue("$scopeType", scopeType);
        command.Parameters.AddWithValue("$scopeId", scopeId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<HealthStateRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM HealthStates ORDER BY ScopeType, ScopeId;";

        var healthStates = new List<HealthStateRecord>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            healthStates.Add(Read(reader));
        }

        return healthStates;
    }

    internal static HealthStateRecord Read(SqliteDataReader reader)
    {
        var errorClass = SqliteRepositorySupport.GetNullableString(reader, 4);

        return new HealthStateRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            SqliteRepositorySupport.ParseEnum<HealthState>(reader.GetString(3)),
            errorClass is null ? null : SqliteRepositorySupport.ParseEnum<HealthErrorClass>(errorClass),
            reader.GetInt32(5),
            SqliteRepositorySupport.GetNullableTimestamp(reader, 6),
            SqliteRepositorySupport.GetNullableTimestamp(reader, 7),
            SqliteRepositorySupport.GetNullableString(reader, 8),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(9)))
        {
            FailureHistory = reader.IsDBNull(10) ? null
                : JsonSerializer.Deserialize<HealthFailureObservation[]>(reader.GetString(10))
                    ?? throw new JsonException("Persisted breaker history must be an array, not null.")
        };
    }
}
