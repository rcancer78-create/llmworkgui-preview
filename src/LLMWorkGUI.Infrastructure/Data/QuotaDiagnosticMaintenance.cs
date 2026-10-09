using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Application.Quotas;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Data;

internal static class QuotaDiagnosticMaintenance
{
    public static async Task<int> RedactLegacyPayloadsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='QuotaSnapshots';";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0) { return 0; }

        await using var bound = connection.CreateCommand();
        bound.CommandText = "SELECT MAX(rowid) FROM QuotaSnapshots;";
        var value = await bound.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull) { return 0; }
        var upper = Convert.ToInt64(value);
        long? cursor = null;
        var changed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new List<(long RowId, string Id, string? Payload, string? Bucket)>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = """
                    SELECT rowid, Id, RawRedactedPayloadJson, Bucket FROM QuotaSnapshots
                    WHERE ($cursor IS NULL OR rowid > $cursor) AND rowid <= $upper
                        AND (RawRedactedPayloadJson IS NOT NULL OR Bucket IS NOT NULL)
                    ORDER BY rowid LIMIT 100;
                    """;
                select.Parameters.AddWithValue("$cursor", cursor.HasValue ? cursor.Value : DBNull.Value);
                select.Parameters.AddWithValue("$upper", upper);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((reader.GetInt64(0), reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }
            if (rows.Count == 0) { return changed; }
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clean = QuotaSnapshotPayloadSanitizer.Redact(row.Payload);
                var cleanBucket = row.Bucket is null ? null : QuotaDiagnosticRedactor.Redact(row.Bucket);
                if (string.Equals(row.Payload, clean, StringComparison.Ordinal)
                    && string.Equals(row.Bucket, cleanBucket, StringComparison.Ordinal)) { continue; }
                await using var update = connection.CreateCommand();
                // Do not overwrite a concurrent writer or a deleted/reused row.
                update.CommandText = """
                    UPDATE QuotaSnapshots SET RawRedactedPayloadJson=$clean, Bucket=$cleanBucket
                    WHERE rowid=$rowid AND Id=$id AND RawRedactedPayloadJson IS $old AND Bucket IS $oldBucket;
                    """;
                update.Parameters.AddWithValue("$clean", (object?)clean ?? DBNull.Value);
                update.Parameters.AddWithValue("$cleanBucket", (object?)cleanBucket ?? DBNull.Value);
                update.Parameters.AddWithValue("$rowid", row.RowId);
                update.Parameters.AddWithValue("$id", row.Id);
                update.Parameters.AddWithValue("$old", (object?)row.Payload ?? DBNull.Value);
                update.Parameters.AddWithValue("$oldBucket", (object?)row.Bucket ?? DBNull.Value);
                changed += await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            cursor = rows[^1].RowId;
        }
    }

    public static async Task RedactSnapshotFileAsync(string path, CancellationToken cancellationToken)
    {
        // Work on the isolated snapshot, never the caller's original backup.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // A verified legacy backup may retain WAL mode. Publish a standalone snapshot:
        // normalize even a clean/empty candidate before any sidecars are removed.
        await using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=DELETE;";
            var mode = await journal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(mode as string, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cannot prepare a standalone sanitized database snapshot.");
            }
        }
        var quotaChanges = await RedactLegacyPayloadsAsync(connection, cancellationToken).ConfigureAwait(false);
        var activityChanges = await ActivityDiagnosticMaintenance.RedactLegacyRowsAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (quotaChanges == 0 && activityChanges == 0) { return; }
        await using var vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM;";
        await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
