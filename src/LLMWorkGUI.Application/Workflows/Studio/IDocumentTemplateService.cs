using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>All seven shipped document template definitions with their required sections.</summary>
public interface IDocumentTemplateService
{
    IReadOnlyList<DocumentTemplateDefinition> GetStandardTemplates();

    DocumentTemplateDefinition GetRequiredTemplate(DocumentTemplateKind kind);

    IReadOnlyList<WorkflowDocumentDraft> ListDrafts();

    WorkflowDocumentDraft GetRequiredDraft(string draftId);

    Task<WorkflowDocumentDraft> GenerateDraftAsync(
        DocumentTemplateKind kind,
        string title,
        string? initialContent = null,
        CancellationToken cancellationToken = default);

    /// <summary>Manual edit: the version is incremented, the hash recomputed and old decisions cleared.</summary>
    Task<WorkflowDocumentDraft> UpdateDraftAsync(
        string draftId,
        string content,
        CancellationToken cancellationToken = default);

    DocumentCompletenessEvaluation EvaluateCompleteness(string draftId);

    /// <summary>
    /// Builds the preview of the data that would be sent: the SHA-256 hash, the version, the secret scan
    /// report and the content with every secret-bearing line redacted. When no scanner is configured the
    /// preview is blocked instead of pretending the content is clean.
    /// </summary>
    Task<DocumentPreSendPreview> GeneratePreSendPreviewAsync(
        string draftId,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a separate reviewer verdict; the verdict hash must equal the current content hash.</summary>
    Task<WorkflowDocumentDraft> AddReviewVerdictAsync(
        string draftId,
        ReviewerVerdictRecord verdict,
        CancellationToken cancellationToken = default);

    /// <summary>Records an explicit user approval; the approval hash must equal the current content hash.</summary>
    Task<WorkflowDocumentDraft> ApproveDraftAsync(
        string draftId,
        UserApprovalEvidence approval,
        CancellationToken cancellationToken = default);
}
