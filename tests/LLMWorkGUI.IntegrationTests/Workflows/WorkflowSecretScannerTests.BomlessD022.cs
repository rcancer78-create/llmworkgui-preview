using System.Text;
using LLMWorkGUI.Application.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowSecretScannerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BomlessUtf16CannotProduceCleanScanForAKnownKey(bool bigEndian)
    {
        const string key = "sk-abcdefgh12345678";
        var encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        var bytes = encoding.GetBytes("api_key = \"" + key + "\"\n");
        var report = await _scanner.ScanFilesAsync(new Dictionary<string, byte[]> { ["secrets.env"] = bytes });
        Assert.True(report.HasFindings);
        Assert.Contains("secrets.env", report.RecommendedExcludedFiles);
        Assert.All(report.Findings, finding => Assert.DoesNotContain(key, finding.RedactedSnippet));
    }
}
