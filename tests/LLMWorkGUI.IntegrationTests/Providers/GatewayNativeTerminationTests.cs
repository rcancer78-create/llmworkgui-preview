using System.Diagnostics;
using LLMGateway.Core;
using LLMGateway.Native;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class GatewayNativeTerminationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public async Task DisposalDuringLargeStandardInputWrite_DrainsBeforeClosingStreams(int delayMs)
    {
        using var directory = new TestDirectory();
        var launch = Command(directory, "ping -n 60 127.0.0.1 > nul\r\n") with
        { StandardInput = new string('x', 512 * 1024) };
        await using var process = NativeProcess.Start(launch);
        await Task.Delay(delayMs);
        await process.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        var stopped = await process.StopAsync();
        Assert.True(stopped.RootExitConfirmed);
        Assert.True(stopped.ContainedTreeExitConfirmed);
    }

    [Fact]
    public async Task StopWaitsForOwnedProcessAndConcurrentDisposalIsIdempotent()
    {
        using var directory = new TestDirectory();
        await using var process = NativeProcess.Start(Command(directory, "echo ready\r\nset /p value=\r\n"), keepInputOpen: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var lines = process.ReadLinesAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await lines.MoveNextAsync());
        Assert.Equal("ready", lines.Current);
        using var owned = Process.GetProcessById(process.Id);
        _ = owned.SafeHandle;
        Assert.False(owned.HasExited);

        var first = process.StopAsync();
        var second = process.StopAsync();
        Assert.Same(first, second);
        var result = await first.WaitAsync(deadline.Token);
        Assert.True(result.KillRequested);
        Assert.True(result.RootExitConfirmed);
        Assert.True(result.ContainmentEstablished);
        Assert.True(result.ContainedTreeExitConfirmed);
        Assert.NotNull(result.ExitCode);
        Assert.Equal(owned.Id, result.ProcessId);
        Assert.True(owned.HasExited);
        await Task.WhenAll(process.DisposeAsync().AsTask(), process.DisposeAsync().AsTask());
        Assert.Same(result, await process.StopAsync());
    }

    [Fact]
    public async Task NaturalExitPreservesExitCodeAndConfirmsContainment()
    {
        using var directory = new TestDirectory();
        await using var process = NativeProcess.Start(Command(directory, "exit /b 7\r\n"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(7, await process.WaitForExitAsync(deadline.Token));
        var result = await process.StopAsync();
        Assert.True(result.RootExitConfirmed);
        // Root exit and an empty Windows job are separate observations. Cleanup may
        // still issue a job termination after root exit; the original exit code must survive.
        Assert.True(result.ContainmentEstablished);
        Assert.True(result.ContainedTreeExitConfirmed);
        Assert.Equal(7, result.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunRejectsInvalidAdmissionBeforeStartingProcess(bool cancelled)
    {
        using var directory = new TestDirectory();
        // A nonexistent executable would throw ProviderUnavailable if admission reached Start.
        var launch = new NativeLaunch(new(directory.GetPath("missing.exe"), [], LaunchKind.Direct, "fixture"), [],
            new Dictionary<string, string?>(), directory.Root);
        using var source = new CancellationTokenSource();
        if (cancelled) source.Cancel();
        if (cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeProcess.RunAsync(launch, TimeSpan.FromSeconds(5), source.Token));
        else
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NativeProcess.RunAsync(launch, TimeSpan.Zero, source.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTimeoutAndCancellationDisposeTheProcess(bool callerCancellation)
    {
        using var directory = new TestDirectory();
        var launch = Command(directory, "ping -n 60 127.0.0.1 > nul\r\n");
        using var source = new CancellationTokenSource();
        if (callerCancellation) source.CancelAfter(TimeSpan.FromMilliseconds(300));
        var run = NativeProcess.RunAsync(launch,
            callerCancellation ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(300), source.Token);
        if (callerCancellation)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(15)));
        else
        {
            var result = await run.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(result.TimedOut);
            Assert.False(result.Success);
        }
    }

    [Fact]
    public async Task NaturalRootExitTerminatesContainedChildHoldingInheritedStderr()
    {
        using var directory = new TestDirectory();
        var script = directory.GetPath("inherited-stderr.ps1");
        await File.WriteAllTextAsync(script, """
            $ErrorActionPreference = 'Stop'
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName = Join-Path ([Environment]::SystemDirectory) 'cmd.exe'
            $info.Arguments = '/d /c ping -n 60 127.0.0.1 > nul'
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardInput = $true
            $child = [Diagnostics.Process]::Start($info)
            [Console]::Out.WriteLine($child.Id)
            [Console]::Out.Flush()
            [Console]::In.ReadLine() | Out-Null
            exit 0
            """);
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        await using var process = NativeProcess.Start(new(new(shell, [], LaunchKind.Direct, shell),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script],
            new Dictionary<string, string?>(), directory.Root), keepInputOpen: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var lines = process.ReadLinesAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await lines.MoveNextAsync());
        using var child = Process.GetProcessById(int.Parse(lines.Current));
        _ = child.SafeHandle;
        try
        {
            using var root = Process.GetProcessById(process.Id);
            Assert.False(child.HasExited);
            await process.Input.WriteLineAsync("exit");
            await process.Input.FlushAsync();
            await root.WaitForExitAsync(deadline.Token);
            var observation = await process.StopAsync();
            Assert.True(observation.RootExitConfirmed);
            Assert.True(observation.ContainmentEstablished);
            Assert.True(observation.ContainedTreeExitConfirmed);
            Assert.True(observation.KillRequested);
            Assert.Equal(0, await process.WaitForExitAsync(deadline.Token));
            await process.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(deadline.Token);
        }
    }

    private static NativeLaunch Command(TestDirectory directory, string body)
    {
        File.WriteAllText(directory.GetPath("fixture.cmd"), "@echo off\r\n" + body);
        var shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return new(new(shell, [], LaunchKind.Direct, shell), ["/d", "/c", "fixture.cmd"],
            new Dictionary<string, string?>(), directory.Root);
    }
}
