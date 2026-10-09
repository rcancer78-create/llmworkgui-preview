using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class InputSanitizerTests : IDisposable
{
    private readonly TestDirectory _directory = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    private string BaseDirectory => _directory.Root;

    [Theory]
    [InlineData("workflows/demo/main.md")]
    [InlineData("a/b/c.txt")]
    [InlineData("file.txt")]
    [InlineData(@"nested\file.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("a//b.txt")]
    public void ResolveSafePath_AcceptsPathsInsideBaseDirectory(string relativePath)
    {
        var resolved = InputSanitizer.ResolveSafePath(BaseDirectory, relativePath);

        Assert.StartsWith(
            BaseDirectory + System.IO.Path.DirectorySeparatorChar,
            resolved,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    [InlineData("../outside.txt")]
    [InlineData(@"..\..\Windows\System32\cmd.exe")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("nested/..")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData("C:relative.txt")]
    [InlineData("/tmp/outside")]
    [InlineData(@"\\server\share\file")]
    [InlineData("file.txt:secret")]
    [InlineData("blob:stream")]
    [InlineData("bad\u0000name")]
    [InlineData("%2e%2e/escape.txt")]
    [InlineData("nested/..%2f..%2fescape.txt")]
    public void ResolveSafePath_RejectsTraversalAndUnsafePaths(string relativePath)
    {
        Assert.False(InputSanitizer.TryResolveSafePath(BaseDirectory, relativePath, out var resolved));
        Assert.Equal(string.Empty, resolved);
        Assert.Throws<PathTraversalException>(() => InputSanitizer.ResolveSafePath(BaseDirectory, relativePath));
    }

    [Fact]
    public void ResolveSafePath_PreventsZipSlipExtractionEscape()
    {
        var resolved = InputSanitizer.ResolveSafePath(BaseDirectory, "workflow/scripts/build.sh");

        Assert.StartsWith(
            BaseDirectory + System.IO.Path.DirectorySeparatorChar,
            resolved,
            StringComparison.OrdinalIgnoreCase);

        Assert.Throws<PathTraversalException>(
            () => InputSanitizer.ResolveSafePath(BaseDirectory, "../../Windows/System32/drivers/etc/hosts"));
    }

    [Fact]
    public void IsPathInsideBase_MatchesZipSlipGuardSemantics()
    {
        Assert.True(InputSanitizer.IsPathInsideBase(BaseDirectory, "workflows/demo/main.md"));
        Assert.True(InputSanitizer.IsPathInsideBase(BaseDirectory, "file.txt"));
        Assert.True(InputSanitizer.IsPathInsideBase(BaseDirectory, @"nested\file.txt"));

        Assert.False(InputSanitizer.IsPathInsideBase(BaseDirectory, string.Empty));
        Assert.False(InputSanitizer.IsPathInsideBase(BaseDirectory, " "));
        Assert.False(InputSanitizer.IsPathInsideBase(BaseDirectory, null));
        Assert.False(InputSanitizer.IsPathInsideBase(BaseDirectory, ".."));
        Assert.False(InputSanitizer.IsPathInsideBase(BaseDirectory, "nested/.."));
        Assert.False(InputSanitizer.IsPathInsideBase(BaseDirectory, @"C:\Windows\System32"));
        Assert.False(InputSanitizer.IsPathInsideBase(BaseDirectory, "C:relative.txt"));
        Assert.False(InputSanitizer.IsPathInsideBase(BaseDirectory, "/tmp/outside"));
        Assert.False(InputSanitizer.IsPathInsideBase(BaseDirectory, @"\\server\share\file"));
        Assert.False(InputSanitizer.IsPathInsideBase(BaseDirectory, "file.txt:secret"));
    }

    [Theory]
    [InlineData("a/b.txt", "a/b.txt")]
    [InlineData(@"a\b.txt", "a/b.txt")]
    [InlineData("a//b.txt", "a/b.txt")]
    [InlineData("./a/b.txt", "a/b.txt")]
    [InlineData("a/./b.txt", "a/b.txt")]
    public void NormalizeArchiveEntryPath_NormalizesSeparatorsAndSegments(string entryName, string expected)
    {
        Assert.Equal(expected, InputSanitizer.NormalizeArchiveEntryPath(entryName));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/absolute.txt")]
    [InlineData(@"C:\absolute.txt")]
    [InlineData("C:relative.txt")]
    [InlineData("a\0b.txt")]
    [InlineData("trailing-dot.")]
    [InlineData("trailing-space ")]
    [InlineData("%2e%2e/evil.txt")]
    public void NormalizeArchiveEntryPath_RejectsUnsafeEntries(string entryName)
    {
        Assert.Throws<PathTraversalException>(() => InputSanitizer.NormalizeArchiveEntryPath(entryName));
    }

    [Fact]
    public void EnsureNoReparsePoints_AllowsRegularDirectoryTree()
    {
        var resolved = InputSanitizer.ResolveSafePath(BaseDirectory, "plain/nested/file.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(resolved)!);

        InputSanitizer.EnsureNoReparsePoints(BaseDirectory, resolved);
    }
}
