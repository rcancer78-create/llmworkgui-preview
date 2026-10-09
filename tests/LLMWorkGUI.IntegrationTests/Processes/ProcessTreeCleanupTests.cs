using System.Diagnostics;
using LLMWorkGUI.Infrastructure.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Processes;

public sealed class ProcessTreeCleanupTests
{
    [WindowsTreeFact]
    public async Task UnassignedJobCapturesAnExistingChildAtTermination()
    {
        using var harness = new TreeHarness();
        using var root = harness.StartRoot();
        using var child = await harness.GetChildAsync();
        using var terminator = ProcessTreeTerminator.Create(NullLogger.Instance);
        // No TryAssign-time capture: exercise the termination-time fallback independently.
        try
        {
            await terminator.TerminateAsync(root, TimeSpan.FromMilliseconds(100), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(child.HasExited);
        }
        finally
        {
            await KillOwnedAsync(child);
            await KillOwnedAsync(root);
        }
    }

    [WindowsTreeFact]
    public async Task DisposalDuringTerminationRetainsCapturedProcessIdentities()
    {
        using var harness = new TreeHarness();
        using var root = harness.StartRoot();
        using var child = await harness.GetChildAsync();
        var terminator = ProcessTreeTerminator.Create(NullLogger.Instance);
        try
        {
            Assert.True(terminator.TryAssign(root));
            var stop = terminator.TerminateAsync(root, TimeSpan.FromMilliseconds(250), CancellationToken.None);
            terminator.Dispose();
            await stop.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(child.HasExited);
        }
        finally
        {
            await KillOwnedAsync(child);
            await KillOwnedAsync(root);
            terminator.Dispose();
        }
    }

    [WindowsTreeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildCreatedBeforeJobAssignmentIsTerminatedEvenAfterRootExit(bool exitRootFirst)
    {
        using var harness = new TreeHarness();
        using var root = harness.StartRoot();
        using var child = await harness.GetChildAsync();
        using var sibling = harness.StartSibling();
        using var terminator = ProcessTreeTerminator.Create(NullLogger.Instance);
        Assert.True(terminator.TryAssign(root));
        if (exitRootFirst)
        {
            root.Kill(); // The retained child identity must survive losing its parent.
            await root.WaitForExitAsync();
        }
        try
        {
            await terminator.TerminateAsync(root, TimeSpan.FromMilliseconds(100), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(root.HasExited);
            Assert.True(child.HasExited);
            Assert.False(sibling.HasExited);
        }
        finally
        {
            await KillOwnedAsync(child);
            await KillOwnedAsync(root);
            await KillOwnedAsync(sibling);
        }
    }

    [WindowsTreeFact]
    public async Task NewGrandchildIsTerminatedAndRepeatedDisposalIsSafe()
    {
        using var harness = new TreeHarness();
        using var root = harness.StartRoot();
        using var child = await harness.GetChildAsync();
        var terminator = ProcessTreeTerminator.Create(NullLogger.Instance);
        Process? grandchild = null;
        try
        {
            Assert.True(terminator.TryAssign(root));
            harness.AllowGrandchild();
            grandchild = await harness.GetGrandchildAsync();
            await terminator.TerminateAsync(root, TimeSpan.FromMilliseconds(100), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(child.HasExited);
            Assert.True(grandchild.HasExited);
            terminator.Dispose();
            terminator.Dispose();
        }
        finally
        {
            if (grandchild is not null) { await KillOwnedAsync(grandchild); grandchild.Dispose(); }
            await KillOwnedAsync(child);
            await KillOwnedAsync(root);
            terminator.Dispose();
        }
    }

    [Fact]
    public void CleanupAssemblyDoesNotShellOutToTaskkill()
    {
        var location = typeof(ProcessTreeTerminator).Assembly.Location;
        var bytes = File.ReadAllBytes(location);
        var ascii = System.Text.Encoding.ASCII.GetString(bytes);
        var unicode = System.Text.Encoding.Unicode.GetString(bytes);
        Assert.DoesNotContain("taskkill", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskkill", unicode, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task KillOwnedAsync(Process process)
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class TreeHarness : IDisposable
    {
        private readonly TestDirectory _directory = new();
        private readonly List<Process> _owned = new();
        private readonly string _childPid;
        private readonly string _grandchildPid;
        private readonly string _gate;
        private readonly string _rootScript;
        private readonly string _sleepScript;

        public TreeHarness()
        {
            _childPid = _directory.GetPath("child.pid");
            _grandchildPid = _directory.GetPath("grandchild.pid");
            _gate = _directory.GetPath("spawn.allow");
            _sleepScript = _directory.GetPath("sleep.ps1");
            File.WriteAllText(_sleepScript, "Start-Sleep -Seconds 3600");
            var childScript = _directory.GetPath("child.ps1");
            File.WriteAllText(childScript,
                "param([string]$PidFile,[string]$Gate,[string]$GrandchildScript,[string]$GrandchildPid)\n" +
                "[IO.File]::WriteAllText($PidFile,[string]$PID)\n" +
                "while (-not (Test-Path -LiteralPath $Gate)) { Start-Sleep -Milliseconds 25 }\n" +
                "$spawn = Start-Process powershell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('" +
                "\"' + $GrandchildScript + '\"')) -WindowStyle Hidden -PassThru\n" +
                "[IO.File]::WriteAllText($GrandchildPid,[string]$spawn.Id)\nStart-Sleep -Seconds 3600\n");
            _rootScript = _directory.GetPath("root.cmd");
            File.WriteAllText(_rootScript, $"@echo off\npowershell -NoProfile -ExecutionPolicy Bypass -File \"{childScript}\" \"{_childPid}\" \"{_gate}\" \"{_sleepScript}\" \"{_grandchildPid}\"\n");
        }

        public Process StartRoot()
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                Arguments = $"/d /s /c \"\"{_rootScript}\"\"", UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            return Track(Process.Start(start)!);
        }

        public Process StartSibling()
        {
            var start = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", _sleepScript }) { start.ArgumentList.Add(argument); }
            return Track(Process.Start(start)!);
        }

        public void AllowGrandchild() => File.WriteAllText(_gate, "owned test only");
        public Task<Process> GetChildAsync() => GetOwnedAsync(_childPid);
        public Task<Process> GetGrandchildAsync() => GetOwnedAsync(_grandchildPid);

        private async Task<Process> GetOwnedAsync(string pidFile)
        {
            var id = await FakeProcessHarness.WaitForChildPidAsync(pidFile, TimeSpan.FromSeconds(10));
            return Track(Process.GetProcessById(id));
        }

        private Process Track(Process process)
        {
            _ = process.SafeHandle;
            var retained = Process.GetProcessById(process.Id);
            _ = retained.SafeHandle;
            if (retained.StartTime != process.StartTime)
            {
                retained.Dispose();
                throw new InvalidOperationException("Owned test process identity changed.");
            }
            _owned.Add(retained); // Separate lifetime: test using declarations may dispose their view first.
            return process;
        }

        public void Dispose()
        {
            foreach (var process in _owned.AsEnumerable().Reverse())
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); }
            }
            _directory.Dispose();
        }
    }
}

public sealed class WindowsTreeFactAttribute : FactAttribute
{
    public WindowsTreeFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) { Skip = "Windows job/Toolhelp process lifetime integration."; }
    }
}

public sealed class WindowsTreeTheoryAttribute : TheoryAttribute
{
    public WindowsTreeTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) { Skip = "Windows job/Toolhelp process lifetime integration."; }
    }
}
