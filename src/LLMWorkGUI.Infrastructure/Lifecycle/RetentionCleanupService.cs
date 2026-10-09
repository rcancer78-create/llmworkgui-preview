using LLMWorkGUI.Application.Concurrency;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Lifecycle;

/// <summary>
/// Default retention cleanup of the hardening phase. Only stale diagnostic bundles and abandoned
/// scratch workspaces are removed; old execution events of terminal executions are archived to an
/// append-only JSONL file before they are deleted, while health audit events and every active run stay
/// untouched (ROADMAP Phase 12).
/// </summary>
public sealed class RetentionCleanupService : IRetentionCleanupService
{
    public const string AuditArchiveDirectoryName = "archive";
    public const string AuditArchiveFilePrefix = "audit-";

    private static readonly string[] BundleSidecarExtensions = { ".sha256", ".sha512" };

    private readonly StorageOptions _storageOptions;
    private readonly TimeProvider _timeProvider;
    private readonly IApplicationInstanceGuard? _instanceGuard;
    private readonly ISqliteConnectionFactory? _connectionFactory;

    public RetentionCleanupService(
        StorageOptions storageOptions,
        TimeProvider? timeProvider = null,
        RetentionPolicy? policy = null,
        ISqliteConnectionFactory? connectionFactory = null,
        IApplicationInstanceGuard? instanceGuard = null)
    {
        ArgumentNullException.ThrowIfNull(storageOptions);

        Policy = policy ?? RetentionPolicy.Default;
        Policy.Validate();

        _storageOptions = storageOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _instanceGuard = instanceGuard;
        _connectionFactory = connectionFactory;
    }

    public RetentionPolicy Policy { get; }

