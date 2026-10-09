using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// The durable evidence a gated stage transition is authorized by: one stored run artifact of exactly the
/// kind the stage requires, addressed to this run and this stage, whose blob id is the SHA-256 hash of the
/// stored bytes.
///
/// The value object records what was committed, not what somebody claims about it. A reviewer verdict, a
/// user approval or a caller-supplied hash can name a document, but only a row that names a blob whose
/// bytes hash to that id can authorize a transition. The three identifiers are therefore one value and are
/// refused when they disagree: <see cref="BlobId"/> is the address of the bytes, <see cref="HashSha256"/> is
/// what those bytes must hash to, and an artifact whose own id is not that hash names nothing anybody can
/// verify.
/// </summary>
public sealed class WorkflowArtifactEvidence
{
    public const string ContentHashPrefix = "sha256:";
    public const int Sha256HexLength = 64;

    public WorkflowArtifactEvidence(
        string artifactId,
        string runId,
        string stageId,
        string kind,
        string blobId,
        string hashSha256,
        DateTimeOffset createdAtUtc,
        long sizeBytes,
        DataClassification classification,
        string? executionId = null)
    {
        ArtifactId = DomainGuard.NotBlank(artifactId, nameof(artifactId));
        RunId = DomainGuard.NotBlank(runId, nameof(runId));
        StageId = DomainGuard.NotBlank(stageId, nameof(stageId));
        Kind = DomainGuard.NotBlank(kind, nameof(kind));
        BlobId = DomainGuard.NotBlank(blobId, nameof(blobId));
        HashSha256 = DomainGuard.NotBlank(hashSha256, nameof(hashSha256));
        CreatedAtUtc = createdAtUtc;
        Classification = classification;
        ExecutionId = DomainGuard.OptionalNotBlank(executionId, nameof(executionId));

        if (!IsContentHash(BlobId))
        {
            throw new ArgumentException(
                $"A workflow artifact blob id must have the form '{ContentHashPrefix}{new string('0', Sha256HexLength)}'.",
                nameof(blobId));
        }

        if (!string.Equals(BlobId, HashSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A workflow artifact blob id is the hash of its own bytes, so the recorded hash must be the "
                    + "recorded blob id.",
                nameof(hashSha256));
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sizeBytes),
                sizeBytes,
                "A workflow artifact cannot have a negative size.");
        }

        if (!Enum.IsDefined(classification))
        {
            throw new ArgumentOutOfRangeException(
                nameof(classification),
                classification,
                "A workflow artifact must carry a declared data classification.");
        }

        SizeBytes = sizeBytes;
    }

    /// <summary>The identity of the persisted artifact row. It is also the tiebreak of the current-artifact choice.</summary>
    public string ArtifactId { get; }

    /// <summary>The run that owns the artifact. An artifact of another run can never authorize this one.</summary>
    public string RunId { get; }

    /// <summary>
    /// The successful execution explicitly associated with the collected bytes. This association is
    /// checked against its persisted session/run/project; it is not a claim of native model authorship.
    /// Null denotes an operator-attached artifact without execution provenance.
    /// </summary>
    public string? ExecutionId { get; }

    /// <summary>
    /// The stage the artifact was recorded for. A newer artifact of the same kind recorded for another
    /// stage - including after a failure loop - belongs to that other stage and does not authorize this one.
    /// </summary>
    public string StageId { get; }

    /// <summary>The artifact kind, compared against a stage's required kind with an exact ordinal match.</summary>
    public string Kind { get; }

    /// <summary>The address of the stored bytes.</summary>
    public string BlobId { get; }

    /// <summary>The hash the stored bytes must produce. Always equal to <see cref="BlobId"/>.</summary>
    public string HashSha256 { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public long SizeBytes { get; }

    /// <summary>The classification the evidence was persisted under, so the row is auditable on its own.</summary>
    public DataClassification Classification { get; }

    /// <summary>
    /// True when the value has the exact form <c>sha256:</c> followed by 64 lowercase hex characters. The
    /// check is deliberately strict: an upper-case digest, a truncated one or a bare hex string could all
    /// address two different files for the same content and is refused instead of normalised.
    /// </summary>
    public static bool IsContentHash(string? value)
    {
        if (value is null || !value.StartsWith(ContentHashPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (value.Length != ContentHashPrefix.Length + Sha256HexLength)
        {
            return false;
        }

        foreach (var character in value.AsSpan(ContentHashPrefix.Length))
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The artifact that currently authorizes a stage: the newest one recorded for the same run, the same
    /// stage and exactly the required kind. The ordering is creation time first and the artifact id second,
    /// so two artifacts recorded inside the same clock tick still resolve to one deterministic answer.
    ///
    /// A null result is the fail-closed answer: no artifact, a null field, a mismatched id/hash pair, a row
    /// of a different kind or a row of another stage are all simply not authorization.
    /// </summary>
    public static WorkflowArtifactEvidence? SelectCurrent(
        IEnumerable<WorkflowArtifactEvidence> artifacts,
        string runId,
        string stageId,
        string kind)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        var guardedRunId = DomainGuard.NotBlank(runId, nameof(runId));
        var guardedStageId = DomainGuard.NotBlank(stageId, nameof(stageId));
        var guardedKind = DomainGuard.NotBlank(kind, nameof(kind));

        WorkflowArtifactEvidence? current = null;

        foreach (var artifact in artifacts)
        {
            if (artifact is null
                || !string.Equals(artifact.RunId, guardedRunId, StringComparison.Ordinal)
                || !string.Equals(artifact.StageId, guardedStageId, StringComparison.Ordinal)
                || !string.Equals(artifact.Kind, guardedKind, StringComparison.Ordinal))
            {
                continue;
            }

            if (current is null || IsNewerThan(artifact, current))
            {
                current = artifact;
            }
        }

        return current;
    }

    private static bool IsNewerThan(WorkflowArtifactEvidence candidate, WorkflowArtifactEvidence current) =>
        candidate.CreatedAtUtc > current.CreatedAtUtc
        || (candidate.CreatedAtUtc == current.CreatedAtUtc
            && string.CompareOrdinal(candidate.ArtifactId, current.ArtifactId) > 0);
}
