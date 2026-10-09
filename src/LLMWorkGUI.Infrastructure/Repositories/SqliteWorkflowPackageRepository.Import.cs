using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed partial class SqliteWorkflowPackageRepository
{
    public async Task<WorkflowImportResult> CommitImportAsync(WorkflowPackage package, WorkflowVersion firstVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(firstVersion);
        if (firstVersion.WorkflowPackageId != package.Id || firstVersion.VersionNumber != 1
            || firstVersion.BlobId != package.OriginalBlobId || firstVersion.OriginalHash != package.OriginalHash)
            throw new ArgumentException("The first version must belong to the imported package and original blob.");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Reserve the write before checking the hash: simultaneous imports share one winning package.
        using var transaction = connection.BeginTransaction(deferred: false);
        WorkflowPackage? existing;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = $"SELECT {SelectColumns} FROM WorkflowPackages WHERE OriginalHash=$hash ORDER BY CreatedAtUtc, Id LIMIT 1;";
            lookup.Parameters.AddWithValue("$hash", package.OriginalHash);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            existing = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPackage(reader) : null;
        }
        var target = existing ?? package;
        WorkflowVersion? version = null;
        if (existing is null)
        {
            await using var insert = CreateInsertCommand(connection, transaction, package);
            // Import creates a new immutable identity; do not update an unrelated ID on collision.
            insert.CommandText = InsertSql[..InsertSql.IndexOf("ON CONFLICT", StringComparison.Ordinal)];
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = $"SELECT {SqliteWorkflowVersionRepository.SelectColumns} FROM WorkflowVersions WHERE WorkflowPackageId=$id AND VersionNumber=1;";
            lookup.Parameters.AddWithValue("$id", target.Id);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                version = SqliteWorkflowVersionRepository.ReadVersion(reader);
        }
        if (version is null)
        {
            version = existing is null ? firstVersion : new WorkflowVersion(firstVersion.Id, target.Id, 1,
                firstVersion.BlobId, firstVersion.OriginalHash, target.SourceType, firstVersion.EntrypointsJson,
                firstVersion.DeclaredRolesJson, firstVersion.BindingsJson, firstVersion.CompatibilityReportJson,
                firstVersion.CreationMetadataJson, firstVersion.CreatedAtUtc, firstVersion.ActivatedAtUtc);
            await using var insert = SqliteWorkflowVersionRepository.CreateInsertCommand(connection, transaction, version);
            insert.CommandText = insert.CommandText[..insert.CommandText.IndexOf("ON CONFLICT", StringComparison.Ordinal)];
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkflowImportResult(target, version, package.OriginalBlobId, IsDuplicate: existing is not null);
    }
}
