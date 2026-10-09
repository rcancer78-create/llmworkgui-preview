using System;
using System.Diagnostics;
using System.IO;
using LLMWorkGUI.ActivityLoadDriver;
using LLMWorkGUI.VisibleWorkflowHarness;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class HarnessTemporaryPathReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harness-path-review-" + Guid.NewGuid().ToString("N"));
    private string? _junction;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OptionsRefuseReparseComponentsBeforeCreatingEvidence(bool activityDriver, bool screenshots)
    {
        Directory.CreateDirectory(_root);
        var target = Directory.CreateDirectory(Path.Combine(_root, "unrelated-target")).FullName;
        var run = Directory.CreateDirectory(Path.Combine(_root, "run")).FullName;
        _junction = Path.Combine(screenshots ? run : _root, "alias");
        CreateJunction(_junction, target);
        var runPath = screenshots ? run : Path.Combine(_junction, "new-run");
        var screenshotPath = screenshots ? Path.Combine(_junction, "new-evidence") : Path.Combine(runPath, "screenshots");
        var arguments = new[] { "--mode", activityDriver ? "smoke" : "ci", "--run-root", runPath,
            "--screenshot-dir", screenshotPath };
        if (activityDriver) Assert.Throws<ActivityLoadUsageException>(() => ActivityLoadOptions.Parse(arguments));
        else Assert.Throws<HarnessUsageException>(() => HarnessOptions.Parse(arguments));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionsPermitOrdinaryIsolatedTemporaryRun(bool activityDriver)
    {
        var run = Path.Combine(_root, "run");
        var arguments = new[] { "--mode", activityDriver ? "smoke" : "ci", "--run-root", run,
            "--screenshot-dir", Path.Combine(run, "screenshots") };
        if (activityDriver) Assert.Equal(run, ActivityLoadOptions.Parse(arguments).RunRoot);
        else Assert.Equal(run, HarnessOptions.Parse(arguments).RunRoot);
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), "Synthetic junction creation timed out.");
        Assert.True(process.ExitCode == 0, output + error);
    }

    public void Dispose()
    {
        var expected = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (_junction is not null && Directory.Exists(_junction))
        {
            Assert.StartsWith(expected, Path.GetFullPath(_junction), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(_junction, recursive: false);
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
