using System.Diagnostics;
using LLMGateway.Core;
using LLMGateway.Native;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class GatewayAuthorizedProcessTests
{
    [Fact]
    public async Task ActualGenerationIsBoundWhileChildCannotRunThenPromptFlowsAfterResume()
    {
        using var directory = new TestDirectory();
        var entered = new TaskCompletionSource<NativeProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var start = NativeProcess.StartAuthorizedAsync(Command(directory) with { StandardInput = "BOUND_PROMPT\r\n" },
            async (process, token) => { entered.SetResult(process); await resume.Task.WaitAsync(token); }, deadline.Token);
        var held = await entered.Task.WaitAsync(deadline.Token);
        try
        {
            Assert.True(held.ProcessGeneration > 0);
            Assert.False(held.HasExited);
            await Task.Delay(100, deadline.Token);
            Assert.False(File.Exists(directory.GetPath("started.marker")));
            Assert.False(start.IsCompleted);
        }
        finally { resume.TrySetResult(); }
        await using var running = await start;
        Assert.Same(held, running);
        var lines = new List<string>();
        await foreach (var line in running.ReadLinesAsync(deadline.Token)) lines.Add(line);
        Assert.Contains("BOUND_PROMPT", lines);
        Assert.Equal(0, await running.WaitForExitAsync(deadline.Token));
        Assert.True(File.Exists(directory.GetPath("started.marker")));
        await using var next = NativeProcess.Start(Command(directory) with { StandardInput = "NEXT\r\n" });
        Assert.True(next.ProcessGeneration > running.ProcessGeneration);
        var stopped = await next.StopAsync();
        Assert.True(stopped.RootExitConfirmed);
        Assert.True(stopped.ContainedTreeExitConfirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedOrCancelledBindingNeverResumesAndTerminatesOwnedTree(bool cancel)
    {
        using var directory = new TestDirectory();
        using var source = new CancellationTokenSource();
        Process? owned = null;
        var error = await Record.ExceptionAsync(() => NativeProcess.StartAuthorizedAsync(Command(directory),
            (native, gateToken) =>
            {
                Assert.True(native.ProcessGeneration > 0);
                owned = Process.GetProcessById(native.Id); _ = owned.SafeHandle;
                Assert.False(File.Exists(directory.GetPath("started.marker")));
                if (cancel) { source.Cancel(); return Task.CompletedTask; }
                throw new InvalidOperationException("binding rejected");
            }, source.Token));
        try
        {
            if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.IsType<InvalidOperationException>(error);
            Assert.NotNull(owned);
            Assert.True(owned.HasExited);
            Assert.False(File.Exists(directory.GetPath("started.marker")));
        }
        finally { owned?.Dispose(); }
    }

    [Fact]
    public async Task FailedOsCreationDoesNotIssueBindingCallback()
    {
        using var directory = new TestDirectory();
        var missing = directory.GetPath("does-not-exist.exe");
        var called = false;
        await Assert.ThrowsAsync<GatewayException>(() => NativeProcess.StartAuthorizedAsync(
            new(new(missing, [], LaunchKind.Direct, missing), [], new Dictionary<string, string?>(), directory.Root),
            (_, _) => { called = true; return Task.CompletedTask; }, CancellationToken.None));
        Assert.False(called);
    }

    private static NativeLaunch Command(TestDirectory directory)
    {
        File.WriteAllText(directory.GetPath("fixture.cmd"), "@echo off\r\necho started>started.marker\r\nset /p value=\r\necho %value%\r\nexit /b 0\r\n");
        var shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return new(new(shell, [], LaunchKind.Direct, shell), ["/d", "/c", "fixture.cmd"], new Dictionary<string, string?>(), directory.Root);
    }
}
