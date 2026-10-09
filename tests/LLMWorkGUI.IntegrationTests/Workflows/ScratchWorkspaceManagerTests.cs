using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class ScratchWorkspaceManagerTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly WorkflowBlobStore _blobStore;
    private readonly ScratchWorkspaceManager _manager;

    [Fact]
    public async Task RepeatedScopeCreatesIsolatedWorkspaces()
    {
        await using var first = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "same-scope");
        await using var second = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "same-scope");
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(second.DirectoryPath, "keep.txt"), "preserved");
        await first.CleanupWorkspaceAsync();
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(second.DirectoryPath, "keep.txt")));
    }

    [Fact]
    public async Task ExtractRejectsCallerConstructedOutsideWorkspace()
    {
        var outside = _directory.GetPath("outside");
        Directory.CreateDirectory(outside);
        var canary = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(canary, "preserved");
        var workspace = new ScratchWorkspace(outside, ScratchScope.Preview, "forged");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => _manager.ExtractBlobToWorkspaceAsync("sha256:" + new string('a', 64), workspace));
        Assert.Equal("preserved", await File.ReadAllTextAsync(canary));
    }

    public ScratchWorkspaceManagerTests()
    {
        _blobStore = new WorkflowBlobStore(_directory.Root);
        _manager = new ScratchWorkspaceManager(_blobStore, new SafeArchiveValidator());
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public async Task CleanupWorkspace_FailedDeletionIsReportedAndCanBeRetried()
    {
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "locked-cleanup");
        var filePath = Path.Combine(workspace.DirectoryPath, "locked.txt");
        await File.WriteAllTextAsync(filePath, "synthetic scratch data");
        using (var locked = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => workspace.CleanupWorkspaceAsync());
            Assert.False(workspace.IsCleanedUp);
            Assert.True(Directory.Exists(workspace.DirectoryPath));
        }

        await workspace.CleanupWorkspaceAsync();
        Assert.True(workspace.IsCleanedUp);
        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task CreateWorkspaceAsync_CreatesDirectoryInsideScratchRoot()
    {
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "scope-123");

        var expectedPath = Path.Combine(_directory.Root, "scratch", "preview", "scope-123");

        Assert.StartsWith(expectedPath + "-", workspace.DirectoryPath, StringComparison.Ordinal);
        Assert.Equal(ScratchScope.Preview, workspace.Scope);
        Assert.Equal("scope-123", workspace.ScopeId);
        Assert.True(Directory.Exists(workspace.DirectoryPath));
        Assert.False(workspace.IsCleanedUp);
    }

    [Theory]
    [InlineData(ScratchScope.Preview, "preview")]
    [InlineData(ScratchScope.Draft, "draft")]
    [InlineData(ScratchScope.Adaptation, "adaptation")]
    [InlineData(ScratchScope.Run, "run")]
    public async Task CreateWorkspaceAsync_UsesLowercaseScopeSegments(ScratchScope scope, string segment)
    {
        var workspace = await _manager.CreateWorkspaceAsync(scope, "Scope_A.1");

        Assert.StartsWith(
            Path.Combine(_directory.Root, "scratch", segment, "Scope_A.1-") ,
            workspace.DirectoryPath, StringComparison.Ordinal);
        Assert.True(Directory.Exists(workspace.DirectoryPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("scope/child")]
    [InlineData("scope\\child")]
    [InlineData("scope:stream")]
    [InlineData("scope*1")]
    [InlineData("scope?1")]
    [InlineData("scope|1")]
    public async Task CreateWorkspaceAsync_RejectsInvalidScopeIds(string scopeId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _manager.CreateWorkspaceAsync(ScratchScope.Preview, scopeId));
    }

    [Fact]
    public async Task CreateWorkspaceAsync_RejectsOverlongScopeId()
    {
        var scopeId = new string('a', 65);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _manager.CreateWorkspaceAsync(ScratchScope.Preview, scopeId));
    }

    [Fact]
    public async Task CreateWorkspaceAsync_AcceptsScopeIdAtMaximumLength()
    {
        var scopeId = new string('a', 64);

        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, scopeId);

        Assert.Equal(scopeId, workspace.ScopeId);
    }

    [Fact]
    public async Task CreateWorkspaceAsync_RejectsUndefinedScope()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _manager.CreateWorkspaceAsync((ScratchScope)42, "scope-1"));
    }

    [Fact]
    public async Task CreateWorkspaceAsync_KeepsScratchOutsideBlobStore()
    {
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Run, "run-7");

        var blobsDirectory = Path.TrimEndingDirectorySeparator(_blobStore.BlobsDirectory);
        var directoryPath = Path.TrimEndingDirectorySeparator(workspace.DirectoryPath);

        Assert.False(directoryPath.StartsWith(blobsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(blobsDirectory, directoryPath);
    }

    [Fact]
    public async Task CreateWorkspaceAsync_RejectsScratchRootInsideProjectRoot()
    {
        var manager = new ScratchWorkspaceManager(
            _blobStore,
            new SafeArchiveValidator(),
            projectRootDirectory: _directory.Root);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => manager.CreateWorkspaceAsync(ScratchScope.Draft, "draft-1"));
    }

    [Fact]
    public async Task ScratchWorkspace_DisposeRemovesCreatedDirectory()
    {
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Draft, "draft-1");
        await File.WriteAllTextAsync(Path.Combine(workspace.DirectoryPath, "file.txt"), "content");

        workspace.Dispose();

        Assert.True(workspace.IsCleanedUp);
        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task ScratchWorkspace_CleanupWorkspaceAsyncIsIdempotent()
    {
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Draft, "draft-2");

        await workspace.CleanupWorkspaceAsync();
        await workspace.CleanupWorkspaceAsync();

        Assert.True(workspace.IsCleanedUp);
        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task ScratchWorkspace_DisposeAsyncRemovesCreatedDirectory()
    {
        string directoryPath;

        await using (var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Run, "run-1"))
        {
            directoryPath = workspace.DirectoryPath;
            Assert.True(Directory.Exists(directoryPath));
        }

        Assert.False(Directory.Exists(directoryPath));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_ExtractsEntriesWithSafeRelativePaths()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddEntry(archive, "run.ps1", "Write-Host 'hi'");
            AddEntry(archive, "prompts/system.md", "You are helpful.");
        });

        var blobId = await StoreBlobAsync(archiveBytes);
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-1");

        await _manager.ExtractBlobToWorkspaceAsync(blobId, workspace);

        Assert.Equal(
            "Write-Host 'hi'",
            await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "run.ps1")));
        Assert.Equal(
            "You are helpful.",
            await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "prompts", "system.md")));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_RejectsZipSlipEntryAndRemovesWorkspace()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(archive, "../escape.txt", "evil"));
        var blobId = await StoreBlobAsync(archiveBytes);
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-2");

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _manager.ExtractBlobToWorkspaceAsync(blobId, workspace));

        Assert.False(File.Exists(Path.Combine(_directory.Root, "escape.txt")));
        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_RejectsAlternateDataStreamEntry()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(archive, "evil.txt:stream", "evil"));
        var blobId = await StoreBlobAsync(archiveBytes);
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-3");

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _manager.ExtractBlobToWorkspaceAsync(blobId, workspace));

        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_RejectsUnixSymlinkEntry()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            var entry = archive.CreateEntry("link", CompressionLevel.Optimal);
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
        });

        var blobId = await StoreBlobAsync(archiveBytes);
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-4");

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _manager.ExtractBlobToWorkspaceAsync(blobId, workspace));

        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_RejectsCompressionRatioBombBeforeInflating()
    {
        var archiveBytes = CreateArchive(archive =>
            WorkflowTestArchiveFactory.AddZeroFilledEntry(archive, "bomb.bin", 1024));

        archiveBytes = WorkflowTestArchiveFactory.PatchCentralDirectorySizes(
            archiveBytes,
            compressedSizeBytes: 1,
            uncompressedSizeBytes: 1024 * 1024);

        var blobId = await StoreBlobAsync(archiveBytes);
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-5");

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _manager.ExtractBlobToWorkspaceAsync(blobId, workspace));

        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_DynamicGuardRejectsEntryWithUnderstatedUncompressedSize()
    {
        var entrySizeBytes = WorkflowImportLimits.MaxSingleFileBytes + (1024 * 1024);

        var archiveBytes = CreateArchive(archive => AddStoredEntry(archive, "bomb.bin", entrySizeBytes));

        archiveBytes = PatchDeclaredEntrySizes(
            archiveBytes,
            compressedSizeBytes: entrySizeBytes,
            uncompressedSizeBytes: 1024 * 1024);

        var blobId = await StoreBlobAsync(archiveBytes);
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-10");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _manager.ExtractBlobToWorkspaceAsync(blobId, workspace));

        Assert.Contains("single file size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(workspace.IsCleanedUp);
        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_DynamicGuardRejectsOverlappingEntriesExceedingTotalLimit()
    {
        const int entrySizeBytes = WorkflowImportLimits.MaxSingleFileBytes;
        const int entryCount = 26;

        var archiveBytes = CreateArchive(archive =>
        {
            AddStoredEntry(archive, "chunk-00.bin", entrySizeBytes);

            for (var i = 1; i < entryCount; i++)
            {
                AddStoredEntry(archive, $"chunk-{i:D2}.bin", 16);
            }
        });

        archiveBytes = PatchDeclaredEntrySizesAndLocalHeaderOffsets(
            archiveBytes,
            compressedSizeBytes: entrySizeBytes,
            uncompressedSizeBytes: 1024 * 1024);

        var blobId = await StoreBlobAsync(archiveBytes);
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-11");

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _manager.ExtractBlobToWorkspaceAsync(blobId, workspace));

        Assert.Contains("total uncompressed size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(workspace.IsCleanedUp);
        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_RemovesPartialFilesWhenExtractionFails()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddEntry(archive, "conflict", "first");
            AddEntry(archive, "conflict/nested.txt", "second");
        });

        var blobId = await StoreBlobAsync(archiveBytes);
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-6");

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _manager.ExtractBlobToWorkspaceAsync(blobId, workspace));

        Assert.True(workspace.IsCleanedUp);
        Assert.False(Directory.Exists(workspace.DirectoryPath));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_ThrowsWhenBlobDoesNotExist()
    {
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-7");
        var missingBlobId = "sha256:" + new string('0', 64);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _manager.ExtractBlobToWorkspaceAsync(missingBlobId, workspace));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_ThrowsWhenSourceBlobFailsHashCheck()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(archive, "run.ps1", "Write-Host 'hi'"));
        var blobId = await StoreBlobAsync(archiveBytes);
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-8");

        await CorruptBlobAsync(blobId);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _manager.ExtractBlobToWorkspaceAsync(blobId, workspace));
    }

    [Fact]
    public async Task ExtractBlobToWorkspaceAsync_RejectsCleanedUpWorkspace()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(archive, "run.ps1", "Write-Host 'hi'"));
        var blobId = await StoreBlobAsync(archiveBytes);
        var workspace = await _manager.CreateWorkspaceAsync(ScratchScope.Preview, "preview-9");

        await workspace.CleanupWorkspaceAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.ExtractBlobToWorkspaceAsync(blobId, workspace));
    }

    [Fact]
    public async Task PostOperationSourceHashCheckAsync_SucceedsForIntactBlob()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(archive, "run.ps1", "Write-Host 'hi'"));
        var blobId = await StoreBlobAsync(archiveBytes);

        await _manager.PostOperationSourceHashCheckAsync(blobId);
    }

    [Fact]
    public async Task PostOperationSourceHashCheckAsync_ThrowsForCorruptedBlob()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(archive, "run.ps1", "Write-Host 'hi'"));
        var blobId = await StoreBlobAsync(archiveBytes);

        await CorruptBlobAsync(blobId);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _manager.PostOperationSourceHashCheckAsync(blobId));
    }

    private static byte[] CreateArchive(Action<ZipArchive> configure)
    {
        return WorkflowTestArchiveFactory.CreateArchive(configure);
    }

    private static byte[] PatchDeclaredEntrySizes(
        byte[] archiveBytes,
        int compressedSizeBytes,
        int uncompressedSizeBytes)
    {
        var patched = WorkflowTestArchiveFactory.PatchCentralDirectorySizes(
            archiveBytes,
            compressedSizeBytes,
            uncompressedSizeBytes);

        return PatchLocalHeaderSizes(patched, compressedSizeBytes, uncompressedSizeBytes);
    }

    private static byte[] PatchDeclaredEntrySizesAndLocalHeaderOffsets(
        byte[] archiveBytes,
        int compressedSizeBytes,
        int uncompressedSizeBytes)
    {
        var patched = PatchDeclaredEntrySizes(archiveBytes, compressedSizeBytes, uncompressedSizeBytes);

        return PatchCentralDirectoryLocalHeaderOffsets(patched);
    }

    private static byte[] PatchCentralDirectoryLocalHeaderOffsets(byte[] archiveBytes)
    {
        const uint centralDirectoryFileHeaderSignature = 0x02014b50;

        var patched = (byte[])archiveBytes.Clone();
        var endOfCentralDirectory = FindEndOfCentralDirectory(patched);
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(endOfCentralDirectory + 10, 2));
        var position = BinaryPrimitives.ReadInt32LittleEndian(patched.AsSpan(endOfCentralDirectory + 16, 4));

        for (var i = 0; i < entryCount; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(position, 4)) != centralDirectoryFileHeaderSignature)
            {
                throw new InvalidOperationException("Unexpected ZIP central directory layout.");
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(position + 28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(position + 30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(position + 32, 2));

            BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(position + 42, 4), 0);

            position += 46 + nameLength + extraLength + commentLength;
        }

        return patched;
    }

    private static int FindEndOfCentralDirectory(byte[] archiveBytes)
    {
        const uint endOfCentralDirectorySignature = 0x06054b50;

        for (var i = archiveBytes.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(archiveBytes.AsSpan(i, 4)) == endOfCentralDirectorySignature)
            {
                return i;
            }
        }

        throw new InvalidOperationException("End of central directory record was not found.");
    }

    private static byte[] PatchLocalHeaderSizes(
        byte[] archiveBytes,
        int compressedSizeBytes,
        int uncompressedSizeBytes)
    {
        const uint localFileHeaderSignature = 0x04034b50;

        var patched = (byte[])archiveBytes.Clone();
        var position = 0;

        while (position + 30 <= patched.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(position, 4)) == localFileHeaderSignature)
        {
            var actualCompressedSize = BinaryPrimitives.ReadInt32LittleEndian(patched.AsSpan(position + 18, 4));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(position + 26, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(patched.AsSpan(position + 28, 2));

            BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(position + 18, 4), compressedSizeBytes);
            BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(position + 22, 4), uncompressedSizeBytes);

            position += 30 + nameLength + extraLength + actualCompressedSize;
        }

        return patched;
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void AddStoredEntry(ZipArchive archive, string name, int sizeBytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        var buffer = new byte[1024 * 1024];
        var remaining = sizeBytes;

        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, buffer.Length);
            stream.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }

    private async Task<string> StoreBlobAsync(byte[] archiveBytes)
    {
        var blob = await _blobStore.SaveBlobAsync(new MemoryStream(archiveBytes));

        return blob.BlobId;
    }

    private async Task CorruptBlobAsync(string blobId)
    {
        await File.WriteAllBytesAsync(_blobStore.GetBlobPath(blobId), new byte[] { 0x01, 0x02, 0x03 });
    }
}
