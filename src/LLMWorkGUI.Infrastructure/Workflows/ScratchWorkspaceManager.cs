using System.IO.Compression;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;

namespace LLMWorkGUI.Infrastructure.Workflows;

public sealed class ScratchWorkspaceManager : IScratchWorkspaceManager
{
    public const string ScratchDirectoryName = "scratch";

    private const int CopyBufferSize = 81920;
    private const int MinBytesForRatioCheck = 1024 * 1024;
    private const int MaxScopeIdLength = 64;

    private readonly WorkflowBlobStore _blobStore;
    private readonly IWorkflowArchiveValidator _archiveValidator;
    private readonly string _scratchRootDirectory;
    private readonly string? _projectRootDirectory;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScratchWorkspace, object> _issuedWorkspaces = new();

    public ScratchWorkspaceManager(
        WorkflowBlobStore blobStore,
        IWorkflowArchiveValidator archiveValidator,
        string? projectRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(blobStore);
        ArgumentNullException.ThrowIfNull(archiveValidator);

        _blobStore = blobStore;
        _archiveValidator = archiveValidator;
        _scratchRootDirectory = Path.Combine(blobStore.AppDataDirectory, ScratchDirectoryName);
        _projectRootDirectory = string.IsNullOrWhiteSpace(projectRootDirectory)
            ? null
            : InputSanitizer.CanonicalizeBaseDirectory(projectRootDirectory);
    }

    public Task<ScratchWorkspace> CreateWorkspaceAsync(
        ScratchScope scope,
        string scopeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ValidateScope(scope);
        ValidateScopeId(scopeId);

        var relativePath = scope.ToWireName() + "/" + scopeId + "-" + Guid.NewGuid().ToString("N");
        string resolvedPath;

        try
        {
            resolvedPath = InputSanitizer.ResolveSafePath(_scratchRootDirectory, relativePath);
        }
        catch (PathTraversalException exception)
        {
            throw new WorkflowValidationException("Scratch workspace path is not allowed.", exception);
        }

        EnsureIsolation(resolvedPath);
        InputSanitizer.EnsureNoReparsePoints(_blobStore.AppDataDirectory, resolvedPath);

        var ownership = scope == ScratchScope.Adaptation ? new AdaptationScratchOwnership(_blobStore.AppDataDirectory) : null;
        ownership?.Register(resolvedPath);
        Directory.CreateDirectory(resolvedPath);

        var workspace = new ScratchWorkspace(resolvedPath, scope, scopeId,
            ownership is null ? null : () => ownership.ValidateCleanup(resolvedPath),
            ownership is null ? null : () => ownership.Complete(resolvedPath));
        _issuedWorkspaces.Add(workspace, new object());
        return Task.FromResult(workspace);
    }

