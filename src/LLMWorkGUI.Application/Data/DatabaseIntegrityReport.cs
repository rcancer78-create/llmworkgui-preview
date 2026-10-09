namespace LLMWorkGUI.Application.Data;

/// <summary>
/// Result of <c>PRAGMA integrity_check</c> plus optional checksum/schema validation for a database
/// file, either the live database or one backup snapshot.
/// </summary>
public sealed record DatabaseIntegrityReport(
    string DatabasePath,
    bool IsHealthy,
    string Sha256,
    string? ExpectedSha256,
    bool? ChecksumMatches,
    int SchemaVersion,
    long SizeBytes,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<string> Messages);
