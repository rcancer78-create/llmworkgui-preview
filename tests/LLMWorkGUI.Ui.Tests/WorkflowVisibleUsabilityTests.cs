using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Evidence for the Workflow Console on the ordinary shipped window (ROADMAP Phase 11 usability).
/// <para>
/// The console used to be a fixed 1260x800 card, so at the shipped 1280x800 window its footer - the run
/// notice and the refusal line - and the right edge of the monitor panel were cut off. What is pinned here
/// is that the card is bounded by the space the overlay actually has: the run notice, the refusal line and
/// the footer stay inside the window, the Activity Monitor's schema and its details drawer stay readable
/// beside each other, and neither a resize nor a high-DPI raster cuts a control off or routes text out of
/// view.
/// </para>
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class WorkflowVisibleUsabilityTests
{
    /// <summary>The window the product ships and the task scenario is stated in.</summary>
    private const double ShippedWidth = 1280;

    private const double ShippedHeight = 800;

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AppTheme.Dark, 96.0)]
    [InlineData(AppTheme.Light, 96.0)]
    [InlineData(AppTheme.Dark, 192.0)]
    public void TheShippedWorkflowConsoleFitsTheShippedWindowAndKeepsItsNoticeAndFooter(
        AppTheme theme,
        double dpi)
    {
        StaTestRunner.EnsureApplication();

        var library = CreateLibraryWithMonitorData();
        var console = new WorkflowConsolidatedViewModel(library);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            console.SelectMode(WorkflowConsoleMode.MonitorSchema);

            var (window, view) = OpenConsole(console, ShippedWidth, ShippedHeight);

            try
            {
                var card = Assert.IsAssignableFrom<Border>(view.FindName("ConsoleCardHost"));
                var footer = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("ConsoleFooter"));

                // The card is bounded by the overlay, not by a fixed size, in both directions.
                Assert.True(card.ActualWidth > 0 && card.ActualHeight > 0, "The console card must be laid out.");
                Assert.True(
                    card.ActualWidth <= view.ActualWidth,
                    $"The console card is {card.ActualWidth} wide inside a {view.ActualWidth} window.");
                Assert.True(
                    card.ActualHeight <= view.ActualHeight,
                    $"The console card is {card.ActualHeight} tall inside a {view.ActualHeight} window.");
                Assert.True(
                    card.ActualWidth < ShippedWidth,
                    "A card that fills the whole window is not the shipped inset card.");

                // The footer - the run notice, the named refusal and the status line - is inside the window
                // and inside the card, not routed past its bottom edge.
                Assert.True(footer.ActualHeight > 0, "The console footer must be laid out at a real height.");
                footer.BringIntoView();
                view.UpdateLayout();
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                Assert.True(IsInside(footer, card), "The console footer must be reachable by scrolling the card.");
                Assert.True(IsInside(footer, view), "The console footer must be inside the window.");

                // The footer is still the product's own run surface, not decoration.
                var footerLines = EnumerateVisuals(footer)
                    .OfType<TextBlock>()
                    .Select(text => (Binding: text.GetBindingExpression(TextBlock.TextProperty), text))
                    .Where(entry => entry.Binding is not null)
                    .Select(entry => entry.Binding!.ParentBinding.Path.Path)
                    .ToArray();

                Assert.Contains("Library.ObservedRunDisplay", footerLines);
                Assert.Contains("Library.ObservedRunNotice", footerLines);
                Assert.Contains("Library.Blocker", footerLines);
                Assert.Contains("Library.StatusMessage", footerLines);

                // The two lines this state really has are readable inside the window.
                var notice = Assert.Single(
                    EnumerateVisuals(footer)
                        .OfType<TextBlock>()
                        .Where(text => text.Text == library.ObservedRunNotice)
                        .ToArray());

                Assert.True(notice.ActualHeight > 0, "The run notice must be laid out at a real height.");
                Assert.True(IsInside(notice, view), "The run notice must be inside the window.");

                var raster = new RenderTargetBitmap(
                    (int)Math.Ceiling(ShippedWidth * dpi / 96.0),
                    (int)Math.Ceiling(ShippedHeight * dpi / 96.0),
                    dpi,
                    dpi,
                    PixelFormats.Pbgra32);

                raster.Render(view);

                Assert.True(raster.PixelWidth > 0 && raster.PixelHeight > 0);
                Assert.False(double.IsNaN(view.DesiredSize.Width));

                var screenshotPath = CaptureScreenshot(
                    view,
                    theme,
                    dpi,
                    $"workflow_console_fits_1280x800_{theme.ToString().ToLowerInvariant()}.png");

                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The schema and the details drawer are two panels of one monitor surface. At the shipped window they
    /// have to be readable side by side, with the drawer's own text on screen, not stacked, clipped or pushed
    /// past the window.
    /// </summary>
    [Fact]
    public void TheShippedWorkflowConsoleKeepsTheMonitorSchemaAndDrawerBesideEachOther()
    {
        StaTestRunner.EnsureApplication();

        var library = CreateLibraryWithMonitorData();
        var console = new WorkflowConsolidatedViewModel(library);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            console.SelectMode(WorkflowConsoleMode.MonitorSchema);

            // Opening the drawer on a node is what an operator does to read one role; the schema stays.
            library.ActivityMonitor.SelectedNode = library.ActivityMonitor.Nodes.First();

            var (window, view) = OpenConsole(console, ShippedWidth, ShippedHeight);

            try
            {
                var card = Assert.IsAssignableFrom<Border>(view.FindName("ConsoleCardHost"));
                var monitor = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("ConsolidatedMonitorView"));

                Assert.True(library.ActivityMonitor.IsSchemaMode);
                Assert.True(library.ActivityMonitor.IsDrawerPanelVisible);

                // The drawer is the panel the monitor styles as its own; the schema is the sibling panel it
                // sits next to in the same three-column row. Locating them that way avoids mistaking a
                // panel's header strip for the panel.
                var drawerStyle = Assert.IsAssignableFrom<Style>(monitor.TryFindResource("MonitorDrawerPanel"));
                var drawer = Assert.Single(
                    EnumerateVisuals(card)
                        .OfType<Border>()
                        .Where(border => ReferenceEquals(border.Style, drawerStyle))
                        .ToArray());

                Assert.IsType<Grid>(drawer.Parent);
                var schema = Assert.Single(
                    ((Grid)drawer.Parent).Children
                        .OfType<Border>()
                        .Where(border => !ReferenceEquals(border, drawer) && border.ActualWidth > 0)
                        .ToArray());

                Assert.True(drawer.ActualWidth > 0 && drawer.ActualHeight > 0, "The drawer must be laid out.");
                Assert.True(schema.ActualWidth > 0 && schema.ActualHeight > 0, "The schema must be laid out.");
                drawer.BringIntoView();
                view.UpdateLayout();
                var viewport = Assert.IsType<ScrollViewer>(view.FindName("ConsoleViewport"));
                viewport.ScrollToVerticalOffset(viewport.VerticalOffset + drawer.TransformToAncestor(viewport).Transform(new Point()).Y);
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                view.UpdateLayout();
                Assert.True(IsInside(drawer, view), $"Drawer {drawer.TransformToAncestor(view).TransformBounds(new Rect(drawer.RenderSize))}; window {view.RenderSize}; viewport {viewport.RenderSize}; offset {viewport.VerticalOffset}/{viewport.ScrollableHeight}");
                Assert.True(IsInside(schema, view), "The schema must be inside the shipped window.");

                // The drawer's own content is on screen: the role it describes and the honest "Not reported"
                // about everything this fixture never stored.
                var drawerTexts = EnumerateVisuals(drawer)
                    .OfType<TextBlock>()
                    .Select(text => text.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToArray();

                Assert.Contains(drawerTexts, text => text.Contains("Model:", StringComparison.Ordinal));
                Assert.Contains(drawerTexts, text => text.Contains("Account:", StringComparison.Ordinal));
                Assert.Contains(drawerTexts, text => text.Contains("Not reported", StringComparison.Ordinal));
                Assert.Contains(
                    EnumerateVisuals(schema).OfType<TextBlock>().Select(text => text.Text),
                    text => text != null && text.Contains("СХЕМА ГРАФА", StringComparison.Ordinal));
                Assert.Contains(
                    EnumerateVisuals(schema).OfType<TextBlock>().Select(text => text.Text),
                    text => text != null && text.Contains("Reviewer", StringComparison.Ordinal));

                // Beside, not below: the drawer starts to the right of the schema and is not cut off by the
                // right edge of the card.
                var schemaOrigin = schema.TransformToAncestor(card).Transform(new Point(0, 0));
                var drawerOrigin = drawer.TransformToAncestor(card).Transform(new Point(0, 0));

                Assert.True(
                    drawerOrigin.X >= schemaOrigin.X + schema.ActualWidth - 1,
                    "The drawer must start to the right of the schema, not under it.");
                Assert.True(
                    drawerOrigin.X + drawer.ActualWidth <= card.ActualWidth + 1,
                    "The drawer must not be cut off by the right edge of the console card.");

                var screenshotPath = CaptureScreenshot(
                    view,
                    AppTheme.Dark,
                    96,
                    "workflow_console_monitor_drawer_1280x800_dark.png");

                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A narrower or shorter window and a doubled raster scale must narrow the console, never cut a control
    /// off: every mode switcher and both run actions stay inside the card in every surface.
    /// </summary>
    [Theory]
    [InlineData(1280.0, 800.0, 96.0)]
    [InlineData(1024.0, 700.0, 96.0)]
    [InlineData(900.0, 560.0, 96.0)]
    [InlineData(1280.0, 800.0, 192.0)]
    public void TheShippedWorkflowConsoleNarrowsInsteadOfCuttingControls(
        double width,
        double height,
        double dpi)
    {
        StaTestRunner.EnsureApplication();

        var library = CreateLibraryWithMonitorData();
        var console = new WorkflowConsolidatedViewModel(library);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenConsole(console, width, height);

            try
            {
                foreach (var mode in Enum.GetValues<WorkflowConsoleMode>())
                {
                    console.SelectMode(mode);
                    view.UpdateLayout();

                    var card = Assert.IsAssignableFrom<Border>(view.FindName("ConsoleCardHost"));

                    Assert.True(
                        card.ActualWidth <= view.ActualWidth + 1,
                        $"{mode}: the card is {card.ActualWidth} wide inside {view.ActualWidth}.");
                    Assert.True(
                        card.ActualHeight <= view.ActualHeight + 1,
                        $"{mode}: the card is {card.ActualHeight} tall inside {view.ActualHeight}.");

                    foreach (var name in new[]
                    {
                        "ConsoleTemplatesButton",
                        "ConsoleRolesButton",
                        "ConsoleDocumentsButton",
                        "ConsoleSchemaButton",
                        "ConsoleActivityButton",
                        "ConsoleInspectorButton",
                        "ConsoleStartRunButton",
                        "ConsoleAdvanceRunButton",
                        "ConsoleQuickImportButton",
                        "ConsoleQuickExportButton",
                        "ConsoleFooter"
                    })
                    {
                        var control = Assert.IsAssignableFrom<FrameworkElement>(view.FindName(name));

                        control.BringIntoView();
                        view.UpdateLayout();
                        view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                        view.UpdateLayout();

                        Assert.True(
                            control.ActualWidth > 0 && control.ActualHeight > 0,
                            $"{mode}: '{name}' has no size.");
                        Assert.True(IsInside(control, card), $"{mode}: '{name}' is unreachable by scrolling the console card.");
                    }

                    var raster = new RenderTargetBitmap(
                        (int)Math.Ceiling(width * dpi / 96.0),
                        (int)Math.Ceiling(height * dpi / 96.0),
                        dpi,
                        dpi,
                        PixelFormats.Pbgra32);

                    raster.Render(view);

                    Assert.True(raster.PixelWidth > 0 && raster.PixelHeight > 0);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static bool IsInside(FrameworkElement control, FrameworkElement ancestor)    {
        var origin = control.TransformToAncestor(ancestor).Transform(new Point(0, 0));

        const double Slack = 1;

        return origin.X >= -Slack
            && origin.Y >= -Slack
            && origin.X + control.ActualWidth <= ancestor.ActualWidth + Slack
            && origin.Y + control.ActualHeight <= ancestor.ActualHeight + Slack;
    }

    private static IEnumerable<DependencyObject> EnumerateVisuals(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            yield return child;

            foreach (var descendant in EnumerateVisuals(child))
            {
                yield return descendant;
            }
        }
    }

    private static (Window Window, WorkflowConsoleView View) OpenConsole(
        WorkflowConsolidatedViewModel viewModel,
        double width,
        double height)
    {
        var view = new WorkflowConsoleView { DataContext = viewModel };

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
        AppTheme theme,
        double dpi,
        string fileName)
    {
        var renderBitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(ShippedWidth * dpi / 96.0),
            (int)Math.Ceiling(ShippedHeight * dpi / 96.0),
            dpi,
            dpi,
            PixelFormats.Pbgra32);

        renderBitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

        var screenshotPath = Path.Combine(ScreenshotFile.OutputDirectory, fileName);

        ScreenshotFile.Save(encoder, screenshotPath);

        Assert.True(File.Exists(screenshotPath), $"Screenshot was not created at {screenshotPath}");

        return screenshotPath;
    }

    /// <summary>
    /// A run with observed nodes, built from the same projection types the monitor reads in production. No
    /// verdict, session or route is invented: the drawer is expected to say "Not reported" about everything
    /// this fixture never stored.
    /// </summary>
    private static WorkflowLibraryViewModel CreateLibraryWithMonitorData()
    {
        var library = new WorkflowLibraryViewModel();
        var monitor = library.ActivityMonitor;

        monitor.LoadActiveVersion(new WorkflowVersion(
            "ver-visible-usability",
            "pkg-visible-usability",
            1,
            "sha256:" + new string('a', 64),
            "sha256:" + new string('b', 64),
            WorkflowSourceType.ZipArchive,
            entrypointsJson: null,
            declaredRolesJson: JsonSerializer.Serialize(new[] { "Architect", "Implementer", "Reviewer" }),
            bindingsJson: null,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            Now.AddDays(-1),
            activatedAtUtc: null));

        var startedAt = Now.AddHours(-1);
        var architecture = CreateStage("stage-architecture", "Architect", "stage-implementation");
        var implementation = CreateStage("stage-implementation", "Implementer", "stage-review");
        var review = CreateStage("stage-review", "Reviewer", nextStageId: null);

        var run = WorkflowRun.Start(
            "run-visible-usability",
            "project-visible-usability",
            "pkg-visible-usability",
            "ver-visible-usability",
            "session-visible-usability",
            architecture,
            startedAt);

        run.AdvanceTo(architecture, implementation, "Architecture document delivered.", startedAt.AddMinutes(10));
        run.AdvanceTo(implementation, review, "Implementation diff delivered for review.", startedAt.AddMinutes(20));

        monitor.LoadRun(run, new[]
        {
            Projection("exec-architect", "Architect", ExecutionState.Succeeded, Now.AddMinutes(-50), Now.AddMinutes(-40)),
            Projection("exec-implementer", "Implementer", ExecutionState.Succeeded, Now.AddMinutes(-35), Now.AddMinutes(-20)),
            Projection("exec-reviewer", "Reviewer", ExecutionState.Running, Now.AddMinutes(-10), Now.AddMinutes(-2))
        });

        return library;
    }

    private static WorkflowStageDefinition CreateStage(string stageId, string role, string? nextStageId) =>
        new(
            stageId,
            $"Display {stageId}",
            role,
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null,
            nextStageId,
            failureStageId: null);

    private static ObservableRunProjection Projection(
        string executionId,
        string roleLabel,
        ExecutionState state,
        DateTimeOffset startedAt,
        DateTimeOffset lastActivityAt)
    {
        return new ObservableRunProjection(
            executionId,
            "session-visible-usability",
            WorkflowRoleParser.Parse(roleLabel),
            roleLabel,
            state,
            "route-star-cliproxy-codex",
            "route-star-cliproxy-codex",
            "native-visible-usability",
            startedAt,
            lastActivityAt,
            endedAtUtc: null,
            EvidenceSourceKind.NativeProtocolEvent,
            isSynthetic: false);
    }
}
