using LLMWorkGUI.Application.Workflows;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class ScratchAsyncCleanupReviewTests
{
    [Fact]
    public async Task CleanupYieldsCallerAndSerializesConcurrentDisposal()
    {
        var path = Path.Combine(Path.GetTempPath(), "llmworkgui-cleanup-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        using var release = new ManualResetEventSlim();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var workspace = new ScratchWorkspace(path, ScratchScope.Run, "review", beforeCleanup: () =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Synthetic cleanup was not released.");
        });
        var first = Task.Run(() =>
        {
            var cleanup = workspace.CleanupWorkspaceAsync();
            returned.TrySetResult();
            return cleanup;
        });
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = workspace.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(workspace.IsCleanedUp);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
            await workspace.DisposeAsync();
        }
        Assert.Equal(1, calls);
        Assert.True(workspace.IsCleanedUp);
        Assert.False(Directory.Exists(path));
    }
}
