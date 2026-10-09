using System.Security.AccessControl;
using System.Security.Principal;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowDiffAccessibilityR2Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InaccessibleSubtreeFailsComparisonInsteadOfPublishingIncompleteFacts(bool denyCandidate)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This owned DACL regression requires Windows.");
        using var directory = new TestDirectory();
        var source = directory.GetPath("source");
        var candidate = directory.GetPath("candidate");
        SeedTree(source, "original hidden content");
        SeedTree(candidate, "changed hidden content");
        var denied = new DirectoryInfo(Path.Combine(denyCandidate ? candidate : source, "private-subtree"));
        var original = denied.GetAccessControl(AccessControlSections.Access);
        var restricted = new DirectorySecurity();
        restricted.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(),
            AccessControlSections.Access);
        var restoration = new DirectorySecurity();
        restoration.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(),
            AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.NotNull(identity.User);
        restricted.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            denied.SetAccessControl(restricted);
            // Prove this is actual OS access denial, not a synthetic enumeration provider.
            Assert.Throws<UnauthorizedAccessException>(() =>
                Directory.EnumerateFileSystemEntries(denied.FullName).ToArray());

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                new WorkflowDiffService().CompareAsync(source, candidate));
        }
        finally
        {
            // Only this GUID-owned test directory's DACL was changed; restore before teardown.
            denied.SetAccessControl(restoration);
        }
    }

    [Fact]
    public async Task AccessibleNestedTreesRetainTheirActualChangedAndUnchangedFiles()
    {
        using var directory = new TestDirectory();
        var source = directory.GetPath("source");
        var candidate = directory.GetPath("candidate");
        SeedTree(source, "original hidden content");
        SeedTree(candidate, "changed hidden content");

        var result = await new WorkflowDiffService().CompareAsync(source, candidate);

        Assert.Equal(2, result.FileDiffs.Count);
        Assert.Equal(1, result.TotalFilesUnchanged);
        Assert.Equal(1, result.TotalFilesModified);
        var modified = Assert.Single(result.FileDiffs.Where(file => file.Kind == WorkflowFileDiffKind.Modified));
        Assert.Equal("private-subtree/content.md", modified.RelativePath);
        Assert.Contains("-original hidden content", modified.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("+changed hidden content", modified.UnifiedDiffText, StringComparison.Ordinal);
    }

    private static void SeedTree(string root, string content)
    {
        Directory.CreateDirectory(Path.Combine(root, "private-subtree"));
        File.WriteAllText(Path.Combine(root, "README.md"), "unchanged visible file\n");
        File.WriteAllText(Path.Combine(root, "private-subtree", "content.md"), content + "\n");
    }
}
