using System.IO.Compression;
using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowPreviewTests : IDisposable
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Local));

    private readonly TestDirectory _directory = new();
    private readonly WorkflowBlobStore _blobStore;
    private readonly WorkflowPreviewService _previewService;

    public WorkflowPreviewTests()
    {
        _blobStore = new WorkflowBlobStore(_directory.Root);
        _previewService = new WorkflowPreviewService(_blobStore);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public async Task GetTreePreviewAsync_ReturnsNormalizedTreeWithSizesFlagsAndTimestamps()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddTextEntry(archive, "README.md", "# Demo", FixedTimestamp);
            AddTextEntry(archive, "prompts/system.md", "You are helpful.", FixedTimestamp);
            AddTextEntry(archive, "prompts\\nested\\notes.txt", "nested", FixedTimestamp);
        });

        var blobId = await StoreBlobAsync(archiveBytes);

        var preview = await _previewService.GetTreePreviewAsync(blobId);

        Assert.Equal(blobId, preview.BlobId);
        Assert.Equal(
            new[] { "README.md", "prompts", "prompts/nested", "prompts/nested/notes.txt", "prompts/system.md" },
            preview.Nodes.Select(node => node.Path).ToArray());

        Assert.All(
            preview.Nodes,
            node => Assert.Equal(FixedTimestamp.UtcDateTime, node.LastModifiedUtc.UtcDateTime));

        var readme = preview.Nodes.Single(node => node.Path == "README.md");
        Assert.False(readme.IsDirectory);
        Assert.Equal("README.md", readme.Name);
        Assert.Equal(Encoding.UTF8.GetByteCount("# Demo"), readme.SizeBytes);
        Assert.True(readme.CompressedSizeBytes > 0);

        var prompts = preview.Nodes.Single(node => node.Path == "prompts");
        Assert.True(prompts.IsDirectory);
        Assert.Equal("prompts", prompts.Name);
        Assert.Equal(
            Encoding.UTF8.GetByteCount("You are helpful.") + Encoding.UTF8.GetByteCount("nested"),
            prompts.SizeBytes);

        var nested = preview.Nodes.Single(node => node.Path == "prompts/nested");
        Assert.True(nested.IsDirectory);
        Assert.Equal("nested", nested.Name);
    }

    [Theory]
    [InlineData("README.md", "primary-readme")]
    [InlineData("SKILL.md", "primary-skill")]
    [InlineData("WORKFLOW.md", "primary-workflow")]
    [InlineData("INSTRUCTIONS.md", "primary-instructions")]
    public async Task GetTreePreviewAsync_PrefersPriorityDocumentationOverFallback(
        string expectedName,
        string expectedContent)
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddTextEntry(archive, "aaa.md", "other-documentation");
            AddTextEntry(archive, expectedName, expectedContent);
        });

        var preview = await _previewService.GetTreePreviewAsync(await StoreBlobAsync(archiveBytes));

        Assert.Equal(expectedName, preview.PrimaryDocumentationPath);
        Assert.Equal(expectedContent, preview.PrimaryDocumentationContent);
        Assert.False(preview.IsPrimaryDocumentationTruncated);
    }

    [Fact]
    public async Task GetTreePreviewAsync_PrefersReadmeOverOtherPriorityDocuments()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddTextEntry(archive, "SKILL.md", "primary-skill");
            AddTextEntry(archive, "WORKFLOW.md", "primary-workflow");
            AddTextEntry(archive, "INSTRUCTIONS.md", "primary-instructions");
            AddTextEntry(archive, "README.md", "primary-readme");
        });

        var preview = await _previewService.GetTreePreviewAsync(await StoreBlobAsync(archiveBytes));

        Assert.Equal("README.md", preview.PrimaryDocumentationPath);
        Assert.Equal("primary-readme", preview.PrimaryDocumentationContent);
    }

    [Fact]
    public async Task GetTreePreviewAsync_FallsBackToFirstOrdinalRootMarkdown()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddTextEntry(archive, "zeta.md", "zeta");
            AddTextEntry(archive, "alpha.md", "alpha");
        });

        var preview = await _previewService.GetTreePreviewAsync(await StoreBlobAsync(archiveBytes));

        Assert.Equal("alpha.md", preview.PrimaryDocumentationPath);
        Assert.Equal("alpha", preview.PrimaryDocumentationContent);
    }

    [Fact]
    public async Task GetTreePreviewAsync_MatchesPriorityDocumentationCaseInsensitively()
    {
        var archiveBytes = CreateArchive(archive => AddTextEntry(archive, "readme.MD", "lowercase-readme"));

        var preview = await _previewService.GetTreePreviewAsync(await StoreBlobAsync(archiveBytes));

        Assert.Equal("readme.MD", preview.PrimaryDocumentationPath);
        Assert.Equal("lowercase-readme", preview.PrimaryDocumentationContent);
    }

    [Fact]
    public async Task GetTreePreviewAsync_IgnoresNestedMarkdownForPrimaryDocumentation()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddTextEntry(archive, "docs/README.md", "nested-readme");
            AddTextEntry(archive, "notes.txt", "plain-text");
        });

        var preview = await _previewService.GetTreePreviewAsync(await StoreBlobAsync(archiveBytes));

        Assert.Null(preview.PrimaryDocumentationPath);
        Assert.Null(preview.PrimaryDocumentationContent);
        Assert.False(preview.IsPrimaryDocumentationTruncated);
    }

    [Fact]
    public async Task GetTreePreviewAsync_TruncatesOversizedPrimaryDocumentation()
    {
        var content = new string('a', IWorkflowPreviewService.MaxPreviewSizeBytes + 1024);
        var archiveBytes = CreateArchive(archive => AddTextEntry(archive, "README.md", content));

        var preview = await _previewService.GetTreePreviewAsync(await StoreBlobAsync(archiveBytes));

        Assert.True(preview.IsPrimaryDocumentationTruncated);
        Assert.NotNull(preview.PrimaryDocumentationContent);
        Assert.Equal(IWorkflowPreviewService.MaxPreviewSizeBytes, preview.PrimaryDocumentationContent!.Length);
    }

    [Fact]
    public async Task GetTreePreviewAsync_DoesNotTruncateDocumentationAtExactLimit()
    {
        var content = new string('a', IWorkflowPreviewService.MaxPreviewSizeBytes);
        var archiveBytes = CreateArchive(archive => AddTextEntry(archive, "README.md", content));

        var preview = await _previewService.GetTreePreviewAsync(await StoreBlobAsync(archiveBytes));

        Assert.False(preview.IsPrimaryDocumentationTruncated);
        Assert.NotNull(preview.PrimaryDocumentationContent);
        Assert.Equal(IWorkflowPreviewService.MaxPreviewSizeBytes, preview.PrimaryDocumentationContent!.Length);
    }

    [Fact]
    public async Task GetTreePreviewAsync_ReturnsEmptyPreviewForEmptyArchive()
    {
        var archiveBytes = CreateArchive(static _ => { });

        var preview = await _previewService.GetTreePreviewAsync(await StoreBlobAsync(archiveBytes));

        Assert.Empty(preview.Nodes);
        Assert.Null(preview.PrimaryDocumentationPath);
        Assert.Null(preview.PrimaryDocumentationContent);
    }

    [Fact]
    public async Task GetTreePreviewAsync_ThrowsWhenBlobDoesNotExist()
    {
        var missingBlobId = "sha256:" + new string('0', 64);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _previewService.GetTreePreviewAsync(missingBlobId));
    }

    [Theory]
    [InlineData("not-a-blob")]
    [InlineData("sha256:0123")]
    [InlineData("sha256:ABCDEF")]
    public async Task GetTreePreviewAsync_ThrowsForInvalidBlobId(string blobId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _previewService.GetTreePreviewAsync(blobId));
    }

    [Fact]
    public async Task GetTreePreviewAsync_ThrowsWhenSourceBlobFailsHashCheck()
    {
        var archiveBytes = CreateArchive(archive => AddTextEntry(archive, "README.md", "# Demo"));
        var blobId = await StoreBlobAsync(archiveBytes);

        await CorruptBlobAsync(blobId);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _previewService.GetTreePreviewAsync(blobId));
    }

    [Fact]
    public async Task GetFilePreviewAsync_ReturnsTextContentWithMetadata()
    {
        var archiveBytes = CreateArchive(archive =>
            AddTextEntry(archive, "prompts/system.md", "You are helpful."));

        var blobId = await StoreBlobAsync(archiveBytes);

        var preview = await _previewService.GetFilePreviewAsync(blobId, "prompts/system.md");

        Assert.Equal(blobId, preview.BlobId);
        Assert.Equal("prompts/system.md", preview.Path);
        Assert.Equal(Encoding.UTF8.GetByteCount("You are helpful."), preview.SizeBytes);
        Assert.False(preview.IsBinary);
        Assert.False(preview.IsTruncated);
        Assert.Equal("You are helpful.", preview.Content);
    }

    [Fact]
    public async Task GetFilePreviewAsync_NormalizesBackslashRequestPaths()
    {
        var archiveBytes = CreateArchive(archive =>
            AddTextEntry(archive, "prompts/system.md", "You are helpful."));

        var blobId = await StoreBlobAsync(archiveBytes);

        var preview = await _previewService.GetFilePreviewAsync(blobId, "prompts\\system.md");

        Assert.Equal("prompts/system.md", preview.Path);
        Assert.Equal("You are helpful.", preview.Content);
    }

    [Fact]
    public async Task GetFilePreviewAsync_DecodesUtf8BomContent()
    {
        var payload = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("hello"))
            .ToArray();

        var archiveBytes = CreateArchive(archive => AddBinaryEntry(archive, "notes.txt", payload));
        var blobId = await StoreBlobAsync(archiveBytes);

        var preview = await _previewService.GetFilePreviewAsync(blobId, "notes.txt");

        Assert.False(preview.IsBinary);
        Assert.Equal("hello", preview.Content);
    }

    [Fact]
    public async Task GetFilePreviewAsync_FlagsBinaryFilesWithoutTextContent()
    {
        var archiveBytes = CreateArchive(archive =>
            AddBinaryEntry(archive, "asset.bin", new byte[] { 0x41, 0x00, 0x42 }));

        var blobId = await StoreBlobAsync(archiveBytes);

        var preview = await _previewService.GetFilePreviewAsync(blobId, "asset.bin");

        Assert.True(preview.IsBinary);
        Assert.Null(preview.Content);
        Assert.False(preview.IsTruncated);
    }

    [Fact]
    public async Task GetFilePreviewAsync_TruncatesOversizedTextFiles()
    {
        var content = new string('b', IWorkflowPreviewService.MaxPreviewSizeBytes + 4096);
        var archiveBytes = CreateArchive(archive => AddTextEntry(archive, "large.md", content));
        var blobId = await StoreBlobAsync(archiveBytes);

        var preview = await _previewService.GetFilePreviewAsync(blobId, "large.md");

        Assert.True(preview.IsTruncated);
        Assert.NotNull(preview.Content);
        Assert.Equal(IWorkflowPreviewService.MaxPreviewSizeBytes, preview.Content!.Length);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("folder/../../escape.txt")]
    public async Task GetFilePreviewAsync_RejectsPathTraversalRequests(string relativePath)
    {
        var archiveBytes = CreateArchive(archive => AddTextEntry(archive, "notes.txt", "text"));
        var blobId = await StoreBlobAsync(archiveBytes);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _previewService.GetFilePreviewAsync(blobId, relativePath));
    }

    [Theory]
    [InlineData("notes.txt:evil")]
    [InlineData("C:/notes.txt")]
    [InlineData("/etc/passwd")]
    public async Task GetFilePreviewAsync_RejectsAlternateDataStreamAndRootedPaths(string relativePath)
    {
        var archiveBytes = CreateArchive(archive => AddTextEntry(archive, "notes.txt", "text"));
        var blobId = await StoreBlobAsync(archiveBytes);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _previewService.GetFilePreviewAsync(blobId, relativePath));
    }

    [Fact]
    public async Task GetFilePreviewAsync_ThrowsWhenFileDoesNotExist()
    {
        var archiveBytes = CreateArchive(archive => AddTextEntry(archive, "notes.txt", "text"));
        var blobId = await StoreBlobAsync(archiveBytes);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _previewService.GetFilePreviewAsync(blobId, "missing.md"));
    }

    [Fact]
    public async Task GetFilePreviewAsync_ThrowsWhenPathTargetsDirectory()
    {
        var archiveBytes = CreateArchive(archive => AddTextEntry(archive, "prompts/system.md", "text"));
        var blobId = await StoreBlobAsync(archiveBytes);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _previewService.GetFilePreviewAsync(blobId, "prompts/"));
    }

    [Fact]
    public async Task GetFilePreviewAsync_ThrowsWhenSourceBlobFailsHashCheck()
    {
        var archiveBytes = CreateArchive(archive => AddTextEntry(archive, "notes.txt", "text"));
        var blobId = await StoreBlobAsync(archiveBytes);

        await CorruptBlobAsync(blobId);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _previewService.GetFilePreviewAsync(blobId, "notes.txt"));
    }

    private static byte[] CreateArchive(Action<ZipArchive> configure)
    {
        return WorkflowTestArchiveFactory.CreateArchive(configure);
    }

    private static void AddTextEntry(
        ZipArchive archive,
        string name,
        string content,
        DateTimeOffset? lastWriteTime = null)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        if (lastWriteTime is not null)
        {
            entry.LastWriteTime = lastWriteTime.Value;
        }

        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void AddBinaryEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);

        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
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
