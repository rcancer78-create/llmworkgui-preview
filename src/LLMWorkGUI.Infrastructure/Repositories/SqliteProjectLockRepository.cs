using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteProjectLockRepository : IProjectLockRepository
{
    private const int SqliteConstraintUnique = 2067;

    private const string SelectColumns = """
        Id, ProjectId, CanonicalRootPath, ExecutionId, ApplicationInstanceId,
        ProcessGeneration, AcquiredAtUtc
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteProjectLockRepository(ISqliteConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
    }

    public async Task<ProjectLock?> GetActiveByRootPathAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM ProjectLocks
            WHERE CanonicalRootPath = $canonicalRootPath AND ReleasedAtUtc IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue(
            "$canonicalRootPath",
            NormalizeRootPath(rootPath));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLock(reader) : null;
    }

    public async Task<bool> TryAcquireAsync(
        ProjectLock projectLock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectLock);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProjectLocks
                (Id, ProjectId, CanonicalRootPath, ExecutionId, ApplicationInstanceId,
                 ProcessGeneration, AcquiredAtUtc, ReleasedAtUtc, ReleaseReason)
            VALUES
                ($id, $projectId, $canonicalRootPath, $executionId, $applicationInstanceId,
                 $processGeneration, $acquiredAtUtc, NULL, NULL);
            """;

        command.Parameters.AddWithValue("$id", projectLock.Id);
        command.Parameters.AddWithValue("$projectId", projectLock.ProjectId);
        command.Parameters.AddWithValue(
            "$canonicalRootPath",
            NormalizeRootPath(projectLock.CanonicalRootPath));
        command.Parameters.AddWithValue("$executionId", projectLock.ExecutionId);
        command.Parameters.AddWithValue("$applicationInstanceId", projectLock.ApplicationInstanceId);
        command.Parameters.AddWithValue("$processGeneration", projectLock.ProcessGeneration);
        command.Parameters.AddWithValue(
            "$acquiredAtUtc",
            SqliteRepositorySupport.FormatTimestamp(projectLock.AcquiredAt));

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            return false;
        }
    }

    public async Task<bool> ReleaseAsync(
        string lockId,
        DateTimeOffset releasedAt,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();

        var lockRecord = await ReadActiveLockAsync(connection, transaction, lockId, cancellationToken)
            .ConfigureAwait(false);

        if (lockRecord is null)
        {
            return false;
        }

        var executionState = await ReadExecutionStateAsync(
                connection,
                transaction,
                lockRecord.ExecutionId,
                cancellationToken)
            .ConfigureAwait(false);

        var sessionState = await ReadSessionStateAsync(
                connection,
                transaction,
                lockRecord.ExecutionId,
                cancellationToken)
            .ConfigureAwait(false);

        var projectLock = ProjectLock.Acquire(
            lockRecord.Id,
            lockRecord.ProjectId,
            lockRecord.CanonicalRootPath,
            lockRecord.ExecutionId,
            lockRecord.ApplicationInstanceId,
            lockRecord.ProcessGeneration,
            lockRecord.AcquiredAt);

        projectLock.Release(releasedAt, reason, executionState, sessionState);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ProjectLocks
            SET ReleasedAtUtc = $releasedAtUtc, ReleaseReason = $releaseReason
            WHERE Id = $id AND ReleasedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$releasedAtUtc", SqliteRepositorySupport.FormatTimestamp(releasedAt));
        command.Parameters.AddWithValue("$releaseReason", reason);
        command.Parameters.AddWithValue("$id", lockId);

        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        transaction.Commit();

        return updated > 0;
    }

    private static async Task<ProjectLock?> ReadActiveLockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string lockId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SelectColumns} FROM ProjectLocks
            WHERE Id = $id AND ReleasedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", lockId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLock(reader) : null;
    }

    private static async Task<ExecutionState> ReadExecutionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string executionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT State FROM Executions WHERE Id = $executionId;";
        command.Parameters.AddWithValue("$executionId", executionId);

        var state = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (state is not string value)
        {
            throw new InvalidOperationException(
                $"Project lock references execution '{executionId}' which does not exist.");
        }

        return SqliteRepositorySupport.ParseEnum<ExecutionState>(value);
    }

    private static async Task<SessionState> ReadSessionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string executionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Sessions.State
            FROM Sessions
            INNER JOIN Executions ON Executions.SessionId = Sessions.Id
            WHERE Executions.Id = $executionId;
            """;
        command.Parameters.AddWithValue("$executionId", executionId);

        var state = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (state is not string value)
        {
            throw new InvalidOperationException(
                $"Project lock references execution '{executionId}' without a linked session.");
        }

        return SqliteRepositorySupport.ParseEnum<SessionState>(value);
    }

    private static ProjectLock ReadLock(SqliteDataReader reader)
    {
        return ProjectLock.Acquire(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(6)));
    }

    private static string NormalizeRootPath(string rootPath)
    {
        var canonicalPath = SqliteRepositorySupport.CanonicalizeRootPath(rootPath);

        return OperatingSystem.IsWindows() ? canonicalPath.ToUpperInvariant() : canonicalPath;
    }
}
