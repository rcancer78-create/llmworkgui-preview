using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowModificationBatchTests
{
    [Fact]
    public void TotalQuotaIncludesAllNewFiles()
    {
        using var directory = new TestDirectory();
        var content = new string('x', WorkflowImportLimits.MaxSingleFileBytes);
        var changes = Enumerable.Range(0, 26).ToDictionary(i => $"file-{i}.txt", _ => content);
        Assert.Throws<WorkflowValidationException>(() => WorkflowModificationBatch.Prepare(directory.Root, changes, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Root));
    }

    [Fact]
    public void FileCountIsCheckedBeforeMutation()
    {
        using var directory = new TestDirectory();
        var changes = Enumerable.Range(0, WorkflowImportLimits.MaxFileCount + 1).ToDictionary(i => $"file-{i}", _ => "");
        Assert.Throws<WorkflowValidationException>(() => WorkflowModificationBatch.Prepare(directory.Root, changes, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Root));
    }

    [Theory]
    [InlineData("a.txt", "A.txt")]
    [InlineData("a/b.txt", "a//b.txt")]
    [InlineData("a", "a/child.txt")]
    public void CollidingPathsAreRefusedAsOneBatch(string first, string second)
    {
        using var directory = new TestDirectory();
        Assert.Throws<WorkflowValidationException>(() => WorkflowModificationBatch.Prepare(directory.Root,
            new Dictionary<string, string> { [first] = "one", [second] = "two" }, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Root));
    }

    [Fact]
    public void FileLeafSymlinkIsRejectedWithoutTouchingTarget()
    {
        using var directory = new TestDirectory();
        using var outside = new TestDirectory();
        var target = outside.GetPath("target.txt");
        File.WriteAllText(target, "original");
        File.CreateSymbolicLink(directory.GetPath("link.txt"), target);
        Assert.Throws<LLMWorkGUI.Infrastructure.Security.PathTraversalException>(() =>
            WorkflowModificationBatch.Prepare(directory.Root, new Dictionary<string, string> { ["link.txt"] = "evil" }, default));
        Assert.Equal("original", File.ReadAllText(target));
    }
}
