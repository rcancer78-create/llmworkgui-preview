using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowImportManifestIntegrationTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly WorkflowBlobStore _blobStore;
    private readonly SqliteWorkflowPackageRepository _packageRepository;
    private readonly SqliteWorkflowVersionRepository _versionRepository;
    private readonly WorkflowImportService _importService;
    private readonly WorkflowImportService _legacyImportService;

    public WorkflowImportManifestIntegrationTests()
    {
        _blobStore = new WorkflowBlobStore(_database.Root);
        _packageRepository = new SqliteWorkflowPackageRepository(_database.Factory);
        _versionRepository = new SqliteWorkflowVersionRepository(_database.Factory);
        _importService = new WorkflowImportService(
            _packageRepository,
            _versionRepository,
            _blobStore,
            new SafeArchiveValidator(),
            TimeProvider.System,
            NullLogger<WorkflowImportService>.Instance,
            new WorkflowManifestParser());
        _legacyImportService = new WorkflowImportService(
            _packageRepository,
            _versionRepository,
            _blobStore,
            new SafeArchiveValidator(),
            TimeProvider.System,
            NullLogger<WorkflowImportService>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task ImportZipAsync_PopulatesEntrypointsAndRolesFromFormalManifest()
    {
        await _database.InitializeAsync();

        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(
                archive,
                "workflow.json",
                "{\"entrypoints\":[\"run.ps1\"],\"declaredRoles\":[\"Executor\"]}");
            WorkflowTestArchiveFactory.AddEntry(archive, "run.ps1", "Write-Host 'hi'");
        });

        var result = await _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Manifest Workflow");

        Assert.Equal("[\"run.ps1\"]", result.Version.EntrypointsJson);
        Assert.Equal("[\"Executor\"]", result.Version.DeclaredRolesJson);
        Assert.Null(result.Version.BindingsJson);
        Assert.Null(result.Version.CompatibilityReportJson);
        Assert.Null(result.Version.CreationMetadataJson);

        var storedVersion = await _versionRepository.GetByIdAsync(result.Version.Id);

        Assert.NotNull(storedVersion);
        Assert.Equal("[\"run.ps1\"]", storedVersion!.EntrypointsJson);
        Assert.Equal("[\"Executor\"]", storedVersion.DeclaredRolesJson);
    }

    [Fact]
    public async Task ImportZipAsync_UsesLegacyHeuristicsWhenFormalManifestIsMissing()
    {
        await _database.InitializeAsync();

        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(archive, "README.md", "The Reviewer checks the output.");
            WorkflowTestArchiveFactory.AddEntry(archive, "run.ps1", "Write-Host 'hi'");
            WorkflowTestArchiveFactory.AddEntry(archive, "main.py", "print('hi')");
        });

        var result = await _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Legacy Workflow");

        Assert.Equal("[\"main.py\",\"run.ps1\"]", result.Version.EntrypointsJson);
        Assert.Equal("[\"Reviewer\"]", result.Version.DeclaredRolesJson);
    }

    [Fact]
    public async Task ImportZipAsync_PopulatesEmptyArraysWhenNothingIsDiscovered()
    {
        await _database.InitializeAsync();

        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
            WorkflowTestArchiveFactory.AddEntry(archive, "notes.txt", "plain text"));

        var result = await _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Empty Workflow");

        Assert.Equal("[]", result.Version.EntrypointsJson);
        Assert.Equal("[]", result.Version.DeclaredRolesJson);
    }

    [Fact]
    public async Task ImportZipAsync_WithoutManifestParserKeepsManifestFieldsNull()
    {
        await _database.InitializeAsync();

        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(
                archive,
                "workflow.json",
                "{\"entrypoints\":[\"run.ps1\"],\"declaredRoles\":[\"Executor\"]}");
            WorkflowTestArchiveFactory.AddEntry(archive, "run.ps1", "Write-Host 'hi'");
        });

        var result = await _legacyImportService.ImportZipAsync(new MemoryStream(archiveBytes), "Legacy Service Flow");

        Assert.Null(result.Version.EntrypointsJson);
        Assert.Null(result.Version.DeclaredRolesJson);
    }

    [Fact]
    public async Task ImportZipAsync_DuplicateImportDoesNotModifyExistingVersion()
    {
        await _database.InitializeAsync();

        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(
                archive,
                "workflow.json",
                "{\"entrypoints\":[\"run.ps1\"],\"declaredRoles\":[\"Executor\"]}");
            WorkflowTestArchiveFactory.AddEntry(archive, "run.ps1", "Write-Host 'hi'");
        });

        var first = await _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Deduplicated Manifest Flow");
        var second = await _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Deduplicated Manifest Flow");

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Version.Id, second.Version.Id);

        var storedVersion = await _versionRepository.GetByIdAsync(first.Version.Id);

        Assert.NotNull(storedVersion);
        Assert.Equal("[\"run.ps1\"]", storedVersion!.EntrypointsJson);
        Assert.Equal("[\"Executor\"]", storedVersion.DeclaredRolesJson);
        Assert.Equal(1, await _database.CountAsync("WorkflowVersions"));
        Assert.Equal(1, await _database.CountAsync("WorkflowPackages"));
    }

    [Fact]
    public async Task ImportDirectoryAsync_PopulatesManifestFieldsFromGeneratedArchive()
    {
        await _database.InitializeAsync();

        var directory = _database.GetWorkspacePath("manifest-src");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "README.md"),
            "Executor and Coordinator roles are declared here.");
        await File.WriteAllTextAsync(Path.Combine(directory, "run.cmd"), "@echo off");

        var result = await _importService.ImportDirectoryAsync(directory, "Directory Manifest Workflow");

        Assert.Equal("[\"run.cmd\"]", result.Version.EntrypointsJson);
        Assert.Equal("[\"Coordinator\",\"Executor\"]", result.Version.DeclaredRolesJson);
    }

    [Fact]
    public async Task ImportDirectoryAsync_ParsesFormalManifestFromGeneratedArchive()
    {
        await _database.InitializeAsync();

        var directory = _database.GetWorkspacePath("formal-manifest-src");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "workflow.json"),
            "{\"entrypoints\":[\"run.ps1\"]}");
        await File.WriteAllTextAsync(Path.Combine(directory, "run.ps1"), "Write-Host 'hi'");
        await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), "The Reviewer checks the output.");

        var result = await _importService.ImportDirectoryAsync(directory, "Directory Formal Workflow");

        Assert.Equal("[\"run.ps1\"]", result.Version.EntrypointsJson);
        Assert.Equal("[]", result.Version.DeclaredRolesJson);
    }

    [Fact]
    public async Task ImportZipAsync_MalformedManifestFailsWithoutPersistingState()
    {
        await _database.InitializeAsync();

        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
            WorkflowTestArchiveFactory.AddEntry(archive, "workflow.json", "{ not json"));

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Malformed Manifest Flow"));

        Assert.Equal(0, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await _database.CountAsync("WorkflowVersions"));
        Assert.Empty(Directory.GetFiles(GetSpoolDirectory()));
        Assert.False(Directory.Exists(_blobStore.BlobsDirectory));
    }

    [Fact]
    public async Task ImportZipAsync_FormalManifestSuppressesLegacyHeuristics()
    {
        await _database.InitializeAsync();

        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(archive, "workflow.json", "{}");
            WorkflowTestArchiveFactory.AddEntry(archive, "run.ps1", "Write-Host 'hi'");
            WorkflowTestArchiveFactory.AddEntry(archive, "README.md", "The Reviewer checks the output.");
        });

        var result = await _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Formal Empty Manifest Flow");

        Assert.Equal("[]", result.Version.EntrypointsJson);
        Assert.Equal("[]", result.Version.DeclaredRolesJson);
    }

    [Fact]
    public async Task ImportZipAsync_KeepsBindingsAndMetadataNullOnVersion()
    {
        await _database.InitializeAsync();

        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
            WorkflowTestArchiveFactory.AddEntry(
                archive,
                "workflow.json",
                """
                {
                  "entrypoints": ["run.ps1"],
                  "bindings": { "provider": "opencode" },
                  "metadata": { "createdBy": "tester" }
                }
                """));

        var result = await _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Bindings Flow");

        Assert.Equal("[\"run.ps1\"]", result.Version.EntrypointsJson);
        Assert.Null(result.Version.BindingsJson);
        Assert.Null(result.Version.CreationMetadataJson);
    }

    [Fact]
    public async Task ImportZipAsync_LegacyDiscoveryFindsMultipleEntrypointsAndRoles()
    {
        await _database.InitializeAsync();

        var archiveBytes = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            WorkflowTestArchiveFactory.AddEntry(archive, "run.ps1", "Write-Host 'hi'");
            WorkflowTestArchiveFactory.AddEntry(archive, "scripts/entrypoint.sh", "echo hi");
            WorkflowTestArchiveFactory.AddEntry(
                archive,
                "SKILL.md",
                "Escalation handles blockers and the Reviewer checks results.");
        });

        var result = await _importService.ImportZipAsync(new MemoryStream(archiveBytes), "Legacy Multi Flow");

        Assert.Equal("[\"run.ps1\",\"scripts/entrypoint.sh\"]", result.Version.EntrypointsJson);
        Assert.Equal("[\"Reviewer\",\"Escalation\"]", result.Version.DeclaredRolesJson);
    }

    private string GetSpoolDirectory()
    {
        return Path.Combine(_database.Root, "workflows", "spool");
    }
}
