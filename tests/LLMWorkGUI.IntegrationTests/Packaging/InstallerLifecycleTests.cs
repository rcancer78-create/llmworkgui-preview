using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Packaging;

/// <summary>
/// Per-user installer lifecycle tests. The delivered PowerShell scripts are executed as real
/// processes against an isolated temporary profile: install, update (app-data preservation and
/// rollback point), rollback and uninstall, including negative paths. No machine-wide state, no
/// registry, and no external CLI installation is touched.
/// </summary>
public sealed partial class InstallerLifecycleTests : IDisposable
{
    private readonly TestDirectory _directory = new();

    [Theory]
    [InlineData("Run-ActivityLoadAcceptance.ps1")]
    [InlineData("Run-VisibleWorkflowAcceptance.ps1")]
    public void AcceptanceWrapper_RejectsLinkedRunRootBeforeCreatingEvidenceOrBuilding(string script)
    {
        var target = _directory.GetPath("unrelated-evidence-owner");
        Directory.CreateDirectory(target);
        var alias = _directory.GetPath("run-alias");
        CreateJunction(alias, target);
        var shim = _directory.GetPath("dotnet-shim");
        Directory.CreateDirectory(shim);
        var canary = Path.Combine(shim, "invoked.txt");
        File.WriteAllText(Path.Combine(shim, "dotnet.cmd"), "@echo invoked>\"%REVIEW_DOTNET_CANARY%\"\r\n@exit /b 1\r\n");
        try
        {
            var result = RunScript(script, new Dictionary<string, string>
                { ["PATH"] = shim + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                    ["REVIEW_DOTNET_CANARY"] = canary }, "-RunRoot", Path.Combine(alias, "new-run"));
            Assert.NotEqual(0, result.ExitCode);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            Assert.False(File.Exists(canary));
        }
        finally { Directory.Delete(alias, recursive: false); }
    }

