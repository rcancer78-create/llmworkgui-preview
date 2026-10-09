using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteSessionRepository : ISessionRepository
{
    private const string SelectColumns = """
        Id, ProjectId, Backend, ProviderProfileId, AccountId, ModelId,
        ReasoningEffort, SpeedMode, ExecutionMode, WorkspaceRootPath, NativeSessionId,
        State, ReconciliationOutcome, CloseReason, ContinuationOfSessionId, ForkedFromSessionId,
        WorkflowRunId, Role, ActiveExecutionId, CreatedAtUtc, LastEventAtUtc
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteSessionRepository(ISqliteConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
    }

    public async Task UpsertAsync(Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await UpsertAsync(connection, null, session, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Session session, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Sessions
                (Id, ProjectId, Backend, ProviderProfileId, AccountId, ModelId,
                 ReasoningEffort, SpeedMode, ExecutionMode, WorkspaceRootPath, NativeSessionId,
                 State, ReconciliationOutcome, CloseReason, ContinuationOfSessionId, ForkedFromSessionId,
                 WorkflowRunId, Role, ActiveExecutionId, CreatedAtUtc, LastEventAtUtc)
            VALUES
                ($id, $projectId, $backend, $providerProfileId, $accountId, $modelId,
                 $reasoningEffort, $speedMode, $executionMode, $workspaceRootPath, $nativeSessionId,
                 $state, $reconciliationOutcome, $closeReason, $continuationOfSessionId, $forkedFromSessionId,
                 $workflowRunId, $role, $activeExecutionId, $createdAtUtc, $lastEventAtUtc)
            ON CONFLICT (Id) DO UPDATE SET
                ProjectId = excluded.ProjectId,
                Backend = excluded.Backend,
                ProviderProfileId = excluded.ProviderProfileId,
                AccountId = excluded.AccountId,
                ModelId = excluded.ModelId,
                ReasoningEffort = excluded.ReasoningEffort,
                SpeedMode = excluded.SpeedMode,
                ExecutionMode = excluded.ExecutionMode,
                WorkspaceRootPath = excluded.WorkspaceRootPath,
                NativeSessionId = excluded.NativeSessionId,
                State = excluded.State,
                ReconciliationOutcome = excluded.ReconciliationOutcome,
                CloseReason = excluded.CloseReason,
                ContinuationOfSessionId = excluded.ContinuationOfSessionId,
                ForkedFromSessionId = excluded.ForkedFromSessionId,
                WorkflowRunId = excluded.WorkflowRunId,
                Role = excluded.Role,
                ActiveExecutionId = excluded.ActiveExecutionId,
                LastEventAtUtc = excluded.LastEventAtUtc;
            """;

        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$projectId", session.ProjectId);
        command.Parameters.AddWithValue("$backend", SqliteRepositorySupport.FormatEnum(session.Binding.Backend));
        command.Parameters.AddWithValue("$providerProfileId", session.Binding.ProviderProfileId);
        command.Parameters.AddWithValue("$accountId", session.Binding.AccountId);
        command.Parameters.AddWithValue("$modelId", session.Binding.ModelId);
        SqliteRepositorySupport.AddNullable(command, "$reasoningEffort", session.Binding.ReasoningEffort);
        SqliteRepositorySupport.AddNullable(command, "$speedMode", session.Binding.SpeedMode);
        SqliteRepositorySupport.AddNullable(command, "$executionMode", session.Binding.ExecutionMode);
        command.Parameters.AddWithValue("$workspaceRootPath", session.WorkspaceRootPath);
        SqliteRepositorySupport.AddNullable(command, "$nativeSessionId", session.NativeSessionId);
        command.Parameters.AddWithValue("$state", SqliteRepositorySupport.FormatEnum(session.State));
        command.Parameters.AddWithValue(
            "$reconciliationOutcome",
            SqliteRepositorySupport.FormatEnum(session.ReconciliationOutcome));
        command.Parameters.AddWithValue("$closeReason", SqliteRepositorySupport.FormatEnum(session.CloseReason));
        SqliteRepositorySupport.AddNullable(command, "$continuationOfSessionId", session.ContinuationOfSessionId);
        SqliteRepositorySupport.AddNullable(command, "$forkedFromSessionId", session.ForkedFromSessionId);
        SqliteRepositorySupport.AddNullable(command, "$workflowRunId", session.WorkflowRunId);
        SqliteRepositorySupport.AddNullable(command, "$role", session.Role);
        SqliteRepositorySupport.AddNullable(command, "$activeExecutionId", session.ActiveExecutionId);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            SqliteRepositorySupport.FormatTimestamp(session.CreatedAt));
        command.Parameters.AddWithValue(
            "$lastEventAtUtc",
            SqliteRepositorySupport.FormatTimestamp(session.LastEventAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Session?> GetByIdAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Sessions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Session>> ListByProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM Sessions
            WHERE ProjectId = $projectId
            ORDER BY CreatedAtUtc, Id;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);

        var sessions = new List<Session>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(Read(reader));
        }

        return sessions;
    }

    public async Task<IReadOnlyList<Session>> ListByAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // An account spans projects, so this deliberately does not filter by project. Ordering is
        // newest-activity-first because that is what an operator triaging an incident needs.
        command.CommandText = $"""
            SELECT {SelectColumns} FROM Sessions
            WHERE AccountId = $accountId
            ORDER BY LastEventAtUtc DESC, Id;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);

        var sessions = new List<Session>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(Read(reader));
        }

        return sessions;
    }

    public async Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Sessions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static Session Read(SqliteDataReader reader)
    {
        var binding = new SessionBinding(
            SqliteRepositorySupport.ParseEnum<BackendType>(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            SqliteRepositorySupport.GetNullableString(reader, 6),
            SqliteRepositorySupport.GetNullableString(reader, 7),
            SqliteRepositorySupport.GetNullableString(reader, 8));

        return new Session(
            reader.GetString(0),
            binding,
            reader.GetString(1),
            reader.GetString(9),
            SqliteRepositorySupport.GetNullableString(reader, 10),
            SqliteRepositorySupport.ParseEnum<SessionState>(reader.GetString(11)),
            SqliteRepositorySupport.ParseEnum<ReconciliationOutcome>(reader.GetString(12)),
            SqliteRepositorySupport.ParseEnum<CloseReason>(reader.GetString(13)),
            SqliteRepositorySupport.GetNullableString(reader, 14),
            SqliteRepositorySupport.GetNullableString(reader, 15),
            SqliteRepositorySupport.GetNullableString(reader, 16),
            SqliteRepositorySupport.GetNullableString(reader, 17),
            SqliteRepositorySupport.GetNullableString(reader, 18),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(19)),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(20)));
    }
}
