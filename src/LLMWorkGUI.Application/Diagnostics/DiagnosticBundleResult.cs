namespace LLMWorkGUI.Application.Diagnostics;

/// <summary>Outcome of a diagnostic bundle export that passed the redaction and secret-scan gates.</summary>
public sealed record DiagnosticBundleResult(
    string BundlePath,
    string PreviewId,
    int FileCount,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAtUtc,
    string ManifestJson);
