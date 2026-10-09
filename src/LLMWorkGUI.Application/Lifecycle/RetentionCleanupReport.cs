using System.Globalization;

namespace LLMWorkGUI.Application.Lifecycle;

/// <summary>
/// The outcome of one retention cleanup pass. Every removed item is counted separately so the report
/// proves that only stale diagnostic bundles, abandoned scratch workspaces and archived audit records
/// were affected while active runs and immutable workflow versions were preserved.
/// </summary>
public sealed record RetentionCleanupReport
{
    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>Stale diagnostic archives removed from the bundles directory.</summary>
    public int DeletedDiagnosticBundleCount { get; init; }

    /// <summary>Abandoned scratch workspace directories removed.</summary>
    public int DeletedScratchWorkspaceCount { get; init; }

    /// <summary>Old audit/event records written to the append-only archive before deletion.</summary>
    public int ArchivedAuditRecordCount { get; init; }

    /// <summary>Old audit/event records removed from the live tables after archiving.</summary>
    public int DeletedAuditRecordCount { get; init; }

    /// <summary>Recent bundles and recently used scratch workspaces that were deliberately kept.</summary>
    public int PreservedActiveItemCount { get; init; }

    /// <summary>Items that could not be processed (locked files, inaccessible directories, ...).</summary>
    public int SkippedItemCount { get; init; }

    /// <summary>The paths of the removed items, in the order they were removed.</summary>
    public IReadOnlyList<string> DeletedPaths { get; init; } = Array.Empty<string>();

    /// <summary>Empty when the archive file was not needed or could be written.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;

    public int TotalDeletedCount =>
        DeletedDiagnosticBundleCount + DeletedScratchWorkspaceCount + DeletedAuditRecordCount;

    public bool IsClean => Warnings.Count == 0 && SkippedItemCount == 0;

    public string Summary => string.Format(
        CultureInfo.InvariantCulture,
        "Retention cleanup: bundles={0}, scratch={1}, archivedAudit={2}, deletedAudit={3}, "
        + "preservedActive={4}, skipped={5}.",
        DeletedDiagnosticBundleCount,
        DeletedScratchWorkspaceCount,
        ArchivedAuditRecordCount,
        DeletedAuditRecordCount,
        PreservedActiveItemCount,
        SkippedItemCount);
}
