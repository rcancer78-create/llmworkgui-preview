using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed class SqliteWorkflowVersionRepository : IWorkflowVersionRepository
{
    internal const string SelectColumns = """
        Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType,
        EntrypointsJson, DeclaredRolesJson, BindingsJson, CompatibilityReportJson,
        CreationMetadataJson, CreatedAtUtc, ActivatedAtUtc
        """;

    private const string InsertSql = """
        INSERT INTO WorkflowVersions (
            Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType,
            EntrypointsJson, DeclaredRolesJson, BindingsJson, CompatibilityReportJson,
            CreationMetadataJson, CreatedAtUtc, ActivatedAtUtc
        )
        VALUES (
            $id, $workflowPackageId, $versionNumber, $blobId, $originalHash, $sourceType,
            $entrypointsJson, $declaredRolesJson, $bindingsJson, $compatibilityReportJson,
            $creationMetadataJson, $createdAtUtc, $activatedAtUtc
        )
        ON CONFLICT (Id) DO NOTHING;
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteWorkflowVersionRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task UpsertAsync(WorkflowVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = CreateInsertCommand(connection, transaction, version);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            // An immutable identity may be replayed, but must never silently accept different content.
            command.CommandText = """
                SELECT COUNT(*) FROM WorkflowVersions WHERE Id=$id
                    AND WorkflowPackageId=$workflowPackageId AND VersionNumber=$versionNumber
                    AND BlobId=$blobId AND OriginalHash=$originalHash AND SourceType=$sourceType
                    AND EntrypointsJson IS $entrypointsJson AND DeclaredRolesJson IS $declaredRolesJson
                    AND BindingsJson IS $bindingsJson AND CompatibilityReportJson IS $compatibilityReportJson
                    AND CreationMetadataJson IS $creationMetadataJson AND CreatedAtUtc=$createdAtUtc
                    AND ActivatedAtUtc IS $activatedAtUtc;
                """;
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1)
                throw new InvalidOperationException("The immutable workflow version ID already has different content.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static SqliteCommand CreateInsertCommand(SqliteConnection connection, SqliteTransaction? transaction, WorkflowVersion version)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSql;
        command.Parameters.AddWithValue("$id", version.Id);
        command.Parameters.AddWithValue("$workflowPackageId", version.WorkflowPackageId);
        command.Parameters.AddWithValue("$versionNumber", version.VersionNumber);
        command.Parameters.AddWithValue("$blobId", version.BlobId);
        command.Parameters.AddWithValue("$originalHash", version.OriginalHash);
        command.Parameters.AddWithValue("$sourceType", SqliteRepositorySupport.FormatEnum(version.SourceType));
        SqliteRepositorySupport.AddNullable(command, "$entrypointsJson", version.EntrypointsJson);
        SqliteRepositorySupport.AddNullable(command, "$declaredRolesJson", version.DeclaredRolesJson);
        SqliteRepositorySupport.AddNullable(command, "$bindingsJson", version.BindingsJson);
        SqliteRepositorySupport.AddNullable(command, "$compatibilityReportJson", version.CompatibilityReportJson);
        SqliteRepositorySupport.AddNullable(command, "$creationMetadataJson", version.CreationMetadataJson);
        command.Parameters.AddWithValue("$createdAtUtc", SqliteRepositorySupport.FormatTimestamp(version.CreatedAtUtc));
        SqliteRepositorySupport.AddNullable(command, "$activatedAtUtc", SqliteRepositorySupport.FormatTimestamp(version.ActivatedAtUtc));

        return command;
    }

    public async Task<WorkflowVersion> InsertCandidateAsync(WorkflowVersion candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.SourceType != WorkflowSourceType.SyntheticDraft || candidate.ActivatedAtUtc is not null)
            throw new ArgumentException("Only an inactive synthetic candidate may allocate a new version.", nameof(candidate));
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        WorkflowVersion? existing;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {SelectColumns} FROM WorkflowVersions WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", candidate.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            existing = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadVersion(reader) : null;
        }
        if (existing is not null)
        {
            // The caller's prototype number is not allocated authority. Every immutable content field is.
            if (!SameCandidateContent(existing, candidate))
                throw new InvalidOperationException("The immutable candidate ID already has different content.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }
        int number;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COALESCE(MAX(VersionNumber),0) FROM WorkflowVersions WHERE WorkflowPackageId=$package;";
            command.Parameters.AddWithValue("$package", candidate.WorkflowPackageId);
            number = checked(Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) + 1);
        }
        var allocated = new WorkflowVersion(candidate.Id, candidate.WorkflowPackageId, number, candidate.BlobId,
            candidate.OriginalHash, candidate.SourceType, candidate.EntrypointsJson, candidate.DeclaredRolesJson,
            candidate.BindingsJson, candidate.CompatibilityReportJson, candidate.CreationMetadataJson,
            candidate.CreatedAtUtc, candidate.ActivatedAtUtc);
        await using (var command = CreateInsertCommand(connection, transaction, allocated))
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The candidate insertion did not allocate its immutable identity.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return allocated;
    }

    private static bool SameCandidateContent(WorkflowVersion left, WorkflowVersion right) =>
        left.WorkflowPackageId == right.WorkflowPackageId && left.BlobId == right.BlobId
        && left.OriginalHash == right.OriginalHash && left.SourceType == right.SourceType
        && left.EntrypointsJson == right.EntrypointsJson && left.DeclaredRolesJson == right.DeclaredRolesJson
        && left.BindingsJson == right.BindingsJson && left.CompatibilityReportJson == right.CompatibilityReportJson
        && left.CreationMetadataJson == right.CreationMetadataJson && left.CreatedAtUtc == right.CreatedAtUtc
        && left.ActivatedAtUtc == right.ActivatedAtUtc;

    public async Task<WorkflowVersion?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM WorkflowVersions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadVersion(reader) : null;
    }

    public async Task<WorkflowVersion?> GetByPackageAndVersionAsync(
        string packageId,
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM WorkflowVersions
            WHERE WorkflowPackageId = $packageId AND VersionNumber = $versionNumber;
            """;
        command.Parameters.AddWithValue("$packageId", packageId);
        command.Parameters.AddWithValue("$versionNumber", versionNumber);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadVersion(reader) : null;
    }

    public async Task<IReadOnlyList<WorkflowVersion>> ListByPackageIdAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM WorkflowVersions
            WHERE WorkflowPackageId = $packageId
            ORDER BY VersionNumber, Id;
            """;
        command.Parameters.AddWithValue("$packageId", packageId);

        var versions = new List<WorkflowVersion>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(ReadVersion(reader));
        }

        return versions;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM WorkflowVersions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    internal static WorkflowVersion ReadVersion(SqliteDataReader reader)
    {
        return new WorkflowVersion(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            SqliteRepositorySupport.ParseEnum<WorkflowSourceType>(reader.GetString(5)),
            SqliteRepositorySupport.GetNullableString(reader, 6),
            SqliteRepositorySupport.GetNullableString(reader, 7),
            SqliteRepositorySupport.GetNullableString(reader, 8),
            SqliteRepositorySupport.GetNullableString(reader, 9),
            SqliteRepositorySupport.GetNullableString(reader, 10),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(11)),
            SqliteRepositorySupport.GetNullableTimestamp(reader, 12));
    }
}
