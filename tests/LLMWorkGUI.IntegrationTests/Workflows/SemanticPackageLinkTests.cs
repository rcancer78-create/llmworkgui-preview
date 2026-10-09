using System.Diagnostics;
using LLMWorkGUI.Application.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class SemanticPackageLinkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_JunctionContentIsNeverFollowedOrSilentlyTrusted(bool rootLink)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "semantic-link-" + Guid.NewGuid().ToString("N"));
        var target = Directory.CreateDirectory(Path.Combine(root, "outside"));
        var package = Path.Combine(root, "package");
        var link = rootLink ? package : Path.Combine(package, "prompts");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.WriteAllText(Path.Combine(target.FullName, "hidden.md"), "External semantic content must never be followed.");
        try
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:SEMANTIC_TEST_LINK -Target $env:SEMANTIC_TEST_TARGET | Out-Null");
            start.Environment["SEMANTIC_TEST_LINK"] = link;
            start.Environment["SEMANTIC_TEST_TARGET"] = target.FullName;
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(30_000));
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());

            var facts = SemanticPackageAnalyzer.Read(package);
            Assert.Empty(facts.DocumentDigests);
            Assert.Contains(SemanticPackageComparison.Compare(facts, facts), change =>
                change.Kind == SemanticChangeKind.UnverifiableContent && change.NonClearable);
            Assert.True(File.Exists(Path.Combine(target.FullName, "hidden.md")));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase);
                Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
                Directory.Delete(link, recursive: false);
            }
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar,
                Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, recursive: true);
        }
    }
}
