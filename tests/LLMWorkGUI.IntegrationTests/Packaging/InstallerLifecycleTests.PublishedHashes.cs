using System.Text.Json;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Packaging;

public sealed partial class InstallerLifecycleTests
{
    [Theory]
    [InlineData("clean")]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("malformed")]
    public void PublishedInventoryIsCheckedBeforeInstalling(string mutation)
    {
        var source = CreateDistribution("manifest", "1.0.0", "synthetic-executable");
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(source, file).Replace('\\', '/'), HashFile);
        var manifest = Path.Combine(source, "package-sha256.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { format = 1, algorithm = "SHA256", files }));
        var payload = Path.Combine(source, "shared", "runtime.dll");
        switch (mutation)
        {
            case "changed": File.AppendAllText(payload, "changed"); break;
            case "missing": File.Delete(payload); break;
            case "extra": File.WriteAllText(Path.Combine(source, "unexpected.dll"), "extra"); break;
            case "malformed": File.WriteAllText(manifest, "{}"); break;
        }
        var install = _directory.GetPath("manifest-install");
        var result = RunScript("Install-LLMWorkGUI.ps1", "-SourceDirectory", source,
            "-InstallDirectory", install, "-DataDirectory", _directory.GetPath("manifest-data"),
            "-NoShortcuts", "-NoDesktopShortcut", "-Json");
        if (mutation == "clean")
        {
            Assert.True(result.ExitCode == 0, result.ToString());
            Assert.True(File.Exists(Path.Combine(install, "LLMWorkGUI.App.exe")));
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(Directory.Exists(install));
        }
    }
}
