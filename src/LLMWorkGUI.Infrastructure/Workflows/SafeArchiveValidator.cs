using System.IO.Compression;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Security;

namespace LLMWorkGUI.Infrastructure.Workflows;

public sealed class SafeArchiveValidator : IWorkflowArchiveValidator
{
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymlinkFileType = 0xA000;

    public void ValidateArchive(
        ZipArchive archive,
        long archiveSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        if (archiveSizeBytes > WorkflowImportLimits.MaxArchiveSizeBytes)
        {
            throw new WorkflowValidationException("Archive exceeds the maximum allowed archive size.");
        }

        var entries = archive.Entries;

        if (entries.Count > WorkflowImportLimits.MaxFileCount)
        {
            throw new WorkflowValidationException("Archive contains more entries than the allowed file count limit.");
        }

        long totalUncompressedBytes = 0;
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = ValidateEntryPath(entry.FullName);
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (!explicitPaths.Add(path) || files.Contains(path) || (!isDirectory && directories.Contains(path)))
                throw new WorkflowValidationException("Archive contains colliding entry paths.");
            if (isDirectory) directories.Add(path);
            else files.Add(path);
            for (var separator = path.LastIndexOf('/'); separator >= 0; separator = path.LastIndexOf('/'))
            {
                path = path[..separator];
                if (files.Contains(path)) throw new WorkflowValidationException("Archive contains a file/directory path collision.");
                directories.Add(path);
            }
            ValidateEntryIsNotSymlink(entry);

            if (entry.Length > WorkflowImportLimits.MaxSingleFileBytes)
            {
                throw new WorkflowValidationException(
                    "Archive contains an entry that exceeds the maximum allowed single file size.");
            }

            totalUncompressedBytes += entry.Length;

            if (totalUncompressedBytes > WorkflowImportLimits.MaxUncompressedTotalBytes)
            {
                throw new WorkflowValidationException(
                    "Archive exceeds the maximum allowed total uncompressed size.");
            }

            var compressionRatio = entry.Length / (double)Math.Max(entry.CompressedLength, 1);

            if (compressionRatio > WorkflowImportLimits.MaxCompressionRatio)
            {
                throw new WorkflowValidationException(
                    "Archive contains an entry with a compression ratio above the allowed limit.");
            }
        }
    }

    private static string ValidateEntryPath(string entryName)
    {
        try
        {
            return InputSanitizer.NormalizeArchiveEntryPath(entryName);
        }
        catch (PathTraversalException exception)
        {
            throw new WorkflowValidationException("Archive contains an entry path that is not allowed.", exception);
        }
    }

    private static void ValidateEntryIsNotSymlink(ZipArchiveEntry entry)
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;

        if (unixFileType == UnixSymlinkFileType)
        {
            throw new WorkflowValidationException("Archive contains a symbolic link entry, which is not allowed.");
        }
    }
}
