using System.Buffers;
using System.Security.Cryptography;

namespace LLMWorkGUI.Infrastructure.Storage;

public sealed record WorkflowBlob(string BlobId, long Length, string FullPath);

public sealed class WorkflowBlobStore
{
    public const string BlobIdPrefix = "sha256:";
    public const int Sha256HexLength = 64;

    public WorkflowBlobStore(string? appDataDirectory = null)
    {
        AppDataDirectory = Path.GetFullPath(appDataDirectory ?? AppDataPaths.DefaultRootDirectory);
        BlobsDirectory = Path.Combine(AppDataDirectory, AppDataPaths.BlobsDirectoryName);
    }

    public string AppDataDirectory { get; }

    public string BlobsDirectory { get; }

    public static string ComputeBlobId(ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(content, hash);

        return BlobIdPrefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsValidBlobId(string? blobId)
    {
        if (blobId is null || !blobId.StartsWith(BlobIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (blobId.Length != BlobIdPrefix.Length + Sha256HexLength)
        {
            return false;
        }

        foreach (var character in blobId.AsSpan(BlobIdPrefix.Length))
        {
            var isHex = character is >= '0' and <= '9' or >= 'a' and <= 'f';

            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    public string GetBlobPath(string blobId)
    {
        var hex = GetHex(blobId);

        return Path.Combine(BlobsDirectory, "sha256", hex[..2], hex);
    }

    public Task<bool> BlobExistsAsync(string blobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(File.Exists(GetBlobPath(blobId)));
    }

    public async Task<WorkflowBlob> SaveBlobAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var tempDirectory = Path.Combine(BlobsDirectory, "sha256", ".tmp");
        Directory.CreateDirectory(tempDirectory);

        var tempPath = AtomicFile.CreateTempPath(tempDirectory);
        long length;

        try
        {
            string blobId;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81920);

                try
                {
                    length = 0;

                    await using (var tempStream = new FileStream(
                                     tempPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     bufferSize: 81920,
                                     FileOptions.Asynchronous))
                    {
                        int read;

                        while ((read = await content
                                   .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                                   .ConfigureAwait(false)) > 0)
                        {
                            await tempStream
                                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                                .ConfigureAwait(false);

                            hasher.AppendData(buffer, 0, read);
                            length += read;
                        }

                        tempStream.Flush(flushToDisk: true);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                blobId = BlobIdPrefix + Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            var destinationPath = GetBlobPath(blobId);

            if (File.Exists(destinationPath))
            {
                await EnsureExistingBlobAsync(blobId, length, cancellationToken).ConfigureAwait(false);
                AtomicFile.TryDelete(tempPath);
                return new WorkflowBlob(blobId, length, destinationPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            try
            {
                File.Move(tempPath, destinationPath);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                await EnsureExistingBlobAsync(blobId, length, cancellationToken).ConfigureAwait(false);
                AtomicFile.TryDelete(tempPath);
            }

            return new WorkflowBlob(blobId, length, destinationPath);
        }
        catch
        {
            AtomicFile.TryDelete(tempPath);
            throw;
        }
    }

    public async Task<Stream> GetBlobStreamAsync(string blobId, CancellationToken cancellationToken = default)
    {
        var path = GetBlobPath(blobId);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Workflow blob was not found.", path);
        }

        return await OpenVerifiedStreamAsync(blobId, maxBytes: null, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"Workflow blob '{blobId}' failed its SHA-256 integrity check.");
    }

    private async Task EnsureExistingBlobAsync(string blobId, long length, CancellationToken cancellationToken)
    {
        await using var existing = await OpenVerifiedStreamAsync(blobId, length, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.Length != length)
            throw new InvalidDataException("Existing workflow blob is corrupt or unavailable; it was not accepted as saved content.");
    }

    /// <summary>
    /// Re-hashes the committed bytes and then hands back a read-only handle to them, but only when they are
    /// still present and still hash to <paramref name="blobId"/> and are not larger than
    /// <paramref name="maxBytes"/>.
    /// <para>
    /// The length and hash are checked on the very same handle returned to the caller; no path is reopened.
    /// Null is the single
    /// "not established" answer: a missing file, an altered blob, an unreadable file and a malformed id are
    /// all reported identically, so no caller can turn a storage fault into text that names a local path.
    /// </para>
    /// <para>
    /// The returned stream is asynchronous, sequential and read-only, and shares read access with anything
    /// else already holding the same immutable, content-addressed file.
    /// </para>
    /// </summary>
    public async Task<Stream?> OpenVerifiedStreamAsync(
        string blobId,
        long? maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidBlobId(blobId))
        {
            return null;
        }

        var path = GetBlobPath(blobId);
        FileStream? stream = null;
        try
        {
            stream = await OpenSharedReadStreamAsync(path, cancellationToken).ConfigureAwait(false);
            if (maxBytes is { } bound && stream.Length > bound)
                return null;
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(hash), blobId[BlobIdPrefix.Length..], StringComparison.OrdinalIgnoreCase))
                return null;
            stream.Position = 0;
            var verified = stream;
            stream = null; // Transfer the verified handle to the caller.
            return verified;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<FileStream> OpenSharedReadStreamAsync(string path, CancellationToken cancellationToken)
    {
        // A concurrent atomic publication can briefly expose the destination while its Windows
        // rename handle still denies sharing. Retry only that open failure, never a bad hash or
        // an arbitrary I/O error. Keep FileShare.Read so verified bytes remain pinned afterward.
        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException error) when (OperatingSystem.IsWindows()
                && error.HResult == unchecked((int)0x80070020) && retry < 5)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The committed length of a verified blob, or null when its bytes cannot be established. Callers that
    /// need both a size and a handle should use <see cref="OpenVerifiedStreamAsync"/> with the bound rather
    /// than composing the two calls themselves.
    /// </summary>
    public async Task<long?> GetVerifiedLengthAsync(string blobId, CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenVerifiedStreamAsync(blobId, null, cancellationToken).ConfigureAwait(false);
        return stream?.Length;
    }

    public async Task<bool> VerifyBlobAsync(string blobId, CancellationToken cancellationToken = default)
    {
        _ = GetHex(blobId); // Preserve the public invalid-id exception contract.
        await using var stream = await OpenVerifiedStreamAsync(blobId, null, cancellationToken).ConfigureAwait(false);
        return stream is not null;
    }

    private static string GetHex(string blobId)
    {
        if (!IsValidBlobId(blobId))
        {
            throw new ArgumentException(
                "Blob id must have the form 'sha256:<64 lowercase hex characters>'.",
                nameof(blobId));
        }

        return blobId[BlobIdPrefix.Length..];
    }
}
