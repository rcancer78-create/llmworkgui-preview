using System.Windows.Controls;
using System.Windows.Threading;
using LLMWorkGUI.Ui.Tests.TestSupport;
using LLMWorkGUI.VisibleWorkflowHarness;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("Cursor dispatcher isolation")]
public sealed class VisibleHarnessContinuationTests
{
    [Fact]
    public async Task AwaitingSettleFromDispatcherPreservesCallerControlAccess()
    {
        var continuation = StaTestRunner.Run<Task>(async () =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Assert.IsType<DispatcherSynchronizationContext>(SynchronizationContext.Current);
            var control = new TextBox();

            await WindowCapture.SettleAsync();

            Assert.True(dispatcher.CheckAccess());
            control.Text = "caller resumed on its UI dispatcher";
            Assert.Equal("caller resumed on its UI dispatcher", control.Text);
        });
        await continuation.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
