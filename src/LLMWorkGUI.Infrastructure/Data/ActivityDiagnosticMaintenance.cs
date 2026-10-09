using LLMWorkGUI.Application.Security;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Data;

/// <summary>
/// Repairs legacy operator-facing fields and their logical FTS projection in bounded transactions.
/// This is not secure erasure of old database pages, WAL history or existing backup files.
/// </summary>
internal static class ActivityDiagnosticMaintenance
{
    public static async Task<int> RedactLegacyRowsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ActivityEvents';";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0) { return 0; }
        await using var searchExists = connection.CreateCommand();
        searchExists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ActivityEventsSearch';";
        var hasSearch = Convert.ToInt64(await searchExists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
        await using var bound = connection.CreateCommand();
        bound.CommandText = "SELECT MAX(rowid) FROM ActivityEvents;";
        var value = await bound.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull) { return 0; }
        var upper = Convert.ToInt64(value);
        long? cursor = null;
        var changed = 0;
        var redactor = new CredentialTextRedactor();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Obtain the write reservation before reading this page. A concurrent writer cannot
            // change its fields/index between our read and update. Never hold the entire history.
            using var transaction = connection.BeginTransaction();
            var rows = new List<(long RowId, string[] Projection, string? Search)>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                var search = hasSearch ? "(SELECT body FROM ActivityEventsSearch WHERE rowid=e.rowid)" : "NULL";
                select.CommandText = $"""
                    SELECT e.rowid, Id, TitleRedacted, DescriptionRedacted, Role, Kind, State, Source,
                        SessionId, ExecutionId, RouteId, ArtifactName, {search}
                    FROM ActivityEvents e
                    WHERE ($cursor IS NULL OR e.rowid > $cursor) AND e.rowid <= $upper
                    ORDER BY e.rowid LIMIT 100;
                    """;
                select.Parameters.AddWithValue("$cursor", cursor.HasValue ? cursor.Value : DBNull.Value);
                select.Parameters.AddWithValue("$upper", upper);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Same field order as ActivityEventSearchText.Build and migration 011.
                    var projection = new string[11];
                    for (var index = 0; index < projection.Length; index++)
                    { projection[index] = reader.IsDBNull(index + 1) ? string.Empty : reader.GetString(index + 1); }
                    rows.Add((reader.GetInt64(0), projection, reader.IsDBNull(12) ? null : reader.GetString(12)));
                }
            }
            if (rows.Count == 0) { transaction.Commit(); return changed; }
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var title = redactor.RedactDiagnostic(row.Projection[1]);
                var description = redactor.RedactDiagnostic(row.Projection[2]);
                var textChanged = title != row.Projection[1] || description != row.Projection[2];
                row.Projection[1] = title;
                row.Projection[2] = description;
                var body = string.Join(' ', row.Projection);
                var searchChanged = hasSearch && !string.Equals(body, row.Search, StringComparison.Ordinal);
                if (!textChanged && !searchChanged) { continue; }
                if (textChanged)
                {
                    await using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE ActivityEvents SET TitleRedacted=$title, DescriptionRedacted=$description WHERE rowid=$rowid;";
                    update.Parameters.AddWithValue("$title", title);
                    update.Parameters.AddWithValue("$description", description);
                    update.Parameters.AddWithValue("$rowid", row.RowId);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                if (searchChanged)
                {
                    await using var updateSearch = connection.CreateCommand();
                    updateSearch.Transaction = transaction;
                    updateSearch.CommandText = "DELETE FROM ActivityEventsSearch WHERE rowid=$rowid; INSERT INTO ActivityEventsSearch(rowid,body) VALUES($rowid,$body);";
                    updateSearch.Parameters.AddWithValue("$rowid", row.RowId);
                    updateSearch.Parameters.AddWithValue("$body", body);
                    await updateSearch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                changed++;
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            cursor = rows[^1].RowId;
        }
    }
}
