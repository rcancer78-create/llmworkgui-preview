using System.Security.Cryptography;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.IntegrationTests;

/// <summary>
/// A blob store for a scenario that drives the run service without the durable storage underneath it. It
/// addresses content exactly as the production store does - <c>sha256:</c> plus the digest of the bytes -
/// and re-hashes on every verification, so a test that rewrites or deletes the stored bytes really does make
/// a previously committed artifact row stop authorizing a transition.
/// </summary>
internal sealed class InMemoryWorkflowArtifactBlobStore : IWorkflowArtifactBlobStore
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

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
        if (!_blobs.TryGetValue(blobId, out var bytes) || ComputeBlobId(bytes) != blobId)
        {
            return Task.FromResult<Stream?>(null);
        }

        if (bytes.Length > maxBytes)
        {
            throw new WorkflowArtifactTooLargeException(blobId, maxBytes);
        }

        return Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false));
    }

    public bool Delete(string blobId) => _blobs.Remove(blobId);
}
