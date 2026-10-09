namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// An immutable record of a stage transition together with the verdicts, the user approval and the durable
/// artifact that authorized it. A transition is never inferred from a single execution result.
///
/// <see cref="AuthorizingArtifactId"/> and <see cref="AuthorizingArtifactHash"/> are recorded as one pair or
/// not at all. A transition made before artifact evidence existed carries neither, which is exactly what
/// makes a gate-free transition distinguishable from a gated one on readback: the pair is absent for the
/// former and names the stored artifact for the latter.
/// </summary>
public sealed class WorkflowTransitionRecord
{
    public WorkflowTransitionRecord(
        string transitionId,
        string fromStageId,
        string toStageId,
        DateTimeOffset triggeredAtUtc,
        string reason,
        IReadOnlyList<ReviewerVerdictRecord> reviewerVerdicts,
        UserApprovalEvidence? userApproval,
        string? authorizingArtifactId = null,
        string? authorizingArtifactHash = null)
    {
        TransitionId = DomainGuard.NotBlank(transitionId, nameof(transitionId));
        FromStageId = DomainGuard.NotBlank(fromStageId, nameof(fromStageId));
        ToStageId = DomainGuard.NotBlank(toStageId, nameof(toStageId));
        TriggeredAtUtc = triggeredAtUtc;
        Reason = DomainGuard.NotBlank(reason, nameof(reason));
        ReviewerVerdicts = DomainGuard.NotNullList(reviewerVerdicts, nameof(reviewerVerdicts));
        UserApproval = userApproval;

        if ((authorizingArtifactId is null) != (authorizingArtifactHash is null))
        {
            throw new ArgumentException(
                "A transition records the authorizing artifact id and hash together, or neither of them.",
                nameof(authorizingArtifactId));
        }

        AuthorizingArtifactId = DomainGuard.OptionalNotBlank(authorizingArtifactId, nameof(authorizingArtifactId));
        AuthorizingArtifactHash = DomainGuard.OptionalNotBlank(authorizingArtifactHash, nameof(authorizingArtifactHash));
    }

    public string TransitionId { get; }

    public string FromStageId { get; }

    public string ToStageId { get; }

    public DateTimeOffset TriggeredAtUtc { get; }

    public string Reason { get; }

    public IReadOnlyList<ReviewerVerdictRecord> ReviewerVerdicts { get; }

    public UserApprovalEvidence? UserApproval { get; }

    /// <summary>The stored artifact whose verified bytes authorized this transition, or null for a gate-free one.</summary>
    public string? AuthorizingArtifactId { get; }

    /// <summary>
    /// The content hash of the authorizing artifact, which is the hash every authorizing verdict and user
    /// approval of this transition pinned. Null for a gate-free transition.
    /// </summary>
    public string? AuthorizingArtifactHash { get; }

    /// <summary>True when this transition was authorized by a verified stored artifact of the stage it left.</summary>
    public bool HasAuthorizingArtifact => AuthorizingArtifactId is not null;
}
