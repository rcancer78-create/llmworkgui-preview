using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.ViewModels.Onboarding;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Consolidated Phase 11 visual acceptance (ROADMAP Phase 11 closing milestone): the compact Workflow
/// Studio surfaces (template editing / role matrix / document preview) and Activity Monitor surfaces
/// (graph schema / activity stream / inspector) are rendered through the shipped
/// <see cref="WorkflowConsoleView"/> and the unified shell overlays at 100%, 150% and 200% DPI in both
/// themes. The required artifacts <c>workflow_studio_consolidated_dark.png</c> and
/// <c>workflow_activity_monitor_consolidated.png</c> are persisted next to the onboarding screenshots.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class Phase11ConsolidatedVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConsolidatedStudio_RoleMatrixSurface_DarkTheme_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var library = CreateLibraryWithMonitorData();
        await library.Studio.RefreshTemplatesAsync();
        var console = new WorkflowConsolidatedViewModel(library);
        console.SelectMode(WorkflowConsoleMode.StudioRoles);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenConsole(console, 1320, 860);

            try
            {
                var texts = ReadTexts(view);

                // One compact switcher over studio and monitor surfaces.
                Assert.Contains("Workflow Console", texts);
                Assert.Contains("Редактирование шаблонов", texts);
                Assert.Contains("Матрица ролей", texts);
                Assert.Contains("Предпросмотр документов", texts);
                Assert.Contains("Схема графа", texts);
                Assert.Contains("Поток активности", texts);
                Assert.Contains("Инспектор", texts);

                // The role matrix of the built-in template is visible and honest.
                Assert.Contains("Матрица ролей выбранного шаблона", texts);
                Assert.True(library.Studio.HasRoleMatrix);
                Assert.Contains(
                    texts,
                    text => text.Contains("этапы:", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.Contains("Coordinator", StringComparison.Ordinal));

                // One-click import/export controls are present and accessible.
                Assert.Equal(
                    "Быстрый импорт пакета сценария",
                    AutomationProperties.GetName(FindByName<Button>(view, "ConsoleQuickImportButton")));
                Assert.Equal(
                    "Быстрый экспорт выбранной версии сценария",
                    AutomationProperties.GetName(FindByName<Button>(view, "ConsoleQuickExportButton")));

                AssertCardFits(view, "ConsoleCardHost");

                var screenshotPath = CaptureScreenshot(
                    view,
                    "workflow_studio_consolidated_dark.png",
                    dpi: 96,
                    pixelWidth: 1320,
                    pixelHeight: 860);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ConsolidatedMonitor_ActivityStreamSurface_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var library = CreateLibraryWithMonitorData();
        var console = new WorkflowConsolidatedViewModel(library);
        console.SelectMode(WorkflowConsoleMode.MonitorActivity);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenConsole(console, 1320, 860);

            try
            {
                var texts = ReadTexts(view);

                Assert.DoesNotContain("Activity stream", texts);
                Assert.Contains("Поток активности", texts);
                Assert.True(library.ActivityMonitor.IsActivityMode);
                Assert.Contains(
                    texts,
                    text => text.StartsWith("Запуск:", StringComparison.Ordinal));

                // The activity feed only carries observed events of the selected run.
                Assert.Contains(
                    texts,
                    text => text.Contains("run-consolidated", StringComparison.Ordinal));

                AssertCardFits(view, "ConsoleCardHost");

                var screenshotPath = CaptureScreenshot(
                    view,
                    "workflow_activity_monitor_consolidated.png",
                    dpi: 96,
                    pixelWidth: 1320,
                    pixelHeight: 860);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, 96.0)]
    [InlineData(AppTheme.Light, 96.0)]
    [InlineData(AppTheme.Dark, 144.0)]
    [InlineData(AppTheme.Light, 144.0)]
    [InlineData(AppTheme.Dark, 192.0)]
    [InlineData(AppTheme.Light, 192.0)]
    public void ConsolidatedConsole_RendersAllSurfacesWithoutClippingAcrossThemesAndDpi(
        AppTheme theme,
        double dpi)
    {
        StaTestRunner.EnsureApplication();

        var library = CreateLibraryWithMonitorData();
        var console = new WorkflowConsolidatedViewModel(library);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);

            var (window, view) = OpenConsole(console, 1320, 860);

            try
            {
                foreach (var mode in Enum.GetValues<WorkflowConsoleMode>())
                {
                    console.SelectMode(mode);
                    view.UpdateLayout();

                    var raster = new RenderTargetBitmap(
                        (int)Math.Ceiling(1320 * dpi / 96.0),
                        (int)Math.Ceiling(860 * dpi / 96.0),
                        dpi,
                        dpi,
                        PixelFormats.Pbgra32);

                    raster.Render(view);

                    Assert.True(raster.PixelWidth > 0 && raster.PixelHeight > 0);
                    AssertCardFits(view, "ConsoleCardHost");
                    Assert.False(double.IsNaN(view.DesiredSize.Width));
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UnifiedShell_HostsTheOnboardingAndWorkflowConsoleOverlays()
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        var library = provider.GetRequiredService<MainWindowViewModel>()
            .Screens.OfType<WorkflowLibraryViewModel>()
            .First();
        var onboarding = new OnboardingViewModel(
            provider.GetRequiredService<CliStatusViewModel>(),
            library,
            new InMemoryApplicationSettingsRepository(),
            new InMemoryProjectRepository(),
            new FixedTimeProvider(Now));
        var console = new WorkflowConsolidatedViewModel(library);

        var shell = new UnifiedWorkspaceShellViewModel(
            provider.GetRequiredService<MainWindowViewModel>(),
            new StubLayoutPersistenceService(),
            activityCenter: null,
            onboarding,
            console);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var view = new UnifiedWorkspaceShellView { DataContext = shell };

            var window = new Window
            {
                Width = 1280,
                Height = 800,
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

            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(0, 0, 1280, 800));
            view.UpdateLayout();

            try
            {
                Assert.Contains(shell.CommandPalette.Items, item => item.Id == "help.onboarding");
                Assert.Contains(shell.CommandPalette.Items, item => item.Id == "workflow.console");

                var onboardingButton = Assert.IsAssignableFrom<Button>(view.FindName("OpenOnboardingButton"));
                var consoleButton = Assert.IsAssignableFrom<Button>(view.FindName("OpenWorkflowConsoleButton"));
                Assert.NotNull(onboardingButton.Command);
                Assert.NotNull(consoleButton.Command);

                var onboardingOverlay = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("OnboardingOverlay"));
                var consoleOverlay = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("WorkflowConsoleOverlay"));

                Assert.False(shell.IsOnboardingOpen);
                Assert.False(shell.IsWorkflowConsoleOpen);

                shell.OpenOnboardingCommand.Execute(null);
                view.UpdateLayout();

                Assert.True(shell.IsOnboardingOpen);
                Assert.False(shell.IsWorkflowConsoleOpen);
                Assert.True(onboardingOverlay.IsVisible);

                shell.OpenWorkflowConsoleCommand.Execute(null);
                view.UpdateLayout();

                Assert.True(shell.IsWorkflowConsoleOpen);
                Assert.False(shell.IsOnboardingOpen);
                Assert.True(consoleOverlay.IsVisible);

                var closeConsole = Assert.IsAssignableFrom<Button>(view.FindName("CloseWorkflowConsoleButton"));
                Assert.NotNull(closeConsole.Command);
                shell.CloseWorkflowConsoleCommand.Execute(null);
                view.UpdateLayout();
                Assert.False(shell.IsWorkflowConsoleOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task UnifiedShell_CompactWidgetsNavigateToQuotasAndHealthCenter()
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        var library = provider.GetRequiredService<MainWindowViewModel>()
            .Screens.OfType<WorkflowLibraryViewModel>()
            .First();
        var onboarding = new OnboardingViewModel(
            provider.GetRequiredService<CliStatusViewModel>(),
            library,
            new InMemoryApplicationSettingsRepository(),
            new InMemoryProjectRepository(),
            new FixedTimeProvider(Now));

        var shell = new UnifiedWorkspaceShellViewModel(
            provider.GetRequiredService<MainWindowViewModel>(),
            new StubLayoutPersistenceService(),
            activityCenter: null,
            onboarding,
            new WorkflowConsolidatedViewModel(library));

        // First-run behavior: the wizard opens once and stays closed after completion.
        Assert.True(await shell.TryAutoOpenOnboardingAsync());
        Assert.True(shell.IsOnboardingOpen);

        await onboarding.CompleteAsync();
        shell.CloseOnboarding();

        Assert.False(await shell.TryAutoOpenOnboardingAsync());
        Assert.False(shell.IsOnboardingOpen);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var view = new UnifiedWorkspaceShellView { DataContext = shell };
            var window = new Window
            {
                Width = 1280,
                Height = 800,
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
            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(0, 0, 1280, 800));
            view.UpdateLayout();

            try
            {
                var quotasWidget = Assert.IsAssignableFrom<Button>(view.FindName("QuotasFooterWidget"));
                var healthWidget = Assert.IsAssignableFrom<Button>(view.FindName("HealthFooterWidget"));

                Assert.Equal(
                    "Открыть Quotas по клику на виджет квот",
                    AutomationProperties.GetName(quotasWidget));
                Assert.Equal(
                    "Открыть Health Center по клику на виджет здоровья",
                    AutomationProperties.GetName(healthWidget));

                quotasWidget.Command.Execute(quotasWidget.CommandParameter);
                Assert.Equal(ScreenId.Quotas, shell.ActiveScreenId);

                healthWidget.Command.Execute(healthWidget.CommandParameter);
                Assert.Equal(ScreenId.HealthCenter, shell.ActiveScreenId);

                // The header and the right inspector mirror the same click-through widgets.
                var quotasHeader = Assert.IsAssignableFrom<Button>(view.FindName("QuotasHeaderWidget"));
                var healthHeader = Assert.IsAssignableFrom<Button>(view.FindName("HealthHeaderWidget"));
                var quotasInspector = Assert.IsAssignableFrom<Button>(view.FindName("QuotasInspectorWidget"));
                var healthInspector = Assert.IsAssignableFrom<Button>(view.FindName("HealthInspectorWidget"));

                Assert.Equal("Квоты: открыть полный раздел Quotas", AutomationProperties.GetName(quotasHeader));
                Assert.Equal("Здоровье: открыть полный раздел Health Center", AutomationProperties.GetName(healthHeader));
                Assert.NotNull(quotasHeader.Command);
                Assert.NotNull(healthHeader.Command);
                Assert.NotNull(quotasInspector.Command);
                Assert.NotNull(healthInspector.Command);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static WorkflowLibraryViewModel CreateLibraryWithMonitorData()
    {
        var library = new WorkflowLibraryViewModel();
        var monitor = library.ActivityMonitor;

        monitor.LoadActiveVersion(new WorkflowVersion(
            "ver-consolidated",
            "pkg-consolidated",
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
            "run-consolidated",
            "project-consolidated",
            "pkg-consolidated",
            "ver-consolidated",
            "session-consolidated",
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
            "session-consolidated",
            WorkflowRoleParser.Parse(roleLabel),
            roleLabel,
            state,
            "route-star-cliproxy-codex",
            "route-star-cliproxy-codex",
            "native-consolidated",
            startedAt,
            lastActivityAt,
            endedAtUtc: null,
            EvidenceSourceKind.NativeProtocolEvent,
            isSynthetic: false);
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

    private static void AssertCardFits(FrameworkElement root, string cardName)
    {
        var card = Assert.IsAssignableFrom<Border>(root.FindName(cardName));

        Assert.True(card.ActualWidth > 0, "The console card must be laid out.");
        Assert.True(card.ActualHeight > 0, "The console card must be laid out.");
        Assert.True(
            card.ActualWidth <= root.ActualWidth,
            "The console card must not be clipped horizontally.");
        Assert.True(
            card.ActualHeight <= root.ActualHeight,
            "The console card must not be clipped vertically.");
    }

    private static T FindByName<T>(FrameworkElement root, string name)
        where T : FrameworkElement =>
        Assert.IsAssignableFrom<T>(root.FindName(name));

    private static string CaptureScreenshot(
        FrameworkElement root,
        string fileName,
        double dpi,
        int pixelWidth,
        int pixelHeight)
    {
        var renderBitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi,
            dpi,
            PixelFormats.Pbgra32);
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

    private sealed class StubLayoutPersistenceService : ILayoutPersistenceService
    {
        public ShellLayoutState State { get; set; } = new();

        public ShellLayoutState Load() => State;

        public void Save(ShellLayoutState state) => State = state;
    }
}
