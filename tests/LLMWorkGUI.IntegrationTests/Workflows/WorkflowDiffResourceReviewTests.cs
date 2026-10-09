using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowDiffResourceReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcessiveOneSidedLineCountsAreRefusedBeforeBuildingAFullLineScript(bool removed)
    {
        using var data = new TestDirectory();
        var source = data.GetPath("source");
        var candidate = data.GetPath("candidate");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(candidate);
        await File.WriteAllTextAsync(Path.Combine(removed ? source : candidate, "many-lines.txt"),
            new string('\n', 100_001));
        await Assert.ThrowsAsync<InvalidDataException>(() => new WorkflowDiffService().CompareAsync(source, candidate));
    }

    [Fact]
    public async Task FileBeyondTheEstablishedImportSizeLimitIsRefusedBeforeReadingItsBytes()
    {
        using var data = new TestDirectory();
        var source = data.GetPath("source");
        var candidate = data.GetPath("candidate");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(candidate);
        await using (var stream = File.Create(Path.Combine(candidate, "large.bin")))
            stream.SetLength((long)WorkflowImportLimits.MaxSingleFileBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WorkflowDiffService().CompareAsync(source, candidate));
    }
}
