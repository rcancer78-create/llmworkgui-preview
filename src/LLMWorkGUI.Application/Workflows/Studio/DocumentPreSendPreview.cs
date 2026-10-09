using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>
/// The pre-send preview of a document draft. <see cref="Content"/> is the redacted payload: any line that
/// the secret scan flagged is replaced, so a preview can never leak a detected secret.
/// </summary>
public sealed record DocumentPreSendPreview(
    string DraftId,
    DocumentTemplateKind Kind,
    string Title,
    string ContentHash,
    int Version,
    string Content,
    WorkflowSecretScanReport ScanReport,
    bool HasScanner,
    bool IsBlocked,
    IReadOnlyList<string> RedactedLines,
    string Summary);

/// <summary>
/// Structural completeness of a draft: whether every required section is present. The completeness
/// criteria themselves are handed to the reviewing models and the user, they are not auto-asserted.
/// </summary>
public sealed record DocumentCompletenessEvaluation(
    bool AllRequiredSectionsPresent,
    IReadOnlyList<string> MissingSections,
    IReadOnlyList<string> CompletenessCriteria,
    string Summary);
