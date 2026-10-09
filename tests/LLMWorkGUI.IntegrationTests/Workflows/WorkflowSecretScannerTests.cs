using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowSecretScannerTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly WorkflowSecretScanner _scanner = new(new SensitiveDataFilter());

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public async Task ScanFilesAsync_DetectsOpenAiKeyAndRedactsSnippet()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config/secrets.env"] = Encoding.UTF8.GetBytes(
                "line one\napi_key = \"sk-abcdefgh12345678\"\nline three")
        };

        var report = await _scanner.ScanFilesAsync(files);

        Assert.True(report.HasFindings);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("config/secrets.env", finding.RelativePath);
        Assert.Equal(2, finding.LineNumber);
        Assert.Equal("OpenAiKey", finding.RuleName);
        Assert.Contains("[REDACTED]", finding.RedactedSnippet, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefgh12345678", finding.RedactedSnippet, StringComparison.Ordinal);
        Assert.Equal(new[] { "config/secrets.env" }, report.RecommendedExcludedFiles);
    }

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "PrivateKey", "-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("token: urn:llmworkgui:secret:abc123", "SecretReference", "urn:llmworkgui:secret:abc123")]
    [InlineData("Authorization: Bearer abcdefghijklmnop", "BearerToken", "abcdefghijklmnop")]
    [InlineData("aws_access_key_id = AKIAIOSFODNN7EXAMPLE", "AwsAccessKey", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "GitHubToken", "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    // Deliberately invalid Slack IDs keep this redaction fixture noncredential.
    [InlineData("xoxb-SYNTHETIC-INVALID-FIXTURE", "SlackToken", "xoxb-SYNTHETIC-INVALID-FIXTURE")]
    [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstu", "GoogleApiKey", "AIzaSyA1234567890abcdefghijklmnopqrstu")]
    [InlineData("password = hunter2secret", "SensitiveAssignment", "hunter2secret")]
    [InlineData("cursor_key = crsr_abcdefghijklmnop", "SensitiveData", "crsr_abcdefghijklmnop")]
    public async Task ScanFilesAsync_ClassifiesRulesAndNeverReturnsRawSecret(
        string content,
        string expectedRule,
        string rawSecret)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config/app.env"] = Encoding.UTF8.GetBytes(content)
        };

        var report = await _scanner.ScanFilesAsync(files);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(expectedRule, finding.RuleName);
        Assert.DoesNotContain(rawSecret, finding.RedactedSnippet, StringComparison.Ordinal);
        Assert.Equal(new[] { "config/app.env" }, report.RecommendedExcludedFiles);
    }

    [Fact]
    public async Task ScanFilesAsync_ReturnsNoFindingsForCleanContent()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["README.md"] = Encoding.UTF8.GetBytes("# Workflow\nThis file has no secrets."),
            ["prompts/executor.md"] = Encoding.UTF8.GetBytes("You are the executor.")
        };

        var report = await _scanner.ScanFilesAsync(files);

        Assert.False(report.HasFindings);
        Assert.Empty(report.Findings);
        Assert.Empty(report.RecommendedExcludedFiles);
    }

    [Fact]
    public async Task ScanFilesAsync_DoesNotTreatDependencyPackageNameAsSecret()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["app/LLMWorkGUI.App.deps.json"] = Encoding.UTF8.GetBytes(
                "\"Microsoft.Extensions.Configuration.UserSecrets\": \"8.0.0\",")
        };

        var report = await _scanner.ScanFilesAsync(files);

        Assert.False(report.HasFindings);
        Assert.Empty(report.Findings);
        Assert.Empty(report.RecommendedExcludedFiles);
    }

    [Fact]
    public async Task ScanFilesAsync_StillDetectsSecretValueBesideDependencyLikeName()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["app/LLMWorkGUI.App.deps.json"] = Encoding.UTF8.GetBytes(
                "\"Microsoft.Extensions.Configuration.UserSecrets\": \"Bearer abcdefghijklmnop\",")
        };

        var report = await _scanner.ScanFilesAsync(files);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("BearerToken", finding.RuleName);
        Assert.DoesNotContain("abcdefghijklmnop", finding.RedactedSnippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanFilesAsync_KeepsDependencyNameSensitiveOutsideDepsMetadata()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config/settings.json"] = Encoding.UTF8.GetBytes(
                "\"Microsoft.Extensions.Configuration.UserSecrets\": \"value\",")
        };

        var report = await _scanner.ScanFilesAsync(files);

        Assert.True(report.HasFindings);
        Assert.Equal("SensitiveData", Assert.Single(report.Findings).RuleName);
    }

    [Fact]
    public async Task ScanFilesAsync_NormalizesWindowsSeparators()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [@"sub\dir\secret.txt"] = Encoding.UTF8.GetBytes("api_key=sk-abcdefgh12345678")
        };

        var report = await _scanner.ScanFilesAsync(files);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("sub/dir/secret.txt", finding.RelativePath);
        Assert.Equal(new[] { "sub/dir/secret.txt" }, report.RecommendedExcludedFiles);
    }

    [Fact]
    public async Task ScanFilesAsync_ReportsEveryFindingWithLineNumbers()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config/app.env"] = Encoding.UTF8.GetBytes(
                "first line\napi_key=sk-abcdefgh12345678\nthird line\npassword = hunter2secret")
        };

        var report = await _scanner.ScanFilesAsync(files);

        Assert.Equal(2, report.Findings.Count);
        Assert.Equal(new[] { 2, 4 }, report.Findings.Select(finding => finding.LineNumber));
        Assert.Equal(new[] { "config/app.env" }, report.RecommendedExcludedFiles);
    }

    [Fact]
    public async Task ScanFilesAsync_DecodesUtf16Content()
    {
        var bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("api_key=sk-abcdefgh12345678"))
            .ToArray();

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config/utf16.env"] = bytes
        };

        var report = await _scanner.ScanFilesAsync(files);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(1, finding.LineNumber);
        Assert.DoesNotContain("sk-abcdefgh12345678", finding.RedactedSnippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanFilesAsync_RejectsNullFileContents()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config/app.env"] = null!
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _scanner.ScanFilesAsync(files));
    }

    [Fact]
    public async Task ScanScratchWorkspaceAsync_ScansNestedFilesAndExcludesMatches()
    {
        var workspaceRoot = _directory.GetPath("workspace");
        var nestedDirectory = Path.Combine(workspaceRoot, "config");
        Directory.CreateDirectory(nestedDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, "README.md"),
            "# Clean workflow");

        await File.WriteAllTextAsync(
            Path.Combine(nestedDirectory, "credentials.env"),
            "api_key=sk-abcdefgh12345678");

        using var workspace = new ScratchWorkspace(workspaceRoot, ScratchScope.Adaptation, "scope-1");

        var report = await _scanner.ScanScratchWorkspaceAsync(workspace);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("config/credentials.env", finding.RelativePath);
        Assert.Equal(new[] { "config/credentials.env" }, report.RecommendedExcludedFiles);
    }

    [Fact]
    public async Task ScanScratchWorkspaceAsync_ThrowsForCleanedUpWorkspace()
    {
        var workspaceRoot = _directory.GetPath("workspace-clean");
        Directory.CreateDirectory(workspaceRoot);

        var workspace = new ScratchWorkspace(workspaceRoot, ScratchScope.Adaptation, "scope-clean");
        await workspace.CleanupWorkspaceAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _scanner.ScanScratchWorkspaceAsync(workspace));
    }
}
