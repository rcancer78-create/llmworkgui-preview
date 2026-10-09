using LLMWorkGUI.Application.Concurrency;
using System.Globalization;
using System.Security.Cryptography;
using LLMWorkGUI.Application.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Data;

/// <summary>
/// Consistent SQLite online backup, checksum/integrity verification and safe restore. The snapshot is
/// produced with <c>VACUUM INTO</c>, which the live WAL database can serve without stopping writers;
/// a restore keeps a rollback snapshot and automatically falls back to it when the restored file
/// fails <c>PRAGMA integrity_check</c> (ROADMAP Phase 12, ТЗ §9.4).
/// </summary>
public sealed class DatabaseBackupService : IDatabaseBackupService
{
    private const string MigrationTableName = "_schema_migrations";

    private const string ChecksumExtension = ".sha256";

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IApplicationInstanceGuard? _instanceGuard;

    public DatabaseBackupService(ISqliteConnectionFactory connectionFactory, TimeProvider? timeProvider = null,
        IApplicationInstanceGuard? instanceGuard = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _instanceGuard = instanceGuard;
    }

    public string DatabasePath => _connectionFactory.DatabasePath;

    public async Task<DatabaseBackupResult> CreateBackupAsync(
        string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = Path.GetFullPath(_connectionFactory.DatabasePath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"The active database '{sourcePath}' does not exist.", sourcePath);
        }

        var createdAt = _timeProvider.GetUtcNow();
        var backupPath = Path.GetFullPath(destinationPath ?? BuildDefaultBackupPath(sourcePath, createdAt));

        if (File.Exists(backupPath))
        {
            throw new IOException($"The backup destination '{backupPath}' already exists.");
        }

