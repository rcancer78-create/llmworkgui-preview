using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Security;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteExecutionRepository : IExecutionRepository
{
    private const string SelectColumns = """
        Id, SessionId, ClientRequestId, State, FailureReason, RequestedRouteId, ObservedRouteId,
        RetryOfExecutionId, ProcessState, ExitCode, TerminationReason, SourceHashBefore, SourceHashAfter,
        CreatedAtUtc, StartedAtUtc, EndedAtUtc,
        (SELECT json_group_array(a.Id ORDER BY a.CreatedAtUtc, a.Id) FROM Artifacts a WHERE a.ExecutionId=Executions.Id)
        """;

    private const string SelectEventColumns = """
        Id, ExecutionId, Sequence, EventKind, NormalizedRedactedPayloadJson, RawRedactedPayloadText,
        DataClassification, OccurredAtUtc
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly SensitiveDataFilter _sensitiveDataFilter;

    public SqliteExecutionRepository(
        ISqliteConnectionFactory connectionFactory,
        SensitiveDataFilter sensitiveDataFilter)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(sensitiveDataFilter);

        _connectionFactory = connectionFactory;
        _sensitiveDataFilter = sensitiveDataFilter;
    }

    public async Task UpsertAsync(Execution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await UpsertAsync(connection, null, execution, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Execution execution, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Executions
                (Id, SessionId, ClientRequestId, State, FailureReason, RequestedRouteId, ObservedRouteId,
                 RetryOfExecutionId, ProcessState, ExitCode, TerminationReason, SourceHashBefore, SourceHashAfter,
                 CreatedAtUtc, StartedAtUtc, EndedAtUtc)
            VALUES
                ($id, $sessionId, $clientRequestId, $state, $failureReason, $requestedRouteId, $observedRouteId,
                 $retryOfExecutionId, $processState, $exitCode, $terminationReason, $sourceHashBefore, $sourceHashAfter,
                 $createdAtUtc, $startedAtUtc, $endedAtUtc)
            ON CONFLICT (Id) DO UPDATE SET
                State = excluded.State,
                FailureReason = excluded.FailureReason,
                ObservedRouteId = excluded.ObservedRouteId,
                RetryOfExecutionId = excluded.RetryOfExecutionId,
                ProcessState = excluded.ProcessState,
                ExitCode = excluded.ExitCode,
                TerminationReason = excluded.TerminationReason,
                SourceHashBefore = excluded.SourceHashBefore,
                SourceHashAfter = excluded.SourceHashAfter,
                StartedAtUtc = excluded.StartedAtUtc,
                EndedAtUtc = excluded.EndedAtUtc;
            """;

        command.Parameters.AddWithValue("$id", execution.Id);
        command.Parameters.AddWithValue("$sessionId", execution.SessionId);
        command.Parameters.AddWithValue("$clientRequestId", execution.ClientRequestId);
        command.Parameters.AddWithValue("$state", SqliteRepositorySupport.FormatEnum(execution.State));
        command.Parameters.AddWithValue(
            "$failureReason",
            SqliteRepositorySupport.FormatEnum(execution.FailureReason));
        command.Parameters.AddWithValue("$requestedRouteId", execution.RequestedRouteId);
        SqliteRepositorySupport.AddNullable(command, "$observedRouteId", execution.ObservedRouteId);
        SqliteRepositorySupport.AddNullable(command, "$retryOfExecutionId", execution.RetryOfExecutionId);
        SqliteRepositorySupport.AddNullable(command, "$processState", execution.ProcessState);
        SqliteRepositorySupport.AddNullable(command, "$exitCode", execution.ExitCode);
        SqliteRepositorySupport.AddNullable(command, "$terminationReason", execution.TerminationReason);
        SqliteRepositorySupport.AddNullable(command, "$sourceHashBefore", execution.SourceHashBefore);
        SqliteRepositorySupport.AddNullable(command, "$sourceHashAfter", execution.SourceHashAfter);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            SqliteRepositorySupport.FormatTimestamp(execution.CreatedAt));
        SqliteRepositorySupport.AddNullable(
            command,
            "$startedAtUtc",
            SqliteRepositorySupport.FormatTimestamp(execution.StartedAt));
        SqliteRepositorySupport.AddNullable(
            command,
            "$endedAtUtc",
            SqliteRepositorySupport.FormatTimestamp(execution.EndedAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Execution?> GetByIdAsync(string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Executions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", executionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadExecution(reader) : null;
    }

    public async Task<IReadOnlyList<Execution>> ListBySessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM Executions
            WHERE SessionId = $sessionId
            ORDER BY CreatedAtUtc, Id;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        var executions = new List<Execution>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            executions.Add(ReadExecution(reader));
        }

        return executions;
    }

    public async Task AppendEventAsync(
        ExecutionEventRecord executionEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ExecutionEvents
                (Id, ExecutionId, Sequence, EventKind, NormalizedRedactedPayloadJson,
                 RawRedactedPayloadText, DataClassification, OccurredAtUtc)
            VALUES
                ($id, $executionId, $sequence, $eventKind, $normalizedRedactedPayloadJson,
                 $rawRedactedPayloadText, $dataClassification, $occurredAtUtc);
            """;

        command.Parameters.AddWithValue("$id", executionEvent.Id);
        command.Parameters.AddWithValue("$executionId", executionEvent.ExecutionId);
        command.Parameters.AddWithValue("$sequence", executionEvent.Sequence);
        command.Parameters.AddWithValue("$eventKind", executionEvent.EventKind);
        SqliteRepositorySupport.AddNullable(
            command,
            "$normalizedRedactedPayloadJson",
            RedactJson(executionEvent.NormalizedRedactedPayloadJson));
        SqliteRepositorySupport.AddNullable(
            command,
            "$rawRedactedPayloadText",
            RedactText(executionEvent.RawRedactedPayloadText));
        SqliteRepositorySupport.AddNullable(
            command,
            "$dataClassification",
            executionEvent.DataClassification is null
                ? null
                : SqliteRepositorySupport.FormatEnum(executionEvent.DataClassification.Value));
        command.Parameters.AddWithValue(
            "$occurredAtUtc",
            SqliteRepositorySupport.FormatTimestamp(executionEvent.OccurredAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExecutionEventRecord>> ListEventsAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectEventColumns} FROM ExecutionEvents
            WHERE ExecutionId = $executionId
            ORDER BY Sequence;
            """;
        command.Parameters.AddWithValue("$executionId", executionId);

        var executionEvents = new List<ExecutionEventRecord>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            executionEvents.Add(ReadEvent(reader));
        }

        return executionEvents;
    }

    private string? RedactText(string? value)
    {
        return value is null ? null : _sensitiveDataFilter.Redact(value);
    }

    private string? RedactJson(string? value)
    {
        return value is null ? null : _sensitiveDataFilter.RedactJson(value);
    }

    private static Execution ReadExecution(SqliteDataReader reader)
    {
        return new Execution(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            SqliteRepositorySupport.ParseEnum<ExecutionState>(reader.GetString(3)),
            SqliteRepositorySupport.ParseEnum<ExecutionFailureReason>(reader.GetString(4)),
            reader.GetString(5),
            SqliteRepositorySupport.GetNullableString(reader, 6),
            SqliteRepositorySupport.GetNullableString(reader, 7),
            SqliteRepositorySupport.GetNullableString(reader, 8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            SqliteRepositorySupport.GetNullableString(reader, 10),
            JsonSerializer.Deserialize<string[]>(reader.GetString(16)) ?? [],
            SqliteRepositorySupport.GetNullableString(reader, 11),
            SqliteRepositorySupport.GetNullableString(reader, 12),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(13)),
            SqliteRepositorySupport.GetNullableTimestamp(reader, 14),
            SqliteRepositorySupport.GetNullableTimestamp(reader, 15));
    }

    private static ExecutionEventRecord ReadEvent(SqliteDataReader reader)
    {
        var dataClassification = SqliteRepositorySupport.GetNullableString(reader, 6);

        return new ExecutionEventRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            SqliteRepositorySupport.GetNullableString(reader, 4),
            SqliteRepositorySupport.GetNullableString(reader, 5),
            dataClassification is null
                ? null
                : SqliteRepositorySupport.ParseEnum<DataClassification>(dataClassification),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(7)));
    }
}
