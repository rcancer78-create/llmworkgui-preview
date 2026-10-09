using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
/// QuickViewer-style WPF UI scenarios of the Phase 10D Activity Monitor. The shipped
/// <see cref="WorkflowActivityMonitorView"/> is rendered over realistic run data: declared version
/// roles, observed transitions, parallel read-only reviewers, proven star-cliproxy Codex/AGY routes and
/// deliberately incomplete fields. Screenshots are persisted for the schema and the details drawer.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class WorkflowActivityMonitorVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly string LongWorkText = string.Join(
        " ",
        Enumerable.Repeat("Ревизия подтверждает полный объём работы без обрезания текста.", 40));

    [Theory]
    [InlineData(WorkflowMonitorPanelMode.Inspector)]
    [InlineData(WorkflowMonitorPanelMode.Schema)]
    public void InspectorDrawerUsesTheWholeRenderedSurfaceWhileSchemaKeepsASideDrawer(WorkflowMonitorPanelMode mode)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var viewModel = CreateRealisticViewModel();
            viewModel.SelectedNode = viewModel.Nodes.Single(node => node.RoleId == "Reviewer");
            viewModel.SelectPanelMode(mode);
            var (window, view) = OpenView(viewModel);
            try
            {
                var style = (Style)view.FindResource("MonitorDrawerPanel");
                var drawer = Assert.Single(FindVisualDescendants<Border>(view)
                    .Where(border => ReferenceEquals(border.Style, style)));
                var surface = Assert.IsType<Grid>(VisualTreeHelper.GetParent(drawer));
                Assert.True(drawer.IsVisible);
                Assert.True(drawer.ActualHeight > 0);
                var bounds = drawer.TransformToAncestor(surface).TransformBounds(
                    new Rect(0, 0, drawer.ActualWidth, drawer.ActualHeight));
                if (mode == WorkflowMonitorPanelMode.Inspector)
                {
                    Assert.InRange(Math.Abs(bounds.X), 0d, 1d);
                    Assert.InRange(Math.Abs(bounds.Width - surface.ActualWidth), 0d, 1d);
                }
                else
                {
                    Assert.True(bounds.X > 0);
                    Assert.InRange(bounds.Width, 389d, 391d);
                    Assert.True(bounds.Right <= surface.ActualWidth + 1);
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void WorkflowActivityMonitor_ShowsTheRoleSchemaWithStatusesAndStarCliProxyRoutes_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = CreateRealisticViewModel();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel);

            try
            {
                var texts = ReadTexts(view);

                // The schema is built from the declared roles of the active version plus observed roles.
                Assert.Contains("Workflow Activity Monitor", texts);
                Assert.Contains("Architect", texts);
                Assert.Contains("Implementer", texts);
                Assert.Contains("Reviewer", texts);
                Assert.Contains("UiReviewer", texts);
                Assert.Contains("Tester", texts);
                Assert.Contains("Current", texts);

                // All five required statuses are rendered from the actual run/projection evidence.
                Assert.Contains("Работает", texts);
                Assert.Contains("Завис", texts);
                Assert.Contains("Остановлен", texts);
                Assert.Contains("Готово", texts);
                Assert.Contains("Ждёт", texts);

                // Parallel read-only reviewers are indicated on the working node.
                Assert.Contains(
                    texts,
                    text => text.Contains("2 parallel read-only turns", StringComparison.Ordinal));

                // Codex and AGY are visible as proven star-cliproxy routes, not as fixed personas.
                Assert.Contains(
                    texts,
                    text => text.Contains(
                        "Codex via star-cliproxy (route-star-cliproxy-codex)",
                        StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.Contains(
                        "AGY via star-cliproxy (route-star-cliproxy-agy)",
                        StringComparison.Ordinal));
                Assert.Contains(viewModel.RouteProvenanceNote, texts);

                // A route the domain did not observe is shown as "Not reported".
                Assert.Contains(viewModel.MissingFieldPlaceholder, texts);

                // Model and account appear only because the observed session binding supplied them.
                Assert.Contains(
                    texts,
                    text => text.Contains(
                        "Model: gpt-5-codex · Account: account-agy",
                        StringComparison.Ordinal));

                var screenshotPath = CaptureScreenshot(view, "workflow_activity_monitor_schema.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WorkflowActivityMonitor_DrawerShowsSourceTimestampsAndFullWorkTextWithoutHidingTheSchema_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = CreateRealisticViewModel();
        var reviewer = viewModel.Nodes.Single(node => node.RoleId == "Reviewer");
        viewModel.SelectedNode = reviewer;

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel);

            try
            {
                var texts = ReadTexts(view);

                // The drawer reports the observed source role and both timestamps.
                Assert.True(viewModel.IsDrawerOpen);
                Assert.Contains("← от Implementer", texts);
                Assert.Contains(
                    texts,
                    text => text.StartsWith("Получено: ", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.StartsWith("Изменено: ", StringComparison.Ordinal));

                // The full, untruncated work text is rendered with wrapping.
                Assert.Contains(texts, text => text.Contains(LongWorkText, StringComparison.Ordinal));

                var workTextBox = FindVisualDescendants<TextBox>(view)
                    .Single(box => string.Equals(box.Text, viewModel.Drawer.WorkText, StringComparison.Ordinal));
                Assert.Equal(TextWrapping.Wrap, workTextBox.TextWrapping);
                Assert.True(workTextBox.IsReadOnly, "The work text is displayed read-only.");

                // The schema stays visible next to the open drawer.
                Assert.Contains("Architect", texts);
                Assert.Contains("Работает", texts);

                var screenshotPath = CaptureScreenshot(view, "workflow_activity_monitor_drawer.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static WorkflowActivityMonitorViewModel CreateRealisticViewModel()
    {
        var viewModel = new WorkflowActivityMonitorViewModel(
            new WorkflowRunTimelineService(),
            new RoleTransferEvidenceProjector(),
            new MonitorTimeProvider(Now));

        viewModel.LoadActiveVersion(new WorkflowVersion(
            "ver-1",
            "pkg-1",
            1,
            Hash('a'),
            Hash('b'),
            WorkflowSourceType.ZipArchive,
            entrypointsJson: null,
            declaredRolesJson: JsonSerializer.Serialize(new[]
            {
                "Coordinator",
                "Architect",
                "Implementer",
                "Reviewer",
                "UiReviewer",
                "Tester",
                "Approver"
            }),
            bindingsJson: null,
            compatibilityReportJson: null,
            creationMetadataJson: null,
            Now.AddDays(-1),
            activatedAtUtc: null));

        var run = CreateRunWithTransitions();

        run.RecordReviewerVerdict(new ReviewerVerdictRecord(
            "Reviewer",
            "route-star-cliproxy-agy",
            Hash('d'),
            WorkflowReviewVerdict.RequestChanges,
            LongWorkText,
            Now.AddMinutes(-6),
            "execution-reviewer",
            "stage-review",
            "artifact-review"));

        viewModel.LoadRun(run, new[]
        {
            Projection(
                "exec-coordinator",
                "Coordinator",
                ExecutionState.Cancelled,
                Now.AddMinutes(-140),
                Now.AddMinutes(-135),
                Now.AddMinutes(-135)),
            Projection(
                "exec-architect",
                "Architect",
                ExecutionState.Succeeded,
                Now.AddMinutes(-120),
                Now.AddMinutes(-110),
                Now.AddMinutes(-110),
                requestedRouteId: "route-star-cliproxy-codex",
                observedRouteId: "route-star-cliproxy-codex",
                nativeSessionId: "native-codex"),
            Projection(
                "exec-implementer",
                "Implementer",
                ExecutionState.Succeeded,
                Now.AddMinutes(-100),
                Now.AddMinutes(-80),
                Now.AddMinutes(-80),
                requestedRouteId: "route-opencode",
                observedRouteId: "route-opencode",
                nativeSessionId: "native-opencode"),
            Projection(
                "exec-reviewer-1",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-30),
                Now.AddMinutes(-10),
                requestedRouteId: "route-star-cliproxy-agy",
                observedRouteId: "route-star-cliproxy-agy",
                nativeSessionId: "native-agy",
                observedModelId: "gpt-5-codex",
                observedAccountId: "account-agy",
                observedExecutionMode: "review",
                isReadOnlyTurn: true),
            Projection(
                "exec-reviewer-2",
                "Reviewer",
                ExecutionState.Running,
                Now.AddMinutes(-28),
                Now.AddMinutes(-8),
                requestedRouteId: "route-star-cliproxy-agy",
                observedRouteId: null,
                nativeSessionId: null,
                observedModelId: "gpt-5-codex",
                observedAccountId: "account-agy",
                observedExecutionMode: "plan",
                isReadOnlyTurn: true),
            Projection(
                "exec-ui-reviewer",
                "UiReviewer",
                ExecutionState.Ambiguous,
                Now.AddMinutes(-25),
                Now.AddMinutes(-20),
                Now.AddMinutes(-20),
                requestedRouteId: "route-cursor",
                observedRouteId: "route-cursor",
                nativeSessionId: "native-cursor")
        });

        return viewModel;
    }

    private static WorkflowRun CreateRunWithTransitions()
    {
        var startedAt = Now.AddHours(-2);
        var architecture = CreateStage("stage-architecture", "Architect", "stage-implementation");
        var implementation = CreateStage("stage-implementation", "Implementer", "stage-review");
        var review = CreateStage("stage-review", "Reviewer", nextStageId: null);

        var run = WorkflowRun.Start(
            "run-1",
            "project-1",
            "pkg-1",
            "ver-1",
            "session-1",
            architecture,
            startedAt);

        run.AdvanceTo(
            architecture,
            implementation,
            "Architecture document delivered.",
            startedAt.AddMinutes(10));
        run.AdvanceTo(
            implementation,
            review,
            "Implementation diff delivered for review.",
            startedAt.AddMinutes(20));

        return run;
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
        DateTimeOffset lastActivityAt,
        DateTimeOffset? endedAt = null,
        string? requestedRouteId = null,
        string? observedRouteId = null,
        string? nativeSessionId = null,
        string? observedModelId = null,
        string? observedAccountId = null,
        string? observedExecutionMode = null,
        bool isReadOnlyTurn = false)
    {
        return new ObservableRunProjection(
            executionId,
            "session-1",
            WorkflowRoleParser.Parse(roleLabel),
            roleLabel,
            state,
            requestedRouteId ?? "route-opencode",
            observedRouteId,
            nativeSessionId,
            startedAt,
            lastActivityAt,
            endedAt,
            EvidenceSourceKind.NativeProtocolEvent,
            isSynthetic: false,
            observedModelId,
            observedAccountId,
            observedExecutionMode,
            isReadOnlyTurn);
    }

    private static (Window Window, WorkflowActivityMonitorView View) OpenView(
        WorkflowActivityMonitorViewModel viewModel)
    {
        var view = new WorkflowActivityMonitorView { DataContext = viewModel };

        var window = new Window
        {
            Width = 1280,
            Height = 760,
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

        view.Measure(new Size(1280, 760));
        view.Arrange(new Rect(0, 0, 1280, 760));
        view.UpdateLayout();

        return (window, view);
    }

    private static string CaptureScreenshot(FrameworkElement root, string fileName)
    {
        var width = (int)Math.Max(1280, root.ActualWidth);
        var height = (int)Math.Max(760, root.ActualHeight);

        var renderBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
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
                TextBlock block when !string.IsNullOrWhiteSpace(block.Text) => block.Text,
                TextBox box when !string.IsNullOrWhiteSpace(box.Text) => box.Text,
                _ => null
            })
            .Where(text => text is not null)
            .Select(text => text!)
            .ToArray();

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

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

    private static string Hash(char character) => "sha256:" + new string(character, 64);

    private sealed class MonitorTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public MonitorTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
