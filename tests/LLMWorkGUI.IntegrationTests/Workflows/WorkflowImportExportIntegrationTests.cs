using System.IO.Compression;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowImportExportIntegrationTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly TestDirectory _sourceDirectory = new();
    private readonly WorkflowBlobStore _blobStore;
    private readonly SqliteWorkflowPackageRepository _packageRepository;
    private readonly SqliteWorkflowVersionRepository _versionRepository;
    private readonly SqliteWorkflowBindingRepository _bindingRepository;
    private readonly WorkflowImportService _importService;
    private readonly WorkflowExportService _exportService;
    private readonly WorkflowPreviewService _previewService;
    private readonly WorkflowBindingService _bindingService;

    public WorkflowImportExportIntegrationTests()
    {
        _blobStore = new WorkflowBlobStore(_database.Root);
        _packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        _versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);
        _bindingRepository = new SqliteWorkflowBindingRepository(_database.Factory);
        _importService = new WorkflowImportService(
            _packageRepository,
            _versionRepository,
            _blobStore,
            new SafeArchiveValidator(),
            TimeProvider.System,
            NullLogger<WorkflowImportService>.Instance,
            new WorkflowManifestParser());
        _exportService = new WorkflowExportService(_versionRepository, _blobStore);
        _previewService = new WorkflowPreviewService(_blobStore);
        _bindingService = new WorkflowBindingService(
            _bindingRepository,
            _packageRepository,
            _versionRepository,
            TimeProvider.System);
    }

    public void Dispose()
    {
        _database.Dispose();
        _sourceDirectory.Dispose();
    }

    [Fact]
    public async Task ImportZipAsync_StoresImmutableOriginalAndExportsByteIdenticalArchive()
    {
        await _database.InitializeAsync();
        var archiveBytes = CreateValidArchive();
        var expectedBlobId = WorkflowTestArchiveFactory.ToBlobId(archiveBytes);

        var result = await _importService.ImportZipAsync(
            new MemoryStream(archiveBytes),
            "Release Workflow",
            "Coordinates release tasks",
            new[] { "release", "ci" });

        Assert.False(result.IsDuplicate);
        Assert.Equal(expectedBlobId, result.BlobId);
        Assert.Equal(expectedBlobId, result.Package.OriginalHash);
        Assert.Equal(expectedBlobId, result.Package.OriginalBlobId);
        Assert.Equal(expectedBlobId, result.Version.OriginalHash);
        Assert.Equal(expectedBlobId, result.Version.BlobId);
        Assert.Equal(WorkflowSourceType.ZipArchive, result.Package.SourceType);
        Assert.Equal(WorkflowSourceType.ZipArchive, result.Version.SourceType);
        Assert.Equal(1, result.Version.VersionNumber);
        Assert.Equal("Release Workflow", result.Package.Name);
        Assert.Equal("Coordinates release tasks", result.Package.Description);
        Assert.Equal(new[] { "release", "ci" }, result.Package.Tags);
        Assert.True(await _blobStore.VerifyBlobAsync(expectedBlobId));
        Assert.Equal(1, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
        Assert.Empty(Directory.GetFiles(GetSpoolDirectory()));

        var storedPackage = await _packageRepository.GetByIdAsync(result.Package.Id);
        Assert.NotNull(storedPackage);
        Assert.Equal(expectedBlobId, storedPackage!.OriginalHash);

        var storedVersion = await _versionRepository.GetByPackageAndVersionAsync(result.Package.Id, 1);
        Assert.NotNull(storedVersion);
        Assert.Equal(result.Version.Id, storedVersion!.Id);

        await using (var exportStream = await _exportService.OpenVersionExportStreamAsync(result.Version.Id))
        using (var buffer = new MemoryStream())
        {
            await exportStream.CopyToAsync(buffer);
            Assert.Equal(archiveBytes, buffer.ToArray());
        }

        var exportPath = _sourceDirectory.GetPath("export.zip");
        await _exportService.ExportToFileAsync(result.Version.Id, exportPath);

        var exportedBytes = await File.ReadAllBytesAsync(exportPath);
        Assert.Equal(archiveBytes, exportedBytes);
        Assert.Equal(expectedBlobId, WorkflowBlobStore.ComputeBlobId(exportedBytes));
    }

    [Fact]
    public async Task WorkflowZip_EndToEndImportPreviewBindingExport()
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync(projectId: "project-1");

        var archiveBytes = CreateRealisticWorkflowArchive();
        var expectedBlobId = WorkflowTestArchiveFactory.ToBlobId(archiveBytes);

        var import = await _importService.ImportZipAsync(
            new MemoryStream(archiveBytes),
            "Release Workflow",
            "Coordinates release tasks",
            new[] { "release", "ci" });

        Assert.False(import.IsDuplicate);
        Assert.Equal(expectedBlobId, import.BlobId);
        Assert.Equal("[\"scripts/run.ps1\"]", import.Version.EntrypointsJson);
        Assert.Equal("[\"Executor\"]", import.Version.DeclaredRolesJson);

        var tree = await _previewService.GetTreePreviewAsync(import.BlobId);

        Assert.Equal(import.BlobId, tree.BlobId);
        Assert.Contains(tree.Nodes, node => node.Path == "workflow.json" && !node.IsDirectory);
        Assert.Contains(tree.Nodes, node => node.Path == "scripts" && node.IsDirectory);
        Assert.Contains(tree.Nodes, node => node.Path == "scripts/run.ps1" && !node.IsDirectory);
        Assert.Contains(tree.Nodes, node => node.Path == "prompts/reviewer.md" && !node.IsDirectory);
        Assert.Equal("README.md", tree.PrimaryDocumentationPath);
        Assert.Equal("# Release Workflow", tree.PrimaryDocumentationContent);

        var filePreview = await _previewService.GetFilePreviewAsync(import.BlobId, "scripts/run.ps1");

        Assert.False(filePreview.IsBinary);
        Assert.Equal("Write-Host 'release'", filePreview.Content);

        var bindingResult = await _bindingService.BindWorkflowToProjectAsync(
            "project-1",
            import.Package.Id,
            import.Version.Id);

        Assert.True(bindingResult.IsNewBinding);
        Assert.Equal(import.Version.Id, bindingResult.Binding.ActiveVersionId);

        var bindings = await _bindingService.GetBindingsForProjectAsync("project-1");

        Assert.Single(bindings);
        Assert.Equal(import.Version.Id, bindings[0].ActiveVersionId);

        var exportPath = _sourceDirectory.GetPath("end-to-end-export.zip");
        await _exportService.ExportToFileAsync(import.Version.Id, exportPath);

        var exportedBytes = await File.ReadAllBytesAsync(exportPath);

        Assert.Equal(archiveBytes, exportedBytes);
        Assert.Equal(expectedBlobId, WorkflowBlobStore.ComputeBlobId(exportedBytes));
    }

    [Fact]
    public async Task ImportZipAsync_DeduplicatesIdenticalOriginalArchive()
    {
        await _database.InitializeAsync();
        var archiveBytes = CreateValidArchive();

        var first = await _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Deduplicated Workflow");
        var second = await _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Deduplicated Workflow");

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Package.Id, second.Package.Id);
        Assert.Equal(first.Version.Id, second.Version.Id);
        Assert.Equal(first.BlobId, second.BlobId);
        Assert.Equal(1, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
        Assert.Single(Directory.GetFiles(_blobStore.BlobsDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ImportDirectoryAsync_BuildsDeterministicArchiveAndExportsIt()
    {
        await _database.InitializeAsync();
        var directory = _sourceDirectory.GetPath("workflow-src");
        Directory.CreateDirectory(Path.Combine(directory, "prompts"));
        await File.WriteAllTextAsync(Path.Combine(directory, "workflow.json"), "{\"name\":\"demo\"}");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "prompts", "system.md"),
            "You are helpful.");

        var first = await _importService.ImportDirectoryAsync(directory, "Directory Workflow");

        Assert.False(first.IsDuplicate);
        Assert.Equal(WorkflowSourceType.Directory, first.Package.SourceType);
        Assert.Equal(WorkflowSourceType.Directory, first.Version.SourceType);
        Assert.Equal(1, first.Version.VersionNumber);

        var exportPath = _sourceDirectory.GetPath("directory-export.zip");
        await _exportService.ExportToFileAsync(first.Version.Id, exportPath);
        var exportedBytes = await File.ReadAllBytesAsync(exportPath);

        Assert.Equal(first.BlobId, WorkflowTestArchiveFactory.ToBlobId(exportedBytes));

        using (var stream = File.OpenRead(exportPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Equal(
                new[] { "prompts/system.md", "workflow.json" },
                names.OrderBy(name => name, StringComparer.Ordinal).ToArray());

            var fixedTimestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            Assert.All(
                archive.Entries,
                entry => Assert.Equal(fixedTimestamp, entry.LastWriteTime.DateTime));
        }

        var second = await _importService.ImportDirectoryAsync(directory, "Directory Workflow");

        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Package.Id, second.Package.Id);
        Assert.Equal(first.BlobId, second.BlobId);
        Assert.Equal(1, await _database.CountAsync("WorkflowPackages"));
    }

    [Fact]
    public async Task ImportZipAsync_RejectsArchiveAboveSpoolLimitWithoutPersistingState()
    {
        await _database.InitializeAsync();

        var oversizedStream = new RepeatingStream(WorkflowImportLimits.MaxArchiveSizeBytes + 1L);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _importService.ImportZipAsync(oversizedStream, "Oversized Workflow"));

        Assert.Contains("maximum allowed archive size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await _database.CountAsync("WorkflowVersions"));
        Assert.Empty(Directory.GetFiles(GetSpoolDirectory()));
        Assert.False(Directory.Exists(_blobStore.BlobsDirectory));
    }

    [Fact]
    public async Task ImportZipAsync_RejectsMaliciousArchiveWithoutPersistingState()
    {
        await _database.InitializeAsync();
        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
            WorkflowTestArchiveFactory.AddEntry(archive, "../../escape.txt", "evil"));

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Malicious Workflow"));

        Assert.Equal(0, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await _database.CountAsync("WorkflowVersions"));
        Assert.Empty(Directory.GetFiles(GetSpoolDirectory()));
        Assert.False(Directory.Exists(_blobStore.BlobsDirectory));
    }

    [Fact]
    public async Task ImportZipAsync_RejectsNonZipPayloadWithoutPersistingState()
    {
        await _database.InitializeAsync();
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _importService.ImportZipAsync(new MemoryStream(payload), "Not A Zip"));

        Assert.Equal(0, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await _database.CountAsync("WorkflowVersions"));
        Assert.Empty(Directory.GetFiles(GetSpoolDirectory()));
        Assert.False(Directory.Exists(_blobStore.BlobsDirectory));
    }

    [Fact]
    public async Task ImportDirectoryAsync_RejectsMissingDirectory()
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _importService.ImportDirectoryAsync(
            _sourceDirectory.GetPath("missing"),
            "Missing Directory Workflow"));
    }

    [Fact]
    public async Task ExportToFileAsync_ThrowsWhenVersionDoesNotExist()
    {
        await _database.InitializeAsync();

        await Assert.ThrowsAsync<FileNotFoundException>(() => _exportService.ExportToFileAsync(
            "missing-version",
            _sourceDirectory.GetPath("missing.zip")));
    }

    private static byte[] CreateRealisticWorkflowArchive()
    {
        return WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(
                archive,
                "workflow.json",
                "{\"entrypoints\":[\"scripts/run.ps1\"],\"declaredRoles\":[\"Executor\"]}");
            WorkflowTestArchiveFactory.AddEntry(archive, "README.md", "# Release Workflow");
            WorkflowTestArchiveFactory.AddEntry(archive, "scripts/run.ps1", "Write-Host 'release'");
            WorkflowTestArchiveFactory.AddEntry(archive, "prompts/reviewer.md", "Review carefully.");
        });
    }

    private static byte[] CreateValidArchive()
    {
        return WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(archive, "workflow.json", "{\"name\":\"demo\"}");
            WorkflowTestArchiveFactory.AddEntry(archive, "prompts/system.md", "You are helpful.");
            WorkflowTestArchiveFactory.AddEntry(archive, "prompts/reviewer.md", "Review carefully.");
        });
    }

    private string GetSpoolDirectory()
    {
        return Path.Combine(_database.Root, "workflows", "spool");
    }

    private sealed class RepeatingStream : Stream
    {
        private readonly long _length;
        private long _position;

        public RepeatingStream(long length)
        {
            _length = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var remaining = _length - _position;

            if (remaining <= 0)
            {
                return 0;
            }

            var read = (int)Math.Min(buffer.Length, remaining);
            _position += read;

            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
