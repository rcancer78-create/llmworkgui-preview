using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowDiffServiceTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly WorkflowDiffService _service = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public async Task CompareAsync_AddedFile_ReportsAddedWithUnifiedDiff()
    {
        var source = CreateDirectory("source");
        var candidate = CreateDirectory("candidate");

        WriteText(candidate, "README.md", "alpha\nbeta\n");

        var diff = await _service.CompareAsync(source, candidate);

        Assert.Equal(1, diff.TotalFilesAdded);
        Assert.Equal(2, diff.TotalLinesAdded);
        Assert.Equal(0, diff.TotalLinesDeleted);

        var file = Assert.Single(diff.FileDiffs);

        Assert.Equal("README.md", file.RelativePath);
        Assert.Equal(WorkflowFileDiffKind.Added, file.Kind);
        Assert.Equal(0, file.LinesDeleted);
        Assert.False(file.IsBinary);
        Assert.Contains("--- a/README.md", file.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("+++ b/README.md", file.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("@@ -0,0 +1,2 @@", file.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("+alpha", file.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("+beta", file.UnifiedDiffText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareAsync_DeletedFile_ReportsDeleted()
    {
        var source = CreateDirectory("source");
        var candidate = CreateDirectory("candidate");

        WriteText(source, "legacy/run.ps1", "line1\nline2\nline3\n");

        var diff = await _service.CompareAsync(source, candidate);

        Assert.Equal(1, diff.TotalFilesDeleted);
        Assert.Equal(3, diff.TotalLinesDeleted);

        var file = Assert.Single(diff.FileDiffs);

        Assert.Equal("legacy/run.ps1", file.RelativePath);
        Assert.Equal(WorkflowFileDiffKind.Deleted, file.Kind);
        Assert.Equal("@@ -1,3 +0,0 @@", FirstHunkHeader(file.UnifiedDiffText));
    }

    [Fact]
    public async Task CompareAsync_ModifiedFile_ReportsLineStatisticsAndHunk()
    {
        var source = CreateDirectory("source");
        var candidate = CreateDirectory("candidate");

        WriteText(source, "prompts/executor.md", "line1\nline2\n");
        WriteText(candidate, "prompts/executor.md", "line1\nline2-changed\nline3\n");

        var diff = await _service.CompareAsync(source, candidate);

        Assert.Equal(1, diff.TotalFilesModified);
        Assert.Equal(2, diff.TotalLinesAdded);
        Assert.Equal(1, diff.TotalLinesDeleted);

        var file = Assert.Single(diff.FileDiffs);

        Assert.Equal(WorkflowFileDiffKind.Modified, file.Kind);
        Assert.Equal(2, file.LinesAdded);
        Assert.Equal(1, file.LinesDeleted);
        Assert.Contains("@@ -1,2 +1,3 @@", file.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("-line2", file.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("+line2-changed", file.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("+line3", file.UnifiedDiffText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareAsync_UnchangedFile_HasNoDiffTextOrStatistics()
    {
        var source = CreateDirectory("source");
        var candidate = CreateDirectory("candidate");

        WriteText(source, "README.md", "# Workflow\n");
        WriteText(candidate, "README.md", "# Workflow\n");

        var diff = await _service.CompareAsync(source, candidate);

        Assert.Equal(1, diff.TotalFilesUnchanged);
        Assert.Equal(0, diff.TotalLinesAdded);
        Assert.Equal(0, diff.TotalLinesDeleted);

        var file = Assert.Single(diff.FileDiffs);

        Assert.Equal(WorkflowFileDiffKind.Unchanged, file.Kind);
        Assert.Equal(string.Empty, file.UnifiedDiffText);
        Assert.False(file.IsBinary);
    }

    [Fact]
    public async Task CompareAsync_BinaryFile_ReportsBinaryWithoutLineStatistics()
    {
        var source = CreateDirectory("source");
        var candidate = CreateDirectory("candidate");

        WriteBytes(source, "assets/logo.bin", new byte[] { 0x00, 0x01, 0x02, 0x03 });
        WriteBytes(candidate, "assets/logo.bin", new byte[] { 0x00, 0x01, 0x02, 0x04 });

        var diff = await _service.CompareAsync(source, candidate);

        var file = Assert.Single(diff.FileDiffs);

        Assert.Equal(WorkflowFileDiffKind.Modified, file.Kind);
        Assert.True(file.IsBinary);
        Assert.Equal(0, file.LinesAdded);
        Assert.Equal(0, file.LinesDeleted);
        Assert.Equal(string.Empty, file.UnifiedDiffText);
    }

    [Fact]
    public async Task CompareAsync_NestedPaths_AreSortedOrdinallyWithTotals()
    {
        var source = CreateDirectory("source");
        var candidate = CreateDirectory("candidate");

        WriteText(source, "zeta.txt", "z\n");
        WriteText(source, "alpha.txt", "a\n");
        WriteText(candidate, "alpha.txt", "a\n");
        WriteText(candidate, "beta/nested.txt", "b\n");

        var diff = await _service.CompareAsync(source, candidate);

        Assert.Equal(1, diff.TotalFilesAdded);
        Assert.Equal(1, diff.TotalFilesDeleted);
        Assert.Equal(1, diff.TotalFilesUnchanged);
        Assert.Equal(
            new[] { "alpha.txt", "beta/nested.txt", "zeta.txt" },
            diff.FileDiffs.Select(file => file.RelativePath));
    }

    [Fact]
    public async Task CompareAsync_MissingCandidateDirectory_TreatsEverySourceFileAsDeleted()
    {
        var source = CreateDirectory("source");
        var candidate = Path.Combine(_directory.Root, "missing-candidate");

        WriteText(source, "README.md", "# Workflow\n");

        var diff = await _service.CompareAsync(source, candidate);

        Assert.Equal(1, diff.TotalFilesDeleted);
        Assert.Equal(WorkflowFileDiffKind.Deleted, Assert.Single(diff.FileDiffs).Kind);
    }

    [Fact]
    public async Task CompareAsync_RejectsBlankDirectories()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CompareAsync(" ", CreateDirectory("candidate")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CompareAsync(CreateDirectory("source"), " "));
    }

    [Fact]
    public async Task CompareAsync_MultipleSeparatedChanges_ProducesMultipleHunks()
    {
        var source = CreateDirectory("source");
        var candidate = CreateDirectory("candidate");

        var sourceLines = Enumerable.Range(1, 40).Select(index => "line" + index).ToArray();
        var candidateLines = (string[])sourceLines.Clone();
        candidateLines[2] = "changed-3";
        candidateLines[35] = "changed-36";

        WriteText(source, "large.txt", string.Join('\n', sourceLines) + "\n");
        WriteText(candidate, "large.txt", string.Join('\n', candidateLines) + "\n");

        var diff = await _service.CompareAsync(source, candidate);
        var file = Assert.Single(diff.FileDiffs);
        var hunkCount = file.UnifiedDiffText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith("@@", StringComparison.Ordinal));

        Assert.Equal(2, hunkCount);
        Assert.Equal(2, file.LinesAdded);
        Assert.Equal(2, file.LinesDeleted);
    }

    private static string FirstHunkHeader(string unifiedDiff)
    {
        foreach (var line in unifiedDiff.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new InvalidOperationException("Unified diff does not contain a hunk header.");
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_directory.Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteText(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void WriteBytes(string root, string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
    }
}
