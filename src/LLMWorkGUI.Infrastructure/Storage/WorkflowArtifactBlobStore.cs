using LLMWorkGUI.Application.Workflows.Orchestration;

namespace LLMWorkGUI.Infrastructure.Storage;

/// <summary>
/// The application's <see cref="IWorkflowArtifactBlobStore"/> over the existing content-addressed
/// <see cref="WorkflowBlobStore"/>.
/// <para>
/// Nothing about the file layout is re-implemented: the same store, the same <c>sha256:</c> addressing and
/// the same integrity check back both the workflow import/export blobs and a run's gate evidence, so a
/// recorded artifact and an imported package are verified by one rule instead of two.
/// </para>
/// </summary>
public sealed class WorkflowArtifactBlobStore : IWorkflowArtifactBlobStore
{
    private readonly WorkflowBlobStore _blobStore;

    public WorkflowArtifactBlobStore(WorkflowBlobStore blobStore)
    {
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    }

    public async Task<WorkflowArtifactBlob> SaveAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var blob = await _blobStore.SaveBlobAsync(content, cancellationToken).ConfigureAwait(false);

        return new WorkflowArtifactBlob(blob.BlobId, blob.Length);
    }

    public async Task<bool> VerifyAsync(string blobId, CancellationToken cancellationToken = default)
    {
        // An unreadable or missing file is "not verified", never an exception: the caller of this method is a
        // gate that has to fail closed, and a thrown IO error would be indistinguishable from a fault in the
        // caller. Cancellation is the one thing that is not a verification answer and is re-thrown.
        try
        {
            return await _blobStore.VerifyBlobAsync(blobId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<Stream?> OpenVerifiedAsync(
        string blobId,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var guardedBlobId = string.IsNullOrWhiteSpace(blobId)
            ? throw new ArgumentException("Value must not be null, empty, or whitespace.", nameof(blobId))
            : blobId;

        if (maxBytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes),
                maxBytes,
                "A verified artifact may only be read under a positive byte bound.");
        }

        Stream? content;

        try
        {
            content = await _blobStore
                .OpenVerifiedStreamAsync(guardedBlobId, maxBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (content is not null)
        {
            return content;
        }

        // The store answers "not established" as one value, so the one reason a caller has to be able to
        // name - content that is authentic but larger than it may read - is separated here rather than by
        // weakening that answer. The length is only consulted on the failure path, and it re-verifies the
        // same bytes, so a bound that trips on unverified content is impossible.
        var verifiedLength = await GetVerifiedLengthQuietlyAsync(guardedBlobId, cancellationToken)
            .ConfigureAwait(false);

        if (verifiedLength is { } length && length > maxBytes)
        {
            throw new WorkflowArtifactTooLargeException(guardedBlobId, maxBytes);
        }

        return null;
    }

    private async Task<long?> GetVerifiedLengthQuietlyAsync(
        string blobId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _blobStore.GetVerifiedLengthAsync(blobId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
