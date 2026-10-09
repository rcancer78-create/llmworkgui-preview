namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// The bytes a run artifact was persisted as, addressed by the hash those very bytes produce. The size is
/// reported so a caller can record what it stored without reading the content back, and the content itself
/// is never part of this value: it stays in the blob store and out of logs, messages and evidence payloads.
/// </summary>
public sealed record WorkflowArtifactBlob(string BlobId, long SizeBytes);

/// <summary>
/// A verified artifact is larger than the caller is prepared to hand to a model. Only the address and the
/// bound are named: the message never repeats the content, the size is the one the store already recorded,
/// and a storage fault of its own is never turned into this text.
/// </summary>
public sealed class WorkflowArtifactTooLargeException : Exception
{
    public WorkflowArtifactTooLargeException(string blobId, long maxBytes)
        : base($"The verified artifact '{blobId}' is larger than the {maxBytes} bytes a caller may read from it.")
    {
        BlobId = blobId;
        MaxBytes = maxBytes;
    }

    public string BlobId { get; }

    public long MaxBytes { get; }
}

/// <summary>
/// The content-addressed store a run artifact is written to and re-verified against.
/// <para>
/// This abstraction exists so the application layer can demand that a gated transition is authorized by
/// bytes that still hash to what was recorded, without taking a dependency on the infrastructure
/// implementation that owns the file layout. <see cref="VerifyAsync"/> is what makes evidence durable
/// rather than declarative: a row whose file is missing or altered must answer "not verified", and the
/// gate must then fail closed instead of trusting the row.
/// </para>
/// <para>
/// <see cref="OpenVerifiedAsync"/> is the only way to read those bytes back, and it exists so that a
/// consumer - a reviewer turn, in practice - cannot obtain content it has not first re-hashed. The two
/// calls cannot be composed by the caller in the other order without giving up that guarantee, and the
/// bound is part of the contract because a content hash proves authenticity, never a size.
/// </para>
/// </summary>
public interface IWorkflowArtifactBlobStore
{
    /// <summary>
    /// Stores the content and returns the address and size of the stored bytes. The caller must not assume
    /// the returned id before the call: it is the hash of what was actually written.
    /// </summary>
    Task<WorkflowArtifactBlob> SaveAsync(Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// True only when the stored bytes are present and still hash to <paramref name="blobId"/>. A missing
    /// file, altered bytes, an unreadable file and a malformed id are all "not verified".
    /// </summary>
    Task<bool> VerifyAsync(string blobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-hashes the stored bytes and, only if they are present and still hash to
    /// <paramref name="blobId"/>, returns a read-only handle to exactly those bytes.
    /// <para>
    /// The caller owns the returned stream and must dispose it. The size bound is checked against the
    /// committed bytes before the handle is handed over: content that is larger than
    /// <paramref name="maxBytes"/> raises <see cref="WorkflowArtifactTooLargeException"/> rather than being
    /// truncated, because a partial document is not the document the hash names.
    /// </para>
    /// <para>
    /// Null is the single "not established" answer: a missing file, altered bytes, an unreadable store and
    /// a malformed address are all reported the same way, as a null handle rather than as an exception
    /// whose text could carry a local path into a durable record. Cancellation is the one thing that is not
    /// an answer about the content and is re-thrown.
    /// </para>
    /// </summary>
    Task<Stream?> OpenVerifiedAsync(
        string blobId,
        long maxBytes,
        CancellationToken cancellationToken = default);
}
