using System.Text.Json;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Packaging;

public sealed partial class InstallerLifecycleTests
{
    [Theory]
    [InlineData(false, "remove")]
    [InlineData(false, "replace")]
    [InlineData(false, "clean")]
    [InlineData(false, "legacy")]
    [InlineData(true, "remove")]
    [InlineData(true, "replace")]
    [InlineData(true, "clean")]
    [InlineData(true, "legacy")]
    public void TransferCannotDiscardOrReplaceTheOriginallyValidatedManifest(bool update, string mutation)
    {
        var source = CreateDistribution("transfer-source", "2.0.0", "original-candidate");
        if (mutation != "legacy")
        {
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .ToDictionary(file => Path.GetRelativePath(source, file).Replace('\\', '/'), HashFile);
            File.WriteAllText(Path.Combine(source, "package-sha256.json"),
                JsonSerializer.Serialize(new { format = 1, algorithm = "SHA256", files }));
        }
        var install = _directory.GetPath("transfer-install");
        var data = _directory.GetPath("transfer-data");
        if (update)
        {
            var previous = CreateDistribution("transfer-previous", "1.0.0", "previous-executable");
            Assert.Equal(0, RunScript("Install-LLMWorkGUI.ps1", "-SourceDirectory", previous,
                "-InstallDirectory", install, "-DataDirectory", data,
                "-NoShortcuts", "-NoDesktopShortcut", "-Json").ExitCode);
        }
        var entry = _directory.GetPath("transfer-entry.ps1");
        File.WriteAllText(entry, """
            param([string]$SourceDirectory,[string]$InstallDirectory,[string]$DataDirectory,[string]$Installer,[string]$Mutation)
            $ErrorActionPreference = 'Stop'
            $env:REVIEW_TRANSFER_MUTATION=$Mutation
            $env:REVIEW_TRANSFER_SOURCE=$SourceDirectory
            function Import-Module {
                param([string]$Name,[switch]$Force)
                Microsoft.PowerShell.Core\Import-Module -Name $Name -Force -Global
                $module=Get-Module LLMWorkGUI.Packaging
                & $module {
                    $script:ReviewOriginalCopy=(Get-Item Function:Copy-LlmDirectoryTree).ScriptBlock
                    function script:Copy-LlmDirectoryTree {
                        param([string]$Source,[string]$Destination)
                        if ($Source -eq $env:REVIEW_TRANSFER_SOURCE) {
                            if ($env:REVIEW_TRANSFER_MUTATION -eq 'remove') {
                                Remove-Item -LiteralPath (Join-Path $Source 'package-sha256.json')
                                [IO.File]::WriteAllText((Join-Path $Source 'LLMWorkGUI.App.exe'),'changed-during-transfer')
                            }
                            elseif ($env:REVIEW_TRANSFER_MUTATION -eq 'replace') {
                                [IO.File]::WriteAllText((Join-Path $Source 'LLMWorkGUI.App.exe'),'changed-during-transfer')
                                Write-LlmPublishedHashes -Directory $Source
                            }
                        }
                        & $script:ReviewOriginalCopy -Source $Source -Destination $Destination
                    }
                }
            }
            & $Installer -SourceDirectory $SourceDirectory -InstallDirectory $InstallDirectory -DataDirectory $DataDirectory -NoShortcuts -NoDesktopShortcut -Json
            exit $LASTEXITCODE
            """);
        var result = RunScript(entry, "-SourceDirectory", source, "-InstallDirectory", install,
            "-DataDirectory", data, "-Installer", Path.Combine(GetScriptsDirectory(),
                update ? "Update-LLMWorkGUI.ps1" : "Install-LLMWorkGUI.ps1"), "-Mutation", mutation);
        if (mutation is "remove" or "replace")
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal("VALIDATION_FAILED", result.Json.GetProperty("errorCode").GetString());
            if (update) Assert.Equal("previous-executable", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
            else Assert.False(Directory.Exists(install));
        }
        else
        {
            Assert.True(result.ExitCode == 0, result.ToString());
            Assert.Equal("original-candidate", File.ReadAllText(Path.Combine(install, "LLMWorkGUI.App.exe")));
        }
    }
}