    [Fact]
    public async Task PublicPublisher_RepublicationDoesNotCarryRemovedPayloadIntoTheNewOutput()
    {
        var output = _directory.GetPath("published");
        var entry = _directory.GetPath("publish-fixture.ps1");
        File.WriteAllText(entry, """
            $ErrorActionPreference = 'Stop'
            function dotnet {
                $outputIndex = [Array]::IndexOf($args, '-o')
                $destination = [string]$args[$outputIndex + 1]
                [void][System.IO.Directory]::CreateDirectory($destination)
                [System.IO.File]::WriteAllText((Join-Path $destination 'LLMWorkGUI.App.exe'), 'synthetic-published-executable')
                [System.IO.File]::WriteAllText((Join-Path $destination 'current.dll'), 'current-payload')
                $global:LASTEXITCODE = 0
            }
            & $env:REVIEW_PUBLISH_SCRIPT -OutputDirectory $env:REVIEW_PUBLISH_OUTPUT -Version '1.0.0'
            [System.IO.File]::WriteAllText((Join-Path $env:REVIEW_PUBLISH_OUTPUT 'removed.dll'), 'removed-payload')
            & $env:REVIEW_PUBLISH_SCRIPT -OutputDirectory $env:REVIEW_PUBLISH_OUTPUT -Version '2.0.0'
            if (Test-Path -LiteralPath (Join-Path $env:REVIEW_PUBLISH_OUTPUT 'removed.dll')) { exit 12 }
            exit 0
            """);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", entry })
            start.ArgumentList.Add(argument);
        start.Environment["REVIEW_PUBLISH_SCRIPT"] = Path.Combine(GetScriptsDirectory(), "Publish-LLMWorkGUI.ps1");
        start.Environment["REVIEW_PUBLISH_OUTPUT"] = output;
        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(20_000));
        Assert.True(process.ExitCode == 0, await standardOutput + await standardError);
        Assert.Equal("current-payload", File.ReadAllText(Path.Combine(output, "current.dll")));
        Assert.False(File.Exists(Path.Combine(output, "removed.dll")));
    }

    [Fact]
    public void Rollback_AtomicallyReplacesMarkerWithoutWritingThroughAnOpenReader()
    {
        var install = _directory.GetPath("app");
        InstallVersion("1.0.0", "exe-v1", install, CreateUserData());
        var second = CreateDistribution("second-atomic", "2.0.0", "exe-v2");
        Assert.Equal(0, RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", second,
            "-InstallDirectory", install, "-NoShortcuts", "-NoDesktopShortcut", "-Json").ExitCode);
        var marker = Path.Combine(GetRollbackDirectory(install), "install-state.json");
        var original = File.ReadAllBytes(marker);
        using var reader = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var result = RunScript("Rollback-LLMWorkGUI.ps1", "-InstallDirectory", install, "-Json");
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        using var retained = new MemoryStream();
        reader.CopyTo(retained);
        Assert.Equal(original, retained.ToArray());
        Assert.Equal("1.0.0", ReadState(install).GetProperty("version").GetString());
        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
    }

    [Fact]
    public void Uninstall_UserDataRemovalFailureRetainsInstallationForRetry()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        using (var held = new FileStream(Path.Combine(data, "llmworkgui.db"), FileMode.Open,
            FileAccess.Read, FileShare.Read))
        {
            var failed = RunScript("Uninstall-LLMWorkGUI.ps1", "-InstallDirectory", install,
                "-DataDirectory", data, "-RemoveUserData", "-StartMenuShortcutRoot", _directory.GetPath("menu"),
                "-DesktopShortcutRoot", _directory.GetPath("desktop"), "-Json");
            Assert.NotEqual(0, failed.ExitCode);
            Assert.True(File.Exists(Path.Combine(install, "install-state.json")));
            Assert.True(File.Exists(Path.Combine(install, "LLMWorkGUI.App.exe")));
        }
        var retry = RunScript("Uninstall-LLMWorkGUI.ps1", "-InstallDirectory", install,
            "-DataDirectory", data, "-RemoveUserData", "-StartMenuShortcutRoot", _directory.GetPath("menu"),
            "-DesktopShortcutRoot", _directory.GetPath("desktop"), "-Json");
        Assert.Equal(0, retry.ExitCode);
        Assert.False(Directory.Exists(data));
        Assert.False(Directory.Exists(install));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Uninstall_PreservesUnownedDefaultRollbackSibling(bool forgedProductMarker)
    {
        var install = _directory.GetPath("app");
        InstallVersion("1.0.0", "exe-v1", install, CreateUserData());
        var unrelated = GetRollbackDirectory(install);
        Directory.CreateDirectory(unrelated);
        var sentinel = Path.Combine(unrelated, "unrelated.txt");
        File.WriteAllText(sentinel, "unrelated owner");
        if (forgedProductMarker)
            File.Copy(Path.Combine(install, "install-state.json"), Path.Combine(unrelated, "install-state.json"));
        var result = RunScript("Uninstall-LLMWorkGUI.ps1", "-InstallDirectory", install,
            "-StartMenuShortcutRoot", _directory.GetPath("menu"),
            "-DesktopShortcutRoot", _directory.GetPath("desktop"), "-Json");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("unrelated owner", File.ReadAllText(sentinel));
        Assert.False(result.Json.GetProperty("removedRollback").GetBoolean());
    }

    [Fact]
    public void Install_RejectsProtectedProfileTargetBeforeCreatingDataOrStaging()
    {
        var profile = _directory.GetPath("synthetic-profile");
        Directory.CreateDirectory(profile);
        var install = Path.Combine(profile, "Documents", "app");
        var data = _directory.GetPath("new-data");
        var source = CreateDistribution("source-protected", "1.0.0", "exe-v1");
        var result = RunScript("Install-LLMWorkGUI.ps1",
            new Dictionary<string, string> { ["USERPROFILE"] = profile,
                ["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local") },
            "-SourceDirectory", source, "-InstallDirectory", install, "-DataDirectory", data,
            "-NoShortcuts", "-NoDesktopShortcut", "-Json");
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(data));
        Assert.False(Directory.Exists(Path.GetDirectoryName(install)));
    }

    [Fact]
    public void Update_PreservesSameNameShortcutPointingToAnUnrelatedHelperInInstallDirectory()
    {
        var install = _directory.GetPath("app");
        InstallVersion("1.0.0", "exe-v1", install, CreateUserData());
        var menu = _directory.GetPath("menu");
        Directory.CreateDirectory(menu);
        var shortcut = Path.Combine(menu, "LLM Work GUI.lnk");
        CreateSyntheticShortcut(shortcut, Path.Combine(install, "unrelated-helper.exe"));
        var before = File.ReadAllBytes(shortcut);
        var source = CreateDistribution("second-helper", "2.0.0", "exe-v2");
        var result = RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", source,
            "-InstallDirectory", install, "-ShortcutRoot", menu,
            "-NoDesktopShortcut", "-Json");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(shortcut));
        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("library")]
    [InlineData("marker")]
    [InlineData("unrecorded")]
    public void Rollback_RejectsTamperedOrUnownedCandidateBeforeMovingAnything(string scenario)
    {
        var install = _directory.GetPath("app");
        InstallVersion("1.0.0", "exe-v1", install, CreateUserData());
        var second = CreateDistribution("second", "2.0.0", "exe-v2");
        Assert.Equal(0, RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", second,
            "-InstallDirectory", install, "-NoShortcuts", "-NoDesktopShortcut", "-Json").ExitCode);
        var rollback = GetRollbackDirectory(install);
        if (scenario == "executable") File.WriteAllText(Path.Combine(rollback, "LLMWorkGUI.App.exe"), "tampered");
        if (scenario == "library") File.WriteAllText(Path.Combine(rollback, "extra.dll"), "untracked binary");
        if (scenario == "marker") File.Delete(Path.Combine(rollback, "install-state.json"));
        if (scenario == "unrecorded")
        {
            var alternate = _directory.GetPath("alternate");
            Assert.StartsWith(Path.GetFullPath(_directory.Root) + Path.DirectorySeparatorChar, Path.GetFullPath(rollback), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(_directory.Root) + Path.DirectorySeparatorChar, Path.GetFullPath(alternate), StringComparison.OrdinalIgnoreCase);
            Directory.Move(rollback, alternate);
            rollback = alternate;
        }
        var beforeState = File.ReadAllBytes(Path.Combine(install, "install-state.json"));
        var result = RunScript("Rollback-LLMWorkGUI.ps1", "-InstallDirectory", install, "-RollbackDirectory", rollback, "-Json");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("exe-v2", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.Equal(beforeState, File.ReadAllBytes(Path.Combine(install, "install-state.json")));
        Assert.True(Directory.Exists(rollback));
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("corrupt")]
    public void Uninstall_WithoutProductOwnership_PreservesTargetAndData(string stateKind)
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var statePath = Path.Combine(install, "install-state.json");
        if (stateKind == "missing") File.Delete(statePath);
        else if (stateKind == "corrupt") File.WriteAllText(statePath, "{ corrupt");
        else
        {
            var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
            state["product"] = "UnrelatedProduct";
            File.WriteAllText(statePath, state.ToJsonString());
        }

        var result = RunScript("Uninstall-LLMWorkGUI.ps1", "-InstallDirectory", install,
            "-DataDirectory", data, "-RemoveUserData", "-StartMenuShortcutRoot", _directory.GetPath("menu"),
            "-DesktopShortcutRoot", _directory.GetPath("desktop"), "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db")));
    }

    [Fact]
    public void Uninstall_InstallTargetIsProtectedRoot_RefusesDespiteProductMarker()
    {
        var install = _directory.GetPath("synthetic-profile");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var result = RunScript("Uninstall-LLMWorkGUI.ps1",
            new Dictionary<string, string> { ["USERPROFILE"] = install, ["LOCALAPPDATA"] = _directory.GetPath("local") },
            "-InstallDirectory", install, "-DataDirectory", data,
            "-StartMenuShortcutRoot", _directory.GetPath("menu"), "-DesktopShortcutRoot", _directory.GetPath("desktop"), "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(install, "LLMWorkGUI.App.exe")));
    }

    [Fact]
    public void Uninstall_DoesNotRemoveNameOnlyOrSiblingPrefixShortcuts()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var menu = _directory.GetPath("menu");
        Directory.CreateDirectory(menu);
        var sameName = Path.Combine(menu, "LLM Work GUI.lnk");
        var siblingPrefix = Path.Combine(menu, "Other.lnk");
        CreateSyntheticShortcut(sameName, _directory.GetPath("unrelated.exe"));
        CreateSyntheticShortcut(siblingPrefix, Path.Combine(_directory.GetPath("app-other"), "other.exe"));

        var result = RunScript("Uninstall-LLMWorkGUI.ps1", "-InstallDirectory", install,
            "-DataDirectory", data, "-StartMenuShortcutRoot", menu,
            "-DesktopShortcutRoot", _directory.GetPath("desktop"), "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(sameName));
        Assert.True(File.Exists(siblingPrefix));
    }

    private static void CreateSyntheticShortcut(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = shell.CreateShortcut(path);
        try
        {
            shortcut.TargetPath = target;
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallOrUpdate_ShortcutFailureRollsBackActivatedFiles(bool update)
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        if (update) InstallVersion("1.0.0", "exe-v1", install, data);
        var source = CreateDistribution("new-source", "2.0.0", "exe-v2");
        var blockedShortcutRoot = _directory.GetPath("shortcut-parent-is-a-file");
        File.WriteAllText(blockedShortcutRoot, "keep");

        var result = RunScript(update ? "Update-LLMWorkGUI.ps1" : "Install-LLMWorkGUI.ps1",
            "-SourceDirectory", source, "-InstallDirectory", install, "-DataDirectory", data,
            "-ShortcutRoot", blockedShortcutRoot, "-NoDesktopShortcut", "-Json");

        Assert.NotEqual(0, result.ExitCode);
        if (update)
        {
            Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
            Assert.Equal("1.0.0", ReadState(install).GetProperty("version").GetString());
        }
        else Assert.False(Directory.Exists(install));
        Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db")));
        Assert.Equal("keep", File.ReadAllText(blockedShortcutRoot));
    }

    [Fact]
    public void Rollback_StateWriteFailureRestoresCurrentInstallationAndSnapshot()
    {
        var install = _directory.GetPath("app");
        InstallVersion("1.0.0", "exe-v1", install, CreateUserData());
        var second = CreateDistribution("second", "2.0.0", "exe-v2");
        Assert.Equal(0, RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", second,
            "-InstallDirectory", install, "-NoShortcuts", "-NoDesktopShortcut", "-Json").ExitCode);
        var rollback = GetRollbackDirectory(install);
        var readOnlyState = Path.Combine(rollback, "install-state.json");
        File.SetAttributes(readOnlyState, File.GetAttributes(readOnlyState) | FileAttributes.ReadOnly);
        try
        {
            var result = RunScript("Rollback-LLMWorkGUI.ps1", "-InstallDirectory", install, "-Json");
            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("exe-v2", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
            Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(rollback, "LLMWorkGUI.App.exe")));
        }
        finally { File.SetAttributes(readOnlyState, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData("update", "missing")]
    [InlineData("update", "foreign")]
    [InlineData("update", "protected")]
    [InlineData("rollback", "missing")]
    [InlineData("rollback", "foreign")]
    [InlineData("rollback", "protected")]
    public void UpdateOrRollback_RefusesUnownedOrProtectedInstallTarget(string operation, string unsafeKind)
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var source = CreateDistribution("second", "2.0.0", "exe-v2");
        Assert.Equal(0, RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", source,
            "-InstallDirectory", install, "-NoShortcuts", "-NoDesktopShortcut", "-Json").ExitCode);
        var statePath = Path.Combine(install, "install-state.json");
        if (unsafeKind == "missing") File.Delete(statePath);
        if (unsafeKind == "foreign")
        {
            var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
            state["product"] = "UnrelatedProduct";
            File.WriteAllText(statePath, state.ToJsonString());
        }
        var environment = new Dictionary<string, string>();
        if (unsafeKind == "protected") environment["USERPROFILE"] = install;
        var args = new List<string> { "-InstallDirectory", install, "-Json" };
        if (operation == "update") args.AddRange(new[] { "-SourceDirectory", source, "-NoShortcuts", "-NoDesktopShortcut" });

        var result = RunScript(operation == "update" ? "Update-LLMWorkGUI.ps1" : "Rollback-LLMWorkGUI.ps1", environment, args.ToArray());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("exe-v2", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(GetRollbackDirectory(install), "LLMWorkGUI.App.exe")));
        Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db")));
    }

    [Fact]
    public void Update_SecondShortcutFailureRestoresTheOriginalFirstShortcut()
    {
        var install = _directory.GetPath("app");
        InstallVersion("1.0.0", "exe-v1", install, CreateUserData());
        var menu = _directory.GetPath("menu");
        Directory.CreateDirectory(menu);
        var shortcut = Path.Combine(menu, "LLM Work GUI.lnk");
        CreateSyntheticShortcut(shortcut, Path.Combine(install, "LLMWorkGUI.App.exe"));
        var before = File.ReadAllBytes(shortcut);
        var next = CreateDistribution("next", "2.0.0", "exe-v2");
        Assert.True(File.Exists(Path.Combine(next, "LLMWorkGUI.App.exe")));
        var blocked = _directory.GetPath("blocked-desktop");
        File.WriteAllText(blocked, "keep");

        var result = RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", next,
            "-InstallDirectory", install, "-ShortcutRoot", menu, "-DesktopShortcutRoot", blocked, "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(blocked, result.Json.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("SOURCE_INVALID", result.Json.GetProperty("errorCode").GetString());
        Assert.Equal(before, File.ReadAllBytes(shortcut));
        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.Equal("keep", File.ReadAllText(blocked));
    }

    [Fact]
    public void Install_DoesNotOverwriteAnUnrelatedSameNameShortcut()
    {
        var menu = _directory.GetPath("menu");
        Directory.CreateDirectory(menu);
        var shortcut = Path.Combine(menu, "LLM Work GUI.lnk");
        CreateSyntheticShortcut(shortcut, _directory.GetPath("foreign.exe"));
        var before = File.ReadAllBytes(shortcut);
        var source = CreateDistribution("new", "1.0.0", "exe-v1");
        var install = _directory.GetPath("app");

        var result = RunScript("Install-LLMWorkGUI.ps1", "-SourceDirectory", source,
            "-InstallDirectory", install, "-DataDirectory", CreateUserData(), "-ShortcutRoot", menu, "-NoDesktopShortcut", "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(shortcut));
        Assert.False(Directory.Exists(install));
    }

    [Fact]
    public void Update_ShortcutFailurePreservesEarlierRollbackSnapshot()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var second = CreateDistribution("second", "2.0.0", "exe-v2");
        Assert.Equal(0, RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", second,
            "-InstallDirectory", install, "-NoShortcuts", "-NoDesktopShortcut", "-Json").ExitCode);
        var third = CreateDistribution("third", "3.0.0", "exe-v3");
        var blocked = _directory.GetPath("blocked-menu");
        File.WriteAllText(blocked, "keep");

        var result = RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", third,
            "-InstallDirectory", install, "-ShortcutRoot", blocked, "-NoDesktopShortcut", "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("exe-v2", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(GetRollbackDirectory(install), "LLMWorkGUI.App.exe")));
        Assert.Equal("1.0.0", ReadState(install).GetProperty("rollbackVersion").GetString());
    }

    [Fact]
    public void Update_DataInsideInstallation_RefusesBeforeMutation()
    {
        var install = _directory.GetPath("app");
        InstallVersion("1.0.0", "exe-v1", install, CreateUserData());
        var data = Path.Combine(install, "user-data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "keep.db"), "keep");
        var statePath = Path.Combine(install, "install-state.json");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        state["dataDirectory"] = data;
        File.WriteAllText(statePath, state.ToJsonString());
        var source = CreateDistribution("new", "2.0.0", "exe-v2");

        var result = RunScript("Update-LLMWorkGUI.ps1", "-SourceDirectory", source,
            "-InstallDirectory", install, "-NoShortcuts", "-NoDesktopShortcut", "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(data, "keep.db")));
        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
    }

    [Fact]
    public void Update_ProcessResolverEnvironmentCannotExecuteCode()
    {
        var install = _directory.GetPath("app");
        InstallVersion("1.0.0", "exe-v1", install, CreateUserData());
        var decoyDir = _directory.GetPath("decoy-env");
        Directory.CreateDirectory(decoyDir);
        var decoyPath = Path.Combine(decoyDir, "LLMWorkGUI.App.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "timeout.exe"), decoyPath);
        var marker = _directory.GetPath("unexpected-code-execution.txt");
        using var decoy = StartProductProcess(decoyPath);
        try
        {
            var source = CreateDistribution("next", "2.0.0", "exe-v2");
            RunScript("Update-LLMWorkGUI.ps1", new Dictionary<string, string>
            {
                ["LLMWORKGUI_TEST_PROCESS_RESOLVER"] = "[IO.File]::WriteAllText('" + marker.Replace("'", "''") + "','executed'); return $null"
            }, "-SourceDirectory", source, "-InstallDirectory", install, "-NoShortcuts", "-NoDesktopShortcut", "-Json");
            Assert.False(File.Exists(marker));
        }
        finally { KillProcess(decoy); }
    }

    [Fact]
    public void Install_CreatesPerUserLayout_CreatesShortcuts_AndPreservesUserData()
    {
        var source = CreateDistribution("source-v1", "1.0.0", "exe-v1");
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        var startMenu = _directory.GetPath("start-menu");
        var desktop = _directory.GetPath("desktop");
        var externalCli = CreateExternalCliInstallations();

        var result = RunScript(
            "Install-LLMWorkGUI.ps1",
            "-SourceDirectory", source,
            "-InstallDirectory", install,
            "-DataDirectory", data,
            "-ShortcutRoot", startMenu,
            "-DesktopShortcutRoot", desktop,
            "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.True(result.Json.GetProperty("validated").GetBoolean());
        Assert.Equal("install", result.Json.GetProperty("operation").GetString());
        Assert.Equal("1.0.0", result.Json.GetProperty("version").GetString());

        var validationChecks = result.Json.GetProperty("validationChecks")
            .EnumerateArray()
            .Select(check => check.GetString())
            .ToArray();
        Assert.Contains("executable-present", validationChecks);
        Assert.Contains("executable-hash-verified", validationChecks);
        Assert.Contains("state-readable", validationChecks);

        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe"), Encoding.ASCII));
        Assert.True(File.Exists(Path.Combine(install, "shared", "runtime.dll")));
        Assert.True(File.Exists(Path.Combine(install, "install-state.json")));
        Assert.Equal("1.0.0", ReadState(install).GetProperty("version").GetString());

        Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db"), Encoding.ASCII));
        Assert.Equal("dark", File.ReadAllText(Path.Combine(data, "settings.json"), Encoding.ASCII));

        var expectedShortcuts = new[]
        {
            Path.Combine(startMenu, "LLM Work GUI.lnk"),
            Path.Combine(desktop, "LLM Work GUI.lnk")
        };
        foreach (var shortcut in expectedShortcuts)
        {
            Assert.True(File.Exists(shortcut), $"Shortcut '{shortcut}' must exist.");
        }

        AssertExternalCliUntouched(externalCli);
    }

    [Fact]
    public void Install_FailsWhenInstallDirectoryAlreadyExists()
    {
        var source = CreateDistribution("source-v1", "1.0.0", "exe-v1");
        var install = _directory.GetPath("app");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "keep.txt"), "pre-existing", Encoding.ASCII);

        var result = RunScript(
            "Install-LLMWorkGUI.ps1",
            "-SourceDirectory", source,
            "-InstallDirectory", install,
            "-DataDirectory", _directory.GetPath("data"),
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Json.GetProperty("success").GetBoolean());
        Assert.Equal("ALREADY_INSTALLED", result.Json.GetProperty("errorCode").GetString());
        Assert.Equal("pre-existing", File.ReadAllText(Path.Combine(install, "keep.txt"), Encoding.ASCII));
    }

    [Fact]
    public void Install_RejectsSourceWithoutProductExecutable()
    {
        var source = _directory.GetPath("source-invalid");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "no executable", Encoding.ASCII);

        var result = RunScript(
            "Install-LLMWorkGUI.ps1",
            "-SourceDirectory", source,
            "-InstallDirectory", _directory.GetPath("app"),
            "-DataDirectory", _directory.GetPath("data"),
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("SOURCE_INVALID", result.Json.GetProperty("errorCode").GetString());
        Assert.False(Directory.Exists(_directory.GetPath("app")));
    }

    [Fact]
    public void Update_ReplacesFiles_PreservesData_AndCreatesRollbackPoint()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var dataHash = HashFile(Path.Combine(data, "llmworkgui.db"));

        var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");
        var result = RunScript(
            "Update-LLMWorkGUI.ps1",
            "-SourceDirectory", sourceV2,
            "-InstallDirectory", install,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.True(result.Json.GetProperty("validated").GetBoolean());
        Assert.Equal("2.0.0", result.Json.GetProperty("version").GetString());
        Assert.Equal("1.0.0", result.Json.GetProperty("previousVersion").GetString());
        Assert.Equal("1.0.0", result.Json.GetProperty("rollbackVersion").GetString());

        Assert.Equal("exe-v2", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe"), Encoding.ASCII));
        Assert.True(File.Exists(Path.Combine(install, "shared", "feature-v2.dll")));

        var state = ReadState(install);
        Assert.Equal("2.0.0", state.GetProperty("version").GetString());
        Assert.Equal("1.0.0", state.GetProperty("previousVersion").GetString());
        Assert.Equal("1.0.0", state.GetProperty("rollbackVersion").GetString());

        var rollback = state.GetProperty("rollbackDirectory").GetString();
        Assert.NotNull(rollback);
        Assert.Equal("1.0.0", ReadState(rollback!).GetProperty("version").GetString());
        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(rollback!, "LLMWorkGUI.App.exe"), Encoding.ASCII));

        Assert.Equal(dataHash, HashFile(Path.Combine(data, "llmworkgui.db")));
    }

    [Fact]
    public void Update_WithInvalidSource_FailsSafely_AndKeepsInstallation()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var invalidSource = _directory.GetPath("source-invalid");
        Directory.CreateDirectory(invalidSource);
        File.WriteAllText(Path.Combine(invalidSource, "version.json"), "{ \"version\": \"2.0.0\" }", Encoding.UTF8);

        var result = RunScript(
            "Update-LLMWorkGUI.ps1",
            "-SourceDirectory", invalidSource,
            "-InstallDirectory", install,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("SOURCE_INVALID", result.Json.GetProperty("errorCode").GetString());
        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe"), Encoding.ASCII));
        Assert.Equal("1.0.0", ReadState(install).GetProperty("version").GetString());
        Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db"), Encoding.ASCII));
        Assert.False(Directory.Exists(GetRollbackDirectory(install)));
    }

    [Theory]
    [InlineData("data-equal")]
    [InlineData("data-inside")]
    [InlineData("data-contains")]
    [InlineData("install-equal")]
    [InlineData("install-inside")]
    [InlineData("source-equal")]
    public void Update_RefusesRollbackDirectoryOverlappingProtectedPaths(string scenario)
    {
        var install = _directory.GetPath("app");
        var data = scenario == "data-contains"
            ? Path.Combine(_directory.GetPath("profile"), "data")
            : _directory.GetPath("data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "llmworkgui.db"), "user-database", Encoding.ASCII);
        var markerPath = Path.Combine(data, "ACCEPTANCE_USER_DATA_MARKER.txt");
        File.WriteAllText(markerPath, "marker-payload", Encoding.ASCII);
        InstallVersion("1.0.0", "exe-v1", install, data);

        var installHash = HashFile(Path.Combine(install, "LLMWorkGUI.App.exe"));
        var markerHash = HashFile(markerPath);
        var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");
        var sourceHash = HashFile(Path.Combine(sourceV2, "LLMWorkGUI.App.exe"));

        var rollbackDirectory = scenario switch
        {
            "data-equal" => data,
            "data-inside" => Path.Combine(data, "rollback-snapshot"),
            "data-contains" => Path.GetDirectoryName(data)!,
            "install-equal" => install,
            "install-inside" => Path.Combine(install, "rollback-snapshot"),
            "source-equal" => sourceV2,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported overlap scenario.")
        };

        var result = RunScript(
            "Update-LLMWorkGUI.ps1",
            "-SourceDirectory", sourceV2,
            "-InstallDirectory", install,
            "-RollbackDirectory", rollbackDirectory,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Json.GetProperty("success").GetBoolean());
        Assert.Equal("ROLLBACK_PATH_INVALID", result.Json.GetProperty("errorCode").GetString());

        Assert.True(Directory.Exists(data), "The app-data directory must survive a rejected update.");
        Assert.True(File.Exists(markerPath), "The user-data marker must survive a rejected update.");
        Assert.Equal(markerHash, HashFile(markerPath));
        Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db"), Encoding.ASCII));
        Assert.False(File.Exists(Path.Combine(data, "LLMWorkGUI.App.exe")), "No installation files may be copied into app-data.");

        Assert.Equal(installHash, HashFile(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.Equal("1.0.0", ReadState(install).GetProperty("version").GetString());
        Assert.False(File.Exists(Path.Combine(install, "shared", "feature-v2.dll")));
        Assert.Equal(sourceHash, HashFile(Path.Combine(sourceV2, "LLMWorkGUI.App.exe")));
        Assert.False(Directory.Exists(GetRollbackDirectory(install)), "A rejected update must not create a rollback snapshot.");

        if (scenario == "data-inside")
        {
            Assert.False(Directory.Exists(rollbackDirectory));
        }
    }

    [Theory]
    [InlineData("data")]
    [InlineData("install")]
    public void Update_WithRollbackDirectoryJunctionToProtectedPath_RefusesAndPreservesState(string targetKind)
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        var markerPath = Path.Combine(data, "ACCEPTANCE_USER_DATA_MARKER.txt");
        File.WriteAllText(markerPath, "marker-payload", Encoding.ASCII);
        InstallVersion("1.0.0", "exe-v1", install, data);

        var target = targetKind == "data" ? data : install;
        var alias = _directory.GetPath($"alias-{targetKind}");
        CreateJunction(alias, target);

        var installHash = HashFile(Path.Combine(install, "LLMWorkGUI.App.exe"));
        var markerHash = HashFile(markerPath);
        var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");

        var result = RunScript(
            "Update-LLMWorkGUI.ps1",
            "-SourceDirectory", sourceV2,
            "-InstallDirectory", install,
            "-RollbackDirectory", alias,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Json.GetProperty("success").GetBoolean());
        Assert.Equal("ROLLBACK_PATH_INVALID", result.Json.GetProperty("errorCode").GetString());

        Assert.True((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0, "The alias itself must remain untouched.");
        Assert.Equal(installHash, HashFile(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.Equal("1.0.0", ReadState(install).GetProperty("version").GetString());
        Assert.Equal(markerHash, HashFile(markerPath));
        Assert.False(File.Exists(Path.Combine(data, "LLMWorkGUI.App.exe")));
        Assert.False(Directory.Exists(GetRollbackDirectory(install)));
    }

    [Fact]
    public void Update_WithUnrelatedExistingRollbackDirectory_RefusesAndLeavesItUntouched()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var installHash = HashFile(Path.Combine(install, "LLMWorkGUI.App.exe"));

        var unrelated = _directory.GetPath("unrelated-rollback");
        Directory.CreateDirectory(unrelated);
        var markerPath = Path.Combine(unrelated, "USER_FILES_MARKER.txt");
        File.WriteAllText(markerPath, "unrelated-content", Encoding.ASCII);

        var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");
        var result = RunScript(
            "Update-LLMWorkGUI.ps1",
            "-SourceDirectory", sourceV2,
            "-InstallDirectory", install,
            "-RollbackDirectory", unrelated,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Json.GetProperty("success").GetBoolean());
        Assert.Equal("ROLLBACK_NOT_OWNED", result.Json.GetProperty("errorCode").GetString());

        Assert.True(File.Exists(markerPath), "An unrelated pre-existing directory must not be deleted.");
        Assert.Equal("unrelated-content", File.ReadAllText(markerPath, Encoding.ASCII));
        Assert.False(File.Exists(Path.Combine(unrelated, "LLMWorkGUI.App.exe")));
        Assert.False(File.Exists(Path.Combine(unrelated, "install-state.json")));

        Assert.Equal(installHash, HashFile(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.Equal("1.0.0", ReadState(install).GetProperty("version").GetString());
        Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db"), Encoding.ASCII));
        Assert.False(Directory.Exists(GetRollbackDirectory(install)));
    }

    [Fact]
    public void Update_ReplacesOwnedRollbackSnapshot_AndPreservesData()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var dataHash = HashFile(Path.Combine(data, "llmworkgui.db"));

        var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");
        Assert.Equal(0, RunScript(
            "Update-LLMWorkGUI.ps1",
            "-SourceDirectory", sourceV2,
            "-InstallDirectory", install,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json").ExitCode);
        Assert.Equal("1.0.0", ReadState(GetRollbackDirectory(install)).GetProperty("version").GetString());

        var sourceV3 = CreateDistribution("source-v3", "3.0.0", "exe-v3");
        var result = RunScript(
            "Update-LLMWorkGUI.ps1",
            "-SourceDirectory", sourceV3,
            "-InstallDirectory", install,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.Equal("update", result.Json.GetProperty("operation").GetString());
        Assert.Equal("3.0.0", result.Json.GetProperty("version").GetString());
        Assert.Equal("2.0.0", result.Json.GetProperty("previousVersion").GetString());
        Assert.Equal("2.0.0", result.Json.GetProperty("rollbackVersion").GetString());

        Assert.Equal("exe-v3", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe"), Encoding.ASCII));
        var rollback = ReadState(GetRollbackDirectory(install));
        Assert.Equal("2.0.0", rollback.GetProperty("version").GetString());
        Assert.Equal("exe-v2", File.ReadAllText(Path.Combine(GetRollbackDirectory(install), "LLMWorkGUI.App.exe"), Encoding.ASCII));
        Assert.Equal(dataHash, HashFile(Path.Combine(data, "llmworkgui.db")));
    }

    [Fact]
    public void Rollback_RestoresPreviousVersion_AndPreservesData()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var dataHash = HashFile(Path.Combine(data, "llmworkgui.db"));

        var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");
        Assert.Equal(0, RunScript(
            "Update-LLMWorkGUI.ps1",
            "-SourceDirectory", sourceV2,
            "-InstallDirectory", install,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json").ExitCode);

        var result = RunScript("Rollback-LLMWorkGUI.ps1", "-InstallDirectory", install, "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.True(result.Json.GetProperty("validated").GetBoolean());
        Assert.Equal("rollback", result.Json.GetProperty("operation").GetString());
        Assert.Equal("1.0.0", result.Json.GetProperty("version").GetString());
        Assert.Equal("2.0.0", result.Json.GetProperty("rolledBackFromVersion").GetString());

        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe"), Encoding.ASCII));
        Assert.False(File.Exists(Path.Combine(install, "shared", "feature-v2.dll")));

        var state = ReadState(install);
        Assert.Equal("1.0.0", state.GetProperty("version").GetString());
        Assert.Equal("2.0.0", state.GetProperty("previousVersion").GetString());

        Assert.Equal(dataHash, HashFile(Path.Combine(data, "llmworkgui.db")));
    }

    [Fact]
    public void Rollback_WithoutSnapshot_FailsSafely()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var result = RunScript("Rollback-LLMWorkGUI.ps1", "-InstallDirectory", install, "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("ROLLBACK_NOT_AVAILABLE", result.Json.GetProperty("errorCode").GetString());
        Assert.Equal("exe-v1", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe"), Encoding.ASCII));
        Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db"), Encoding.ASCII));
    }

    [Fact]
    public void Uninstall_RemovesApplicationAndShortcuts_ButPreservesDataByDefault()
    {
        var source = CreateDistribution("source-v1", "1.0.0", "exe-v1");
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        var startMenu = _directory.GetPath("start-menu");
        var desktop = _directory.GetPath("desktop");

        Assert.Equal(0, RunScript(
            "Install-LLMWorkGUI.ps1",
            "-SourceDirectory", source,
            "-InstallDirectory", install,
            "-DataDirectory", data,
            "-ShortcutRoot", startMenu,
            "-DesktopShortcutRoot", desktop,
            "-Json").ExitCode);

        var result = RunScript(
            "Uninstall-LLMWorkGUI.ps1",
            "-InstallDirectory", install,
            "-DataDirectory", data,
            "-StartMenuShortcutRoot", startMenu,
            "-DesktopShortcutRoot", desktop,
            "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.True(result.Json.GetProperty("dataPreserved").GetBoolean());
        Assert.False(result.Json.GetProperty("dataRemoved").GetBoolean());

        Assert.False(Directory.Exists(install));
        Assert.False(File.Exists(Path.Combine(startMenu, "LLM Work GUI.lnk")));
        Assert.False(File.Exists(Path.Combine(desktop, "LLM Work GUI.lnk")));
        Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db"), Encoding.ASCII));
    }

    [Fact]
    public void Uninstall_WithRemoveUserData_RemovesApplicationAndUserData()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var result = RunScript(
            "Uninstall-LLMWorkGUI.ps1",
            "-InstallDirectory", install,
            "-DataDirectory", data,
            "-RemoveUserData",
            "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.False(result.Json.GetProperty("dataPreserved").GetBoolean());
        Assert.True(result.Json.GetProperty("dataRemoved").GetBoolean());
        Assert.False(Directory.Exists(install));
        Assert.False(Directory.Exists(data));
    }

    [Fact]
    public void Uninstall_WithRollbackDirectoryRecordedAsDataDirectory_PreservesDataAndSucceeds()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var markerPath = Path.Combine(data, "ACCEPTANCE_USER_DATA_MARKER.txt");
        var databasePath = Path.Combine(data, "llmworkgui.db");
        File.WriteAllText(markerPath, "marker-payload", Encoding.ASCII);
        var markerHash = HashFile(markerPath);
        var databaseHash = HashFile(databasePath);

        SetStateRollbackDirectory(install, data);

        var result = RunScript(
            "Uninstall-LLMWorkGUI.ps1",
            "-InstallDirectory", install,
            "-DataDirectory", data,
            "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.True(result.Json.GetProperty("dataPreserved").GetBoolean());
        Assert.False(result.Json.GetProperty("dataRemoved").GetBoolean());
        Assert.False(result.Json.GetProperty("removedRollback").GetBoolean());

        Assert.False(Directory.Exists(install), "The application must still be removed.");
        Assert.True(File.Exists(markerPath), "The user-data marker must survive uninstall.");
        Assert.Equal(markerHash, HashFile(markerPath));
        Assert.True(File.Exists(databasePath), "The user database must survive uninstall.");
        Assert.Equal(databaseHash, HashFile(databasePath));
    }

    [Fact]
    public void Uninstall_WithRollbackDirectoryRecordedAsUnrelatedDirectory_PreservesUnrelatedDirectory()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var unrelated = _directory.GetPath("unrelated-rollback");
        Directory.CreateDirectory(unrelated);
        var markerPath = Path.Combine(unrelated, "USER_FILES_MARKER.txt");
        File.WriteAllText(markerPath, "unrelated-content", Encoding.ASCII);

        SetStateRollbackDirectory(install, unrelated);

        var result = RunScript(
            "Uninstall-LLMWorkGUI.ps1",
            "-InstallDirectory", install,
            "-DataDirectory", data,
            "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.False(result.Json.GetProperty("removedRollback").GetBoolean());

        Assert.True(File.Exists(markerPath), "An unrelated directory recorded as rollback must not be deleted.");
        Assert.Equal("unrelated-content", File.ReadAllText(markerPath, Encoding.ASCII));
        Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db"), Encoding.ASCII));
        Assert.False(Directory.Exists(install));
    }

    [Fact]
    public void Uninstall_WithRollbackDirectoryJunctionToData_PreservesData()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var markerPath = Path.Combine(data, "ACCEPTANCE_USER_DATA_MARKER.txt");
        var databasePath = Path.Combine(data, "llmworkgui.db");
        File.WriteAllText(markerPath, "marker-payload", Encoding.ASCII);
        var markerHash = HashFile(markerPath);
        var databaseHash = HashFile(databasePath);

        var alias = _directory.GetPath("alias-rollback");
        CreateJunction(alias, data);
        SetStateRollbackDirectory(install, alias);

        var result = RunScript(
            "Uninstall-LLMWorkGUI.ps1",
            "-InstallDirectory", install,
            "-DataDirectory", data,
            "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.False(result.Json.GetProperty("removedRollback").GetBoolean());

        Assert.True((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0, "The junction must not be deleted.");
        Assert.True(File.Exists(markerPath), "The user-data marker must survive uninstall.");
        Assert.Equal(markerHash, HashFile(markerPath));
        Assert.True(File.Exists(databasePath), "The user database must survive uninstall.");
        Assert.Equal(databaseHash, HashFile(databasePath));
    }

    [Fact]
    public void Uninstall_RemovesLegitimateRollbackSnapshot()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var databaseHash = HashFile(Path.Combine(data, "llmworkgui.db"));

        var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");
        Assert.Equal(0, RunScript(
            "Update-LLMWorkGUI.ps1",
            "-SourceDirectory", sourceV2,
            "-InstallDirectory", install,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json").ExitCode);

        var rollback = GetRollbackDirectory(install);
        Assert.True(Directory.Exists(rollback), "The updater must create the rollback snapshot.");
        Assert.True(File.Exists(Path.Combine(rollback, "install-state.json")));

        var result = RunScript(
            "Uninstall-LLMWorkGUI.ps1",
            "-InstallDirectory", install,
            "-DataDirectory", data,
            "-Json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.True(result.Json.GetProperty("removedRollback").GetBoolean());
        Assert.True(result.Json.GetProperty("dataPreserved").GetBoolean());

        Assert.False(Directory.Exists(rollback), "The owned rollback snapshot must be removed.");
        Assert.False(Directory.Exists(install));
        Assert.Equal(databaseHash, HashFile(Path.Combine(data, "llmworkgui.db")));
    }

    [Fact]
    public void Uninstall_WithUnresolvableRollbackCandidate_RemovesInstallAndExitsZero()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var databasePath = Path.Combine(data, "llmworkgui.db");
        var databaseHash = HashFile(databasePath);

        var rollback = GetRollbackDirectory(install);
        var loopPartner = _directory.GetPath("rollback-loop-partner");
        CreateJunction(loopPartner, rollback);
        CreateJunction(rollback, loopPartner);
        Assert.True(
            (File.GetAttributes(rollback) & FileAttributes.ReparsePoint) != 0,
            "The default rollback path must be a looping reparse point.");

        try
        {
            var result = RunScript(
                "Uninstall-LLMWorkGUI.ps1",
                "-InstallDirectory", install,
                "-DataDirectory", data,
                "-StartMenuShortcutRoot", _directory.GetPath("start-menu"),
                "-DesktopShortcutRoot", _directory.GetPath("desktop"),
                "-Json");

            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Json.GetProperty("success").GetBoolean());
            Assert.True(result.Json.GetProperty("dataPreserved").GetBoolean());
            Assert.False(result.Json.GetProperty("removedRollback").GetBoolean());

            Assert.False(Directory.Exists(install), "The application must be removed despite the unresolvable rollback candidate.");
            Assert.True(
                (File.GetAttributes(rollback) & FileAttributes.ReparsePoint) != 0,
                "The unresolvable reparse point must be skipped, not deleted.");
            Assert.True((File.GetAttributes(loopPartner) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(databaseHash, HashFile(databasePath));
        }
        finally
        {
            TryDeleteReparsePoint(rollback);
            TryDeleteReparsePoint(loopPartner);
        }
    }

    [Fact]
    public void Uninstall_WithRemoveUserDataOnProtectedRoot_FailsClosedAndPreservesRoot()
    {
        var profileRoot = _directory.GetPath("isolated-protected-profile");
        var install = Path.Combine(profileRoot, "AppData", "Local", "Programs", "LLMWorkGUI");
        var isolatedEnvironment = new Dictionary<string, string>
        {
            ["USERPROFILE"] = profileRoot,
            ["LOCALAPPDATA"] = Path.Combine(profileRoot, "AppData", "Local"),
            ["APPDATA"] = Path.Combine(profileRoot, "AppData", "Roaming")
        };

        var protectedScratch = Path.Combine(
            profileRoot,
            "AppData",
            "LocalLow",
            "LLMWorkGUI.Tests",
            Guid.NewGuid().ToString("N"));
        var data = Path.Combine(protectedScratch, "data");
        try
        {
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "llmworkgui.db"), "user-database", Encoding.ASCII);
            var source = CreateDistribution("source-protected-root", "1.0.0", "exe-v1");
            var installed = RunScript("Install-LLMWorkGUI.ps1", isolatedEnvironment,
                "-SourceDirectory", source, "-InstallDirectory", install, "-DataDirectory", data,
                "-NoShortcuts", "-NoDesktopShortcut", "-Json");
            Assert.Equal(0, installed.ExitCode);

            var markerPath = Path.Combine(data, "ACCEPTANCE_USER_DATA_MARKER.txt");
            File.WriteAllText(markerPath, "marker-payload", Encoding.ASCII);
            var markerHash = HashFile(markerPath);

            var result = RunScript(
                "Uninstall-LLMWorkGUI.ps1",
                isolatedEnvironment,
                "-InstallDirectory", install,
                "-DataDirectory", data,
                "-RemoveUserData",
                "-StartMenuShortcutRoot", _directory.GetPath("start-menu"),
                "-DesktopShortcutRoot", _directory.GetPath("desktop"),
                "-Json");

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(result.Json.GetProperty("success").GetBoolean());
            Assert.Equal("UNINSTALL_PATH_INVALID", result.Json.GetProperty("errorCode").GetString());

            Assert.True(Directory.Exists(protectedScratch), "The simulated protected root must survive.");
            Assert.True(Directory.Exists(data), "The protected data directory must not be deleted.");
            Assert.True(File.Exists(markerPath), "The user-data marker must survive a refused uninstall.");
            Assert.Equal(markerHash, HashFile(markerPath));
            Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db"), Encoding.ASCII));
        }
        finally
        {
            var ownedRoot = Path.GetFullPath(_directory.Root) + Path.DirectorySeparatorChar;
            Assert.StartsWith(ownedRoot, Path.GetFullPath(profileRoot), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(profileRoot)) Directory.Delete(profileRoot, recursive: true);
            Assert.False(Directory.Exists(protectedScratch));
            Assert.False(Directory.Exists(profileRoot));
        }
    }

    [Theory]
    [InlineData("data-equal")]
    [InlineData("data-inside")]
    [InlineData("data-contains")]
    public void Rollback_WithRollbackDirectoryOverlappingData_FailsClosedAndPreservesData(string scenario)
    {
        var install = _directory.GetPath("app");
        var dataRoot = _directory.GetPath("profile");
        var data = scenario == "data-contains" ? Path.Combine(dataRoot, "data") : _directory.GetPath("data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "llmworkgui.db"), "user-database", Encoding.ASCII);
        InstallVersion("1.0.0", "exe-v1", install, data);

        var markerPath = Path.Combine(data, "ACCEPTANCE_USER_DATA_MARKER.txt");
        var databasePath = Path.Combine(data, "llmworkgui.db");
        File.WriteAllText(markerPath, "marker-payload", Encoding.ASCII);
        File.WriteAllText(Path.Combine(data, "LLMWorkGUI.App.exe"), "planted-exe", Encoding.ASCII);
        var nested = Path.Combine(data, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "LLMWorkGUI.App.exe"), "nested-exe", Encoding.ASCII);
        File.WriteAllText(Path.Combine(nested, "nested.txt"), "nested-content", Encoding.ASCII);

        var installHash = HashFile(Path.Combine(install, "LLMWorkGUI.App.exe"));
        var markerHash = HashFile(markerPath);
        var databaseHash = HashFile(databasePath);
        var nestedHash = HashFile(Path.Combine(nested, "nested.txt"));

        var rollbackDirectory = scenario switch
        {
            "data-equal" => data,
            "data-inside" => nested,
            "data-contains" => dataRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unsupported overlap scenario.")
        };

        var result = RunScript(
            "Rollback-LLMWorkGUI.ps1",
            "-InstallDirectory", install,
            "-RollbackDirectory", rollbackDirectory,
            "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Json.GetProperty("success").GetBoolean());
        Assert.Equal("ROLLBACK_PATH_INVALID", result.Json.GetProperty("errorCode").GetString());

        Assert.True(Directory.Exists(install), "The installation must not be moved.");
        Assert.Equal(installHash, HashFile(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.True(Directory.Exists(data), "The app-data directory must survive a rejected rollback.");
        Assert.True(File.Exists(markerPath), "The user-data marker must survive a rejected rollback.");
        Assert.Equal(markerHash, HashFile(markerPath));
        Assert.Equal(databaseHash, HashFile(databasePath));
        Assert.True(File.Exists(Path.Combine(data, "LLMWorkGUI.App.exe")), "The planted executable must not move out of app-data.");
        Assert.Equal("planted-exe", File.ReadAllText(Path.Combine(data, "LLMWorkGUI.App.exe"), Encoding.ASCII));
        Assert.Equal(nestedHash, HashFile(Path.Combine(nested, "nested.txt")));
    }

    [Fact]
    public void Rollback_WithRollbackDirectoryJunctionToData_FailsClosedAndPreservesData()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var markerPath = Path.Combine(data, "ACCEPTANCE_USER_DATA_MARKER.txt");
        var databasePath = Path.Combine(data, "llmworkgui.db");
        File.WriteAllText(markerPath, "marker-payload", Encoding.ASCII);
        var plantedExecutable = Path.Combine(data, "LLMWorkGUI.App.exe");
        File.WriteAllText(plantedExecutable, "planted-exe", Encoding.ASCII);

        var installHash = HashFile(Path.Combine(install, "LLMWorkGUI.App.exe"));
        var markerHash = HashFile(markerPath);
        var databaseHash = HashFile(databasePath);

        var alias = _directory.GetPath("alias-rollback");
        CreateJunction(alias, data);

        var result = RunScript(
            "Rollback-LLMWorkGUI.ps1",
            "-InstallDirectory", install,
            "-RollbackDirectory", alias,
            "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Json.GetProperty("success").GetBoolean());
        Assert.Equal("ROLLBACK_PATH_INVALID", result.Json.GetProperty("errorCode").GetString());

        Assert.True((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0, "The junction itself must remain untouched.");
        Assert.Equal(installHash, HashFile(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.True(File.Exists(markerPath), "The user-data marker must survive a rejected rollback.");
        Assert.Equal(markerHash, HashFile(markerPath));
        Assert.Equal(databaseHash, HashFile(databasePath));
        Assert.True(File.Exists(plantedExecutable), "The planted executable must not move out of app-data.");
        Assert.Equal("planted-exe", File.ReadAllText(plantedExecutable, Encoding.ASCII));
    }

    [Fact]
    public void Rollback_WithAncestorJunctionToData_FailsClosedAndPreservesData()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var markerPath = Path.Combine(data, "ACCEPTANCE_USER_DATA_MARKER.txt");
        var databasePath = Path.Combine(data, "llmworkgui.db");
        File.WriteAllText(markerPath, "marker-payload", Encoding.ASCII);
        var plantedExecutable = Path.Combine(data, "LLMWorkGUI.App.exe");
        File.WriteAllText(plantedExecutable, "planted-exe", Encoding.ASCII);

        var installHash = HashFile(Path.Combine(install, "LLMWorkGUI.App.exe"));
        var markerHash = HashFile(markerPath);
        var databaseHash = HashFile(databasePath);

        var aliasParent = _directory.GetPath("alias-parent");
        CreateJunction(aliasParent, _directory.Root);
        var rollbackDirectory = Path.Combine(aliasParent, "data");
        Assert.True(Directory.Exists(rollbackDirectory), "The ancestor junction must expose the data directory.");

        var result = RunScript(
            "Rollback-LLMWorkGUI.ps1",
            "-InstallDirectory", install,
            "-RollbackDirectory", rollbackDirectory,
            "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Json.GetProperty("success").GetBoolean());
        Assert.Equal("ROLLBACK_PATH_INVALID", result.Json.GetProperty("errorCode").GetString());

        Assert.True(
            (File.GetAttributes(aliasParent) & FileAttributes.ReparsePoint) != 0,
            "The ancestor junction must remain untouched.");
        Assert.True(Directory.Exists(install), "The installation must not be moved.");
        Assert.Equal(installHash, HashFile(Path.Combine(install, "LLMWorkGUI.App.exe")));
        Assert.True(File.Exists(markerPath), "The user-data marker must survive a rejected rollback.");
        Assert.Equal(markerHash, HashFile(markerPath));
        Assert.Equal(databaseHash, HashFile(databasePath));
        Assert.True(File.Exists(plantedExecutable), "The planted executable must not move out of app-data.");
        Assert.Equal("planted-exe", File.ReadAllText(plantedExecutable, Encoding.ASCII));
    }

    [Fact]
    public void Rollback_WithOscillatingAncestorJunctions_FailsClosedAndPreservesData()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var markerPath = Path.Combine(data, "ACCEPTANCE_USER_DATA_MARKER.txt");
        var databasePath = Path.Combine(data, "llmworkgui.db");
        File.WriteAllText(markerPath, "marker-payload", Encoding.ASCII);
        var plantedExecutable = Path.Combine(data, "LLMWorkGUI.App.exe");
        File.WriteAllText(plantedExecutable, "planted-exe", Encoding.ASCII);

        var installHash = HashFile(Path.Combine(install, "LLMWorkGUI.App.exe"));
        var markerHash = HashFile(markerPath);
        var databaseHash = HashFile(databasePath);

        var oscillatingA = _directory.GetPath("osc-a");
        var oscillatingB = _directory.GetPath("osc-b");
        CreateJunction(oscillatingA, Path.Combine(oscillatingB, "inner-a"));
        CreateJunction(oscillatingB, Path.Combine(oscillatingA, "inner-b"));

        try
        {
            var rollbackDirectory = Path.Combine(oscillatingA, "inner-b");
            var result = RunScript(
                "Rollback-LLMWorkGUI.ps1",
                "-InstallDirectory", install,
                "-RollbackDirectory", rollbackDirectory,
                "-Json");

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(result.Json.GetProperty("success").GetBoolean());
            Assert.Equal("ROLLBACK_PATH_INVALID", result.Json.GetProperty("errorCode").GetString());

            Assert.True(
                (File.GetAttributes(oscillatingA) & FileAttributes.ReparsePoint) != 0,
                "The first oscillating junction must remain untouched.");
            Assert.True(
                (File.GetAttributes(oscillatingB) & FileAttributes.ReparsePoint) != 0,
                "The second oscillating junction must remain untouched.");
            Assert.True(Directory.Exists(install), "The installation must not be moved.");
            Assert.Equal(installHash, HashFile(Path.Combine(install, "LLMWorkGUI.App.exe")));
            Assert.True(File.Exists(markerPath), "The user-data marker must survive a rejected rollback.");
            Assert.Equal(markerHash, HashFile(markerPath));
            Assert.Equal(databaseHash, HashFile(databasePath));
            Assert.True(File.Exists(plantedExecutable), "The planted executable must not move out of app-data.");
            Assert.Equal("planted-exe", File.ReadAllText(plantedExecutable, Encoding.ASCII));
        }
        finally
        {
            TryDeleteReparsePoint(oscillatingA);
            TryDeleteReparsePoint(oscillatingB);
        }
    }

    /// <summary>
    /// A process that merely shares the product executable name but runs from an unrelated directory
    /// must not be mistaken for the installed application. Matching by process name alone made the
    /// lifecycle refuse legitimate updates with APP_RUNNING and made this suite order-dependent, so the
    /// running-process precondition is asserted to be path-accurate.
    /// </summary>
    [Fact]
    public void Update_WithUnrelatedProcessSharingTheExecutableName_StillUpdates()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var decoyDirectory = _directory.GetPath("decoy");
        Directory.CreateDirectory(decoyDirectory);
        var decoyExecutable = Path.Combine(decoyDirectory, "LLMWorkGUI.App.exe");
        File.Copy(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "timeout.exe"),
            decoyExecutable);

        using var decoy = StartProductProcess(decoyExecutable);
        try
        {
            Assert.False(decoy.HasExited, "The decoy process must be running for this test to be meaningful.");

            var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");
            var result = RunScript(
                "Update-LLMWorkGUI.ps1",
                "-SourceDirectory", sourceV2,
                "-InstallDirectory", install,
                "-NoShortcuts", "-NoDesktopShortcut",
                "-Json");

            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Json.GetProperty("success").GetBoolean());
            Assert.Equal("2.0.0", ReadState(install).GetProperty("version").GetString());
            Assert.Equal("exe-v2", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe"), Encoding.ASCII));
            Assert.Equal("user-database", File.ReadAllText(Path.Combine(data, "llmworkgui.db"), Encoding.ASCII));
        }
        finally
        {
            KillProcess(decoy);
        }
    }

    /// <summary>
    /// The running-process precondition must refuse a lifecycle mutation when the installed product
    /// executable itself is running. The placeholder executable is replaced by a real PE so the
    /// child process can be observed exactly at the install path.
    /// </summary>
    [Fact]
    public void Update_WithInstalledProcessRunning_RefusesWithAppRunningAndPreservesState()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var databasePath = Path.Combine(data, "llmworkgui.db");
        var databaseHash = HashFile(databasePath);
        var executablePath = ReplaceInstalledExecutable(install);
        var installHash = HashFile(executablePath);

        using var running = StartProductProcess(executablePath);
        try
        {
            Assert.False(running.HasExited, "The installed process must be running for this test to be meaningful.");

            var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");
            var result = RunScript(
                "Update-LLMWorkGUI.ps1",
                "-SourceDirectory", sourceV2,
                "-InstallDirectory", install,
                "-NoShortcuts", "-NoDesktopShortcut",
                "-Json");

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(result.Json.GetProperty("success").GetBoolean());
            Assert.Equal("APP_RUNNING", result.Json.GetProperty("errorCode").GetString());

            Assert.Equal(installHash, HashFile(executablePath));
            Assert.Equal("1.0.0", ReadState(install).GetProperty("version").GetString());
            Assert.Equal(databaseHash, HashFile(databasePath));
            Assert.False(
                Directory.Exists(GetRollbackDirectory(install)),
                "A refused update must not create a rollback snapshot.");
        }
        finally
        {
            KillProcess(running);
        }
    }

    [Fact]
    public void Rollback_WithInstalledProcessRunning_RefusesWithAppRunningAndPreservesState()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var databasePath = Path.Combine(data, "llmworkgui.db");
        var databaseHash = HashFile(databasePath);

        var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");
        Assert.Equal(0, RunScript(
            "Update-LLMWorkGUI.ps1",
            "-SourceDirectory", sourceV2,
            "-InstallDirectory", install,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json").ExitCode);

        var rollback = GetRollbackDirectory(install);
        Assert.True(Directory.Exists(rollback), "The updater must create the rollback snapshot.");

        var executablePath = ReplaceInstalledExecutable(install);
        var installHash = HashFile(executablePath);
        var rollbackExecutable = Path.Combine(rollback, "LLMWorkGUI.App.exe");
        var rollbackHash = HashFile(rollbackExecutable);

        using var running = StartProductProcess(executablePath);
        try
        {
            Assert.False(running.HasExited, "The installed process must be running for this test to be meaningful.");

            var result = RunScript("Rollback-LLMWorkGUI.ps1", "-InstallDirectory", install, "-Json");

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(result.Json.GetProperty("success").GetBoolean());
            Assert.Equal("APP_RUNNING", result.Json.GetProperty("errorCode").GetString());

            Assert.Equal(installHash, HashFile(executablePath));
            Assert.Equal("2.0.0", ReadState(install).GetProperty("version").GetString());
            Assert.True(Directory.Exists(rollback), "The rollback snapshot must survive a refused rollback.");
            Assert.Equal(rollbackHash, HashFile(rollbackExecutable));
            Assert.Equal(databaseHash, HashFile(databasePath));
        }
        finally
        {
            KillProcess(running);
        }
    }

    [Fact]
    public void Uninstall_WithInstalledProcessRunning_RefusesWithAppRunningAndPreservesState()
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var databasePath = Path.Combine(data, "llmworkgui.db");
        var databaseHash = HashFile(databasePath);
        var executablePath = ReplaceInstalledExecutable(install);
        var installHash = HashFile(executablePath);

        using var running = StartProductProcess(executablePath);
        try
        {
            Assert.False(running.HasExited, "The installed process must be running for this test to be meaningful.");

            var result = RunScript(
                "Uninstall-LLMWorkGUI.ps1",
                "-InstallDirectory", install,
                "-DataDirectory", data,
                "-StartMenuShortcutRoot", _directory.GetPath("start-menu"),
                "-DesktopShortcutRoot", _directory.GetPath("desktop"),
                "-Json");

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(result.Json.GetProperty("success").GetBoolean());
            Assert.Equal("APP_RUNNING", result.Json.GetProperty("errorCode").GetString());

            Assert.True(Directory.Exists(install), "The application must not be removed while it is running.");
            Assert.Equal(installHash, HashFile(executablePath));
            Assert.Equal("1.0.0", ReadState(install).GetProperty("version").GetString());
            Assert.Equal(databaseHash, HashFile(databasePath));
        }
        finally
        {
            KillProcess(running);
        }
    }

    /// <summary>
    /// A live same-named candidate whose executable path cannot be resolved must fail closed:
    /// an unknown path is never assumed to be a known-different executable, so the lifecycle
    /// refuses the mutation instead of touching a possibly running installation.
    /// </summary>
    [Theory]
    [InlineData("unavailable")]
    [InlineData("access-denied")]
    public void Update_WithUnresolvableCandidatePath_FailsClosedWithAppRunningAndPreservesState(string resolver)
    {
        var install = _directory.GetPath("app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);

        var databasePath = Path.Combine(data, "llmworkgui.db");
        var databaseHash = HashFile(databasePath);
        var installExecutable = Path.Combine(install, "LLMWorkGUI.App.exe");
        var installHash = HashFile(installExecutable);

        var decoyDirectory = _directory.GetPath($"decoy-unresolvable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(decoyDirectory);
        var decoyExecutable = Path.Combine(decoyDirectory, "LLMWorkGUI.App.exe");
        File.Copy(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "timeout.exe"),
            decoyExecutable);

        using var decoy = StartProductProcess(decoyExecutable);
        try
        {
            Assert.False(decoy.HasExited, "The decoy process must be running for this test to be meaningful.");

            var sourceV2 = CreateDistribution("source-v2", "2.0.0", "exe-v2");
            var result = RunScript(
                "Update-LLMWorkGUI.ps1",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["LLMWORKGUI_TEST_PROCESS_PATH_FAILURE"] = resolver
                },
                "-SourceDirectory", sourceV2,
                "-InstallDirectory", install,
                "-NoShortcuts", "-NoDesktopShortcut",
                "-Json");

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(result.Json.GetProperty("success").GetBoolean());
            Assert.Equal("APP_RUNNING", result.Json.GetProperty("errorCode").GetString());

            Assert.Equal(installHash, HashFile(installExecutable));
            Assert.Equal("1.0.0", ReadState(install).GetProperty("version").GetString());
            Assert.Equal(databaseHash, HashFile(databasePath));
            Assert.False(
                Directory.Exists(GetRollbackDirectory(install)),
                "A refused update must not create a rollback snapshot.");
        }
        finally
        {
            KillProcess(decoy);
        }
    }

    private static string ReplaceInstalledExecutable(string installDirectory)
    {
        var timeoutSource = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "timeout.exe");
        Assert.True(File.Exists(timeoutSource), $"System timeout.exe '{timeoutSource}' must exist.");

        var executablePath = Path.Combine(installDirectory, "LLMWorkGUI.App.exe");
        File.Copy(timeoutSource, executablePath, overwrite: true);
        return executablePath;
    }

    private static Process StartProductProcess(string executablePath)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/T", "120", "/NOBREAK" }
        }) ?? throw new InvalidOperationException($"Unable to start process '{executablePath}'.");
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool IsWindows()
    {
        return OperatingSystem.IsWindows();
    }

    private string CreateDistribution(string directoryName, string version, string executableContent)
    {
        var target = _directory.GetPath($"{directoryName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(target, "shared"));

        var executableName = "LLMWorkGUI.App.exe";
        File.WriteAllText(Path.Combine(target, executableName), executableContent, Encoding.ASCII);
        File.WriteAllText(Path.Combine(target, "shared", "runtime.dll"), $"runtime-{version}", Encoding.ASCII);
        if (version.StartsWith("2", StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(target, "shared", "feature-v2.dll"), "feature-v2", Encoding.ASCII);
        }

        File.WriteAllText(
            Path.Combine(target, "version.json"),
            $"{{ \"product\": \"LLMWorkGUI\", \"version\": \"{version}\" }}",
            Encoding.UTF8);

        return target;
    }

    private static void TryDeleteReparsePoint(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        processStartInfo.ArgumentList.Add("/c");
        processStartInfo.ArgumentList.Add("mklink");
        processStartInfo.ArgumentList.Add("/J");
        processStartInfo.ArgumentList.Add(linkPath);
        processStartInfo.ArgumentList.Add(targetPath);

        var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Unable to start cmd.exe to create a junction.");

        using (process)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "mklink /J did not finish in time.");
            Assert.True(
                process.ExitCode == 0,
                $"mklink /J '{linkPath}' -> '{targetPath}' failed with exit code {process.ExitCode}: {output} {error}");
        }
    }

    private void InstallVersion(string version, string executableContent, string installDirectory, string dataDirectory)
    {
        var source = CreateDistribution($"source-v{version}", version, executableContent);
        var result = RunScript(
            "Install-LLMWorkGUI.ps1",
            "-SourceDirectory", source,
            "-InstallDirectory", installDirectory,
            "-DataDirectory", dataDirectory,
            "-NoShortcuts", "-NoDesktopShortcut",
            "-Json");

        Assert.Equal(0, result.ExitCode);
    }

    private string CreateUserData()
    {
        var data = _directory.GetPath("data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "llmworkgui.db"), "user-database", Encoding.ASCII);
        File.WriteAllText(Path.Combine(data, "settings.json"), "dark", Encoding.ASCII);
        return data;
    }

    private Dictionary<string, string> CreateExternalCliInstallations()
    {
        var cli = _directory.GetPath("external-cli");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in new[] { "opencode\\opencode.exe", "codex\\codex.exe", "agy\\agy-profile.exe", "mirasim\\mirasim.exe" })
        {
            var path = Path.Combine(cli, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"external-{relative}", Encoding.ASCII);
            hashes[path] = HashFile(path);
        }

        return hashes;
    }

    private static void AssertExternalCliUntouched(Dictionary<string, string> hashes)
    {
        foreach (var pair in hashes)
        {
            Assert.True(File.Exists(pair.Key), $"External CLI file '{pair.Key}' must still exist.");
            Assert.Equal(pair.Value, HashFile(pair.Key));
        }
    }

    private static JsonElement ReadState(string installDirectory)
    {
        var path = Path.Combine(installDirectory, "install-state.json");
        Assert.True(File.Exists(path), $"State file '{path}' must exist.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static void SetStateRollbackDirectory(string installDirectory, string rollbackDirectory)
    {
        var statePath = Path.Combine(installDirectory, "install-state.json");
        Assert.True(File.Exists(statePath), $"State file '{statePath}' must exist.");

        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        state["rollbackDirectory"] = rollbackDirectory;
        File.WriteAllText(statePath, state.ToJsonString());
    }

    private static string GetRollbackDirectory(string installDirectory)
    {
        return $"{installDirectory}.rollback";
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string GetRepositoryRoot()
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

        throw new InvalidOperationException("Repository root containing LLMWorkGUI.sln was not found.");
    }

    private static string GetScriptsDirectory()
    {
        return Path.Combine(GetRepositoryRoot(), "scripts");
    }

    private static ScriptRunResult RunScript(string scriptName, params string[] arguments)
    {
        return RunScript(scriptName, environmentVariables: null, arguments);
    }

    private static ScriptRunResult RunScript(
        string scriptName,
        IReadOnlyDictionary<string, string>? environmentVariables,
        params string[] arguments)
    {
        if (!IsWindows())
        {
            throw new PlatformNotSupportedException("The per-user installer targets Windows.");
        }

        var scriptPath = Path.Combine(GetScriptsDirectory(), scriptName);
        Assert.True(File.Exists(scriptPath), $"Packaging script '{scriptPath}' must exist.");

        var processStartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = GetRepositoryRoot(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (environmentVariables is not null)
        {
            foreach (var pair in environmentVariables)
            {
                processStartInfo.Environment[pair.Key] = pair.Value;
            }
        }

        processStartInfo.ArgumentList.Add("-NoLogo");
        processStartInfo.ArgumentList.Add("-NoProfile");
        processStartInfo.ArgumentList.Add("-NonInteractive");
        processStartInfo.ArgumentList.Add("-ExecutionPolicy");
        processStartInfo.ArgumentList.Add("Bypass");
        processStartInfo.ArgumentList.Add("-File");
        processStartInfo.ArgumentList.Add(scriptPath);

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = processStartInfo };
        process.Start();

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)TimeSpan.FromMinutes(3).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Packaging script '{scriptName}' did not finish in time.");
        }

        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult();

        return new ScriptRunResult(process.ExitCode, output, error);
    }

    private sealed record ScriptRunResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public JsonElement Json
        {
            get
            {
                var line = StandardOutput
                    .Split('\n')
                    .Select(candidate => candidate.Trim())
                    .LastOrDefault(candidate => candidate.StartsWith('{') && candidate.EndsWith('}'));

                Assert.False(
                    line is null,
                    $"Script output does not contain a JSON result. stdout: '{StandardOutput}' stderr: '{StandardError}'");

                using var document = JsonDocument.Parse(line!);
                return document.RootElement.Clone();
            }
        }
    }
}
