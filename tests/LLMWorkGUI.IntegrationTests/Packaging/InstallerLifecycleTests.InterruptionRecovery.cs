using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Packaging;

public sealed partial class InstallerLifecycleTests
{
    [Theory]
    [InlineData("update", 1, false, "none")]
    [InlineData("update", 2, false, "none")]
    [InlineData("update", 3, true, "none")]
    [InlineData("update", 4, true, "none")]
    [InlineData("rollback", 1, false, "none")]
    [InlineData("rollback", 2, false, "none")]
    [InlineData("rollback", 3, false, "none")]
    [InlineData("update", 1, false, "changed-old")]
    [InlineData("update", 1, false, "foreign-rollback")]
    [InlineData("update", 1, false, "occupied-install")]
    [InlineData("update", 1, false, "duplicate-rollback")]
    public async Task InterruptedLifecycle_PublicRetryReconcilesOnlyVerifiedOwnedSnapshots(
        string operation, int phase, bool priorRollback, string fault)
    {
        var install = _directory.GetPath("owned-app");
        var data = CreateUserData();
        var sentinel = Path.Combine(data, "preserve.txt");
        File.WriteAllText(sentinel, "interruption-data-must-survive");
        var sentinelHash = HashFile(sentinel);
        var dataInventory = RecoveryFileInventory(data);
        var first = CreateDistribution("recovery-v1", "1.0.0", "owned-version-one");
        var second = CreateDistribution("recovery-v2", "2.0.0", "owned-version-two");
        var third = CreateDistribution("recovery-v3", "3.0.0", "owned-version-three");
        var installed = RunScript("Install-LLMWorkGUI.ps1", "-SourceDirectory", first, "-InstallDirectory", install,
            "-DataDirectory", data, "-NoShortcuts", "-NoDesktopShortcut", "-Json");
        Assert.Equal(0, installed.ExitCode);
        if (operation == "rollback" || priorRollback)
        {
            var prepared = RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", second,
                "-InstallDirectory", install, "-DataDirectory", data, "-NoShortcuts", "-NoDesktopShortcut", "-Json");
            Assert.Equal(0, prepared.ExitCode);
        }
        var source = priorRollback ? third : second;
        var marker = _directory.GetPath("actual-rename-barrier.json");
        var entry = _directory.GetPath("owned-rename-barrier.ps1");
        File.WriteAllText(entry, RecoveryBarrierScript);
        using var child = StartRecoveryBarrier(entry, operation, phase, source, install, data, marker);
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(marker) && !child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(25);
            var barrierFailure = "The real rename/write barrier was not reached.";
            if (!File.Exists(marker) && child.HasExited)
            {
                barrierFailure += $" Child exit code: {child.ExitCode}. stdout: {await output.WaitAsync(TimeSpan.FromSeconds(5))}"
                    + $" stderr: {await error.WaitAsync(TimeSpan.FromSeconds(5))}";
            }
            Assert.True(File.Exists(marker), barrierFailure);
            var reached = JsonNode.Parse(File.ReadAllText(marker))!.AsObject();
            Assert.Equal(child.Id, reached["pid"]!.GetValue<int>());
            Assert.Equal(phase, reached["phase"]!.GetValue<int>());
            child.Kill(entireProcessTree: true);
            using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync(exitDeadline.Token);
            Assert.True(child.HasExited);
            Assert.NotEqual(0, child.ExitCode);
            Assert.Equal(phase != 1, Directory.Exists(install));
            var journalPath = install + ".packaging-transaction.json";
            Assert.True(File.Exists(journalPath));
            var journal = JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
            var id = journal["id"]!.GetValue<string>();
            var superseded = install + ".superseded-" + id;
            Assert.True(Directory.Exists(superseded));
            if (fault == "changed-old")
                File.AppendAllText(Path.Combine(superseded, "LLMWorkGUI.App.exe"), "unverified-changed-bytes");
            else if (fault == "foreign-rollback")
            {
                var foreign = _directory.GetPath("foreign-snapshot");
                Directory.CreateDirectory(foreign);
                File.WriteAllText(Path.Combine(foreign, "preserve.txt"), "not-authorized-by-journal");
                journal["rollback"] = foreign;
                File.WriteAllText(journalPath, journal.ToJsonString());
            }
            else if (fault == "duplicate-rollback")
            {
                foreach (var file in Directory.EnumerateFiles(superseded, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(install + ".rollback", Path.GetRelativePath(superseded, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
            }
            else if (fault == "occupied-install")
            {
                foreach (var file in Directory.EnumerateFiles(superseded, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(install, Path.GetRelativePath(superseded, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
            }
            var beforeRetry = RecoveryFileInventory(Path.GetDirectoryName(install)!);
            var retry = operation == "update"
                ? RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", source, "-InstallDirectory", install,
                    "-DataDirectory", data, "-NoShortcuts", "-NoDesktopShortcut", "-Json")
                : RunScript("Rollback-LLMWorkGUI.ps1", "-InstallDirectory", install, "-Json");
            if (fault != "none")
            {
                Assert.NotEqual(0, retry.ExitCode);
                Assert.Contains("PACKAGING_RECOVERY_REQUIRED", retry.StandardOutput + retry.StandardError);
                Assert.Equal(beforeRetry, RecoveryFileInventory(Path.GetDirectoryName(install)!));
                Assert.True(File.Exists(journalPath));
            }
            else
            {
                Assert.True(retry.ExitCode == 0, retry.StandardOutput + retry.StandardError);
                Assert.False(File.Exists(journalPath));
                Assert.True(File.Exists(install + ".packaging-resolved-" + id + ".json"));
                Assert.Equal(operation == "rollback" ? "1.0.0" : priorRollback ? "3.0.0" : "2.0.0",
                    ReadState(install).GetProperty("version").GetString());
                Assert.True(retry.Json.GetProperty("validated").GetBoolean());
                if (operation == "update")
                    Assert.Equal(priorRollback ? "2.0.0" : "1.0.0", ReadState(install + ".rollback").GetProperty("version").GetString());
            }
            Assert.Equal(sentinelHash, HashFile(sentinel));
            Assert.Equal(dataInventory, RecoveryFileInventory(data));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            await output;
            await error;
        }
    }

    private static string[] RecoveryFileInventory(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        // Fresh PowerShell processes legitimately update their isolated startup cache.
        // The compared inventory includes all product/snapshot/data/foreign-owner files.
        .Where(path => !Path.GetRelativePath(root, path).StartsWith("isolated-recovery-profile" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Path.GetRelativePath(root, path).StartsWith("recovery-temp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .Select(path => Path.GetRelativePath(root, path) + ":" + HashFile(path)).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private Process StartRecoveryBarrier(string entry, string operation, int phase, string source,
        string install, string data, string marker)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", entry,
            "-ModulePath", Path.Combine(GetScriptsDirectory(), "LLMWorkGUI.Packaging.psm1"), "-Operation", operation,
            "-Phase", phase.ToString(System.Globalization.CultureInfo.InvariantCulture), "-SourceDirectory", source,
            "-InstallDirectory", install, "-DataDirectory", data, "-MarkerPath", marker }) start.ArgumentList.Add(argument);
        var profile = _directory.GetPath("isolated-recovery-profile");
        start.Environment["USERPROFILE"] = profile;
        // Keep the actual LocalAppData root: the owned install is in its Temp subtree.
        // Overriding it makes Windows PowerShell classify that install as protected.
        start.Environment["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
        start.Environment["TEMP"] = _directory.GetPath("recovery-temp");
        start.Environment["TMP"] = start.Environment["TEMP"];
        Directory.CreateDirectory(start.Environment["TEMP"]!);
        return Process.Start(start)!;
    }

    private const string RecoveryBarrierScript = """
        param([string]$ModulePath, [string]$Operation, [int]$Phase, [string]$SourceDirectory,
            [string]$InstallDirectory, [string]$DataDirectory, [string]$MarkerPath)
        $ErrorActionPreference='Stop'
        $module=Import-Module $ModulePath -Force -PassThru
        & $module {
            param($install,$marker,$phase,$operation)
            $script:BarrierRoot=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($install))+[IO.Path]::DirectorySeparatorChar
            $script:BarrierInstall=$install; $script:BarrierMarker=$marker; $script:BarrierPhase=$phase
            $script:BarrierOperation=$operation; $script:BarrierMoves=0
            $script:OriginalStateWriter=(Get-Command Write-LlmState).ScriptBlock
            function script:Hold-OwnedBarrier {
                [IO.File]::WriteAllText($script:BarrierMarker, (@{pid=$PID;phase=$script:BarrierPhase}|ConvertTo-Json))
                while($true){[Threading.Thread]::Sleep(100)}
            }
            function script:Move-Item {
                [CmdletBinding()]param([string]$LiteralPath,[string]$Destination)
                foreach($path in @($LiteralPath,$Destination)) {
                    if(-not [IO.Path]::GetFullPath($path).StartsWith($script:BarrierRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Not an owned move.'}
                }
                Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
                $script:BarrierMoves++
                if($script:BarrierMoves -eq $script:BarrierPhase -and -not ($script:BarrierOperation -eq 'rollback' -and $script:BarrierPhase -eq 3)){Hold-OwnedBarrier}
            }
            function script:Write-LlmState {
                param([string]$Directory,[hashtable]$State)
                & $script:OriginalStateWriter -Directory $Directory -State $State
                if($script:BarrierOperation -eq 'rollback' -and $script:BarrierPhase -eq 3 -and $Directory -eq $script:BarrierInstall){Hold-OwnedBarrier}
            }
        } $InstallDirectory $MarkerPath $Phase $Operation
        if($Operation -eq 'update'){Invoke-LlmUpdate -SourceDirectory $SourceDirectory -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory}
        else {Invoke-LlmRollback -InstallDirectory $InstallDirectory}
        """;
}
