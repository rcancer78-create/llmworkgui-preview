using System.Buffers;
using System.IO.Compression;
using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;

namespace LLMWorkGUI.Infrastructure.Workflows;

public sealed class WorkflowPreviewService : IWorkflowPreviewService
{
    private const int BinarySniffSizeBytes = 8192;

    private static readonly string[] PrimaryDocumentationNames =
    {
        "README.md",
        "SKILL.md",
        "WORKFLOW.md",
        "INSTRUCTIONS.md"
    };

    private static readonly byte[] Utf8Preamble = { 0xEF, 0xBB, 0xBF };
    private static readonly byte[] Utf16LittleEndianPreamble = { 0xFF, 0xFE };
    private static readonly byte[] Utf16BigEndianPreamble = { 0xFE, 0xFF };

    private readonly WorkflowBlobStore _blobStore;

    public WorkflowPreviewService(WorkflowBlobStore blobStore)
    {
        ArgumentNullException.ThrowIfNull(blobStore);

        _blobStore = blobStore;
    }

    public async Task<WorkflowTreePreview> GetTreePreviewAsync(
        string blobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobId);

        await using var blobStream = await _blobStore
            .GetBlobStreamAsync(blobId, cancellationToken)
            .ConfigureAwait(false);

        List<WorkflowTreeNode> nodes;
        string? primaryDocumentationPath = null;
        string? primaryDocumentationContent = null;
        var isPrimaryDocumentationTruncated = false;

        try
        {
            using var archive = new ZipArchive(blobStream, ZipArchiveMode.Read, leaveOpen: true);

            nodes = BuildNodes(archive, cancellationToken);

            var primaryEntry = FindPrimaryDocumentationEntry(archive);

            if (primaryEntry is not null)
            {
                primaryDocumentationPath = NormalizeEntryPath(primaryEntry.FullName);

                (primaryDocumentationContent, isPrimaryDocumentationTruncated) =
                    await ReadEntryTextAsync(primaryEntry, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidDataException exception)
        {
            throw new WorkflowValidationException(WorkflowValidationFailure.InvalidArchive, "Workflow blob is not a valid ZIP archive.", exception);
        }

        await EnsureSourceBlobIntactAsync(blobId, cancellationToken).ConfigureAwait(false);

        return new WorkflowTreePreview(
            blobId,
            nodes,
            primaryDocumentationPath,
            primaryDocumentationContent,
            isPrimaryDocumentationTruncated);
    }

    public async Task<WorkflowFilePreview> GetFilePreviewAsync(
        string blobId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobId);

        var normalizedPath = SanitizeRequestedPath(relativePath);

        await using var blobStream = await _blobStore
            .GetBlobStreamAsync(blobId, cancellationToken)
            .ConfigureAwait(false);

        WorkflowFilePreview preview;

        try
        {
            using var archive = new ZipArchive(blobStream, ZipArchiveMode.Read, leaveOpen: true);

            var entry = FindFileEntry(archive, normalizedPath)
                ?? throw new FileNotFoundException(
                    "The requested file does not exist in the workflow archive.",
                    normalizedPath);

            preview = await BuildFilePreviewAsync(blobId, normalizedPath, entry, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new WorkflowValidationException(WorkflowValidationFailure.InvalidArchive, "Workflow blob is not a valid ZIP archive.", exception);
        }

        await EnsureSourceBlobIntactAsync(blobId, cancellationToken).ConfigureAwait(false);

        return preview;
    }

    private static string SanitizeRequestedPath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        try
        {
            return InputSanitizer.NormalizeArchiveEntryPath(relativePath);
        }
        catch (PathTraversalException exception)
        {
            throw new WorkflowValidationException("Requested file path is not allowed.", exception);
        }
    }

    private static ZipArchiveEntry? FindFileEntry(ZipArchive archive, string normalizedPath)
    {
        foreach (var entry in archive.Entries)
        {
            if (IsDirectoryEntry(entry))
            {
                continue;
            }

            var entryPath = NormalizeEntryPath(entry.FullName);

            if (entryPath is not null && string.Equals(entryPath, normalizedPath, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    private static List<WorkflowTreeNode> BuildNodes(ZipArchive archive, CancellationToken cancellationToken)
    {
        var fileNodes = new List<WorkflowTreeNode>();
        var directoryPaths = new HashSet<string>(StringComparer.Ordinal);
        var directoryTimestamps = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = NormalizeEntryPath(entry.FullName);

            if (path is null)
            {
                continue;
            }

            if (IsDirectoryEntry(entry))
            {
                directoryPaths.Add(path);
                directoryTimestamps[path] = MaxTimestamp(
                    directoryTimestamps.GetValueOrDefault(path),
                    entry.LastWriteTime);

                AddAncestorDirectories(directoryPaths, path);
                continue;
            }

            fileNodes.Add(new WorkflowTreeNode(
                path,
                GetName(path),
                IsDirectory: false,
                entry.Length,
                entry.CompressedLength,
                entry.LastWriteTime.ToUniversalTime()));

            AddAncestorDirectories(directoryPaths, path);
        }

        var directorySizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var directoryCompressedSizes = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var file in fileNodes)
        {
            var parent = GetDirectoryName(file.Path);

            while (parent.Length > 0)
            {
                directorySizes[parent] = directorySizes.GetValueOrDefault(parent) + file.SizeBytes;
                directoryCompressedSizes[parent] =
                    directoryCompressedSizes.GetValueOrDefault(parent) + file.CompressedSizeBytes;
                directoryTimestamps[parent] = MaxTimestamp(directoryTimestamps.GetValueOrDefault(parent), file.LastModifiedUtc);
                parent = GetDirectoryName(parent);
            }
        }

        var nodes = new List<WorkflowTreeNode>(fileNodes.Count + directoryPaths.Count);
        nodes.AddRange(fileNodes);

        foreach (var directoryPath in directoryPaths)
        {
            nodes.Add(new WorkflowTreeNode(
                directoryPath,
                GetName(directoryPath),
                IsDirectory: true,
                directorySizes.GetValueOrDefault(directoryPath),
                directoryCompressedSizes.GetValueOrDefault(directoryPath),
                directoryTimestamps.GetValueOrDefault(directoryPath).ToUniversalTime()));
        }

        nodes.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));

        return nodes;
    }

