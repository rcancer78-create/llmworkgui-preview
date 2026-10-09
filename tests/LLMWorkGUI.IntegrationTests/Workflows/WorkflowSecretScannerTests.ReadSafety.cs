using System.Text;
using LLMWorkGUI.Application.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowSecretScannerTests
{
    [Fact]
    public async Task UnreadableScratchFileCannotYieldCleanScan()
    {
        var root = _directory.GetPath("scratch");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "secrets.env");
        await File.WriteAllTextAsync(path, "password = hidden-secret-value");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var workspace = new ScratchWorkspace(root, ScratchScope.Adaptation, "read-safety");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => _scanner.ScanScratchWorkspaceAsync(workspace));
    }

    [Fact]
    public async Task LongPrefixDoesNotHideSensitiveAssignmentBeyondSnippetBoundary()
    {
        var content = new string('a', 32 * 1024) + " password = protected-secret";
        var report = await Task.Run(() => _scanner.ScanFilesAsync(new Dictionary<string, byte[]>
        {
            ["config.txt"] = Encoding.UTF8.GetBytes(content)
        })).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(report.HasFindings);
        Assert.Contains(report.Findings, finding => finding.RuleName == "SensitiveAssignment");
        Assert.DoesNotContain("protected-secret", report.Findings[0].RedactedSnippet);
    }
}
