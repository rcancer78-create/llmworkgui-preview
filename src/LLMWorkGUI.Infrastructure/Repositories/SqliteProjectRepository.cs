using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteProjectRepository : IProjectRepository
{
    internal const string SelectColumns = """
        Id, DisplayName, RootPath, GitBranch, IsDirty, HasRequiredInstructions,
        DefaultWorkflowId, DefaultRoutePolicyId, DataClassification
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteProjectRepository(ISqliteConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
    }

    public async Task UpsertAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var timestamp = SqliteRepositorySupport.FormatTimestamp(DateTimeOffset.UtcNow);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Projects
                (Id, DisplayName, RootPath, GitBranch, IsDirty, HasRequiredInstructions,
                 DefaultWorkflowId, DefaultRoutePolicyId, DataClassification, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ($id, $displayName, $rootPath, $gitBranch, $isDirty, $hasRequiredInstructions,
                 $defaultWorkflowId, $defaultRoutePolicyId, $dataClassification, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT (Id) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                RootPath = excluded.RootPath,
                GitBranch = excluded.GitBranch,
                IsDirty = excluded.IsDirty,
                HasRequiredInstructions = excluded.HasRequiredInstructions,
                DefaultWorkflowId = excluded.DefaultWorkflowId,
                DefaultRoutePolicyId = excluded.DefaultRoutePolicyId,
                DataClassification = excluded.DataClassification,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;

        command.Parameters.AddWithValue("$id", project.Id);
        command.Parameters.AddWithValue("$displayName", project.DisplayName);
        command.Parameters.AddWithValue(
            "$rootPath",
            SqliteRepositorySupport.CanonicalizeRootPath(project.RootPath));
        SqliteRepositorySupport.AddNullable(command, "$gitBranch", project.GitBranch);
        command.Parameters.AddWithValue("$isDirty", project.IsDirty);
        command.Parameters.AddWithValue("$hasRequiredInstructions", project.HasRequiredInstructions);
        SqliteRepositorySupport.AddNullable(command, "$defaultWorkflowId", project.DefaultWorkflowId);
        SqliteRepositorySupport.AddNullable(command, "$defaultRoutePolicyId", project.DefaultRoutePolicyId);
        command.Parameters.AddWithValue(
            "$dataClassification",
            SqliteRepositorySupport.FormatEnum(project.DataClassification));
        command.Parameters.AddWithValue("$createdAtUtc", timestamp);
        command.Parameters.AddWithValue("$updatedAtUtc", timestamp);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Project?> GetByIdAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Projects WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", projectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Projects WHERE RootPath = $rootPath;";
        command.Parameters.AddWithValue(
            "$rootPath",
            SqliteRepositorySupport.CanonicalizeRootPath(rootPath));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Projects ORDER BY DisplayName, Id;";

        var projects = new List<Project>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projects.Add(Read(reader));
        }

        return projects;
    }

    public async Task<bool> DeleteAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Projects WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", projectId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    internal static Project Read(SqliteDataReader reader)
    {
        return new Project(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            SqliteRepositorySupport.GetNullableString(reader, 3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            SqliteRepositorySupport.GetNullableString(reader, 6),
            SqliteRepositorySupport.GetNullableString(reader, 7),
            SqliteRepositorySupport.ParseEnum<DataClassification>(reader.GetString(8)));
    }
}