    public Task<int> RecoverAbandonedAdaptationWorkspacesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new AdaptationScratchOwnership(_blobStore.AppDataDirectory).Recover(cancellationToken));

    public async Task ExtractBlobToWorkspaceAsync(
        string blobId,
        ScratchWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!_issuedWorkspaces.TryGetValue(workspace, out _))
            throw new WorkflowValidationException("The scratch workspace was not issued by this manager.");
        EnsureIsolation(workspace.DirectoryPath);
        InputSanitizer.EnsureNoReparsePoints(_blobStore.AppDataDirectory, workspace.DirectoryPath);

        if (workspace.IsCleanedUp)
        {
            throw new InvalidOperationException("Scratch workspace has already been cleaned up.");
        }

        await using var blobStream = await _blobStore
            .GetBlobStreamAsync(blobId, cancellationToken)
            .ConfigureAwait(false);

        var archiveSizeBytes = blobStream.Length;

        try
        {
            using var archive = new ZipArchive(blobStream, ZipArchiveMode.Read, leaveOpen: true);

            _archiveValidator.ValidateArchive(archive, archiveSizeBytes, cancellationToken);

            var budget = new ExtractionBudget();

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsDirectoryEntry(entry))
                {
                    continue;
                }

                await ExtractEntryAsync(workspace.DirectoryPath, entry, budget, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception)
        {
            await workspace.CleanupAfterFailureAsync(exception).ConfigureAwait(false);
            throw;
        }
        catch (WorkflowValidationException exception)
        {
            await workspace.CleanupAfterFailureAsync(exception).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or PathTraversalException
            or NotSupportedException)
        {
            await workspace.CleanupAfterFailureAsync(exception).ConfigureAwait(false);

            var failure = new WorkflowValidationException(
                "Workflow archive could not be extracted safely into the scratch workspace.",
                exception);
            if (exception.Data["ScratchCleanupPending"] is true) failure.Data["ScratchCleanupPending"] = true;
            throw failure;
        }

        try
        {
            await PostOperationSourceHashCheckAsync(blobId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            await workspace.CleanupAfterFailureAsync(exception).ConfigureAwait(false);
            throw;
        }
        catch (InvalidDataException exception)
        {
            await workspace.CleanupAfterFailureAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task PostOperationSourceHashCheckAsync(
        string blobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobId);

        var isIntact = await _blobStore.VerifyBlobAsync(blobId, cancellationToken).ConfigureAwait(false);

        if (!isIntact)
        {
            throw new InvalidDataException(
                $"Workflow blob '{blobId}' failed its post-operation SHA-256 integrity check.");
        }
    }

    private static async Task ExtractEntryAsync(
        string workspaceDirectory,
        ZipArchiveEntry entry,
        ExtractionBudget budget,
        CancellationToken cancellationToken)
    {
        string resolvedPath;

        try
        {
            resolvedPath = InputSanitizer.ResolveSafePath(workspaceDirectory, entry.FullName);
            InputSanitizer.EnsureNoReparsePoints(workspaceDirectory, resolvedPath);
        }
        catch (PathTraversalException exception)
        {
            throw new WorkflowValidationException("Archive contains an entry path that is not allowed.", exception);
        }

        var parentDirectory = Path.GetDirectoryName(resolvedPath);

        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        await using var target = new FileStream(
            resolvedPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous);

        await using var source = entry.Open();

        var buffer = new byte[CopyBufferSize];
        long entryBytes = 0;

        int read;

        while ((read = await source
                   .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            entryBytes += read;
            budget.AddUncompressedBytes(read);

            EnsureWithinDynamicLimits(entry, entryBytes, budget.TotalUncompressedBytes);

            await target
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Flush(flushToDisk: true);
    }

    private static void EnsureWithinDynamicLimits(
        ZipArchiveEntry entry,
        long entryBytes,
        long totalBytes)
    {
        if (entryBytes > WorkflowImportLimits.MaxSingleFileBytes)
        {
            throw new WorkflowValidationException(
                "Archive contains an entry that exceeds the maximum allowed single file size while extracting.");
        }

        if (totalBytes > WorkflowImportLimits.MaxUncompressedTotalBytes)
        {
            throw new WorkflowValidationException(
                "Archive exceeds the maximum allowed total uncompressed size while extracting.");
        }

        if (entry.CompressedLength > 0 && entryBytes > MinBytesForRatioCheck)
        {
            var compressionRatio = entryBytes / (double)entry.CompressedLength;

            if (compressionRatio > WorkflowImportLimits.MaxCompressionRatio)
            {
                throw new WorkflowValidationException(
                    "Archive contains an entry with a compression ratio above the allowed limit while extracting.");
            }
        }
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    {
        return entry.FullName.EndsWith('/')
            || entry.FullName.EndsWith('\\')
            || entry.Name.Length == 0;
    }

    private static void ValidateScope(ScratchScope scope)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scratch scope.");
        }
    }

    private static void ValidateScopeId(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new ArgumentException("Scope id must not be null, empty, or whitespace.", nameof(scopeId));
        }

        if (scopeId.Length > MaxScopeIdLength)
        {
            throw new ArgumentException(
                $"Scope id must not exceed {MaxScopeIdLength} characters.",
                nameof(scopeId));
        }

        if (scopeId is "." or "..")
        {
            throw new ArgumentException("Scope id must not be a relative path segment.", nameof(scopeId));
        }

        foreach (var character in scopeId)
        {
            if (!IsAllowedScopeIdCharacter(character))
            {
                throw new ArgumentException(
                    $"Scope id contains an unsupported character '{character}'.",
                    nameof(scopeId));
            }
        }
    }

    private static bool IsAllowedScopeIdCharacter(char character)
    {
        return character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-'
            or '_'
            or '.';
    }

    private void EnsureIsolation(string resolvedPath)
    {
        if (IsInsideDirectory(_blobStore.BlobsDirectory, resolvedPath))
        {
            throw new WorkflowValidationException("Scratch workspace must not be created inside the blob store.");
        }

        if (_projectRootDirectory is not null && IsInsideDirectory(_projectRootDirectory, resolvedPath))
        {
            throw new WorkflowValidationException("Scratch workspace must not be created inside the project root.");
        }
    }

    private static bool IsInsideDirectory(string parentDirectory, string candidatePath)
    {
        var canonicalParent = InputSanitizer.CanonicalizeBaseDirectory(parentDirectory);
        var canonicalCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(canonicalParent, canonicalCandidate, comparison))
        {
            return true;
        }

        return canonicalCandidate.StartsWith(
            canonicalParent + Path.DirectorySeparatorChar,
            comparison);
    }

    private sealed class ExtractionBudget
    {
        public long TotalUncompressedBytes { get; private set; }

        public void AddUncompressedBytes(int bytes)
        {
            TotalUncompressedBytes += bytes;
        }
    }
}
