using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LLMWorkGUI.Infrastructure.Storage;
using static LLMWorkGUI.Infrastructure.Storage.WorkflowBlobStore;
using static LLMWorkGUI.Infrastructure.Security.InputSanitizer;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class SecurityAndStorageContractTests
{
    private const string DemoBaseDirectory = @"C:\workspace\demo-app";

    private static readonly Regex SecretReferenceUrnPattern = new(
        @"^urn:llmworkgui:secret:[a-z0-9_-]+$",
        RegexOptions.CultureInvariant);

    private static readonly Regex SecretUrnCandidatePattern = new(
        @"urn:llmworkgui:[A-Za-z0-9._:\-\[\]\^\$]+",
        RegexOptions.CultureInvariant);

    private static readonly Regex DataFlowRowPattern = new(
        @"^\|\s*`(?<id>F\d+[a-z]?)`\s*\|",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly (string Name, string Pattern)[] ForbiddenPatterns =
    {
        ("OpenAI-style API key", @"\bsk-[A-Za-z0-9_-]{12,}\b"),
        ("GitHub token", @"\bgh[pousr]_[A-Za-z0-9]{20,}\b"),
        ("AWS access key", @"\bAKIA[0-9A-Z]{16}\b"),
        ("Slack token", @"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"),
        ("Cursor API key", @"\bcrsr_[A-Za-z0-9_-]{12,}\b"),
        ("Generic credential assignment", @"\b(api[_-]?key|token|secret|password)\b\s*[:=]\s*""[A-Za-z0-9._\-]{8,}"""),
        ("Bearer credential", @"\bBearer\s+[A-Za-z0-9._~+/-]{16,}"),
        ("Private key block", @"-----BEGIN [A-Z ]*PRIVATE KEY-----"),
        ("Windows user profile path", @"[A-Za-z]:\\+Users\\+"),
        ("POSIX home path", @"/home/[A-Za-z0-9._-]+/"),
        ("Non-placeholder email", @"[A-Za-z0-9._%+-]+@(?!example\.com)[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
    };

    private static readonly string[] SecurityArtifacts =
    {
        ThreatModelPath,
        DataFlowPath,
        Adr0005Path,
        Adr0006Path,
        TestSourcePath
    };

    private static readonly string[] SecretUrnArtifacts =
    {
        ThreatModelPath,
        DataFlowPath,
        Adr0005Path
    };

    [Fact]
    public void ThreatModel_ExistsAndDeclaresStrideBoundariesClassificationsAndMitigations()
    {
        var content = File.ReadAllText(ThreatModelPath);

        Assert.Contains("**Статус:** Accepted", content, StringComparison.Ordinal);

        foreach (var heading in new[]
                 {
                     "## 1. Назначение и область",
                     "## 2. Trust boundaries",
                     "## 3. Классификация данных",
                     "## 4. STRIDE анализ",
                     "## 5. Контрмеры",
                     "## 6. Остаточные риски",
                     "## 7. Связь с контрактными тестами"
                 })
        {
            Assert.Contains(heading, content, StringComparison.Ordinal);
        }

        foreach (var boundary in new[] { "TB-1", "TB-2", "TB-3", "TB-4", "TB-5", "TB-6" })
        {
            Assert.Contains(boundary, content, StringComparison.Ordinal);
        }

        foreach (var boundaryName in new[] { "UI", "App Core", "Supervisor", "Local Storage", "Managed Process", "Network" })
        {
            Assert.Contains(boundaryName, content, StringComparison.Ordinal);
        }

        foreach (var strideCategory in new[]
                 {
                     "Spoofing",
                     "Tampering",
                     "Repudiation",
                     "Information disclosure",
                     "Denial of service",
                     "Elevation of privilege"
                 })
        {
            Assert.Contains(strideCategory, content, StringComparison.Ordinal);
        }

        foreach (var dataClass in new[] { "PublicSource", "PrivateSource", "Restricted" })
        {
            Assert.Contains(dataClass, content, StringComparison.Ordinal);
        }

        Assert.Contains("Классом по умолчанию", content, StringComparison.Ordinal);
        Assert.Contains("`PrivateSource`", content, StringComparison.Ordinal);

        foreach (var mitigation in new[]
                 {
                     "Redaction pipeline",
                     "named OS mutex",
                     "checkout writer lock",
                     "Path traversal prevention",
                     "NTFS alternate data stream"
                 })
        {
            Assert.Contains(mitigation, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DataFlow_DeclaresLevel0Level1AndRequiredFlows()
    {
        var content = File.ReadAllText(DataFlowPath);

        Assert.Contains("**Статус:** Accepted", content, StringComparison.Ordinal);

        Assert.Contains("## 3. Level 0", content, StringComparison.Ordinal);
        Assert.Contains("## 4. Level 1", content, StringComparison.Ordinal);

        foreach (var flowTopic in new[] { "secret", "prompt", "SSE", "approval", "workflow blob", "scratch" })
        {
            Assert.Contains(flowTopic, content, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("вне project root", content, StringComparison.Ordinal);
        Assert.Contains("вне blob store", content, StringComparison.Ordinal);
        Assert.Contains("redaction", content, StringComparison.OrdinalIgnoreCase);

        var flowIds = DataFlowRowPattern.Matches(content)
            .Select(match => match.Groups["id"].Value)
            .ToArray();

        Assert.True(flowIds.Length >= 12, "Data flow table must declare at least 12 numbered flows.");
        Assert.Equal(flowIds.Length, flowIds.Distinct(StringComparer.Ordinal).Count());

        foreach (var requiredFlow in new[] { "F1", "F4", "F5", "F7", "F9", "F12", "F13", "F14" })
        {
            Assert.Contains(requiredFlow, flowIds);
        }
    }

    [Fact]
    public void Adr0005_IsAcceptedAndFixesSecretStorageInvariants()
    {
        var content = File.ReadAllText(Adr0005Path);

        Assert.Contains("**Статус:** Accepted", content, StringComparison.Ordinal);

        foreach (var heading in new[] { "## Контекст", "## Решение", "## Последствия", "## Риски и ограничения", "## Альтернативы" })
        {
            Assert.Contains(heading, content, StringComparison.Ordinal);
        }

        Assert.Contains("Windows Credential Manager", content, StringComparison.Ordinal);
        Assert.Contains("DPAPI", content, StringComparison.Ordinal);
        Assert.Contains("CurrentUser", content, StringComparison.Ordinal);
        Assert.Contains("urn:llmworkgui:secret:", content, StringComparison.Ordinal);
        Assert.Contains("reference-only", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redaction", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plaintext", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQLite", content, StringComparison.Ordinal);
        Assert.Contains("command line", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("process arguments", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logs", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adr0006_IsAcceptedAndFixesBlobAndVersionStorageInvariants()
    {
        var content = File.ReadAllText(Adr0006Path);

        Assert.Contains("**Статус:** Accepted", content, StringComparison.Ordinal);

        foreach (var heading in new[] { "## Контекст", "## Решение", "## Последствия", "## Риски и ограничения", "## Альтернативы" })
        {
            Assert.Contains(heading, content, StringComparison.Ordinal);
        }

        Assert.Contains("content-addressed", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SHA-256", content, StringComparison.Ordinal);
        Assert.Contains("immutable", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blobs/sha256/<first2>/<full-hex>", content, StringComparison.Ordinal);
        Assert.Contains("scratch", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("вне project root", content, StringComparison.Ordinal);
        Assert.Contains("вне blob store", content, StringComparison.Ordinal);
        Assert.Contains("Path traversal guard", content, StringComparison.Ordinal);
        Assert.Contains("`..`", content, StringComparison.Ordinal);
        Assert.Contains("NTFS alternate data stream", content, StringComparison.Ordinal);
        Assert.Contains("temp file", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rename", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("транзакцион", content, StringComparison.OrdinalIgnoreCase);

        foreach (var retention in new[] { "30 дней", "14 дней", "7 дней", "90 дней", "180 дней" })
        {
            Assert.Contains(retention, content, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("urn:llmworkgui:secret:openai")]
    [InlineData("urn:llmworkgui:secret:openai-api-key")]
    [InlineData("urn:llmworkgui:secret:my_provider_2")]
    [InlineData("urn:llmworkgui:secret:demo")]
    public void SecretReferenceUrn_AcceptsValidReferences(string value)
    {
        Assert.Matches(SecretReferenceUrnPattern, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("urn:llmworkgui:secret:")]
    [InlineData("urn:llmworkgui:secret:OpenAI")]
    [InlineData("urn:llmworkgui:secret:openai.api")]
    [InlineData("urn:llmworkgui:secret:openai/api")]
    [InlineData("urn:llmworkgui:secret:openai api")]
    [InlineData("urn:llmworkgui:secret:../escape")]
    [InlineData("urn:llmworkgui:secret:openai:extra")]
    [InlineData("urn:llmworkgui:token:openai")]
    [InlineData(" urn:llmworkgui:secret:openai")]
    [InlineData("urn:llmworkgui:secret:openai ")]
    [InlineData("URN:llmworkgui:secret:openai")]
    [InlineData("urn:llmworkgui:secret:openai?query")]
    public void SecretReferenceUrn_RejectsInvalidReferences(string value)
    {
        Assert.DoesNotMatch(SecretReferenceUrnPattern, value);
    }

    [Fact]
    public void SecretArtifacts_UseOnlyValidSecretReferenceUrns()
    {
        var candidates = new List<string>();

        foreach (var path in SecretUrnArtifacts)
        {
            var content = File.ReadAllText(path);
            Assert.Contains("urn:llmworkgui:secret:", content, StringComparison.Ordinal);

            foreach (Match match in SecretUrnCandidatePattern.Matches(content))
            {
                var token = match.Value.TrimEnd('.', ',', ';', ')', '`');
                if (token.IndexOfAny(new[] { '[', ']', '^', '$' }) >= 0)
                {
                    continue;
                }

                candidates.Add(token);
            }
        }

        Assert.NotEmpty(candidates);

        foreach (var candidate in candidates)
        {
            Assert.Matches(SecretReferenceUrnPattern, candidate);
        }
    }

    [Fact]
    public void ContentAddressedBlobHashing_IsDeterministic()
    {
        Assert.Equal(
            "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ComputeBlobId(Array.Empty<byte>()));

        Assert.Equal(
            "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ComputeBlobId(Encoding.UTF8.GetBytes("abc")));

        var content = Encoding.UTF8.GetBytes("workflow-package-demo");

        var first = ComputeBlobId(content);
        var second = ComputeBlobId(content);
        Assert.Equal(first, second);
        Assert.Equal(first, ComputeBlobId(content.ToArray()));

        using (var stream = new MemoryStream(content, writable: false))
        {
            Assert.Equal(first, "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }

        using (var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            incremental.AppendData(content, 0, 5);
            incremental.AppendData(content, 5, content.Length - 5);
            Assert.Equal(first, "sha256:" + Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant());
        }

        Assert.NotEqual(first, ComputeBlobId(Encoding.UTF8.GetBytes("workflow-package-demo!")));
        Assert.NotEqual(first, ComputeBlobId(Encoding.UTF8.GetBytes("workflow-package-demo ")));
    }

    [Fact]
    public void ContentAddressedBlobPath_IsDerivedFromHash()
    {
        var emptyBlobId = ComputeBlobId(Array.Empty<byte>());
        Assert.Equal(
            "blobs/sha256/e3/e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            BuildBlobRelativePath(emptyBlobId));

        var alphaBlobId = ComputeBlobId(Encoding.UTF8.GetBytes("alpha"));
        var betaBlobId = ComputeBlobId(Encoding.UTF8.GetBytes("beta"));
        Assert.NotEqual(alphaBlobId, betaBlobId);

        var alphaPath = BuildBlobRelativePath(alphaBlobId);
        var segments = alphaPath.Split('/');

        Assert.Equal(4, segments.Length);
        Assert.Equal("blobs", segments[0]);
        Assert.Equal("sha256", segments[1]);
        Assert.Equal(alphaBlobId["sha256:".Length..][..2], segments[2]);
        Assert.Equal(alphaBlobId["sha256:".Length..], segments[3]);
        Assert.NotEqual(alphaPath, BuildBlobRelativePath(betaBlobId));
    }

    [Fact]
    public void PathTraversalGuard_RejectsEscapingAbsolutePathsAndAlternateDataStreams()
    {
        Assert.True(IsPathInsideBase(DemoBaseDirectory, "workflows/demo/main.md"));
        Assert.True(IsPathInsideBase(DemoBaseDirectory, "a/b/c.txt"));
        Assert.True(IsPathInsideBase(DemoBaseDirectory, "file.txt"));

        Assert.False(IsPathInsideBase(DemoBaseDirectory, string.Empty));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, " "));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, ".."));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, "../outside.txt"));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, "..\\..\\Windows\\System32\\cmd.exe"));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, "sub/../../outside.txt"));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, "nested/.."));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, @"C:\Windows\System32"));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, "C:relative.txt"));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, "/tmp/outside"));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, @"\\server\share\file"));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, "file.txt:secret"));
        Assert.False(IsPathInsideBase(DemoBaseDirectory, "blob:stream"));
    }

    [Fact]
    public void SecurityArtifacts_ContainNoSecretsOrUserProfilePaths()
    {
        foreach (var path in SecurityArtifacts)
        {
            var content = File.ReadAllText(path);

            foreach (var (name, pattern) in ForbiddenPatterns)
            {
                Assert.False(
                    Regex.IsMatch(content, pattern, RegexOptions.CultureInvariant),
                    $"Artifact '{Path.GetFileName(path)}' matches forbidden pattern '{name}'.");
            }
        }
    }

    private static string BuildBlobRelativePath(string blobId)
    {
        var store = new WorkflowBlobStore(DemoBaseDirectory);
        return Path.GetRelativePath(store.AppDataDirectory, store.GetBlobPath(blobId)).Replace('\\', '/');
    }

    private static string RepositoryPath(params string[] segments)
    {
        return Path.Combine(FindRepositoryRoot(), Path.Combine(segments));
    }

    private static string ThreatModelPath =>
        RepositoryPath("docs", "architecture", "THREAT_MODEL.md");

    private static string DataFlowPath =>
        RepositoryPath("docs", "architecture", "DATA_FLOW.md");

    private static string Adr0005Path =>
        RepositoryPath("docs", "adr", "ADR-0005-secret-storage-windows.md");

    private static string Adr0006Path =>
        RepositoryPath("docs", "adr", "ADR-0006-workflow-blob-and-version-storage.md");

    private static string TestSourcePath =>
        RepositoryPath("tests", "LLMWorkGUI.Backends.ContractTests", "SecurityAndStorageContractTests.cs");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LLMWorkGUI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root containing 'LLMWorkGUI.sln' was not found.");
    }
}
