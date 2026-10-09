using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.Application.Diagnostics;

/// <summary>
/// Redacted preview of a diagnostic bundle. Export requires the preview to exist and to contain no
/// blocking secret findings; a blocked preview can never be turned into an archive (ТЗ §9.3).
/// </summary>
public sealed record DiagnosticBundlePreview
{
    public required string PreviewId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset ExpiresAtUtc { get; init; }

    public required DiagnosticBundleRequest Request { get; init; }

    public required IReadOnlyList<DiagnosticFileItem> Files { get; init; }

    /// <summary>Full scanner output, kept for transparency in the preview.</summary>
    public required WorkflowSecretScanReport SecretScan { get; init; }

    /// <summary>Findings that are not secret URN references and must block the export.</summary>
    public required IReadOnlyList<WorkflowSecretFinding> BlockingFindings { get; init; }

    /// <summary>
    /// Secret URN references are reference-only metadata, not secret material, so they are reported
    /// but never block the bundle.
    /// </summary>
    public required int SecretReferenceCount { get; init; }

    /// <summary>Non-fatal collection problems (unreadable files, skipped budgets, failed probes).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public long TotalSizeBytes => Files.Sum(file => file.SizeBytes);

    public bool IsBlocked => BlockingFindings.Count > 0;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;
}
