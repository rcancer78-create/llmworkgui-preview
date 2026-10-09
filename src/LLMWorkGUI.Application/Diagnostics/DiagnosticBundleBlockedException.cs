using LLMWorkGUI.Application.Workflows;

namespace LLMWorkGUI.Application.Diagnostics;

/// <summary>
/// Raised when the secret scan of a diagnostic bundle reports material that the redaction pipeline did
/// not remove. The archive is never written in that case (ТЗ §9.3).
/// </summary>
public sealed class DiagnosticBundleBlockedException : InvalidOperationException
{
    public DiagnosticBundleBlockedException(IReadOnlyList<WorkflowSecretFinding> findings)
        : base($"The diagnostic bundle was blocked: {findings.Count} secret finding(s) survived redaction.")
    {
        ArgumentNullException.ThrowIfNull(findings);

        Findings = findings.ToArray();
    }

    public IReadOnlyList<WorkflowSecretFinding> Findings { get; }
}
