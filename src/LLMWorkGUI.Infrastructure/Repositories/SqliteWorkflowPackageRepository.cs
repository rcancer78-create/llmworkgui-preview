using System.Text.Json;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed partial class SqliteWorkflowPackageRepository : IWorkflowPackageRepository, IWorkflowImportStore
{
    private const string SelectColumns = """
        Id, Name, Description, TagsJson, SourceType, OriginalHash, OriginalBlobId,
        CreatedAtUtc, UpdatedAtUtc
        """;

    private const string InsertSql = """
        INSERT INTO WorkflowPackages (
            Id, Name, Description, TagsJson, SourceType, OriginalHash, OriginalBlobId,
            CreatedAtUtc, UpdatedAtUtc
        )
        VALUES (
            $id, $name, $description, $tagsJson, $sourceType, $originalHash, $originalBlobId,
            $createdAtUtc, $updatedAtUtc
        )
        ON CONFLICT (Id) DO UPDATE SET
            Name = excluded.Name,
            Description = excluded.Description,
            TagsJson = excluded.TagsJson,
            SourceType = excluded.SourceType,
            OriginalHash = excluded.OriginalHash,
            OriginalBlobId = excluded.OriginalBlobId,
            UpdatedAtUtc = excluded.UpdatedAtUtc;
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteWorkflowPackageRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task UpsertAsync(WorkflowPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = CreateInsertCommand(connection, null, package);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static SqliteCommand CreateInsertCommand(SqliteConnection connection, SqliteTransaction? transaction, WorkflowPackage package)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSql;
        command.Parameters.AddWithValue("$id", package.Id);
        command.Parameters.AddWithValue("$name", package.Name);
        SqliteRepositorySupport.AddNullable(command, "$description", package.Description);
        command.Parameters.AddWithValue("$tagsJson", SerializeTags(package.Tags));
        command.Parameters.AddWithValue("$sourceType", SqliteRepositorySupport.FormatEnum(package.SourceType));
        command.Parameters.AddWithValue("$originalHash", package.OriginalHash);
        command.Parameters.AddWithValue("$originalBlobId", package.OriginalBlobId);
        command.Parameters.AddWithValue("$createdAtUtc", SqliteRepositorySupport.FormatTimestamp(package.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", SqliteRepositorySupport.FormatTimestamp(package.UpdatedAtUtc));

        return command;
    }

    public async Task<WorkflowPackage?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM WorkflowPackages WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPackage(reader) : null;
    }

    public async Task<WorkflowPackage?> GetByOriginalHashAsync(
        string originalHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalHash);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM WorkflowPackages WHERE OriginalHash = $originalHash;";
        command.Parameters.AddWithValue("$originalHash", originalHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPackage(reader) : null;
    }

    public async Task<IReadOnlyList<WorkflowPackage>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM WorkflowPackages ORDER BY CreatedAtUtc, Id;";

        var packages = new List<WorkflowPackage>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            packages.Add(ReadPackage(reader));
        }

        return packages;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM WorkflowPackages WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    private static string SerializeTags(IReadOnlyList<string> tags)
    {
        return JsonSerializer.Serialize(tags);
    }

    private static IReadOnlyList<string> DeserializeTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<string>();
        }

        var tags = JsonSerializer.Deserialize<string[]>(tagsJson);

        return tags is null or { Length: 0 } ? Array.Empty<string>() : tags;
    }

    private static WorkflowPackage ReadPackage(SqliteDataReader reader)
    {
        return new WorkflowPackage(
            reader.GetString(0),
            reader.GetString(1),
            SqliteRepositorySupport.GetNullableString(reader, 2),
            DeserializeTags(SqliteRepositorySupport.GetNullableString(reader, 3)),
            SqliteRepositorySupport.ParseEnum<WorkflowSourceType>(reader.GetString(4)),
            reader.GetString(5),
            reader.GetString(6),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(7)),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(8)));
    }
}
