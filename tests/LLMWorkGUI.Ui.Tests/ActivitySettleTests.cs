using System.Diagnostics;
using System.Windows.Threading;
using LLMWorkGUI.ActivityLoadDriver;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ActivitySettleTests
{
    [Fact]
    public async Task UnpumpedDispatcher_TimesOutInsteadOfReportingSuccessfulSettle()
    {
        // A dedicated thread creates a real dispatcher but deliberately never pumps it.
        var source = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var thread = new Thread(() => { source.SetResult(Dispatcher.CurrentDispatcher); release.Wait(); });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var originalDispatcher = ActivityWindowCapture.UiDispatcher;
        ActivityWindowCapture.UiDispatcher = await source.Task;
        try
        {
            var elapsed = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(() => ActivityWindowCapture.SettleAsync(TimeSpan.FromMilliseconds(25)));
            elapsed.Stop();
            var reported = ActivityWindowCapture.LastSettleMilliseconds;
            Assert.True(double.IsFinite(reported) && reported > 0 && reported <= elapsed.Elapsed.TotalMilliseconds,
                $"Settle telemetry={reported:R} ms; enclosing elapsed={elapsed.Elapsed.TotalMilliseconds:R} ms; requested timeout=25 ms.");
        }
        finally
        {
            ActivityWindowCapture.UiDispatcher = originalDispatcher;
            release.Set();
            thread.Join();
        }
    }
}
