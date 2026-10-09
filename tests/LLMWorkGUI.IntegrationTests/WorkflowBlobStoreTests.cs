using System.Text;
using LLMWorkGUI.Infrastructure.Storage;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class WorkflowBlobStoreTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly WorkflowBlobStore _store;

    public WorkflowBlobStoreTests()
    {
        _store = new WorkflowBlobStore(_directory.Root);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public void ComputeBlobId_IsDeterministicSha256()
    {
        Assert.Equal(
            "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            WorkflowBlobStore.ComputeBlobId([]));

        Assert.Equal(
            "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            WorkflowBlobStore.ComputeBlobId("abc"u8));
    }

    [Fact]
    public async Task SaveBlobAsync_StoresContentAddressedBlobUnderSha256Path()
    {
        var content = Encoding.UTF8.GetBytes("workflow-package-demo");

        var blob = await _store.SaveBlobAsync(new MemoryStream(content));

        Assert.Equal(WorkflowBlobStore.ComputeBlobId(content), blob.BlobId);
        Assert.Equal(content.Length, blob.Length);

        var hex = blob.BlobId[WorkflowBlobStore.BlobIdPrefix.Length..];
        var expectedPath = Path.Combine(_store.BlobsDirectory, "sha256", hex[..2], hex);

        Assert.Equal(expectedPath, blob.FullPath);
        Assert.True(File.Exists(expectedPath));
        Assert.True(await _store.VerifyBlobAsync(blob.BlobId));
        Assert.True(await _store.BlobExistsAsync(blob.BlobId));
        Assert.Empty(Directory.GetFiles(_store.BlobsDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SaveBlobAsync_DeduplicatesIdenticalContent()
    {
        var content = Encoding.UTF8.GetBytes("workflow-package-demo");

        var first = await _store.SaveBlobAsync(new MemoryStream(content));
        var second = await _store.SaveBlobAsync(new MemoryStream(content));

        Assert.Equal(first.BlobId, second.BlobId);
        Assert.Equal(first.FullPath, second.FullPath);
        Assert.Single(Directory.GetFiles(_store.BlobsDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SaveBlobAsync_RefusesCorruptExistingDestination()
    {
        var content = "expected content"u8.ToArray();
        var blob = await _store.SaveBlobAsync(new MemoryStream(content));
        await File.WriteAllTextAsync(blob.FullPath, "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveBlobAsync(new MemoryStream(content)));
        Assert.Empty(Directory.GetFiles(_store.BlobsDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OpenVerifiedStream_HoldsTheVerifiedBytesUntilDisposed()
    {
        var content = "verified immutable content"u8.ToArray();
        var blob = await _store.SaveBlobAsync(new MemoryStream(content));
        await using var stream = await _store.OpenVerifiedStreamAsync(blob.BlobId, content.Length);
        Assert.NotNull(stream);
        Assert.Equal(0, stream.Position);
        Assert.Equal(content.Length, stream.Length);
        Assert.Throws<IOException>(() => File.WriteAllText(blob.FullPath, "replacement"));
        Assert.Throws<IOException>(() => File.Delete(blob.FullPath));
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Assert.Equal(blob.BlobId, WorkflowBlobStore.ComputeBlobId(bytes.ToArray()));
    }

    [Fact]
    public async Task ConcurrentIdenticalSaves_ReturnOnlyVerifiedContent()
    {
        var bytes = "concurrent identical writes"u8.ToArray();
        var blobs = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => _store.SaveBlobAsync(new MemoryStream(bytes))));
        Assert.Single(blobs.Select(b => b.BlobId).Distinct());
        Assert.All(blobs, blob => Assert.Equal(bytes.Length, blob.Length));
        Assert.True(await _store.VerifyBlobAsync(blobs[0].BlobId));
    }

    [Fact]
    public async Task OpenVerifiedStream_RetriesTransientSharingConflictAndPinsVerifiedContent()
    {
        var content = "content published by a concurrent writer"u8.ToArray();
        var blob = await _store.SaveBlobAsync(new MemoryStream(content));
        using var publishing = new FileStream(blob.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var opening = _store.OpenVerifiedStreamAsync(blob.BlobId, content.Length);
        Assert.False(opening.IsCompleted);
        publishing.Dispose();

        await using var verified = await opening.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(verified);
        using var bytes = new MemoryStream();
        await verified.CopyToAsync(bytes);
        Assert.Equal(content, bytes.ToArray());
        Assert.Throws<IOException>(() => File.Delete(blob.FullPath));
    }

    [Fact]
    public async Task OpenVerifiedStream_SharingRetryStillRejectsSameLengthCorruption()
    {
        var content = "verified content"u8.ToArray();
        var blob = await _store.SaveBlobAsync(new MemoryStream(content));
        using var publishing = new FileStream(blob.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var opening = _store.OpenVerifiedStreamAsync(blob.BlobId, content.Length);
        Assert.False(opening.IsCompleted);
        publishing.WriteByte((byte)'X');
        publishing.Dispose();

        Assert.Null(await opening.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task OpenVerifiedStream_SharingRetryHonorsCancellation()
    {
        var blob = await _store.SaveBlobAsync(new MemoryStream("content"u8.ToArray()));
        using var publishing = new FileStream(blob.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        var opening = _store.OpenVerifiedStreamAsync(blob.BlobId, null, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
    }

    [Fact]
    public async Task OpenVerifiedStream_PersistentSharingConflictIsBoundedAndRefused()
    {
        var blob = await _store.SaveBlobAsync(new MemoryStream("content"u8.ToArray()));
        using var publishing = new FileStream(blob.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Null(await _store.OpenVerifiedStreamAsync(blob.BlobId, null).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task SaveBlobAsync_StoresEmptyContent()
    {
        var blob = await _store.SaveBlobAsync(new MemoryStream([]));

        Assert.Equal(
            "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            blob.BlobId);
        Assert.Equal(0, blob.Length);
        Assert.True(File.Exists(blob.FullPath));
    }

    [Fact]
    public async Task GetBlobStreamAsync_RoundTripsBytes()
    {
        var content = Encoding.UTF8.GetBytes("immutable workflow bytes");
        var blob = await _store.SaveBlobAsync(new MemoryStream(content));

        await using var stream = await _store.GetBlobStreamAsync(blob.BlobId);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal(content, buffer.ToArray());
    }

    [Fact]
    public async Task VerifyBlobAsync_DetectsTamperedContent()
    {
        var blob = await _store.SaveBlobAsync(new MemoryStream(Encoding.UTF8.GetBytes("original")));

        Assert.True(await _store.VerifyBlobAsync(blob.BlobId));

        await File.WriteAllBytesAsync(blob.FullPath, Encoding.UTF8.GetBytes("tampered"));

        Assert.False(await _store.VerifyBlobAsync(blob.BlobId));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.GetBlobStreamAsync(blob.BlobId));
    }

    [Fact]
    public async Task GetBlobStreamAsync_ThrowsWhenBlobIsMissing()
    {
        var missingBlobId = "sha256:" + new string('a', WorkflowBlobStore.Sha256HexLength);

        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.GetBlobStreamAsync(missingBlobId));
        Assert.False(await _store.VerifyBlobAsync(missingBlobId));
        Assert.False(await _store.BlobExistsAsync(missingBlobId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("sha256:abc")]
    [InlineData("sha256:../../escape")]
    [InlineData("sha256:\\..\\escape")]
    [InlineData("md5:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("sha256:E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")]
    public async Task BlobIdentifierValidation_RejectsMalformedIdentifiers(string blobId)
    {
        Assert.False(WorkflowBlobStore.IsValidBlobId(blobId));
        Assert.Throws<ArgumentException>(() => _store.GetBlobPath(blobId));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.VerifyBlobAsync(blobId));
    }
}
