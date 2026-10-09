using System.Text.Json;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowImportManifestIntegrationTests
{
    [Theory]
    [InlineData("../outside.ps1")]
    [InlineData("scripts\\..\\outside.ps1")]
    [InlineData("C:/outside.ps1")]
    [InlineData("\\\\server\\outside.ps1")]
    [InlineData("NUL.ps1")]
    public async Task Review_FormalUnsafeEntrypointCannotPublishVersion(string entrypoint)
    {
        await AssertRejectedManifestAsync(new[] { entrypoint });
    }

    [Theory]
    [InlineData("run.ps1", "./run.ps1")]
    [InlineData("scripts/run.ps1", "scripts\\run.ps1")]
    [InlineData("run.ps1", "RUN.ps1")]
    public async Task Review_FormalCanonicalEntrypointCollisionCannotPublishVersion(string first, string second)
    {
        await AssertRejectedManifestAsync(new[] { first, second });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_EmptyDirectoryFloodCannotBypassWorkflowTraversalBudget(bool scan)
    {
        await _database.InitializeAsync();
        var root = _database.GetWorkspacePath("empty-directory-flood");
        Directory.CreateDirectory(root);
        for (var index = 0; index <= WorkflowImportLimits.MaxFileCount; index++)
            Directory.CreateDirectory(Path.Combine(root, $"d{index:D5}"));

        if (scan)
        {
            var scanner = new WorkflowSecretScanner();
            await Assert.ThrowsAsync<WorkflowValidationException>(() => scanner.ScanScratchWorkspaceAsync(
                new ScratchWorkspace(root, ScratchScope.Adaptation, "directory-budget")));
        }
        else
        {
            await Assert.ThrowsAsync<WorkflowValidationException>(() =>
                _importService.ImportDirectoryAsync(root, "Empty directory flood"));
            Assert.Equal(0, await _database.CountAsync("WorkflowPackages"));
            Assert.Equal(0, await _database.CountAsync("WorkflowVersions"));
            Assert.False(Directory.Exists(_blobStore.BlobsDirectory));
        }
    }

    private async Task AssertRejectedManifestAsync(string[] entrypoints)
    {
        await _database.InitializeAsync();
        var archive = WorkflowTestArchiveFactory.CreateArchive(zip =>
        {
            WorkflowTestArchiveFactory.AddEntry(zip, "workflow.json", JsonSerializer.Serialize(new { entrypoints }));
            WorkflowTestArchiveFactory.AddEntry(zip, "README.md", "A declared workflow.");
        });
        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            _importService.ImportZipAsync(new MemoryStream(archive), "Rejected formal path"));
        Assert.Equal(0, await _database.CountAsync("WorkflowPackages"));
        Assert.Equal(0, await _database.CountAsync("WorkflowVersions"));
        Assert.Empty(Directory.GetFiles(GetSpoolDirectory()));
        Assert.False(Directory.Exists(_blobStore.BlobsDirectory));
    }
}
