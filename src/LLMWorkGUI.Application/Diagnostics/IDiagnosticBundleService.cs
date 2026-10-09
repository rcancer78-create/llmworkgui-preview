namespace LLMWorkGUI.Application.Diagnostics;

/// <summary>
/// Redacted diagnostic bundle generator. A bundle can only be exported through a preview that
/// belongs to the same service instance, so the mandatory preview step cannot be skipped (ТЗ §9.3).
/// </summary>
public interface IDiagnosticBundleService
{
    /// <summary>
    /// Collects, redacts and scans the requested content and returns the preview contract. Raw file
    /// bytes never leave the service; only file metadata, hashes and redacted findings are exposed.
    /// </summary>
    Task<DiagnosticBundlePreview> PreviewAsync(
        DiagnosticBundleRequest? request = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-scans the previewed content and writes the <c>.zip</c> archive. Throws
    /// <see cref="DiagnosticBundleBlockedException"/> when the scan still reports sensitive material,
    /// and refuses preview ids that are unknown, expired or belong to another request.
    /// </summary>
    Task<DiagnosticBundleResult> CreateBundleAsync(
        DiagnosticBundlePreview preview,
        CancellationToken cancellationToken = default);
}
