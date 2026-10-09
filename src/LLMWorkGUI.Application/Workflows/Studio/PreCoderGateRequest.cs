using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>Stable identifiers of the separate pre-coder gate checks.</summary>
public static class PreCoderGateCheckIds
{
    public const string RequiredDocuments = "required-documents";
    public const string ReviewerUnanimity = "reviewer-unanimity";
    public const string UserApproval = "user-approval";
    public const string HashIntegrity = "hash-integrity";
    public const string RouteEvidence = "route-evidence";
    public const string UiArtifact = "ui-artifact";
    public const string VisualAcceptance = "visual-acceptance";
}

/// <summary>One separately reported check of the pre-coder gate.</summary>
public sealed record PreCoderGateCheck(
    string CheckId,
    string DisplayName,
    bool IsSatisfied,
    string Detail);

/// <summary>
/// One required document as seen by the gate: the current hash, the separate reviewer verdicts, the user
/// approval and the UI acceptance evidence. The verdict hashes are deliberately not re-pinned here, so a
/// document edited after review is detected instead of hidden.
/// </summary>
public sealed record PreCoderGateDocument(
    DocumentTemplateKind Kind,
    string DocumentId,
    string ContentHash,
    int Version,
    IReadOnlyList<ReviewerVerdictRecord> ReviewerVerdicts,
    UserApprovalEvidence? UserApproval = null,
    bool HasUiArtifact = false,
    bool HasVisualAcceptance = false);

/// <summary>
/// The pre-coder gate request: required documents, required reviewer roles, the named transition that is
/// being guarded, the route confirmation evidence and the UI acceptance requirements.
/// This is a supplied planning snapshot, not independently persisted model or route authority.
/// A passing evaluation cannot authorize a production run transition or native dispatch.
/// </summary>
public sealed class PreCoderGateRequest
{
    public PreCoderGateRequest(
        IReadOnlyList<DocumentTemplateKind> requiredDocumentKinds,
        IReadOnlyList<string> requiredReviewerRoles,
        IReadOnlyList<PreCoderGateDocument> documents,
        string blockedTransitionId,
        string? requestedRouteId = null,
        string? observedRouteId = null,
        bool requiresUiArtifact = false,
        bool requiresUserVisualAcceptance = false,
        string? escalationTargetNodeId = null)
    {
        ArgumentNullException.ThrowIfNull(requiredDocumentKinds);
        ArgumentNullException.ThrowIfNull(requiredReviewerRoles);
        ArgumentNullException.ThrowIfNull(documents);

        RequiredDocumentKinds = Array.AsReadOnly(requiredDocumentKinds.ToArray());
        RequiredReviewerRoles = Array.AsReadOnly(requiredReviewerRoles.ToArray());
        Documents = Array.AsReadOnly(documents.ToArray());
        BlockedTransitionId = ApplicationGuard.NotBlank(blockedTransitionId, nameof(blockedTransitionId));
        RequestedRouteId = ApplicationGuard.OptionalNotBlank(requestedRouteId, nameof(requestedRouteId));
        ObservedRouteId = ApplicationGuard.OptionalNotBlank(observedRouteId, nameof(observedRouteId));
        RequiresUiArtifact = requiresUiArtifact;
        RequiresUserVisualAcceptance = requiresUserVisualAcceptance;
        EscalationTargetNodeId = ApplicationGuard.OptionalNotBlank(
            escalationTargetNodeId,
            nameof(escalationTargetNodeId));
    }

    public IReadOnlyList<DocumentTemplateKind> RequiredDocumentKinds { get; }

    public IReadOnlyList<string> RequiredReviewerRoles { get; }

    public IReadOnlyList<PreCoderGateDocument> Documents { get; }

    /// <summary>The transition named by the scheme that must not start while a check fails.</summary>
    public string BlockedTransitionId { get; }

    public string? RequestedRouteId { get; }

    public string? ObservedRouteId { get; }

    public bool RequiresUiArtifact { get; }

    public bool RequiresUserVisualAcceptance { get; }

    /// <summary>The node named by the scheme that resolves conflicting verdicts; null means the user.</summary>
    public string? EscalationTargetNodeId { get; }
}
