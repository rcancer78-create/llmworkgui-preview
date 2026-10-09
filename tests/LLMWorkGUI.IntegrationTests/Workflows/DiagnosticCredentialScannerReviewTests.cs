using System.Text;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;
namespace LLMWorkGUI.IntegrationTests.Workflows;
public sealed class DiagnosticCredentialScannerReviewTests
{
    [Theory]
    [InlineData("accessToken=owned-scan-canary")]
    [InlineData("refreshToken=owned-scan-canary")]
    [InlineData("https://owned:owned-scan-canary@example.test/v1")]
    [InlineData("eyJvd25lZCI6dHJ1ZX0.eyJmaXh0dXJlIjp0cnVlfQ.ownedSyntheticSignature")]
    public async Task CredentialShapesAreExcludedWithoutRawFindingMaterial(string text)
    {
        var report = await new WorkflowSecretScanner().ScanFilesAsync(new Dictionary<string,byte[]> { ["owned.txt"] = Encoding.UTF8.GetBytes(text) });
        Assert.True(report.HasFindings);
        Assert.Contains("owned.txt", report.RecommendedExcludedFiles);
        Assert.All(report.Findings, finding => Assert.DoesNotContain("owned-scan-canary", finding.RedactedSnippet));
    }
}
