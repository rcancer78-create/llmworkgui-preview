using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

// The packaging-path observer watches process TEMP without changing it. Prevent unrelated test
// collections from creating adaptation containers while this real-filesystem observation is active.
[CollectionDefinition("Workflow adaptation packaging isolation", DisableParallelization = true)]
public sealed class WorkflowAdaptationPackagingIsolationCollection { }

[Collection("Workflow adaptation packaging isolation")]
public sealed partial class WorkflowAdaptationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_Utf16BomSourceBodyAppearsInBothPreviewAndActualModelRequest(bool bigEndian)
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        const string body = "UTF16 workflow body: сохранить проверку результата, étapes de validation.";
        var encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        var raw = WorkflowTestArchiveFactory.CreateArchive(archive =>
        {
            var entry = archive.CreateEntry("README.md", CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(encoding.GetPreamble());
            stream.Write(encoding.GetBytes(body));
        });
        var (_, version) = await SeedWorkflowAsync("version-1", raw);
        var preview = await _service.PreparePreSendPreviewAsync(version.Id, "acct-1", AdaptationGoal.Balanced);
        Assert.Contains("README.md", preview.IncludedFiles);
        Assert.Contains(body, preview.PromptPreview, StringComparison.Ordinal);
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var result = await _service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        Assert.Contains(_modelInvoker.Requests.Single().Messages,
            message => message.Content.Contains(body, StringComparison.Ordinal));
        await _service.DiscardSessionAsync(result.SessionId);
    }

    [Fact]
    public async Task Review_CandidatePackagingContainerStaysInItsIssuedScratchAndNeverPackagesItself()
    {
        await _database.InitializeAsync();
        await SeedProviderAndAccountAsync();
        var (_, version) = await SeedWorkflowAsync("version-1", CreateArchive(("README.md", "owned candidate")));
        _modelInvoker.EnqueueResponse(CreateResponse(mappings: new[] { CreateMapping() }));
        var result = await _service.StartAdaptationAsync(new(version.Id, "acct-1", AdaptationGoal.Balanced));
        var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ConcurrentBag<string>();
        using var outside = PackagingWatcher(Path.GetTempPath());
        using var inside = PackagingWatcher(result.CandidateWorkspacePath);
        FileSystemWatcher PackagingWatcher(string directory)
        {
            var watcher = new FileSystemWatcher(directory, "llmworkgui-adaptation-*.zip")
                { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
            watcher.Created += (_, args) =>
            {
                observed.Add(Path.GetFullPath(args.FullPath));
                created.TrySetResult(Path.GetFullPath(args.FullPath));
            };
            watcher.Error += (_, args) => created.TrySetException(args.GetException());
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        var saved = await _service.SaveCandidateVersionAsync(result.SessionId);
        var actualContainer = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var scopePrefix = Path.GetFullPath(result.CandidateWorkspacePath) + Path.DirectorySeparatorChar;
        Assert.StartsWith(scopePrefix, actualContainer, StringComparison.OrdinalIgnoreCase);
        Assert.All(observed, path => Assert.StartsWith(scopePrefix, path, StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(actualContainer));
        Assert.False(Directory.Exists(result.CandidateWorkspacePath));
        using var archive = ZipFile.OpenRead(_blobStore.GetBlobPath(saved.BlobId));
        Assert.Equal(new[] { "README.md" }, archive.Entries.Select(entry => entry.FullName).ToArray());
        using var reader = new StreamReader(archive.Entries.Single().Open());
        Assert.Equal("owned candidate", await reader.ReadToEndAsync());
    }
}
