using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteWorkflowBindingRepository : IWorkflowBindingRepository
{
    private const string SelectColumns = """
        Id, ProjectId, WorkflowPackageId, ActiveVersionId, RoutePolicyId, CreatedAtUtc, UpdatedAtUtc
        """;

    private const string InsertSql = """
        INSERT INTO WorkflowBindings (
            Id, ProjectId, WorkflowPackageId, ActiveVersionId, RoutePolicyId, CreatedAtUtc, UpdatedAtUtc
        )
        VALUES (
            $id, $projectId, $workflowPackageId, $activeVersionId, $routePolicyId, $createdAtUtc, $updatedAtUtc
        )
        ON CONFLICT (ProjectId, WorkflowPackageId) DO UPDATE SET
            ActiveVersionId = excluded.ActiveVersionId,
            RoutePolicyId = excluded.RoutePolicyId,
            UpdatedAtUtc = excluded.UpdatedAtUtc;
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteWorkflowBindingRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task UpsertAsync(WorkflowBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = InsertSql;
        command.Parameters.AddWithValue("$id", binding.Id);
        command.Parameters.AddWithValue("$projectId", binding.ProjectId);
        command.Parameters.AddWithValue("$workflowPackageId", binding.WorkflowPackageId);
        command.Parameters.AddWithValue("$activeVersionId", binding.ActiveVersionId);
        SqliteRepositorySupport.AddNullable(command, "$routePolicyId", binding.RoutePolicyId);
        command.Parameters.AddWithValue("$createdAtUtc", SqliteRepositorySupport.FormatTimestamp(binding.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", SqliteRepositorySupport.FormatTimestamp(binding.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowBinding?> TrySetActiveVersionAsync(WorkflowBinding expected, string activeVersionId,
        DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeVersionId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        WorkflowBinding? result;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE WorkflowBindings SET ActiveVersionId=$target,UpdatedAtUtc=$updated
                WHERE Id=$id AND ProjectId=$project AND WorkflowPackageId=$package AND ActiveVersionId=$expected
                    AND EXISTS(SELECT 1 FROM WorkflowVersions WHERE Id=$target AND WorkflowPackageId=$package)
                RETURNING {SelectColumns};
                """;
            command.Parameters.AddWithValue("$target", activeVersionId);
            command.Parameters.AddWithValue("$updated", SqliteRepositorySupport.FormatTimestamp(updatedAtUtc));
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$project", expected.ProjectId);
            command.Parameters.AddWithValue("$package", expected.WorkflowPackageId);
            command.Parameters.AddWithValue("$expected", expected.ActiveVersionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBinding(reader) : null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<WorkflowBinding?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM WorkflowBindings WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBinding(reader) : null;
    }

    public async Task<WorkflowBinding?> GetByProjectAndPackageAsync(
        string projectId,
        string workflowPackageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowPackageId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM WorkflowBindings
            WHERE ProjectId = $projectId AND WorkflowPackageId = $workflowPackageId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$workflowPackageId", workflowPackageId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBinding(reader) : null;
    }

    public async Task<IReadOnlyList<WorkflowBinding>> ListByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM WorkflowBindings
            WHERE ProjectId = $projectId
            ORDER BY CreatedAtUtc, Id;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);

        var bindings = new List<WorkflowBinding>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            bindings.Add(ReadBinding(reader));
        }

        return bindings;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM WorkflowBindings WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    private static WorkflowBinding ReadBinding(SqliteDataReader reader)
    {
        return new WorkflowBinding(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            SqliteRepositorySupport.GetNullableString(reader, 4),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(5)),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(6)));
    }
}
