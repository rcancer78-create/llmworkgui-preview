using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// A separate, evidence-backed review record. Verdicts of different reviewers are never folded into a
/// single anonymous pass/fail value: every entry keeps its role, route, document hash and evidence
/// (ТЗ §6.15).
/// <para>
/// A record is <em>linked</em> when it names the reviewer execution that produced it, the stage it was
/// recorded on and the exact artifact row it was about. A linked record is the only kind that may
/// authorize a pinned model-review stage: a role, a route string and a hash typed by a caller prove only
/// that the string was stored, and <see cref="WorkflowRun"/> refuses such a record at the transition gate
/// rather than treating an unlinked approval as model evidence. An unlinked record is kept for the
/// explicitly legacy, non-model path and is written through
/// <see cref="Entities.WorkflowRun.RecordLegacyUnlinkedReviewerVerdict"/>.
/// </para>
/// </summary>
public sealed class ReviewerVerdictRecord
{
    public ReviewerVerdictRecord(
        string reviewerRole,
        string routeId,
        string documentHash,
        WorkflowReviewVerdict verdict,
        string evidenceSummary,
        DateTimeOffset recordedAtUtc,
        string? executionId = null,
        string? stageId = null,
        string? reviewedArtifactId = null)
    {
        ReviewerRole = DomainGuard.NotBlank(reviewerRole, nameof(reviewerRole));
        RouteId = DomainGuard.NotBlank(routeId, nameof(routeId));
        DocumentHash = DomainGuard.NotBlank(documentHash, nameof(documentHash));
        Verdict = verdict;
        EvidenceSummary = DomainGuard.NotBlank(evidenceSummary, nameof(evidenceSummary));
        RecordedAtUtc = recordedAtUtc;
        ExecutionId = DomainGuard.OptionalNotBlank(executionId, nameof(executionId));
        StageId = DomainGuard.OptionalNotBlank(stageId, nameof(stageId));
        ReviewedArtifactId = DomainGuard.OptionalNotBlank(reviewedArtifactId, nameof(reviewedArtifactId));

        EnsureLinkageIsWhole();
    }

    public string ReviewerRole { get; }

    public string RouteId { get; }

    public string DocumentHash { get; }

    public WorkflowReviewVerdict Verdict { get; }

    public string EvidenceSummary { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>
    /// The persisted reviewer execution this verdict was read out of, or null for an unlinked record.
    /// A non-null value is a claim about a row, not proof of one: the transition gate only accepts it
    /// together with a matching <see cref="ReviewerExecutionEvidence"/>.
    /// </summary>
    public string? ExecutionId { get; }

    /// <summary>The stage the reviewer was recorded on, or null for an unlinked record.</summary>
    public string? StageId { get; }

    /// <summary>
    /// The stored artifact row the reviewer was about, or null for an unlinked record. The id binds the
    /// verdict to one specific artifact even when two artifacts of a stage happen to carry the same bytes.
    /// </summary>
    public string? ReviewedArtifactId { get; }

    /// <summary>True only when this record names the execution, stage and artifact that produced it.</summary>
    public bool IsLinked => ExecutionId is not null;

    /// <summary>
    /// The linkage is one value. A record that names an execution but no stage, or no artifact row, claims
    /// a model review it cannot bind to this run, and a partially populated link is refused here rather
    /// than being completed by whoever reads the record later.
    /// </summary>
    private void EnsureLinkageIsWhole()
    {
        if (!IsLinked)
        {
            return;
        }

        if (StageId is null || ReviewedArtifactId is null)
        {
            throw new ArgumentException(
                "A reviewer verdict that names an execution must also name the stage and the stored artifact "
                    + "row it was recorded on, or none of them.",
                nameof(ExecutionId));
        }
    }
}
