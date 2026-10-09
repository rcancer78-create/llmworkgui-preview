using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

/// <summary>
/// Append-only SQLite store of health transitions over the existing <c>HealthEvents</c> table.
/// Inserts never upsert: a duplicate id is a programming error and must surface instead of silently
/// overwriting an audit entry.
/// </summary>
public sealed class SqliteHealthEventRepository : IHealthEventRepository
{
    private const string SelectColumns = """
        Id, ScopeType, ScopeId, PreviousState, NewState, ErrorClass, Reason,
        EvidenceRedactedJson, OccurredAtUtc
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteHealthEventRepository(ISqliteConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
    }

    public async Task AppendAsync(
        HealthEventRecord healthEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(healthEvent);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await AppendAsync(connection, null, healthEvent, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task AppendAsync(SqliteConnection connection, SqliteTransaction? transaction,
        HealthEventRecord healthEvent, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO HealthEvents
                (Id, ScopeType, ScopeId, PreviousState, NewState, ErrorClass, Reason,
                 EvidenceRedactedJson, OccurredAtUtc)
            VALUES
                ($id, $scopeType, $scopeId, $previousState, $newState, $errorClass, $reason,
                 $evidenceRedactedJson, $occurredAtUtc);
            """;

        command.Parameters.AddWithValue("$id", healthEvent.Id);
        command.Parameters.AddWithValue("$scopeType", healthEvent.ScopeType);
        command.Parameters.AddWithValue("$scopeId", healthEvent.ScopeId);
        SqliteRepositorySupport.AddNullable(
            command,
            "$previousState",
            healthEvent.PreviousState is null
                ? null
                : SqliteRepositorySupport.FormatEnum(healthEvent.PreviousState.Value));
        command.Parameters.AddWithValue(
            "$newState",
            SqliteRepositorySupport.FormatEnum(healthEvent.NewState));
        SqliteRepositorySupport.AddNullable(
            command,
            "$errorClass",
            healthEvent.ErrorClass is null
                ? null
                : SqliteRepositorySupport.FormatEnum(healthEvent.ErrorClass.Value));
        SqliteRepositorySupport.AddNullable(command, "$reason", healthEvent.Reason);
        SqliteRepositorySupport.AddNullable(
            command,
            "$evidenceRedactedJson",
            healthEvent.EvidenceRedactedJson);
        command.Parameters.AddWithValue(
            "$occurredAtUtc",
            SqliteRepositorySupport.FormatTimestamp(healthEvent.OccurredAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<HealthEventRecord?> FindByIdAsync(string scopeType, string scopeId, string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM HealthEvents WHERE Id=$id AND ScopeType=$type AND ScopeId=$scope;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$type", scopeType);
        command.Parameters.AddWithValue("$scope", scopeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<HealthEventRecord>> ListByScopeAsync(
        string scopeType,
        string scopeId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        if (limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "The limit must be positive when specified.");
        }

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // rowid is the tie-breaker: several transitions can legitimately share a timestamp, and the
        // audit must then fall back on insertion order. Ordering by Id would be arbitrary, because the
        // id is a random GUID and would scramble same-instant transitions.
        command.CommandText = $"""
            SELECT {SelectColumns} FROM HealthEvents
            WHERE ScopeType = $scopeType AND ScopeId = $scopeId
            ORDER BY OccurredAtUtc DESC, rowid DESC
            {(limit is null ? string.Empty : "LIMIT $limit")};
            """;
        command.Parameters.AddWithValue("$scopeType", scopeType);
        command.Parameters.AddWithValue("$scopeId", scopeId);

        if (limit is not null)
        {
            command.Parameters.AddWithValue("$limit", limit.Value);
        }

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HealthEventRecord>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit must be positive.");
        }

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM HealthEvents
            ORDER BY OccurredAtUtc DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<HealthEventRecord>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var events = new List<HealthEventRecord>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(Read(reader));
        }

        return events;
    }

    private static HealthEventRecord Read(SqliteDataReader reader)
    {
        var previousState = SqliteRepositorySupport.GetNullableString(reader, 3);
        var errorClass = SqliteRepositorySupport.GetNullableString(reader, 5);

        return new HealthEventRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            previousState is null ? null : SqliteRepositorySupport.ParseEnum<HealthState>(previousState),
            SqliteRepositorySupport.ParseEnum<HealthState>(reader.GetString(4)),
            errorClass is null ? null : SqliteRepositorySupport.ParseEnum<HealthErrorClass>(errorClass),
            SqliteRepositorySupport.GetNullableString(reader, 6),
            SqliteRepositorySupport.GetNullableString(reader, 7),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(8)));
    }
}
