using System.Buffers;
using System.Security.Cryptography;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Storage;

namespace LLMWorkGUI.Infrastructure.Workflows;

public sealed class WorkflowExportService : IWorkflowExportService
{
    private const int CopyBufferSize = 81920;

    private readonly IWorkflowVersionRepository _versionRepository;
    private readonly WorkflowBlobStore _blobStore;

    public WorkflowExportService(
        IWorkflowVersionRepository versionRepository,
        WorkflowBlobStore blobStore)
    {
        ArgumentNullException.ThrowIfNull(versionRepository);
        ArgumentNullException.ThrowIfNull(blobStore);

        _versionRepository = versionRepository;
        _blobStore = blobStore;
    }

    public async Task<Stream> OpenVersionExportStreamAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var version = await GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);

        var verified = await _blobStore.VerifyBlobAsync(version.BlobId, cancellationToken).ConfigureAwait(false);

        if (!verified)
        {
            throw new InvalidDataException("Workflow version blob failed its integrity check.");
        }

        return await _blobStore.GetBlobStreamAsync(version.BlobId, cancellationToken).ConfigureAwait(false);
    }

    public Task ExportToFileAsync(
        string versionId,
        string destinationFilePath,
        CancellationToken cancellationToken = default) =>
        ExportCoreAsync(versionId, destinationFilePath, overwrite: true, cancellationToken);

    public Task ExportToNewFileAsync(string versionId, string destinationFilePath, CancellationToken cancellationToken = default) =>
        ExportCoreAsync(versionId, destinationFilePath, overwrite: false, cancellationToken);

    private async Task ExportCoreAsync(string versionId, string destinationFilePath, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        var version = await GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        var destinationFullPath = Path.GetFullPath(destinationFilePath);
        var destinationDirectory = Path.GetDirectoryName(destinationFullPath);

        if (string.IsNullOrEmpty(destinationDirectory))
        {
            throw new ArgumentException(
                "Destination path must include a directory.",
                nameof(destinationFilePath));
        }

        Directory.CreateDirectory(destinationDirectory);
        var tempPath = AtomicFile.CreateTempPath(destinationDirectory);

        try
        {
            await using (var source = await OpenVersionExportStreamAsync(versionId, cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                string exportedBlobId;

                try
                {
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    int read;

                    while ((read = await source
                               .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        await destination
                            .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);

                        hasher.AppendData(buffer, 0, read);
                    }

                    exportedBlobId = WorkflowBlobStore.BlobIdPrefix
                        + Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                if (!string.Equals(exportedBlobId, version.OriginalHash, StringComparison.Ordinal)
                    || !string.Equals(exportedBlobId, version.BlobId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Exported workflow archive does not match the recorded original hash.");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            File.Move(tempPath, destinationFullPath, overwrite);
        }
        catch
        {
            AtomicFile.TryDelete(tempPath);
            throw;
        }
    }

    private async Task<WorkflowVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var version = await _versionRepository.GetByIdAsync(versionId, cancellationToken).ConfigureAwait(false);

        return version ?? throw new FileNotFoundException("Workflow version was not found.");
    }
}
