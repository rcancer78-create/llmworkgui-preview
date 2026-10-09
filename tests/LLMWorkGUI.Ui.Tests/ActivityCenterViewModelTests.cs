using System;
using System.Linq;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Headless behaviour of the Phase 11B Activity Center: multi-criteria filtering, paging, selection,
/// details hand-off to the diff viewer, the standard empty/error states and the guarantee that secret
/// values never become searchable.
/// </summary>
public sealed class ActivityCenterViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly SensitiveDataFilter Filter = new();

    [Fact]
    public void WithoutEvents_ReportsAnEmptyStateWithoutInventingCounts()
    {
        var viewModel = CreateViewModel();

        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.HasEvents);
        Assert.False(viewModel.HasError);
        Assert.Equal(0, viewModel.TotalCount);
        Assert.Equal(0, viewModel.FilteredCount);
        Assert.Equal("0 событий записано", viewModel.TotalCountDisplay);
        Assert.Equal("0 найдено из 0", viewModel.FilteredCountDisplay);
        Assert.Equal("Страница 1 из 1", viewModel.PageDisplay);
        Assert.Equal("Событий активности пока нет", viewModel.EmptyState.Title);
        Assert.False(viewModel.EmptyState.HasAction);
        Assert.False(viewModel.HasPreviousPage);
        Assert.False(viewModel.HasNextPage);
    }

    [Fact]
    public void Filters_CombineTimeWindowRoleStateAndSource_AndResetRestoresEverything()
    {
        var service = CreateService();

        service.AppendRange(new[]
        {
            Event("architect", Now.AddMinutes(-5), ActivityRoleNames.Architect, ActivityEventState.Completed),
            Event("reviewer-running", Now.AddMinutes(-10), ActivityRoleNames.Reviewer, ActivityEventState.Running),
            Event("reviewer-old-failed", Now.AddHours(-2), ActivityRoleNames.Reviewer, ActivityEventState.Failed),
            Event("coder-synthetic", Now.AddHours(-30), ActivityRoleNames.Coder, ActivityEventState.Running, ActivityEventSource.Synthetic)
        });

        var viewModel = new ActivityCenterViewModel(service, timeProvider: new FixedTimeProvider(Now), clipboard: new RecordingClipboard());

        Assert.Equal(4, viewModel.FilteredCount);

        // Time window.
        viewModel.SelectedTimeRangeOption = viewModel.TimeRangeOptions.Single(option => option.Value == ActivityTimeRange.LastHour);
        Assert.Equal(2, viewModel.FilteredCount);

        // Role plus state are combined.
        viewModel.RoleFilters.Single(filter => filter.Display == ActivityRoleNames.Reviewer).IsSelected = true;
        viewModel.StateFilters.Single(filter => filter.State == ActivityEventState.Running).IsSelected = true;
        Assert.Equal(new[] { "reviewer-running" }, viewModel.Events.Select(item => item.Id).ToArray());

        // Reset restores the full stream before the provenance filter is exercised on its own.
        viewModel.ResetFiltersCommand.Execute(null);
        Assert.Equal(4, viewModel.FilteredCount);

        viewModel.SelectedSourceOption = viewModel.SourceOptions.Single(option => option.Value == ActivityEventSource.Synthetic);
        Assert.Equal(new[] { "coder-synthetic" }, viewModel.Events.Select(item => item.Id).ToArray());

        // Reset clears every criterion again.
        viewModel.ResetFiltersCommand.Execute(null);
        Assert.Equal(4, viewModel.FilteredCount);
        Assert.Equal(ActivityTimeRange.All, viewModel.SelectedTimeRangeOption.Value);
        Assert.Null(viewModel.SelectedSourceOption.Value);
        Assert.All(viewModel.RoleFilters, filter => Assert.False(filter.IsSelected));
        Assert.All(viewModel.StateFilters, filter => Assert.False(filter.IsSelected));
        Assert.Equal(string.Empty, viewModel.SearchQuery);
    }

    [Fact]
    public void Search_FindsRedactedTextButNeverSecretValues()
    {
        var service = CreateService();
        const string apiKey = "sk-live-9999888877776666";

        service.Append(Event(
            "secret-event",
            Now,
            ActivityRoleNames.TechLead,
            ActivityEventState.Running,
            description: $"api_key={apiKey}"));
        service.Append(Event(
            "clean-event",
            Now.AddMinutes(-1),
            ActivityRoleNames.Coder,
            ActivityEventState.Completed,
            description: "build completed"));

        var viewModel = new ActivityCenterViewModel(service, timeProvider: new FixedTimeProvider(Now), clipboard: new RecordingClipboard());

        viewModel.SearchQuery = "build";
        Assert.Equal(new[] { "clean-event" }, viewModel.Events.Select(item => item.Id).ToArray());

        viewModel.SearchQuery = apiKey;
        Assert.Empty(viewModel.Events);
        Assert.Equal(0, viewModel.FilteredCount);
        Assert.Equal(2, viewModel.TotalCount);
        Assert.True(viewModel.IsEmpty);
        Assert.Equal("Нет событий, соответствующих текущим фильтрам", viewModel.EmptyState.Title);
        Assert.True(viewModel.EmptyState.HasAction);
        Assert.Equal("Сбросить фильтры", viewModel.EmptyState.ActionLabel);

        viewModel.SearchQuery = "[REDACTED]";
        Assert.Equal(new[] { "secret-event" }, viewModel.Events.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void Paging_NavigatesPagesAndReportsExactCounts()
    {
        var service = CreateService();

        for (var i = 0; i < 7; i++)
        {
            service.Append(Event($"event-{i}", Now.AddMinutes(i - 7), ActivityRoleNames.Coder, ActivityEventState.Running));
        }

        var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            clipboard: new RecordingClipboard(),
            pageSize: 3);

        Assert.Equal(7, viewModel.TotalCount);
        Assert.Equal(7, viewModel.FilteredCount);
        Assert.Equal(3, viewModel.PageCount);
        Assert.Equal(new[] { "event-6", "event-5", "event-4" }, viewModel.Events.Select(item => item.Id).ToArray());
        Assert.False(viewModel.HasPreviousPage);
        Assert.True(viewModel.HasNextPage);

        viewModel.NextPageCommand.Execute(null);
        Assert.Equal("Страница 2 из 3", viewModel.PageDisplay);
        Assert.Equal(new[] { "event-3", "event-2", "event-1" }, viewModel.Events.Select(item => item.Id).ToArray());

        viewModel.NextPageCommand.Execute(null);
        Assert.Equal(new[] { "event-0" }, viewModel.Events.Select(item => item.Id).ToArray());
        Assert.False(viewModel.HasNextPage);

        viewModel.GoToPage(99);
        Assert.Equal(3, viewModel.CurrentPage);

        viewModel.FirstPageCommand.Execute(null);
        Assert.Equal(1, viewModel.CurrentPage);
    }

    [Fact]
    public void SelectingAnEvent_LoadsDiffOrArtifactIntoTheViewer_AndPlainEventsKeepItClosed()
    {
        var service = CreateService();

        service.Append(new ActivityEvent(
            "diff-event",
            Now,
            ActivityEventKind.Execution,
            ActivityRoleNames.Coder,
            ActivityEventState.Completed,
            ActivityEventSource.Native,
            "Diff delivered",
            "The coder delivered the patch.",
            diffText: "--- a/app.txt\n+++ b/app.txt\n@@ -1,2 +1,2 @@\n-old\n+new\n keep",
            artifactName: "app.txt"));

        service.Append(new ActivityEvent(
            "artifact-event",
            Now.AddMinutes(-1),
            ActivityEventKind.Execution,
            ActivityRoleNames.TechLead,
            ActivityEventState.Completed,
            ActivityEventSource.Native,
            "Artifact delivered",
            "The tech lead delivered the plan.",
            artifactName: "plan.json",
            artifactContent: "{\"stage\":\"plan\"}",
            artifactChangeStatus: "Modified"));

        service.Append(Event("plain-event", Now.AddMinutes(-2), ActivityRoleNames.Reviewer, ActivityEventState.Running));

        var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            clipboard: new RecordingClipboard());

        var diffItem = viewModel.Events.Single(item => item.Id == "diff-event");
        Assert.True(diffItem.HasDiff);
        Assert.Equal("DIFF", diffItem.DetailsBadge);

        viewModel.SelectedEvent = diffItem;
        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.IsDiffViewerOpen);
        Assert.True(viewModel.DiffViewer.HasDiff);
        Assert.Single(viewModel.DiffViewer.UnifiedLines.Where(line => line.IsAddition));

        viewModel.SelectedEvent = viewModel.Events.Single(item => item.Id == "artifact-event");
        Assert.True(viewModel.DiffViewer.IsArtifactMode);
        Assert.True(viewModel.DiffViewer.HasArtifact);
        Assert.Equal("plan.json", viewModel.DiffViewer.ArtifactNameDisplay);
        Assert.Equal("Изменён", viewModel.DiffViewer.ArtifactChangeStatusDisplay);

        viewModel.SelectedEvent = viewModel.Events.Single(item => item.Id == "plain-event");
        Assert.False(viewModel.IsDiffViewerOpen);
        Assert.False(viewModel.DiffViewer.HasDiff);
        Assert.False(viewModel.DiffViewer.HasArtifact);

        viewModel.SelectedEvent = diffItem;
        viewModel.CloseDiffViewerCommand.Execute(null);
        Assert.False(viewModel.IsDiffViewerOpen);

        viewModel.DiffViewer.CopyDiffCommand.Execute(null);
        Assert.Equal("Скопировано в буфер обмена.", viewModel.DiffViewer.CopyNotice);
    }

    [Fact]
    public void Refresh_PicksUpNewlyAppendedEvents()
    {
        var service = CreateService();
        service.Append(Event("event-1", Now.AddMinutes(-1), ActivityRoleNames.Coder, ActivityEventState.Running));

        var viewModel = new ActivityCenterViewModel(service, timeProvider: new FixedTimeProvider(Now), clipboard: new RecordingClipboard());

        Assert.Single(viewModel.Events);

        service.Append(Event("event-2", Now, ActivityRoleNames.Reviewer, ActivityEventState.Completed));

        viewModel.RefreshCommand.Execute(null);

        Assert.Equal(2, viewModel.Events.Count);
        Assert.Equal("event-2", viewModel.Events[0].Id);
        Assert.Equal("2 событий записано", viewModel.TotalCountDisplay);
    }

    [Fact]
    public void ShowErrorAndDismissError_ToggleTheStandardErrorState()
    {
        var viewModel = CreateViewModel();

        viewModel.ShowError("The activity query failed", "The repository is unavailable.", "System.InvalidOperationException: boom");

        Assert.True(viewModel.HasError);
        Assert.False(viewModel.IsEmpty);
        Assert.Equal("The repository is unavailable.", viewModel.ErrorMessage);
        Assert.Equal("The activity query failed", viewModel.ErrorState.Title);
        Assert.True(viewModel.ErrorState.HasDetails);
        Assert.False(viewModel.ErrorState.IsDetailsExpanded);

        viewModel.ErrorState.ToggleDetailsCommand.Execute(null);
        Assert.True(viewModel.ErrorState.IsDetailsExpanded);

        viewModel.DismissErrorCommand.Execute(null);
        Assert.False(viewModel.HasError);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);

        Assert.Throws<ArgumentException>(() => viewModel.ShowError("  ", "message"));
        Assert.Throws<ArgumentException>(() => viewModel.ShowError("title", " "));
    }

    [Fact]
    public void SelectedEventDetailsAndAutomationLabels_ExposeHonestPlaceholders()
    {
        var service = CreateService();
        service.Append(Event("plain", Now, ActivityRoleNames.Architect, ActivityEventState.Warning));

        var viewModel = new ActivityCenterViewModel(service, timeProvider: new FixedTimeProvider(Now), clipboard: new RecordingClipboard());
        var item = viewModel.Events.Single();

        Assert.Equal("Architect", item.RoleDisplay);
        Assert.Equal(ActivityEventState.Warning.ToString(), item.StateDisplay);
        Assert.Equal(ActivityEventItemViewModel.NotReportedPlaceholder, item.RouteDisplay);
        Assert.Equal(ActivityEventItemViewModel.NotReportedPlaceholder, item.SessionDisplay);
        Assert.Equal(ActivityEventItemViewModel.NotReportedPlaceholder, item.ExecutionDisplay);
        Assert.False(item.HasDetails);
        Assert.Equal(string.Empty, item.DetailsBadge);
        Assert.Contains("Architect", item.Summary, StringComparison.Ordinal);
        Assert.False(viewModel.OpenSelectedDiffCommand.CanExecute(null));
    }

    private static ActivityCenterViewModel CreateViewModel(int pageSize = ActivityCenterViewModel.DefaultPageSize) =>
        new(
            CreateService(),
            timeProvider: new FixedTimeProvider(Now),
            clipboard: new RecordingClipboard(),
            pageSize: pageSize);

    private static ActivityCenterService CreateService() =>
        new(Filter.Redact, new FixedTimeProvider(Now));

    private static ActivityEvent Event(
        string id,
        DateTimeOffset occurredAt,
        string role,
        ActivityEventState state,
        ActivityEventSource source = ActivityEventSource.Native,
        string description = "") =>
        new(
            id,
            occurredAt,
            ActivityEventKind.Execution,
            role,
            state,
            source,
            $"Event {id}",
            description);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? LastText { get; private set; }

        public void SetText(string text)
        {
            LastText = text;
        }
    }
}
