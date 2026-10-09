using System.IO;
using System.Windows.Threading;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("Keyboard focus isolation")]
public sealed class StartupCliResponsivenessTests
{
    [Fact]
    public async Task SynchronousProbeLeavesDispatcherAvailableAndPublishesOnDispatcher()
    {
        using var probe = new BlockingProbe();
        var propertyThreads = new List<int>();
        var state = StaTestRunner.Run(() =>
        {
            var model = new CliStatusViewModel(probe, TimeProvider.System);
            model.PropertyChanged += (_, _) => propertyThreads.Add(Environment.CurrentManagedThreadId);
            return (model, task: model.RefreshAsync(), thread: Environment.CurrentManagedThreadId);
        });
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(state.thread, probe.WorkerThread);
            Assert.False(state.task.IsCompleted);
            Assert.True(StaTestRunner.Run(() => state.model.IsDetectionPending));
            // A queued input-priority action must execute while filesystem discovery is still blocked.
            var input = StaTestRunner.Run(() => Dispatcher.CurrentDispatcher.InvokeAsync(
                () => state.model.IsDetectionPending, DispatcherPriority.Input).Task);
            Assert.True(await input.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { probe.Release.Set(); }
        await state.task.WaitAsync(TimeSpan.FromSeconds(10));
        StaTestRunner.Run(() =>
        {
            Assert.True(state.model.IsChecked);
            Assert.False(state.model.IsDetectionPending);
            Assert.All(propertyThreads, thread => Assert.Equal(state.thread, thread));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledLifetimeDoesNotPublishLateSnapshotOrFailure(bool fail)
    {
        using var probe = new BlockingProbe { Fail = fail };
        using var cancellation = new CancellationTokenSource();
        var model = new CliStatusViewModel(probe, TimeProvider.System);
        var refresh = model.RefreshAsync(cancellation.Token);
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
        }
        finally { probe.Release.Set(); }
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(model.IsChecked);
        Assert.False(model.HasDetectionFailed);
        Assert.False(model.IsDetectionPending);
        Assert.Empty(model.Tools);
        Assert.Equal(CliStatusViewModel.NotCheckedHeadline, model.Headline);
    }

    [Fact]
    public async Task AlreadyCancelledLifetimeDoesNotStartDiscovery()
    {
        using var probe = new BlockingProbe();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var model = new CliStatusViewModel(probe, TimeProvider.System);
        await model.RefreshAsync(cancellation.Token);
        Assert.False(probe.Entered.Task.IsCompleted);
        Assert.False(model.IsChecked);
    }

    private sealed class BlockingProbe : ICliDetectionService, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public int WorkerThread { get; private set; }
        public bool Fail { get; init; }

        public Task<CliDetectionSnapshot> DetectAsync(CancellationToken cancellationToken = default)
        {
            WorkerThread = Environment.CurrentManagedThreadId;
            Entered.TrySetResult();
            // Models an OS filesystem call that cannot observe cancellation until it returns.
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Probe release was not signalled.");
            if (Fail) throw new IOException("Synthetic late filesystem failure.");
            return Task.FromResult(new CliDetectionSnapshot(Array.Empty<CliStatus>(), DateTimeOffset.UtcNow));
        }

        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
}
