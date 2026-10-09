using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// An explicit user decision. Document approval pins the artifact hash and the approver named by the
/// active scheme; it is a first-class workflow action, not a tool-approval reply (ТЗ §6.15).
/// </summary>
public sealed class UserApprovalEvidence
{
    public UserApprovalEvidence(
        string approvalId,
        string approvedBy,
        string stageId,
        string artifactHash,
        UserApprovalDecision decision,
        string comment,
        DateTimeOffset decidedAtUtc)
    {
        ApprovalId = DomainGuard.NotBlank(approvalId, nameof(approvalId));
        ApprovedBy = DomainGuard.NotBlank(approvedBy, nameof(approvedBy));
        StageId = DomainGuard.NotBlank(stageId, nameof(stageId));
        ArtifactHash = DomainGuard.NotBlank(artifactHash, nameof(artifactHash));
        Decision = decision;
        Comment = DomainGuard.NotBlank(comment, nameof(comment));
        DecidedAtUtc = decidedAtUtc;
    }

    public string ApprovalId { get; }

    public string ApprovedBy { get; }

    public string StageId { get; }

    public string ArtifactHash { get; }

    public UserApprovalDecision Decision { get; }

    public string Comment { get; }

    public DateTimeOffset DecidedAtUtc { get; }
}
