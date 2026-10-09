using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;
using ActivityEvent = LLMWorkGUI.Application.Observability.ActivityEvent;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Headless behaviour of the Activity Center's display projection: bounded paging, the bounded row preview,
/// and the honest overflow and coalescing counters the screen reports.
/// </summary>
public sealed class ActivityCenterProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly SensitiveDataFilter Filter = new();

    [Fact]
    public void PagingMaterializesOnlyTheRequestedPage_WhileReportingTheExactCount()
    {
        var service = CreateService(events: 5_000);

        var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            clipboard: new SilentClipboard(),
            pageSize: 200);

        Assert.Equal(5_000, viewModel.TotalCount);
        Assert.Equal(5_000, viewModel.FilteredCount);
        Assert.Equal(25, viewModel.PageCount);
        Assert.Equal(200, viewModel.Events.Count);

        // A deep page must not build the whole stream. The previous shape asked the service for
        // pageSize * currentPage events and sliced locally, so page 25 built 5 000 event objects to show
        // 200 of them - on the normative 100 000-event history, 500 000.
        viewModel.GoToPage(25);

        Assert.Equal(25, viewModel.CurrentPage);
        Assert.Equal(5_000, viewModel.FilteredCount);
        Assert.Equal(200, viewModel.Events.Count);

        // Newest first, so the last page runs from the oldest event back towards the newest of that page.
        Assert.Equal("event-0000199", viewModel.Events[0].Id);
        Assert.Equal("event-0000000", viewModel.Events[^1].Id);
    }

    [Fact]
    public void PagingIsNewestFirstAndTheExactCountNeverChanges()
    {
        var service = CreateService(events: 500);

        var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            clipboard: new SilentClipboard(),
            pageSize: 100);

        Assert.Equal(500, viewModel.FilteredCount);
        Assert.Equal("event-0000499", viewModel.Events[0].Id);

        viewModel.NextPageCommand.Execute(null);
        Assert.Equal("event-0000399", viewModel.Events[0].Id);
        Assert.Equal(500, viewModel.FilteredCount);

        viewModel.GoToPage(5);
        Assert.Equal("event-0000099", viewModel.Events[0].Id);
        Assert.Equal("event-0000000", viewModel.Events[^1].Id);
        Assert.Equal(100, viewModel.Events.Count);
    }

    [Fact]
    public void QueryPage_ReportsExactCountsWithoutMaterializingTheStream()
    {
        var service = CreateService(events: 1_000);

        var result = service.QueryPage(new ActivityFilterCriteria(), page: 3, pageSize: 100);

        Assert.Equal(1_000, result.TotalCount);
        Assert.Equal(1_000, result.FilteredCount);
        Assert.Equal(10, result.PageCount);
        Assert.Equal(100, result.Items.Count);

        // Newest first: page 3 of 1 000 at 100 per page starts at the 201st newest.
        Assert.Equal("event-0000799", result.Items[0].Id);
        Assert.Equal("event-0000700", result.Items[^1].Id);
    }

    [Fact]
    public void ARowShowsABoundedPreview_AndTheFullTextStaysOnTheModel()
    {
        var massive = new string('m', 256 * 1024);
        var service = new ActivityCenterService(Filter.Redact);

        service.Append(new ActivityEvent(
            "massive",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Running,
            ActivityEventSource.Native,
            "Large payload",
            massive));

        var item = new ActivityEventItemViewModel(service.Snapshot().Single());

        // The row template renders the preview into a trimming TextBlock, and WPF measures the whole
        // string to lay a row out. With a 256 KiB body and 200 rows that is a layout pass proportional to
        // the entire retained history on every refresh.
        Assert.True(
            item.DescriptionPreview.Length <= ActivityEventItemViewModel.PreviewLength + 2,
            $"the preview was {item.DescriptionPreview.Length} characters.");
        Assert.EndsWith("…", item.DescriptionPreview, StringComparison.Ordinal);

        // The whole message is still available to the detail pane; nothing is lost, only not laid out.
        Assert.Equal(massive.Length, item.Description.Length);
    }

    [Fact]
    public void ThePreviewCollapsesNewlines_AndCoversShortMessagesExactly()
    {
        var shortMessage = new ActivityEvent(
            "short",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Running,
            ActivityEventSource.Native,
            "t",
            "one line");

        Assert.Equal("one line", new ActivityEventItemViewModel(shortMessage).DescriptionPreview);

        var multiline = new ActivityEvent(
            "multiline",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Running,
            ActivityEventSource.Native,
            "t",
            "first\nsecond\r\nthird");

        var preview = new ActivityEventItemViewModel(multiline).DescriptionPreview;

        // A line break inside a trimming TextBlock defeats the trimming, so the preview is one line.
        Assert.DoesNotContain('\n', preview);
        Assert.DoesNotContain('\r', preview);
        Assert.Equal("first second third", preview);
    }

    [Fact]
    public void AnEmptyDescriptionYieldsAnEmptyPreviewRatherThanAPlaceholder()
    {
        var item = new ActivityEventItemViewModel(new ActivityEvent(
            "empty",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Running,
            ActivityEventSource.Native,
            "t",
            string.Empty));

        Assert.Equal(string.Empty, item.DescriptionPreview);
    }

    [Fact]
    public void OverflowIsVisibleOnTheScreen_EvenWhenNothingWasEvicted()
    {
        var service = new ActivityCenterService(Filter.Redact, capacity: 3);
        var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            clipboard: new SilentClipboard());

        // Nothing evicted yet: the label still states the accounting, so an operator can see the bound.
        Assert.False(viewModel.HasOverflowed);
        Assert.NotEmpty(viewModel.OverflowDisplay);
        Assert.Contains("0", viewModel.OverflowDisplay, StringComparison.Ordinal);

        for (var index = 0; index < 10; index++)
        {
            service.Append(Event($"event-{index}"));
        }

        viewModel.Refresh();

        Assert.True(viewModel.HasOverflowed);
        Assert.Contains("7", viewModel.OverflowDisplay, StringComparison.Ordinal);
        Assert.Equal("10", viewModel.RetentionFacts["offered"]);
        Assert.Equal("7", viewModel.RetentionFacts["evicted"]);
        Assert.Equal("3", viewModel.RetentionFacts["retained"]);
    }

    [Fact]
    public void TheCapacityBoundIsSetForTheHistoryItMustHold()
    {
        // The shipped default has to hold the normative already-saved history on its own.
        Assert.Equal(100_000, ActivityCenterService.DefaultCapacity);
        Assert.Equal(100_000, EventSearchIndex.DefaultCapacity);
    }

    [Fact]
    public void TheStreamFollowsItsAppends_AndTheCountersDescribeTheCoalescing()
    {
        var service = new ActivityCenterService(Filter.Redact);
        var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            clipboard: new SilentClipboard());

        // The immediate scheduler runs inline, so the coalescing counters are observable here without a
        // message loop: the refresh happened, and the appends that arrived around it were folded in.
        service.Append(Event("first"));
        service.Append(Event("second"));

        Assert.Equal(2, viewModel.TotalCount);
        Assert.Equal(2, viewModel.Events.Count);
        Assert.True(viewModel.RefreshCount >= 1, "no refresh ran for the appends.");
    }

    [Fact]
    public void PageSizeMustBePositive()
    {
        var service = new ActivityCenterService(Filter.Redact);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityCenterViewModel(
            service,
            timeProvider: TimeProvider.System,
            pageSize: 0));
    }

    private static ActivityCenterService CreateService(int events)
    {
        var service = new ActivityCenterService(Filter.Redact, TimeProvider.System, capacity: events + 10);

        for (var index = 0; index < events; index++)
        {
            service.Append(Event($"event-{index:D7}", Now.AddSeconds(index)));
        }

        return service;
    }

    private static ActivityEvent Event(string id) => Event(id, Now);

    private static ActivityEvent Event(string id, DateTimeOffset occurredAt) => new(
        id,
        occurredAt,
        ActivityEventKind.Execution,
        ActivityRoleNames.Coder,
        ActivityEventState.Running,
        ActivityEventSource.Native,
        $"title {id}",
        $"description for {id}");

    private sealed class SilentClipboard : IClipboardService
    {
        public void SetText(string text)
        {
        }
    }
}
