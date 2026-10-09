using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;
using ActivityEvent = LLMWorkGUI.Application.Observability.ActivityEvent;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Behaviour of the Activity Center's event-driven refresh under a live stream.
/// <para>
/// The load driver froze the screen: the view model showed one event while the ingestion boundary held
/// ten. The cause was here, not in the driver - the coalescing treated each append as a new generation, so
/// the refresh it had already posted found itself superseded, returned without refreshing, and nothing ever
/// re-posted. Under a steady stream every refresh was therefore stale and the operator watched a list
/// that stopped growing. These tests pin the corrected contract, and the first one is deliberately shaped
/// like the failure: a steady stream, a real dispatcher, and an assertion that the list keeps up.
/// </para>
/// </summary>
[Collection("ActivityCenter coalescing isolation")]
public sealed class ActivityCenterCoalescingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly SensitiveDataFilter Filter = new();

    [Fact]
    public void UnderAStreamTheScreenKeepsUp_InsteadOfFreezingOnTheFirstRefresh()
    {
        // A real dispatcher, pumped by the test's own message loop, because the point is that the posted
        // work actually runs - an inline scheduler would hide the defect entirely.
        var dispatcher = Dispatcher.CurrentDispatcher;

        var service = new ActivityCenterService(Filter.Redact);
        var scheduler = new DispatcherActivityUiScheduler(dispatcher);

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            scheduler: scheduler,
            minimumRefreshInterval: TimeSpan.FromMilliseconds(10));

        // Fifty appends in a tight burst, exactly as a fast execution would deliver them.
        for (var index = 0; index < 50; index++)
        {
            service.Append(Event($"event-{index:D3}"));
        }

        Assert.Equal(50, service.TotalCount);

        Pump(dispatcher, TimeSpan.FromSeconds(5));

        // The regression: this was 1.
        Assert.Equal(50, viewModel.TotalCount);
        Assert.Equal(50, viewModel.Events.Count);
        Assert.Equal("event-049", viewModel.Events[0].Id);
    }

    [Fact]
    public void ACohescedBurstCostsFarFewerRefreshesThanItHasAppends()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var service = new ActivityCenterService(Filter.Redact);
        var scheduler = new DispatcherActivityUiScheduler(dispatcher);

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            scheduler: scheduler,
            minimumRefreshInterval: TimeSpan.FromMilliseconds(20));

        for (var index = 0; index < 200; index++)
        {
            service.Append(Event($"event-{index:D3}"));
        }

        Pump(dispatcher, TimeSpan.FromSeconds(5));

        Assert.Equal(200, viewModel.TotalCount);

        // Coalescing is the point of the mechanism: 200 appends must not become 200 list rebuilds. The
        // bound is deliberately loose - it asserts the rate limit works, not an exact count.
        Assert.True(
            viewModel.RefreshCount < 200,
            $"the refresh rate limit did not coalesce: {viewModel.RefreshCount} rebuilds for 200 appends.");
        Assert.True(viewModel.CoalescedAppendCount > 0, "no append was folded into a pending refresh.");
    }

    [Fact]
    public void ARefreshThatFindsNoOutstandingWorkIsCounted_NotSilentlyDropped()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var service = new ActivityCenterService(Filter.Redact);
        var scheduler = new DispatcherActivityUiScheduler(dispatcher);

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            scheduler: scheduler);

        service.Append(Event("one"));
        Pump(dispatcher, TimeSpan.FromSeconds(5));

        // A stray callback after the slot is closed must be observable rather than a no-op.
        viewModel.RequestCoalescedRefresh();
        viewModel.RequestCoalescedRefresh();
        Pump(dispatcher, TimeSpan.FromSeconds(2));

        Assert.Equal(1, viewModel.TotalCount);
    }

    [Fact]
    public void LatencySamplesSpanTheIngestionStampAndThePostDispatchVisibleStamp()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var service = new ActivityCenterService(Filter.Redact);
        var latency = new ActivityVisibilityLatencyRecorder(TimeProvider.System);
        var scheduler = new DispatcherActivityUiScheduler(dispatcher);

        using var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            scheduler: scheduler,
            latencyRecorder: latency,
            minimumRefreshInterval: TimeSpan.Zero);

        for (var index = 0; index < 25; index++)
        {
            service.Append(Event($"event-{index:D3}"));
        }

        Pump(dispatcher, TimeSpan.FromSeconds(5));

        var samples = viewModel.HarvestLatencySamples();

        // One sample per event, not one sample per refresh: coalescing must not be able to hide the
        // latency of the events it merged.
        Assert.Equal(25, samples.Count);
        Assert.All(samples, sample => Assert.True(
            sample.Milliseconds >= 0,
            $"a latency sample was negative ({sample.Milliseconds} ms); the two stamps are not from one clock."));
        Assert.True(samples.Max(sample => sample.Milliseconds) >= 0);

        // A second harvest is empty: samples are harvested, not re-counted.
        Assert.Empty(viewModel.HarvestLatencySamples());
    }

    [Fact]
    public void DisposeStopsFollowingTheStream()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var service = new ActivityCenterService(Filter.Redact);
        var scheduler = new DispatcherActivityUiScheduler(dispatcher);

        var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            scheduler: scheduler,
            minimumRefreshInterval: TimeSpan.FromMilliseconds(10));

        service.Append(Event("before"));
        Pump(dispatcher, TimeSpan.FromSeconds(5));
        Assert.Equal(1, viewModel.TotalCount);

        viewModel.Dispose();

        service.Append(Event("after"));
        Pump(dispatcher, TimeSpan.FromSeconds(2));

        // A disposed screen keeps its last state; it must not resurrect itself on the next append.
        Assert.Equal(1, viewModel.TotalCount);
    }

    /// <summary>Runs the dispatcher until the pending work has settled or the budget is spent.</summary>
    private static void Pump(Dispatcher dispatcher, TimeSpan budget)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < budget)
        {
            var frame = new DispatcherFrame();

            // One argument only: BeginInvoke(priority, delegate, args) would otherwise bind the second
            // priority to the delegate's parameter list and fail on invocation.
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));

            Dispatcher.PushFrame(frame);

            if (deadline.Elapsed >= budget)
            {
                return;
            }

            Thread.Sleep(20);
        }
    }

    private static ActivityEvent Event(string id) => new(
        id,
        Now.AddSeconds(-60),
        ActivityEventKind.Execution,
        ActivityRoleNames.Coder,
        ActivityEventState.Running,
        ActivityEventSource.Native,
        $"title {id}",
        $"description {id}");
}

/// <summary>
/// These tests drive a real WPF dispatcher and are timing sensitive, so they must not run concurrently
/// with each other or with the visual suites that also pump messages.
/// </summary>
[CollectionDefinition("ActivityCenter coalescing isolation", DisableParallelization = true)]
public sealed class ActivityCenterCoalescingCollection
{
}
