using System.Security.Cryptography;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Tests.TestSupport;

/// <summary>
/// A blob store that keeps the bytes in memory and addresses them by the same <c>sha256:</c> form the
/// production store uses, so a test that records an artifact for a stage gets a real content hash to pin its
/// verdicts and its approval to. The blobs are mutable on purpose: a test has to be able to delete them or
/// rewrite them to prove that a stored row whose bytes are gone stops authorizing a transition.
/// </summary>
internal sealed class InMemoryWorkflowArtifactBlobStore : IWorkflowArtifactBlobStore
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _swaps = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> BlobIds => _blobs.Keys;

    public static string ComputeBlobId(byte[] content) =>
        WorkflowArtifactEvidence.ContentHashPrefix
        + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public Task<WorkflowArtifactBlob> SaveAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        var bytes = buffer.ToArray();
        var blobId = ComputeBlobId(bytes);

        _blobs[blobId] = bytes;

        return Task.FromResult(new WorkflowArtifactBlob(blobId, bytes.Length));
    }

    public Task<bool> VerifyAsync(string blobId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_blobs.TryGetValue(blobId, out var bytes) && ComputeBlobId(bytes) == blobId);

    public Task<Stream?> OpenVerifiedAsync(
        string blobId,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetVerified(blobId, out var bytes))
        {
            return Task.FromResult<Stream?>(null);
        }

        if (bytes!.Length > maxBytes)
        {
            throw new WorkflowArtifactTooLargeException(blobId, maxBytes);
        }

        // The one race a verify-then-read contract cannot close by itself, and the reason a caller has to
        // hash what it copied: the store re-hashed the content, and then opened a second, independent handle
        // on the same address. The handle this method hands back is the one the caller will read, and an
        // armed swap makes it carry bytes the verification never saw. It is armed explicitly and consumed
        // once, so no test can trip it by accident.
        if (_swaps.Remove(blobId, out var replacement))
        {
            return Task.FromResult<Stream?>(new MemoryStream(replacement, writable: false));
        }

        return Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false));
    }

    private bool TryGetVerified(string blobId, out byte[]? bytes) =>
        _blobs.TryGetValue(blobId, out bytes) && ComputeBlobId(bytes!) == blobId;

    /// <summary>Removes the stored bytes, leaving whatever row points at them exactly as it was.</summary>
    public bool Delete(string blobId) => _blobs.Remove(blobId);

    /// <summary>Rewrites the stored bytes in place, so the row's recorded hash no longer describes them.</summary>
    public void Alter(string blobId, byte[] replacement) => _blobs[blobId] = replacement;

    /// <summary>
    /// Arms the next successful open of <paramref name="blobId"/> to hand back
    /// <paramref name="replacement"/> instead of the bytes that were just verified.
    /// </summary>
    public void SwapOnNextOpen(string blobId, byte[] replacement) => _swaps[blobId] = replacement;
}
