namespace LLMWorkGUI.Application.Data;

/// <summary>
/// Consistent SQLite backup, integrity verification and safe restore. Backups are created from a live
/// online snapshot (<c>VACUUM INTO</c>), verified with <c>PRAGMA integrity_check</c> and a SHA-256
/// sidecar, and a restore always keeps a rollback snapshot of the previous database (ТЗ §9.4).
/// </summary>
public interface IDatabaseBackupService
{
    /// <summary>Path of the active database this service protects.</summary>
    string DatabasePath { get; }

    /// <summary>
    /// Creates a consistent backup next to the database (or at an explicit path) and writes the
    /// matching <c>.sha256</c> sidecar. Never overwrites an existing destination.
    /// Legacy quota diagnostics are redacted in the snapshot before its hash is computed;
    /// the live source database is unchanged by that redaction.
    /// </summary>
    Task<DatabaseBackupResult> CreateBackupAsync(
        string? destinationPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies one database file (<paramref name="databasePath"/> or the live database) with
    /// <c>PRAGMA integrity_check</c>, reads its schema version and compares the SHA-256 sidecar when
    /// one is present.
    /// </summary>
    Task<DatabaseIntegrityReport> VerifyIntegrityAsync(
        string? databasePath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a backup after verifying its checksum and integrity. The previous database is
    /// snapshotted first; a failed post-restore integrity check automatically rolls back to it.
    /// Database users must be quiescent. Remaining journals refuse replacement; on Windows active
    /// SQLite handles also prevent file replacement.
    /// Cancellation is honored before publication; validation and rollback after publication must finish.
    /// Legacy quota diagnostics and journal mode are normalized in the staged copy. The result hash describes the
    /// published database, which may differ from the verified source backup; that backup is unchanged.
    /// </summary>
    Task<DatabaseRestoreResult> RestoreAsync(
        string backupPath,
        CancellationToken cancellationToken = default);
}
