using System.Text;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class BoundedOutputCaptureReviewTests
{
    [Theory]
    [InlineData("ABCDEFGHI", 6, 3, 3, "ABC", "GHI")]
    [InlineData("A😀БZ", 6, 3, 3, "A", "БZ")]
    [InlineData("ABCDEFGHI", 4, 0, 3, "", "GHI")]
    [InlineData("ABCDEFGHI", 4, 3, 0, "ABC", "")]
    public void OversizedSingleChunkRetainsExactUtf8HeadAndTailBudgets(string text, int memory, int head, int tail,
        string expectedHead, string expectedTail)
    {
        var capture = new BoundedOutputCapture(memory, head, tail);
        capture.Append(text, Encoding.UTF8.GetByteCount(text));
        Assert.True(capture.Overflowed);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), capture.TotalBytes);
        Assert.True(Encoding.UTF8.GetByteCount(capture.HeadText) <= head);
        Assert.True(Encoding.UTF8.GetByteCount(capture.TailText) <= tail);
        Assert.Equal(expectedHead, capture.HeadText);
        Assert.Equal(expectedTail, capture.TailText);
    }

    [Fact]
    public void BelowMemoryLimitStillPreservesCompleteOutputBeforeOverflow()
    {
        var capture = new BoundedOutputCapture(20, 3, 3);
        capture.Append("ABCDEFGHI", 9);
        Assert.False(capture.Overflowed);
        Assert.Equal("ABCDEFGHI", capture.HeadText);
        Assert.Empty(capture.TailText);
    }

    [Fact]
    public async Task RealStdoutChunkHonorsSmallByteBudgetsWhileSpoolPreservesFullOutput()
    {
        using var data = new TestDirectory();
        using var workspace = new TestDirectory();
        var supervisor = new ProcessSupervisor(Options.Create(new ProcessSupervisorOptions
        {
            OutputMemoryLimitBytes = 6, OutputHeadRetentionBytes = 3, OutputTailRetentionBytes = 3
        }), new StorageOptions { AppDataDirectory = data.Root });
        var result = await supervisor.ExecuteAsync(new ProcessStartSpecification
        {
            ExecutionId = "small-real-output-budget",
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('ABCDEFGHI')"],
            WorkingDirectory = workspace.Root
        });
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputOverflowed);
        Assert.Equal(9, result.StandardOutputBytes);
        Assert.Equal("ABCDEFGHI", await File.ReadAllTextAsync(result.StandardOutputLogPath));
        Assert.Equal("ABC", result.StandardOutputHead);
        Assert.Equal("GHI", result.StandardOutputTail);
    }
}
