using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Workflows;

public sealed class WorkflowImportService : IWorkflowImportService
{
    private const int CopyBufferSize = 81920;
    private const string WorkflowsDirectoryName = "workflows";
    private const string SpoolDirectoryName = "spool";

    private static readonly DateTimeOffset DeterministicEntryTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IWorkflowPackageRepository _packageRepository;
    private readonly WorkflowBlobStore _blobStore;
    private readonly IWorkflowArchiveValidator _archiveValidator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowImportService> _logger;
    private readonly IWorkflowManifestParser? _manifestParser;
    private readonly string _spoolDirectory;

    // Isolated deterministic I/O fixtures can observe the real copy and use smaller declared budgets.
    // Production composition does not set this internal hook; its fixed limits and file ownership remain.
    internal DirectoryImportTestHooks? DirectoryTestHooks { get; init; }

    public WorkflowImportService(
        IWorkflowPackageRepository packageRepository,
        IWorkflowVersionRepository versionRepository,
        WorkflowBlobStore blobStore,
        IWorkflowArchiveValidator archiveValidator,
        TimeProvider timeProvider,
        ILogger<WorkflowImportService> logger,
        IWorkflowManifestParser? manifestParser = null)
    {
        ArgumentNullException.ThrowIfNull(packageRepository);
        ArgumentNullException.ThrowIfNull(versionRepository);
        ArgumentNullException.ThrowIfNull(blobStore);
        ArgumentNullException.ThrowIfNull(archiveValidator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _packageRepository = packageRepository;
        _blobStore = blobStore;
        _archiveValidator = archiveValidator;
        _timeProvider = timeProvider;
        _logger = logger;
        _manifestParser = manifestParser;
        _spoolDirectory = Path.Combine(blobStore.AppDataDirectory, WorkflowsDirectoryName, SpoolDirectoryName);
    }

    public async Task<WorkflowImportResult> ImportZipAsync(
        Stream archiveStream,
        string packageName,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        EnsureSpoolDirectory();

        var spoolPath = AtomicFile.CreateTempPath(_spoolDirectory);

        try
        {
            var (length, blobId) = await SpoolArchiveAsync(archiveStream, spoolPath, cancellationToken)
                .ConfigureAwait(false);

            return await ImportSpooledArchiveAsync(
                    spoolPath,
                    length,
                    blobId,
                    WorkflowSourceType.ZipArchive,
                    packageName,
                    description,
                    tags,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            AtomicFile.TryDelete(spoolPath);
        }
    }

    public async Task<WorkflowImportResult> ImportDirectoryAsync(
        string directoryPath,
        string packageName,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        var canonicalDirectory = Path.GetFullPath(directoryPath);

        if (!Directory.Exists(canonicalDirectory))
        {
            throw new DirectoryNotFoundException("The directory to import was not found.");
        }

        EnsureNoReparsePointsOrThrow(canonicalDirectory, canonicalDirectory);

        EnsureSpoolDirectory();

        var spoolPath = AtomicFile.CreateTempPath(_spoolDirectory);

        try
        {
            await CreateDeterministicArchiveAsync(canonicalDirectory, spoolPath, cancellationToken)
                .ConfigureAwait(false);

            var length = new FileInfo(spoolPath).Length;

            if (length > (DirectoryTestHooks?.MaxArchiveSizeBytes ?? WorkflowImportLimits.MaxArchiveSizeBytes))
            {
                throw new WorkflowValidationException(
                    "Generated archive exceeds the maximum allowed archive size.");
            }

            var blobId = await ComputeBlobIdAsync(spoolPath, cancellationToken).ConfigureAwait(false);

            return await ImportSpooledArchiveAsync(
                    spoolPath,
                    length,
                    blobId,
                    WorkflowSourceType.Directory,
                    packageName,
                    description,
                    tags,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            AtomicFile.TryDelete(spoolPath);
        }
    }

    private async Task<WorkflowImportResult> ImportSpooledArchiveAsync(
        string spoolPath,
        long archiveSizeBytes,
        string blobId,
        WorkflowSourceType sourceType,
        string packageName,
        string? description,
        IReadOnlyList<string>? tags,
        CancellationToken cancellationToken)
    {
        var store = _packageRepository as IWorkflowImportStore
            ?? throw new WorkflowValidationException("Atomic workflow import storage is unavailable.");

        ValidateArchive(spoolPath, archiveSizeBytes, cancellationToken);

        var manifest = await ParseManifestAsync(spoolPath, cancellationToken).ConfigureAwait(false);

        await SaveSpoolBlobAsync(spoolPath, blobId, cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var package = new WorkflowPackage(
            "wfp-" + Guid.NewGuid().ToString("N"),
            packageName,
            description,
            tags ?? Array.Empty<string>(),
            sourceType,
            blobId,
            blobId,
            now,
            now);

        var version = CreateVersion(package.Id, blobId, sourceType, now, manifest);

        var result = await store.CommitImportAsync(package, version, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Imported workflow package {PackageId}; duplicate={IsDuplicate}.",
            result.Package.Id, result.IsDuplicate);
        return result;
    }

    private async Task<WorkflowBlob> SaveSpoolBlobAsync(
        string spoolPath,
        string expectedBlobId,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            spoolPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var blob = await _blobStore.SaveBlobAsync(source, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(blob.BlobId, expectedBlobId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Spooled archive hash does not match the computed blob id.");
        }

        return blob;
    }

    private void ValidateArchive(string spoolPath, long archiveSizeBytes, CancellationToken cancellationToken)
    {
        using var archiveStream = new FileStream(
            spoolPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.SequentialScan);

        try
        {
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

            _archiveValidator.ValidateArchive(archive, archiveSizeBytes, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new WorkflowValidationException(WorkflowValidationFailure.InvalidArchive, "Archive is not a valid ZIP archive.", exception);
        }
    }

    private static async Task<(long Length, string BlobId)> SpoolArchiveAsync(
        Stream source,
        string spoolPath,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;

            await using (var spool = new FileStream(
                             spoolPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous))
            {
                int read;

                while ((read = await source
                           .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    length += read;

                    if (length > WorkflowImportLimits.MaxArchiveSizeBytes)
                    {
                        throw new WorkflowValidationException(
                            "Archive exceeds the maximum allowed archive size.");
                    }

                    await spool
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);

                    hasher.AppendData(buffer, 0, read);
                }

                await spool.FlushAsync(cancellationToken).ConfigureAwait(false);
                spool.Flush(flushToDisk: true);
            }

            var blobId = WorkflowBlobStore.BlobIdPrefix
                + Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();

            return (length, blobId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async Task CreateDeterministicArchiveAsync(
        string rootDirectory,
        string spoolPath,
        CancellationToken cancellationToken)
    {
        var files = CollectFiles(rootDirectory, cancellationToken);
        files.Sort(static (left, right) => string.CompareOrdinal(left.EntryPath, right.EntryPath));

        await using var spool = DirectoryTestHooks?.CreateSpool?.Invoke(spoolPath) ?? new FileStream(
            spoolPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous);

        using var boundedSpool = new DirectoryArchiveBudgetStream(spool,
            DirectoryTestHooks?.MaxArchiveSizeBytes ?? WorkflowImportLimits.MaxArchiveSizeBytes);
        var archive = new ZipArchive(boundedSpool, ZipArchiveMode.Create, leaveOpen: true);
        Exception? archiveFailure = null;
        long copiedTotal = 0;
        try
        {
            foreach (var (entryPath, fullPath) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                entry.LastWriteTime = DeterministicEntryTimestamp;

                DirectoryTestHooks?.BeforeFileCopy?.Invoke(fullPath);
                // Metadata enumeration does not keep this path bound to the observed tree.
                // Refuse a reparse point introduced before copy; this is a fresh path check,
                // not an atomic guarantee against hostile retargeting after the check.
                EnsureNoReparsePointsOrThrow(rootDirectory, fullPath);
                await using var source = DirectoryTestHooks?.OpenSource?.Invoke(fullPath) ?? new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var destination = entry.Open();
                Exception? copyFailure = null;
                try
                {
                    copiedTotal = await CopyDirectoryFileAsync(source, destination, copiedTotal,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) { copyFailure = error; throw; }
                finally
                {
                    if (copyFailure is null) await destination.DisposeAsync().ConfigureAwait(false);
                    else
                    {
                        try { await destination.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception) { copyFailure.Data["ArchiveEntryFinalizationIncomplete"] = true; }
                    }
                }
            }
        }
        catch (Exception error) { archiveFailure = error; throw; }
        finally
        {
            if (archiveFailure is null) archive.Dispose();
            else
            {
                try { archive.Dispose(); }
                catch (Exception) { archiveFailure.Data["ArchiveFinalizationIncomplete"] = true; }
            }
        }

        await spool.FlushAsync(cancellationToken).ConfigureAwait(false);
        spool.Flush(flushToDisk: true);
    }

    private async Task<long> CopyDirectoryFileAsync(Stream source, Stream destination, long copiedTotal,
        CancellationToken cancellationToken)
    {
        var fileLimit = DirectoryTestHooks?.MaxSingleFileBytes ?? WorkflowImportLimits.MaxSingleFileBytes;
        var totalLimit = DirectoryTestHooks?.MaxUncompressedTotalBytes ?? WorkflowImportLimits.MaxUncompressedTotalBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long copiedFile = 0;
        try
        {
            while (true)
            {
                var remaining = Math.Min(fileLimit - copiedFile, totalLimit - copiedTotal);
                if (remaining < 0) throw DirectoryCopyLimitExceeded();
                // Read one sentinel byte beyond the remaining budget, never a whole unbounded chunk.
                var readCapacity = (int)Math.Min(remaining, (long)buffer.Length - 1) + 1;
                var read = await source.ReadAsync(buffer.AsMemory(0, readCapacity), cancellationToken).ConfigureAwait(false);
                if (read == 0) return copiedTotal;
                if (read > remaining) throw DirectoryCopyLimitExceeded();
                copiedFile += read;
                copiedTotal += read;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static WorkflowValidationException DirectoryCopyLimitExceeded() =>
        new("Directory file content exceeds the allowed actual file or total size.");

    private List<(string EntryPath, string FullPath)> CollectFiles(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        var files = new List<(string EntryPath, string FullPath)>();
        var pendingDirectories = new Stack<string>();
        var directoryCount = 1;
        long totalBytes = 0;

        pendingDirectories.Push(rootDirectory);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();

            foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++directoryCount > WorkflowImportLimits.MaxFileCount)
                    throw new WorkflowValidationException("Directory exceeds the allowed directory count.");
                if (IsReparsePoint(directory))
                {
                    throw new WorkflowValidationException("Directory import tree contains a reparse point.");
                }

                pendingDirectories.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsReparsePoint(file))
                {
                    throw new WorkflowValidationException("Directory import tree contains a reparse point.");
                }

                string entryPath;

                try
                {
                    entryPath = InputSanitizer.NormalizeArchiveEntryPath(
                        Path.GetRelativePath(rootDirectory, file));
                }
                catch (PathTraversalException)
                {
                    throw new WorkflowValidationException("Directory contains an entry path that is not allowed.");
                }

                var fileLength = new FileInfo(file).Length;

                if (fileLength > (DirectoryTestHooks?.MaxSingleFileBytes ?? WorkflowImportLimits.MaxSingleFileBytes))
                {
                    throw new WorkflowValidationException(
                        "Directory contains a file that exceeds the maximum allowed single file size.");
                }

                totalBytes += fileLength;

                if (totalBytes > (DirectoryTestHooks?.MaxUncompressedTotalBytes ?? WorkflowImportLimits.MaxUncompressedTotalBytes))
                {
                    throw new WorkflowValidationException(
                        "Directory exceeds the maximum allowed total uncompressed size.");
                }

                if (files.Count >= WorkflowImportLimits.MaxFileCount)
                {
                    throw new WorkflowValidationException(
                        "Directory contains more files than the allowed file count limit.");
                }

                files.Add((entryPath, file));
            }
        }

        return files;
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static void EnsureNoReparsePointsOrThrow(string baseDirectory, string resolvedPath)
    {
        try
        {
            InputSanitizer.EnsureNoReparsePoints(baseDirectory, resolvedPath);
        }
        catch (PathTraversalException)
        {
            throw new WorkflowValidationException("Directory import tree contains a reparse point.");
        }
    }

    private static async Task<string> ComputeBlobIdAsync(string filePath, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int read;

            while ((read = await stream
                       .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }

            return WorkflowBlobStore.BlobIdPrefix
                + Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static WorkflowVersion CreateVersion(
        string packageId,
        string blobId,
        WorkflowSourceType sourceType,
        DateTimeOffset createdAtUtc,
        WorkflowManifest? manifest)
    {
        return new WorkflowVersion(
            "wfv-" + Guid.NewGuid().ToString("N"),
            packageId,
            1,
            blobId,
            blobId,
            sourceType,
            entrypointsJson: manifest?.EntrypointsJson,
            declaredRolesJson: manifest?.DeclaredRolesJson,
            bindingsJson: null,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            createdAtUtc,
            activatedAtUtc: null);
    }

    private async Task<WorkflowManifest?> ParseManifestAsync(
        string spoolPath,
        CancellationToken cancellationToken)
    {
        if (_manifestParser is null)
        {
            return null;
        }

        await using var stream = new FileStream(
            spoolPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await _manifestParser.ParseManifestAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSpoolDirectory()
    {
        Directory.CreateDirectory(_spoolDirectory);
    }
}
