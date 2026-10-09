using System.Security.Cryptography;
using System.Text;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// A verifiable workflow document draft. The content hash pins the exact bytes a reviewer or the user
/// approved; editing the content increments the version, recomputes the SHA-256 hash and resets every
/// previous reviewer verdict and user approval, because those decisions belonged to the old content.
/// </summary>
public sealed class WorkflowDocumentDraft
{
    private const string HashPrefix = "sha256:";

    private readonly List<ReviewerVerdictRecord> _reviewerVerdicts = new();
    private string _content;
    private string _contentHash;
    private int _version;
    private DateTimeOffset _updatedAtUtc;
    private UserApprovalEvidence? _userApproval;

    public WorkflowDocumentDraft(
        string draftId,
        DocumentTemplateKind kind,
        string title,
        string content,
        int version,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        IReadOnlyList<ReviewerVerdictRecord>? reviewerVerdicts = null,
        UserApprovalEvidence? userApproval = null)
    {
        DraftId = DomainGuard.NotBlank(draftId, nameof(draftId));
        Kind = kind;
        Title = DomainGuard.NotBlank(title, nameof(title));
        ArgumentNullException.ThrowIfNull(content);

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A document version starts at 1.");
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAtUtc),
                "A document cannot be updated before it was created.");
        }

        _content = content;
        _contentHash = ComputeContentHash(content);
        _version = version;
        CreatedAtUtc = createdAtUtc;
        _updatedAtUtc = updatedAtUtc;

        if (reviewerVerdicts is not null)
        {
            foreach (var verdict in reviewerVerdicts)
            {
                ArgumentNullException.ThrowIfNull(verdict);

                if (!string.Equals(verdict.DocumentHash, _contentHash, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "A reviewer verdict must reference the current document hash.",
                        nameof(reviewerVerdicts));
                }

                _reviewerVerdicts.Add(verdict);
            }
        }

        if (userApproval is not null)
        {
            if (!string.Equals(userApproval.ArtifactHash, _contentHash, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A user approval must reference the current document hash.",
                    nameof(userApproval));
            }

            _userApproval = userApproval;
        }
    }

    public string DraftId { get; }

    public DocumentTemplateKind Kind { get; }

    public string Title { get; }

    public string Content => _content;

    /// <summary>SHA-256 of the current content in <c>sha256:&lt;lowercase hex&gt;</c> form.</summary>
    public string ContentHash => _contentHash;

    public int Version => _version;

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc => _updatedAtUtc;

    /// <summary>Separate verdicts of the reviewers, each pinned to its own document hash.</summary>
    public IReadOnlyList<ReviewerVerdictRecord> ReviewerVerdicts => _reviewerVerdicts.ToArray();

    public UserApprovalEvidence? UserApproval => _userApproval;

    /// <summary>True only when the user approved this exact hash; editing the content clears it.</summary>
    public bool IsApprovedForCoding =>
        _userApproval is { Decision: UserApprovalDecision.Approved } approval
        && string.Equals(approval.ArtifactHash, _contentHash, StringComparison.Ordinal);

    /// <summary>
    /// Replaces the content: the version is incremented, the hash recomputed and every verdict and
    /// approval recorded for the previous content is dropped.
    /// </summary>
    public void UpdateContent(string newContent, DateTimeOffset? updatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(newContent);

        _content = newContent;
        _contentHash = ComputeContentHash(newContent);
        _version++;
        _reviewerVerdicts.Clear();
        _userApproval = null;
        _updatedAtUtc = EnsureNotBeforeCreation(updatedAtUtc ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Adds a separate reviewer verdict; the verdict hash must equal the current content hash.</summary>
    public void RecordReviewerVerdict(
        ReviewerVerdictRecord verdict,
        DateTimeOffset? recordedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        if (!string.Equals(verdict.DocumentHash, _contentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reviewer '{verdict.ReviewerRole}' reviewed '{verdict.DocumentHash}', but the current "
                + $"document hash is '{_contentHash}'. A stale review cannot be attached to new content.");
        }

        _reviewerVerdicts.Add(verdict);
        _updatedAtUtc = EnsureNotBeforeCreation(recordedAtUtc ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Records an explicit user decision; the approval hash must equal the current content hash.</summary>
    public void RecordUserApproval(
        UserApprovalEvidence approval,
        DateTimeOffset? decidedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(approval);

        if (!string.Equals(approval.ArtifactHash, _contentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The user approval references '{approval.ArtifactHash}', but the current document hash "
                + $"is '{_contentHash}'. The approval must pin the current content.");
        }

        _userApproval = approval;
        _updatedAtUtc = EnsureNotBeforeCreation(decidedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public static string ComputeContentHash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));

        return HashPrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private DateTimeOffset EnsureNotBeforeCreation(DateTimeOffset timestamp) =>
        timestamp < CreatedAtUtc ? CreatedAtUtc : timestamp;
}
