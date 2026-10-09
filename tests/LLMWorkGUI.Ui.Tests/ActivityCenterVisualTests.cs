using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// QuickViewer-style WPF scenarios of the Phase 11B Activity Center. The shipped
/// <see cref="ActivityCenterView"/> is rendered over a realistic multi-source stream in Dark and Light
/// themes at 100% DPI plus a 200% DPI pass; screenshots are persisted to the Screenshots folder.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class ActivityCenterVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly string SampleDiff =
        "--- a/src/App/ViewModels/ActivityCenterViewModel.cs\n" +
        "+++ b/src/App/ViewModels/ActivityCenterViewModel.cs\n" +
        "@@ -1,6 +1,7 @@\n" +
        " using System.Collections.ObjectModel;\n" +
        "-\n" +
        "+using LLMWorkGUI.Application.Observability;\n" +
        "+\n" +
        " namespace LLMWorkGUI.App.ViewModels;\n" +
        " \n" +
        " public sealed class ActivityCenterViewModel\n" +
        "@@ -40,6 +41,8 @@ public sealed class ActivityCenterViewModel\n" +
        "-    private readonly IActivityCenterService _service; void Run() {}\n" +
        "+    private readonly IActivityCenterService _service;\n" +
        "+    private readonly TimeProvider _timeProvider;\n" +
        "     public string Title => \"Activity Center\";";

    [Fact]
    public void ActivityCenter_DarkTheme_ShowsFiltersEventsDetailsAndDiff_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = CreateRealisticViewModel();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel, 1280, 800);

            try
            {
                var texts = ReadTexts(view);

                // Header, counts and every filter group are visible.
                Assert.Contains("Центр активности", texts);
                Assert.Contains("Период:", texts);
                Assert.Contains("Роли:", texts);
                Assert.Contains("Состояния:", texts);
                Assert.Contains("Источник:", texts);
                Assert.Contains("Поиск:", texts);
                Assert.Contains("12 событий записано", texts);
                Assert.Contains("12 найдено из 12", texts);
                Assert.Contains("Страница 1 из 1", texts);

                // The operator roles and normalized states are rendered.
                Assert.Contains("Architect", texts);
                Assert.Contains("TechLead", texts);
                Assert.Contains("Reviewer", texts);
                Assert.Contains("Coder", texts);
                Assert.Contains("System", texts);
                Assert.Contains("Running", texts);
                Assert.Contains("Completed", texts);
                Assert.Contains("Failed", texts);
                Assert.Contains("Cancelled", texts);
                Assert.Contains("Warning", texts);

                // Synthetic provenance is badged, never silently blended in.
                Assert.Contains("SYNTHETIC", texts);

                // Selecting the diff event opens the unified viewer with additions and deletions.
                Assert.True(viewModel.HasSelection);
                Assert.True(viewModel.IsDiffViewerOpen);
                Assert.Contains(
                    texts,
                    text => text.Contains("using LLMWorkGUI.Application.Observability;", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.Contains("private readonly TimeProvider _timeProvider;", StringComparison.Ordinal));

                var screenshotPath = CaptureScreenshot(view, "activity_center_dark_100.png", dpi: 96, 1280, 800);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ActivityCenter_LightTheme_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = CreateRealisticViewModel();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Light);

            var (window, view) = OpenView(viewModel, 1280, 800);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("Центр активности", texts);
                Assert.Contains("12 событий записано", texts);
                Assert.Contains("SYNTHETIC", texts);

                var screenshotPath = CaptureScreenshot(view, "activity_center_light.png", dpi: 96, 1280, 800);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ActivityCenter_AtTwoHundredPercentDpi_KeepsEveryElementLaidOut_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = CreateRealisticViewModel();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            // The same device-independent layout is rasterized at 200% DPI (2x pixels), exactly like a
            // 200% DPI display would render it.
            var (window, view) = OpenView(viewModel, 1280, 800);

            try
            {
                var eventsList = Assert.IsAssignableFrom<System.Windows.Controls.ListBox>(
                    view.FindName("EventsList"));
                var diffPane = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("DiffViewerPane"));

                Assert.True(eventsList.ActualWidth > 0, "The event list must not collapse at 200% DPI.");
                Assert.True(eventsList.ActualHeight > 0, "The event list must keep its height at 200% DPI.");
                Assert.True(diffPane.ActualWidth > 0, "The diff viewer must not collapse at 200% DPI.");
                Assert.False(double.IsNaN(view.DesiredSize.Width));
                Assert.False(double.IsNaN(view.DesiredSize.Height));

                var screenshotPath = CaptureScreenshot(view, "activity_center_dark_200.png", dpi: 192, 2560, 1600);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AutoScroll_FollowsNewestEventAtTopOfDescendingPage()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var service = new ActivityCenterService(value => value);
            for (var index = 0; index < 100; index++)
                service.Append(ActivityEvent.SystemEvent($"event-{index}", $"Event {index}", "Details",
                    Now.AddSeconds(index), ActivityEventState.Completed));
            using var viewModel = new ActivityCenterViewModel(service);
            var (window, view) = OpenView(viewModel, 1280, 800);
            try
            {
                var list = (System.Windows.Controls.ListBox)view.FindName("EventsList");
                list.ScrollIntoView(viewModel.Events[^1]);
                view.UpdateLayout();
                service.Append(ActivityEvent.SystemEvent("newest", "Newest event", "Details",
                    Now.AddSeconds(101), ActivityEventState.Completed));
                view.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() => { }));
                view.UpdateLayout();
                var scroll = EnumerateVisuals(list).OfType<System.Windows.Controls.ScrollViewer>().First();
                Assert.Equal("system:newest", viewModel.Events[0].Id);
                Assert.Equal(0, scroll.VerticalOffset);
            }
            finally { window.Close(); }
        });
    }

    private static ActivityCenterViewModel CreateRealisticViewModel()
    {
        var service = new ActivityCenterService(new SensitiveDataFilter().Redact, new FixedTimeProvider(Now));

        service.AppendRange(new[]
        {
            new ActivityEvent(
                "event-1",
                Now.AddDays(-1).AddHours(-2),
                ActivityEventKind.System,
                ActivityRoleNames.System,
                ActivityEventState.Warning,
                ActivityEventSource.Native,
                "Retention run completed",
                "No active artifacts were removed."),
            new ActivityEvent(
                "event-2",
                Now.AddHours(-5),
                ActivityEventKind.Health,
                ActivityRoleNames.System,
                ActivityEventState.Failed,
                ActivityEventSource.Native,
                "Health scope route-gpt4o: QuarantinedAuto",
                "Probe must pass before the route returns to rotation."),
            new ActivityEvent(
                "event-3",
                Now.AddHours(-4),
                ActivityEventKind.Session,
                ActivityRoleNames.Architect,
                ActivityEventState.Completed,
                ActivityEventSource.Native,
                "Architecture document delivered",
                "Stage architecture advanced to implementation."),
            new ActivityEvent(
                "event-4",
                Now.AddHours(-3),
                ActivityEventKind.UserAction,
                ActivityRoleNames.System,
                ActivityEventState.Completed,
                ActivityEventSource.Native,
                "User approval recorded",
                "Operator approved the coding stage."),
            new ActivityEvent(
                "event-5",
                Now.AddHours(-2),
                ActivityEventKind.Execution,
                ActivityRoleNames.Coder,
                ActivityEventState.Running,
                ActivityEventSource.Native,
                "Implementation diff delivered",
                "Coder pushed the patch for review.",
                routeId: "route-opencode",
                diffText: SampleDiff,
                artifactName: "ActivityCenterViewModel.cs"),
            new ActivityEvent(
                "event-6",
                Now.AddHours(-2).AddMinutes(10),
                ActivityEventKind.Execution,
                ActivityRoleNames.Reviewer,
                ActivityEventState.Failed,
                ActivityEventSource.Native,
                "Reviewer turn failed",
                "Route mismatch was observed on the reviewer turn.",
                routeId: "route-star-cliproxy-agy"),
            new ActivityEvent(
                "event-7",
                Now.AddHours(-1),
                ActivityEventKind.Execution,
                ActivityRoleNames.TechLead,
                ActivityEventState.Warning,
                ActivityEventSource.Synthetic,
                "Synthetic reviewer escalation",
                "Synthetic fixture replayed an escalation turn."),
            new ActivityEvent(
                "event-8",
                Now.AddMinutes(-50),
                ActivityEventKind.Execution,
                ActivityRoleNames.Coder,
                ActivityEventState.Cancelled,
                ActivityEventSource.Native,
                "Coder turn cancelled",
                "The operator cancelled the run."),
            new ActivityEvent(
                "event-9",
                Now.AddMinutes(-40),
                ActivityEventKind.Execution,
                ActivityRoleNames.Reviewer,
                ActivityEventState.Completed,
                ActivityEventSource.Native,
                "Review verdict delivered",
                "Reviewer requested changes and supplied evidence.",
                routeId: "route-cursor"),
            new ActivityEvent(
                "event-10",
                Now.AddMinutes(-25),
                ActivityEventKind.Execution,
                ActivityRoleNames.Architect,
                ActivityEventState.Running,
                ActivityEventSource.Native,
                "Architecture revision",
                "Architect is revising the document after review."),
            new ActivityEvent(
                "event-11",
                Now.AddMinutes(-15),
                ActivityEventKind.Health,
                ActivityRoleNames.System,
                ActivityEventState.Completed,
                ActivityEventSource.Native,
                "Health scope route-opencode: Ready",
                "Probe verified recovery."),
            new ActivityEvent(
                "event-12",
                Now.AddMinutes(-5),
                ActivityEventKind.System,
                ActivityRoleNames.System,
                ActivityEventState.Completed,
                ActivityEventSource.Synthetic,
                "Synthetic activity stream replay",
                "The synthetic fixture replays the last recorded events.")
        });

        var viewModel = new ActivityCenterViewModel(
            service,
            timeProvider: new FixedTimeProvider(Now),
            clipboard: new SilentClipboard());

        viewModel.SelectedEvent = viewModel.Events.Single(item => item.Id == "event-5");

        return viewModel;
    }

    private static (Window Window, ActivityCenterView View) OpenView(
        ActivityCenterViewModel viewModel,
        double width,
        double height)
    {
        var view = new ActivityCenterView { DataContext = viewModel };

        var window = new Window
        {
            Width = width,
            Height = height,
            Content = view,
            ShowActivated = false,
            WindowStyle = WindowStyle.None
        };

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml",
                UriKind.Absolute)
        });

        window.Show();

        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();

        return (window, view);
    }

    private static string CaptureScreenshot(
        FrameworkElement root,
        string fileName,
        double dpi,
        int pixelWidth,
        int pixelHeight)
    {
        var renderBitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        renderBitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

        Directory.CreateDirectory(ScreenshotOutputDir);
        var screenshotPath = Path.Combine(ScreenshotOutputDir, fileName);

        ScreenshotFile.Save(encoder, screenshotPath);

        Assert.True(File.Exists(screenshotPath), $"Screenshot was not created at {screenshotPath}");

        return screenshotPath;
    }

    private static string[] ReadTexts(DependencyObject root) =>
        EnumerateVisuals(root)
            .Select(visual => visual switch
            {
                System.Windows.Controls.TextBlock block when !string.IsNullOrWhiteSpace(block.Text) => block.Text,
                System.Windows.Controls.TextBox box when !string.IsNullOrWhiteSpace(box.Text) => box.Text,
                _ => null
            })
            .Where(text => text is not null)
            .Select(text => text!)
            .ToArray();

    private static IEnumerable<DependencyObject> EnumerateVisuals(DependencyObject root)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            yield return child;

            foreach (var descendant in EnumerateVisuals(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class SilentClipboard : IClipboardService
    {
        public void SetText(string text)
        {
        }
    }
}
