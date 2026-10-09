using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Result of packaging a candidate scratch workspace into a new immutable workflow version. The
/// candidate is stored as a <see cref="WorkflowSourceType.SyntheticDraft"/> with
/// <see cref="ActivatedAtUtc"/> equal to <c>null</c>; the active binding is never touched here.
/// </summary>
public sealed record SaveCandidateVersionResult
{
    public SaveCandidateVersionResult(
        string versionId,
        string workflowPackageId,
        int versionNumber,
        string blobId,
        WorkflowSourceType sourceType,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? activatedAtUtc,
        IReadOnlyList<AdaptationBlockerKind> blockers)
    {
        ArgumentNullException.ThrowIfNull(blockers);

        VersionId = ApplicationGuard.NotBlank(versionId, nameof(versionId));
        WorkflowPackageId = ApplicationGuard.NotBlank(workflowPackageId, nameof(workflowPackageId));
        VersionNumber = versionNumber;
        BlobId = ApplicationGuard.NotBlank(blobId, nameof(blobId));
        SourceType = sourceType;
        CreatedAtUtc = createdAtUtc;
        ActivatedAtUtc = activatedAtUtc;
        Blockers = blockers.ToArray();
    }

    public string VersionId { get; }

    public string WorkflowPackageId { get; }

    public int VersionNumber { get; }

    public string BlobId { get; }

    public WorkflowSourceType SourceType { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? ActivatedAtUtc { get; }

    public IReadOnlyList<AdaptationBlockerKind> Blockers { get; }

    public bool HasBlockers => Blockers.Count > 0;

    /// <summary>The version is committed; the retained session still needs scratch cleanup.</summary>
    public bool CleanupPending { get; init; }
}
