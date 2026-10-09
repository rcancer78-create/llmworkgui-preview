using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Packaging;

public sealed partial class InstallerLifecycleTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("relative")]
    [InlineData("foreign")]
    public void Uninstall_RequiresMarkerBoundToTheActualInstallDirectory(string binding)
    {
        var install = _directory.GetPath("owned-app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var marker = Path.Combine(install, "install-state.json");
        var state = JsonNode.Parse(File.ReadAllText(marker))!.AsObject();
        if (binding == "missing") state.Remove("installDirectory");
        else state["installDirectory"] = binding == "relative" ? "owned-app" : _directory.GetPath("other-app");
        File.WriteAllText(marker, state.ToJsonString());
        var inventory = RecoveryFileInventory(_directory.Root);

        var result = RunScript("Uninstall-LLMWorkGUI.ps1", "-InstallDirectory", install,
            "-DataDirectory", data, "-RemoveUserData", "-StartMenuShortcutRoot", _directory.GetPath("menu"),
            "-DesktopShortcutRoot", _directory.GetPath("desktop"), "-Json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(inventory, RecoveryFileInventory(_directory.Root));
    }

    [Fact]
    public void Uninstall_LockedOwnedShortcutRefusesWithoutClaimingRemoval()
    {
        var install = _directory.GetPath("owned-app");
        var data = CreateUserData();
        InstallVersion("1.0.0", "exe-v1", install, data);
        var menu = _directory.GetPath("menu");
        Directory.CreateDirectory(menu);
        var shortcut = Path.Combine(menu, "LLM Work GUI.lnk");
        CreateSyntheticShortcut(shortcut, Path.Combine(install, "LLMWorkGUI.App.exe"));
        // Allow the shell to inspect the shortcut, but withhold Windows delete sharing.
        using var held = new FileStream(shortcut, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var result = RunScript("Uninstall-LLMWorkGUI.ps1", "-InstallDirectory", install,
            "-DataDirectory", data, "-StartMenuShortcutRoot", menu,
            "-DesktopShortcutRoot", _directory.GetPath("desktop"), "-Json");
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(shortcut));
        Assert.True(File.Exists(Path.Combine(install, "LLMWorkGUI.App.exe")));
    }
}
