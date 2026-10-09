namespace LLMWorkGUI.Application.Data;

/// <summary>
/// Outcome of a consistent SQLite online backup. The archive path is paired with a SHA-256 sidecar so
/// a later restore can prove the snapshot was not altered (R-4, ТЗ §9.4).
/// </summary>
public sealed record DatabaseBackupResult(
    string DatabasePath,
    string BackupPath,
    string ChecksumPath,
    long SizeBytes,
    string Sha256,
    bool IntegrityOk,
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> IntegrityMessages);
