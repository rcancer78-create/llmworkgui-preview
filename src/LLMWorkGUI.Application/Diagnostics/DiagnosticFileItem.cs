namespace LLMWorkGUI.Application.Diagnostics;

/// <summary>
/// One previewed file of a diagnostic bundle. Only metadata and the content hash are exposed in the
/// preview, never raw file bytes (ТЗ §9.3, countermeasure I-6).
/// </summary>
public sealed record DiagnosticFileItem(
    string RelativePath,
    string Category,
    long SizeBytes,
    string Sha256,
    bool IsRedacted);
