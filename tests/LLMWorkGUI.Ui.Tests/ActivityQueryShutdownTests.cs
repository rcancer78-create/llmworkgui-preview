using LLMWorkGUI.App.Services;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ActivityQueryShutdownTests
{
    [Fact]
    public void DisposalWaitsForTheCurrentQueryBeforeReturning()
    {
        var executor = new BoundedActivityQueryExecutor();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        executor.Enqueue(() => { entered.Set(); release.Wait(); });
        using var finished = new ManualResetEventSlim();
        using var disposeEntered = new ManualResetEventSlim();
        var started = false;
        Exception? failure = null;
        // The controller must not depend on a thread-pool continuation while Dispose joins
        // a deliberately blocked worker; the full UI suite can saturate that same pool.
        var disposing = new Thread(() =>
        {
            try { disposeEntered.Set(); executor.Dispose(); }
            catch (Exception exception) { failure = exception; }
            finally { finished.Set(); }
        }) { IsBackground = true };
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            disposing.Start();
            started = true;
            Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(finished.Wait(TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            release.Set();
            if (started)
                Assert.True(disposing.Join(TimeSpan.FromSeconds(10)));
            else executor.Dispose();
        }
        Assert.Null(failure);
        Assert.Equal(1, executor.ExecutedCount);
    }
}