    private static ZipArchiveEntry? FindPrimaryDocumentationEntry(ZipArchive archive)
    {
        var rootMarkdown = new List<(string Name, ZipArchiveEntry Entry)>();

        foreach (var entry in archive.Entries)
        {
            if (IsDirectoryEntry(entry))
            {
                continue;
            }

            var path = NormalizeEntryPath(entry.FullName);

            if (path is null || path.Contains('/') || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rootMarkdown.Add((path, entry));
        }

        if (rootMarkdown.Count == 0)
        {
            return null;
        }

        foreach (var name in PrimaryDocumentationNames)
        {
            foreach (var (candidateName, entry) in rootMarkdown)
            {
                if (string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }

        return rootMarkdown
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Select(candidate => candidate.Entry)
            .First();
    }

    private static async Task<(string? Content, bool IsTruncated)> ReadEntryTextAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(IWorkflowPreviewService.MaxPreviewSizeBytes + 1);

        try
        {
            await using var stream = entry.Open();

            var total = 0;

            while (total <= IWorkflowPreviewService.MaxPreviewSizeBytes)
            {
                var read = await stream
                    .ReadAsync(
                        buffer.AsMemory(total, IWorkflowPreviewService.MaxPreviewSizeBytes + 1 - total),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            var isTruncated = total > IWorkflowPreviewService.MaxPreviewSizeBytes;
            var contentLength = Math.Min(total, IWorkflowPreviewService.MaxPreviewSizeBytes);

            return (DecodeText(buffer.AsSpan(0, contentLength)), isTruncated);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<WorkflowFilePreview> BuildFilePreviewAsync(
        string blobId,
        string normalizedPath,
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(IWorkflowPreviewService.MaxPreviewSizeBytes + 1);

        try
        {
            await using var stream = entry.Open();

            var total = 0;

            while (total <= IWorkflowPreviewService.MaxPreviewSizeBytes)
            {
                var read = await stream
                    .ReadAsync(
                        buffer.AsMemory(total, IWorkflowPreviewService.MaxPreviewSizeBytes + 1 - total),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (ContainsNullByte(buffer.AsSpan(0, Math.Min(total, BinarySniffSizeBytes))))
            {
                return new WorkflowFilePreview(
                    blobId,
                    normalizedPath,
                    entry.Length,
                    IsBinary: true,
                    IsTruncated: false,
                    Content: null);
            }

            var isTruncated = total > IWorkflowPreviewService.MaxPreviewSizeBytes;
            var contentLength = Math.Min(total, IWorkflowPreviewService.MaxPreviewSizeBytes);

            return new WorkflowFilePreview(
                blobId,
                normalizedPath,
                entry.Length,
                IsBinary: false,
                isTruncated,
                DecodeText(buffer.AsSpan(0, contentLength)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async Task EnsureSourceBlobIntactAsync(string blobId, CancellationToken cancellationToken)
    {
        var isIntact = await _blobStore.VerifyBlobAsync(blobId, cancellationToken).ConfigureAwait(false);

        if (!isIntact)
        {
            throw new InvalidDataException(
                $"Workflow blob '{blobId}' failed its post-operation SHA-256 integrity check.");
        }
    }

    private static string? NormalizeEntryPath(string entryName)
    {
        try
        {
            return InputSanitizer.NormalizeArchiveEntryPath(entryName);
        }
        catch (PathTraversalException)
        {
            return null;
        }
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    {
        return entry.FullName.EndsWith('/')
            || entry.FullName.EndsWith('\\')
            || entry.Name.Length == 0;
    }

    private static void AddAncestorDirectories(HashSet<string> directories, string path)
    {
        var parent = GetDirectoryName(path);

        while (parent.Length > 0)
        {
            directories.Add(parent);
            parent = GetDirectoryName(parent);
        }
    }

    private static string GetDirectoryName(string path)
    {
        var separatorIndex = path.LastIndexOf('/');

        return separatorIndex <= 0 ? string.Empty : path[..separatorIndex];
    }

    private static string GetName(string path)
    {
        var separatorIndex = path.LastIndexOf('/');

        return separatorIndex < 0 ? path : path[(separatorIndex + 1)..];
    }

    private static DateTimeOffset MaxTimestamp(DateTimeOffset current, DateTimeOffset candidate)
    {
        return candidate > current ? candidate : current;
    }

    private static bool ContainsNullByte(ReadOnlySpan<byte> bytes)
    {
        return bytes.IndexOf((byte)0) >= 0;
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8Preamble))
        {
            return Encoding.UTF8.GetString(bytes[Utf8Preamble.Length..]);
        }

        if (bytes.StartsWith(Utf16LittleEndianPreamble))
        {
            return Encoding.Unicode.GetString(bytes[Utf16LittleEndianPreamble.Length..]);
        }

        if (bytes.StartsWith(Utf16BigEndianPreamble))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[Utf16BigEndianPreamble.Length..]);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
