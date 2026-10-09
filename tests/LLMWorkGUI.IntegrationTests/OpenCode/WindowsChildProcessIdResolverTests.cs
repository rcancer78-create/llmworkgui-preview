using System.Diagnostics;
using LLMWorkGUI.Backends.OpenCode;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class WindowsChildProcessIdResolverTests
{
    [Fact]
    public async Task ResolveChildProcessId_FindsSpawnedChildProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var uniqueName = "llmworkgui-probe-" + Guid.NewGuid().ToString("N");
        var executablePath = Path.Combine(directory.Root, uniqueName + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), executablePath);

        var startedAfter = DateTimeOffset.UtcNow.AddSeconds(-5);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-n", "60", "127.0.0.1" }
        });

        Assert.NotNull(process);

        try
        {
            var resolved = WindowsChildProcessIdResolver.Instance
                .ResolveChildProcessId(executablePath, startedAfter);

            Assert.Equal(process!.Id, resolved);
        }
        finally
        {
            if (!process!.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public void ResolveChildProcessId_WhenNoChildMatches_ReturnsNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var resolved = WindowsChildProcessIdResolver.Instance.ResolveChildProcessId(
            @"C:\nonexistent\llmworkgui-probe.exe",
            DateTimeOffset.UtcNow);

        Assert.Null(resolved);
    }
}