        var directory = Path.GetDirectoryName(backupPath);

        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The backup destination must be an absolute path.");
        }

        Directory.CreateDirectory(directory);

        await VacuumIntoAsync(backupPath, cancellationToken).ConfigureAwait(false);
        await QuotaDiagnosticMaintenance.RedactSnapshotFileAsync(backupPath, cancellationToken).ConfigureAwait(false);

        var sha256 = ComputeFileHash(backupPath);
        var checksumPath = backupPath + ChecksumExtension;

        await File.WriteAllTextAsync(checksumPath, sha256, cancellationToken).ConfigureAwait(false);

        var integrity = await VerifyIntegrityAsync(backupPath, cancellationToken).ConfigureAwait(false);
        var sizeBytes = new FileInfo(backupPath).Length;

        return new DatabaseBackupResult(
            sourcePath,
            backupPath,
            checksumPath,
            sizeBytes,
            sha256,
            integrity.IsHealthy,
            integrity.SchemaVersion,
            createdAt,
            integrity.Messages);
    }

    public async Task<DatabaseIntegrityReport> VerifyIntegrityAsync(
        string? databasePath = null,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(databasePath ?? _connectionFactory.DatabasePath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The database file '{path}' does not exist.", path);
        }

        var checkedAt = _timeProvider.GetUtcNow();
        var messages = new List<string>();
        var schemaVersion = 0;

        try
        {
            await using var connection = new SqliteConnection(BuildReadOnlyConnectionString(path));

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    messages.Add(reader.GetString(0));
                }
            }

            if (messages.Count == 0)
            {
                messages.Add("PRAGMA integrity_check returned no result rows.");
            }

            schemaVersion = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            messages.Add(exception.Message);
        }

        var sha256 = ComputeFileHash(path);
        var expectedSha256 = TryReadChecksum(path);
        var checksumMatches = expectedSha256 is null
            ? (bool?)null
            : string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);

        var isHealthy = messages.Count == 1
            && string.Equals(messages[0], "ok", StringComparison.OrdinalIgnoreCase);

        return new DatabaseIntegrityReport(
            path,
            isHealthy,
            sha256,
            expectedSha256,
            checksumMatches,
            schemaVersion,
            new FileInfo(path).Length,
            checkedAt,
            messages);
    }

    /// <summary>
    /// Verifies the owner's backup before sanitizing its staged copy. The returned hash belongs
    /// to the published database and may differ after legacy redaction or journal normalization.
    /// </summary>
    public async Task<DatabaseRestoreResult> RestoreAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullBackupPath = Path.GetFullPath(backupPath);
        var databasePath = Path.GetFullPath(_connectionFactory.DatabasePath);
        var stagePath = databasePath + "." + Guid.NewGuid().ToString("N") + ".restore";
        var restoredAt = _timeProvider.GetUtcNow();

        if (!File.Exists(fullBackupPath))
        {
            throw new FileNotFoundException($"The backup '{fullBackupPath}' does not exist.", fullBackupPath);
        }

        var expectedSha256 = TryReadChecksum(fullBackupPath)
            ?? throw new InvalidDataException(
                $"The backup '{fullBackupPath}' has no SHA-256 sidecar; the restore is refused.");

        try
        {
            // Verify exactly the bytes that will be published. The caller's backup path may
            // change after this copy without changing the accepted restore candidate.
            await CopyDatabaseAsync(fullBackupPath, stagePath, cancellationToken).ConfigureAwait(false);
            var backupSha256 = ComputeFileHash(stagePath);
            if (!string.Equals(backupSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The backup checksum does not match its sidecar: expected {expectedSha256}, observed {backupSha256}.");
            }

            var backupIntegrity = await VerifyIntegrityAsync(stagePath, cancellationToken).ConfigureAwait(false);
            if (!backupIntegrity.IsHealthy)
            {
                throw new InvalidDataException(
                    "The backup failed the integrity check and cannot be restored: "
                    + string.Join("; ", backupIntegrity.Messages));
            }

            // The source checksum has already been verified. Sanitize only our staged candidate.
            await QuotaDiagnosticMaintenance.RedactSnapshotFileAsync(stagePath, cancellationToken).ConfigureAwait(false);

            ClearDatabasePool();
            string? rollbackPath = null;
            if (File.Exists(databasePath))
            {
                rollbackPath = BuildRollbackPath(databasePath, restoredAt);
                await VacuumIntoAsync(rollbackPath, cancellationToken).ConfigureAwait(false);
                await QuotaDiagnosticMaintenance.RedactSnapshotFileAsync(rollbackPath, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(rollbackPath + ChecksumExtension,
                    ComputeFileHash(rollbackPath), cancellationToken).ConfigureAwait(false);
            }

            PublishDatabase(stagePath, databasePath, cancellationToken);

            // Cancellation is accepted up to publication. After replacing the database we
            // must finish validation or compensation, even if the caller has gone away.
            DatabaseIntegrityReport restored;
            try
            {
                restored = await VerifyIntegrityAsync(databasePath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await RollBackAsync(databasePath, rollbackPath, new[] { exception.Message },
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (!restored.IsHealthy)
            {
                return await RollBackAsync(databasePath, rollbackPath, restored.Messages,
                    CancellationToken.None).ConfigureAwait(false);
            }

            return new DatabaseRestoreResult(databasePath, fullBackupPath, rollbackPath,
                restored.Sha256, restored.SchemaVersion, restoredAt);
        }
        finally
        {
            DeleteFile(stagePath);
            DeleteSidecarFiles(stagePath);
        }
    }

    private async Task<DatabaseRestoreResult> RollBackAsync(
        string databasePath,
        string? rollbackPath,
        IReadOnlyList<string> failureMessages,
        CancellationToken cancellationToken)
    {
        if (rollbackPath is null || !File.Exists(rollbackPath))
        {
            DeleteFile(databasePath);
            DeleteSidecarFiles(databasePath);

            throw new InvalidDataException(
                $"The restored database failed the integrity check and no previous database existed to "
                + $"roll back to: {string.Join("; ", failureMessages)}");
        }

        await ReplaceDatabaseAsync(rollbackPath, databasePath, cancellationToken).ConfigureAwait(false);

        var rollback = await VerifyIntegrityAsync(databasePath, cancellationToken).ConfigureAwait(false);

        if (!rollback.IsHealthy)
        {
            throw new InvalidDataException(
                $"The restore failed and the rollback snapshot also failed the integrity check: "
                + $"{string.Join("; ", failureMessages)}");
        }

        throw new InvalidDataException(
            $"The restored database failed the integrity check; the previous database was restored from "
            + $"'{rollbackPath}': {string.Join("; ", failureMessages)}");
    }

    private async Task ReplaceDatabaseAsync(
        string sourcePath,
        string databasePath,
        CancellationToken cancellationToken)
    {
        var tempPath = databasePath + "." + Guid.NewGuid().ToString("N") + ".restore";

        try
        {
            await CopyDatabaseAsync(sourcePath, tempPath, cancellationToken).ConfigureAwait(false);
            PublishDatabase(tempPath, databasePath, cancellationToken);
        }
        finally
        {
            DeleteFile(tempPath);
        }
    }

    private static async Task CopyDatabaseAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, bufferSize: 81920, useAsync: true);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PublishDatabase(string stagePath, string databasePath, CancellationToken cancellationToken)
    {
        ClearDatabasePool();
        // Restore requires quiescent database users. Never discard a journal to force a
        // replacement: it may contain committed data. Windows also refuses Move for an
        // active SQLite file handle; preserve that refusal instead of deleting the target.
        if (File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm")
            || File.Exists(databasePath + "-journal"))
        {
            throw new IOException("Database journals are still present; close database users and recover/checkpoint the database before restoring.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(stagePath, databasePath, overwrite: true);
    }

    private void ClearDatabasePool()
    {
        // Only this factory's connections belong to the database being restored.
        // Other databases may have concurrent readers or writers in the same process.
        using var connection = _connectionFactory.CreateConnection();
        SqliteConnection.ClearPool(connection);
    }

    private async Task VacuumIntoAsync(string destinationPath, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"VACUUM INTO {QuoteLiteral(destinationPath)};";

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COALESCE(MAX(version), 0) FROM {MigrationTableName};";

        try
        {
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return value is null or DBNull
                ? 0
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static string BuildDefaultBackupPath(string databasePath, DateTimeOffset createdAt)
    {
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The database path must include a directory.");

        var timestamp = createdAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        return Path.Combine(
            directory,
            "backups",
            $"{Path.GetFileNameWithoutExtension(databasePath)}-{timestamp}-{suffix}.db");
    }

    private static string BuildRollbackPath(string databasePath, DateTimeOffset restoredAt)
    {
        var timestamp = restoredAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        return $"{databasePath}.rollback-{timestamp}-{suffix}";
    }

    private static string BuildReadOnlyConnectionString(string databasePath)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            options: FileOptions.SequentialScan);

        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? TryReadChecksum(string databasePath)
    {
        var checksumPath = databasePath + ChecksumExtension;

        if (!File.Exists(checksumPath))
        {
            return null;
        }

        try
        {
            var value = File.ReadAllText(checksumPath).Trim();

            return value.Length == 0 ? null : value.ToLowerInvariant();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string QuoteLiteral(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static void DeleteSidecarFiles(string databasePath)
    {
        DeleteFile(databasePath + "-wal");
        DeleteFile(databasePath + "-shm");
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
