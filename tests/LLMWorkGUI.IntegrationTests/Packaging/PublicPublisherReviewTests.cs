using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Packaging;

public sealed class PublicPublisherReviewTests
{
    [Fact]
    public async Task ConcurrentPublisherCannotActivateInsideAnotherWritersOutput()
    {
        using var directory = new TestDirectory();
        var output = directory.GetPath("output");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "version.json"), "{\"product\":\"LLMWorkGUI\",\"version\":\"0\"}");
        File.WriteAllText(Path.Combine(output, "LLMWorkGUI.App.exe"), "original");
        var reached = directory.GetPath("first-renamed");
        var release = directory.GetPath("release-first");
        var entry = directory.GetPath("entry.ps1");
        File.WriteAllText(entry, """
            $ErrorActionPreference = 'Stop'
            function dotnet {
                $index = [Array]::IndexOf($args, '-o')
                $destination = [string]$args[$index + 1]
                [void][System.IO.Directory]::CreateDirectory($destination)
                [System.IO.File]::WriteAllText((Join-Path $destination 'LLMWorkGUI.App.exe'), $env:REVIEW_ROLE)
                $global:LASTEXITCODE = 0
            }
            function Move-Item {
                param([string]$LiteralPath, [string]$Destination)
                Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
                if ($env:REVIEW_ROLE -eq 'A' -and $LiteralPath -eq $env:REVIEW_OUTPUT) {
                    [System.IO.File]::WriteAllText($env:REVIEW_REACHED, 'renamed')
                    while (-not [System.IO.File]::Exists($env:REVIEW_RELEASE)) { Start-Sleep -Milliseconds 10 }
                }
            }
            & $env:REVIEW_SCRIPT -OutputDirectory $env:REVIEW_OUTPUT -Version $env:REVIEW_ROLE
            """);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LLMWorkGUI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        Process Start(string role)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", entry })
                start.ArgumentList.Add(arg);
            start.Environment["REVIEW_ROLE"] = role;
            start.Environment["REVIEW_SCRIPT"] = Path.Combine(root.FullName, "scripts", "Publish-LLMWorkGUI.ps1");
            start.Environment["REVIEW_OUTPUT"] = output;
            start.Environment["REVIEW_REACHED"] = reached;
            start.Environment["REVIEW_RELEASE"] = release;
            return Process.Start(start)!;
        }
        using var first = Start("A");
        var firstOut = first.StandardOutput.ReadToEndAsync();
        var firstError = first.StandardError.ReadToEndAsync();
        Process? second = null;
        Task<string>? secondOut = null, secondError = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(reached)) await Task.Delay(10, deadline.Token);
            second = Start("B");
            secondOut = second.StandardOutput.ReadToEndAsync();
            secondError = second.StandardError.ReadToEndAsync();
            await second.WaitForExitAsync(deadline.Token);
            Assert.NotEqual(0, second.ExitCode);
            Assert.Contains("PUBLISH_BUSY", await secondError);
        }
        finally
        {
            File.WriteAllText(release, "release");
            try { await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                if (!first.HasExited) first.Kill(entireProcessTree: true);
                if (second is not null && !second.HasExited) { second.Kill(entireProcessTree: true); await second.WaitForExitAsync(); }
                if (secondOut is not null) await secondOut;
                if (secondError is not null) await secondError;
                second?.Dispose();
            }
        }
        Assert.True(first.ExitCode == 0, await firstOut + await firstError);
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "version.json")));
        Assert.Equal("A", marker.RootElement.GetProperty("version").GetString());
        Assert.Equal("A", File.ReadAllText(Path.Combine(output, "LLMWorkGUI.App.exe")));
        Assert.Empty(Directory.EnumerateDirectories(output));
    }
}