    public async Task<RetentionCleanupReport> CleanupAsync(
        CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = _timeProvider.GetUtcNow();
        var dataRoot = ResolveDataRoot(_storageOptions);
        var diagnosticsDirectory = AppDataPaths.GetDiagnosticBundlesDirectory(dataRoot);
        var deletedPaths = new List<string>();
        var warnings = new List<string>();
        var skipped = 0;
        var preserved = 0;

        var deletedBundles = DeleteStaleDiagnosticBundles(
            diagnosticsDirectory,
            Policy.GetDiagnosticBundleCutoff(startedAt),
            deletedPaths,
            ref preserved,
            ref skipped,
            cancellationToken);

        var deletedScratch = DeleteStaleScratchWorkspaces(
            Path.Combine(dataRoot, ScratchWorkspaceManager.ScratchDirectoryName),
            Policy.GetScratchWorkspaceCutoff(startedAt),
            deletedPaths,
            ref preserved,
            ref skipped,
            cancellationToken);

        var archivedAudit = 0;
        var deletedAudit = 0;

        if (_connectionFactory is not null)
        {
            try
            {
                (archivedAudit, deletedAudit) = await ArchiveAndDeleteAuditRecordsAsync(
                    diagnosticsDirectory,
                    Policy.GetAuditRecordCutoff(startedAt),
                    startedAt,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception)
            {
                warnings.Add(
                    "Audit records were not archived because the database is unavailable: "
                    + exception.Message);
            }
        }

        return new RetentionCleanupReport
        {
            StartedAtUtc = startedAt,
            CompletedAtUtc = _timeProvider.GetUtcNow(),
            DeletedDiagnosticBundleCount = deletedBundles,
            DeletedScratchWorkspaceCount = deletedScratch,
            ArchivedAuditRecordCount = archivedAudit,
            DeletedAuditRecordCount = deletedAudit,
            PreservedActiveItemCount = preserved,
            SkippedItemCount = skipped,
            DeletedPaths = deletedPaths,
            Warnings = warnings
        };
    }

    private static int DeleteStaleDiagnosticBundles(
        string diagnosticsDirectory,
        DateTimeOffset cutoff,
        List<string> deletedPaths,
        ref int preserved,
        ref int skipped,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(diagnosticsDirectory))
        {
            return 0;
        }

        var deleted = 0;

        string[] files;
        try
        {
            files = RetentionFileSystem.ReadDirectory(diagnosticsDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            skipped++;
            return 0;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(Path.GetExtension(file), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                RetentionFileSystem.EnsureNoReparsePoints(file);
                if ((File.GetAttributes(file) & FileAttributes.Directory) != 0) continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            if (File.GetLastWriteTimeUtc(file) >= cutoff.UtcDateTime)
            {
                preserved++;
                continue;
            }

            if (TryDeleteFile(file, deletedPaths, ref skipped))
            {
                deleted++;

                foreach (var sidecarExtension in BundleSidecarExtensions)
                {
                    var sidecar = Path.ChangeExtension(file, sidecarExtension);

                    if (File.Exists(sidecar))
                    {
                        TryDeleteFile(sidecar, deletedPaths, ref skipped);
                    }
                }
            }
        }

        return deleted;
    }

    private static int DeleteStaleScratchWorkspaces(
        string scratchRootDirectory,
        DateTimeOffset cutoff,
        List<string> deletedPaths,
        ref int preserved,
        ref int skipped,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(scratchRootDirectory))
        {
            return 0;
        }

        var deleted = 0;

        string[] scopes;
        try
        {
            scopes = RetentionFileSystem.ReadDirectory(scratchRootDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            skipped++;
            return 0;
        }

        foreach (var scopeDirectory in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] workspaces;
            try
            {
                if ((File.GetAttributes(scopeDirectory) & FileAttributes.Directory) == 0) continue;
                workspaces = RetentionFileSystem.ReadDirectory(scopeDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            foreach (var workspaceDirectory in workspaces)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if ((File.GetAttributes(workspaceDirectory) & FileAttributes.Directory) == 0) continue;
                    // Managed adaptation scratch belongs to process-aware recovery, not an age sweep.
                    if (AdaptationScratchOwnership.IsManaged(Path.GetDirectoryName(scratchRootDirectory)!, workspaceDirectory))
                    {
                        preserved++;
                        continue;
                    }
                    if (GetLastActivityUtc(workspaceDirectory, cancellationToken) >= cutoff)
                    {
                        preserved++;
                        continue;
                    }

                    RetentionFileSystem.EnsureNoReparsePoints(workspaceDirectory);
                    Directory.Delete(workspaceDirectory, recursive: true);
                    deletedPaths.Add(workspaceDirectory);
                    deleted++;
                }
                catch (IOException)
                {
                    skipped++;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
        }

        return deleted;
    }

    /// <summary>
    /// Reads stale execution events of terminal executions, writes them to the append-only audit archive
    /// and only deletes them once the archive file is safely on disk. Active executions and the
    /// append-only health audit are never touched.
    /// </summary>
    private async Task<(int Archived, int Deleted)> ArchiveAndDeleteAuditRecordsAsync(
        string diagnosticsDirectory,
        DateTimeOffset cutoff,
        DateTimeOffset nowUtc,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var archivedRows = new List<Dictionary<string, string?>>();
        var archivedIds = new List<string>();

        await using (var connection = await _connectionFactory!
                         .OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            var activeStates = ExecutionStateMachine.NonTerminalStates
                .Select(state => state.ToString())
                .ToArray();

            var placeholders = string.Join(
                ", ",
                activeStates.Select((_, index) => $"$activeState{index}"));

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT e.Id, e.ExecutionId, e.Sequence, e.EventKind,
                       e.NormalizedRedactedPayloadJson, e.RawRedactedPayloadText,
                       e.DataClassification, e.OccurredAtUtc
                FROM ExecutionEvents e
                JOIN Executions x ON x.Id = e.ExecutionId
                WHERE e.OccurredAtUtc < $cutoff
                  AND x.EndedAtUtc IS NOT NULL
                  AND x.State <> 'Ambiguous'
                  AND x.State NOT IN ({placeholders});
                """;
            command.Parameters.AddWithValue("$cutoff", SqliteRepositorySupport.FormatTimestamp(cutoff));

            for (var index = 0; index < activeStates.Length; index++)
            {
                command.Parameters.AddWithValue($"$activeState{index}", activeStates[index]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                archivedRows.Add(ReadRow(reader));
                archivedIds.Add(reader.GetString(0));
            }
        }

        if (archivedRows.Count == 0)
        {
            return (0, 0);
        }

        try
        {
            await WriteAuditArchiveAsync(
                diagnosticsDirectory,
                nowUtc,
                archivedRows,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(
                "The audit archive could not be written, so the stale execution events were kept: "
                + exception.Message);

            return (0, 0);
        }

        var deletedRows = 0;
        await using (var connection = await _connectionFactory!
                         .OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            using var transaction = connection.BeginTransaction();

            foreach (var id in archivedIds)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM ExecutionEvents WHERE Id = $id
                    AND EXISTS (SELECT 1 FROM Executions x WHERE x.Id = ExecutionEvents.ExecutionId
                        AND x.EndedAtUtc IS NOT NULL
                        AND x.State NOT IN ('Ambiguous', 'Queued', 'Starting', 'SessionConfirmed', 'Running', 'WaitingApproval', 'Cancelling'));
                    """;
                delete.Parameters.AddWithValue("$id", id);
                deletedRows += await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }

        return (archivedRows.Count, deletedRows);
    }

    private static Dictionary<string, string?> ReadRow(SqliteDataReader reader)
    {
        var row = new Dictionary<string, string?>(reader.FieldCount, StringComparer.Ordinal);

        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            row[reader.GetName(ordinal)] = reader.IsDBNull(ordinal)
                ? null
                : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        return row;
    }

    private static async Task WriteAuditArchiveAsync(
        string diagnosticsDirectory,
        DateTimeOffset nowUtc,
        IReadOnlyList<Dictionary<string, string?>> rows,
        CancellationToken cancellationToken)
    {
        var archiveDirectory = Path.Combine(diagnosticsDirectory, AuditArchiveDirectoryName);
        RetentionFileSystem.EnsureNoReparsePoints(archiveDirectory);
        Directory.CreateDirectory(archiveDirectory);

        var archivePath = ResolveArchivePath(archiveDirectory, nowUtc);
        RetentionFileSystem.EnsureNoReparsePoints(archivePath);

        await using var stream = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var row in rows)
        {
            var line = JsonSerializer.Serialize(row);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        // Flush the OS file buffers before committing deletion of the only SQL copy.
        stream.Flush(flushToDisk: true);
    }

    private static string ResolveArchivePath(string archiveDirectory, DateTimeOffset nowUtc)
    {
        var stem = AuditArchiveFilePrefix
            + nowUtc.UtcDateTime.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);

        var candidate = Path.Combine(archiveDirectory, stem + ".jsonl");
        var suffix = 1;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(
                archiveDirectory,
                string.Create(CultureInfo.InvariantCulture, $"{stem}-{suffix}.jsonl"));
            suffix++;
        }

        return candidate;
    }

    private static bool TryDeleteFile(string file, List<string> deletedPaths, ref int skipped)
    {
        try
        {
            RetentionFileSystem.EnsureNoReparsePoints(file);
            File.Delete(file);
            deletedPaths.Add(file);

            return true;
        }
        catch (IOException)
        {
            skipped++;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            skipped++;
            return false;
        }
    }

    private static DateTimeOffset GetLastActivityUtc(string directory, CancellationToken cancellationToken)
    {
        RetentionFileSystem.EnsureNoReparsePoints(directory);
        var lastWriteTimeUtc = Directory.GetLastWriteTimeUtc(directory);
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in RetentionFileSystem.ReadDirectory(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RetentionFileSystem.EnsureNoReparsePoints(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) pending.Push(entry);
                var candidate = File.GetLastWriteTimeUtc(entry);
                if (candidate > lastWriteTimeUtc) lastWriteTimeUtc = candidate;
            }
        }

        return new DateTimeOffset(lastWriteTimeUtc, TimeSpan.Zero);
    }

    private static string ResolveDataRoot(StorageOptions options)
    {
        return string.IsNullOrWhiteSpace(options.AppDataDirectory)
            ? AppDataPaths.DefaultRootDirectory
            : Path.GetFullPath(options.AppDataDirectory);
    }
}
