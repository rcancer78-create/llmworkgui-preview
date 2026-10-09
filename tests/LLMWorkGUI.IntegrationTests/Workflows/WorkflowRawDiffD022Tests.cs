using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowRawDiffD022Tests
{
    [Theory]
    [InlineData("bom")]
    [InlineData("endianness")]
    [InlineData("line-ending")]
    public async Task ChangedRawBytesNeverProduceAnEmptyNonbinaryModifiedDiff(string change)
    {
        using var directory = new TestDirectory();
        var before = directory.GetPath("source");
        var after = directory.GetPath("candidate");
        Directory.CreateDirectory(before);
        Directory.CreateDirectory(after);
        byte[] source = Encoding.UTF8.GetBytes("alpha\nbeta\n");
        byte[] candidate;
        if (change == "bom") candidate = Encoding.UTF8.GetPreamble().Concat(source).ToArray();
        else if (change == "endianness")
        {
            source = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("alpha\nbeta\n")).ToArray();
            candidate = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("alpha\nbeta\n")).ToArray();
        }
        else candidate = Encoding.UTF8.GetBytes("alpha\r\nbeta\r\n");
        await File.WriteAllBytesAsync(Path.Combine(before, "README.md"), source);
        await File.WriteAllBytesAsync(Path.Combine(after, "README.md"), candidate);

        var diff = await new WorkflowDiffService().CompareAsync(before, after);

        var file = Assert.Single(diff.FileDiffs);
        Assert.Equal(WorkflowFileDiffKind.Modified, file.Kind);
        // A no-line-statistics binary/encoding outcome is acceptable; silent textual identity is not.
        Assert.True(file.IsBinary || !string.IsNullOrWhiteSpace(file.UnifiedDiffText));
        Assert.Equal(0, file.LinesAdded);
        Assert.Equal(0, file.LinesDeleted);
    }
}
