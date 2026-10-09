namespace LLMWorkGUI.Application.Data;

/// <summary>
/// Outcome of a restore. <see cref="RollbackPath"/> holds the pre-restore snapshot that is kept so a
/// damaged restore can always be rolled back (ТЗ §9.4).
/// <see cref="Sha256"/> describes the published database, after any legacy quota diagnostic
/// redaction and standalone journal normalization. It can differ from the verified source backup
/// hash even when diagnostics are clean; the source backup is unchanged.
/// </summary>
public sealed record DatabaseRestoreResult(
    string DatabasePath,
    string BackupPath,
    string? RollbackPath,
    string Sha256,
    int SchemaVersion,
    DateTimeOffset RestoredAtUtc);
