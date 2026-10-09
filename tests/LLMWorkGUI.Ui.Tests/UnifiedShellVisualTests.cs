using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// QuickViewer-style WPF scenarios of the Phase 11 unified workspace shell. The shipped
/// <see cref="UnifiedWorkspaceShellView"/> is rendered over the real screen catalog in Dark and Light
/// themes, at 100% and 200% DPI, and the command palette is rendered with a live filter. Screenshots are
/// persisted to the Screenshots folder.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class UnifiedShellVisualTests
{
    [Fact]
    public void CompactInspectorDoesNotOverwriteDesktopPreference()
    {
        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);
        shell.SetViewportWidth(900);
        Assert.False(shell.IsInspectorDisplayed);
        Assert.True(shell.IsRightPaneVisible);
        Assert.Equal(0, shell.RightPaneGridLength.Value);
        shell.ToggleRightPane();
        Assert.True(shell.IsInspectorDisplayed);
        shell.ToggleRightPane();
        shell.SetViewportWidth(1280);
        Assert.True(shell.IsInspectorDisplayed);
    }

    [Fact]
    public void ActivityOverlayHasReachableCloseButton()
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);
        StaTestRunner.Run(() =>
        {
            var (window, view) = OpenShell(shell, 900, 560);
            try
            {
                shell.OpenActivityCenter(); view.UpdateLayout();
                var close = Assert.IsType<Button>(view.FindName("CloseActivityCenterButton"));
                Assert.True(close.IsVisible);
                Assert.True(close.IsEnabled);
                close.Command.Execute(close.CommandParameter);
                Assert.False(shell.IsActivityCenterOpen);
            }
            finally { window.Close(); }
        });
    }
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    [Fact]
    public void ShellActionIconsKeepTheirFontUnderApplicationTypography()
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);
        StaTestRunner.Run(() =>
        {
            var resources = System.Windows.Application.Current.Resources;
            var previousStyle = resources[typeof(TextBlock)];
            var typography = new Style(typeof(TextBlock));
            typography.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI")));
            resources[typeof(TextBlock)] = typography;
            Window? window = null;
            try
            {
                var opened = OpenShell(shell, 1280, 800);
                window = opened.Item1;
                foreach (var name in new[] { "ToggleLeftPaneButton", "OpenWorkflowConsoleButton",
                    "OpenActivityCenterButton", "OpenOnboardingButton", "ToggleRightPaneButton" })
                {
                    var button = Assert.IsType<Button>(opened.Item2.FindName(name));
                    var glyph = Assert.Single(FindVisualDescendants<TextBlock>(button));
                    Assert.Equal("Segoe MDL2 Assets", glyph.FontFamily.Source);
                }
            }
            finally
            {
                window?.Close();
                if (previousStyle is null) resources.Remove(typeof(TextBlock));
                else resources[typeof(TextBlock)] = previousStyle;
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task ProjectCatalogRendersSavedProjectsInTheShippedShell(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        await provider.GetRequiredService<IProjectRepository>().UpsertAsync(new Project(
            "sample-project", "Локальный проект", @"D:\Projects\workspace", "main", false, true,
            null, null, DataClassification.Restricted));
        var shell = CreateShell(provider);
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, 1280, 800);
            try
            {
                shell.NavigateTo(ScreenId.Projects);
                view.UpdateLayout();
                Assert.Contains("Локальный проект", ReadTexts(view));
                Assert.Contains("Ограниченные данные", ReadTexts(view));
                CaptureScreenshot(view, $"modern_projects_{theme}.png", 96, 1280, 800);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, 900, 560)]
    [InlineData(AppTheme.Light, 900, 560)]
    [InlineData(AppTheme.Dark, 1280, 800)]
    [InlineData(AppTheme.Light, 1280, 800)]
    public void ShellToolbarAndPanesStayInsideWindow(AppTheme theme, double width, double height)
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenShell(shell, width, height);
            try
            {
                foreach (var name in new[] { "OpenCommandPaletteButton", "ToggleLeftPaneButton",
                    "ToggleRightPaneButton", "OpenWorkflowConsoleButton", "OpenActivityCenterButton", "OpenOnboardingButton", "OpenHelpGuideButton",
                    "DarkThemeButton", "LightThemeButton", "SystemThemeButton", "LeftPane", "CenterPane", "RightPane" })
                {
                    var element = Assert.IsAssignableFrom<FrameworkElement>(view.FindName(name));
                    var bounds = element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
                    Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= view.ActualWidth + 1 && bounds.Bottom <= view.ActualHeight + 1,
                        $"{name}: {bounds} outside {view.ActualWidth}x{view.ActualHeight}");
                }
                CaptureScreenshot(view, $"modern_shell_{theme}_{width}.png", 96, (int)width, (int)height);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void UnifiedShell_ThreePaneLayout_DarkTheme_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var texts = ReadTexts(view);

                // The three panes and their headers are visible.
                Assert.Contains("Навигация", texts);
                Assert.Contains("Инспектор / контекст", texts);
                Assert.Contains("Рабочая область", texts);
                Assert.Contains("Ctrl+1", texts);

                // The compact widgets and the activity center are part of the shell.
                Assert.Contains("Краткий статус", texts);
                Assert.Contains("Квоты", texts);
                Assert.Contains("Здоровье предохранителей", texts);
                Assert.Contains("Обнаружение CLI", texts);
                Assert.Contains("Центр активности", texts);

                var left = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("LeftPane"));
                var center = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("CenterPane"));
                var right = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("RightPane"));

                Assert.True(left.ActualWidth > 0, "The left pane must be laid out.");
                Assert.True(center.ActualWidth > 0, "The center pane must be laid out.");
                Assert.True(right.ActualWidth > 0, "The right pane must be laid out.");

                var screenshotPath = CaptureScreenshot(
                    view,
                    "shell_three_pane_layout.png",
                    dpi: 96,
                    pixelWidth: 1280,
                    pixelHeight: 800);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UnifiedShell_CommandPalette_FiltersCommandsAndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);
        shell.CommandPalette.Open();
        shell.CommandPalette.SearchQuery = "пакета";

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("Палитра команд", texts);
                Assert.Contains("пакета", texts);

                // Virtualization may leave lower rows unrealized; assert the filtered model for both matches.
                Assert.Contains(shell.CommandPalette.FilteredItems, item => item.Title == "Импорт пакета сценария");
                Assert.Contains(shell.CommandPalette.FilteredItems, item => item.Title == "Экспорт пакета сценария");
                Assert.Contains(shell.CommandPalette.Items, item => item.Title == "Открыть Workflow Studio");
                Assert.Contains(shell.CommandPalette.Items, item => item.Title == "Открыть Activity Monitor");
                Assert.Contains("Процессы", texts);
                Assert.DoesNotContain("Переключить на тёмную тему", texts);

                Assert.True(shell.CommandPalette.HasResults);
                Assert.Equal(0, shell.CommandPalette.SelectedIndex);

                // Arrow commands drive the rendered selection without any mouse interaction.
                shell.CommandPalette.SelectNextCommand.Execute(null);
                view.UpdateLayout();

                var paletteView = FindVisualDescendants<CommandPaletteView>(view).First();
                var results = Assert.IsAssignableFrom<ListBox>(paletteView.FindName("ResultsList"));
                Assert.Equal(1, results.SelectedIndex);
                Assert.Same(shell.CommandPalette.SelectedItem, results.SelectedItem);

                shell.CommandPalette.SelectPreviousCommand.Execute(null);
                view.UpdateLayout();
                Assert.Equal(0, results.SelectedIndex);

                var screenshotPath = CaptureScreenshot(
                    view,
                    "shell_command_palette.png",
                    dpi: 96,
                    pixelWidth: 1280,
                    pixelHeight: 800);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UnifiedShell_AtTwoHundredPercentDpi_LightTheme_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Light);

            // A 1800x1200 physical window at 200% DPI exposes 900x600 device-independent pixels.
            var (window, view) = OpenShell(shell, 900, 600);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("Навигация", texts);
                Assert.Contains("Инспектор / контекст", texts);

                var left = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("LeftPane"));
                var center = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("CenterPane"));
                var right = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("RightPane"));

                // Compact layouts fold the inspector but retain its desktop preference.
                Assert.True(left.ActualWidth > 0, "The left pane must not collapse at 200% DPI.");
                Assert.Equal(view.ActualWidth >= 1100, shell.IsInspectorDisplayed);
                Assert.True(shell.IsRightPaneVisible);
                if (shell.IsInspectorDisplayed) Assert.True(right.ActualWidth > 0);
                Assert.True(center.ActualWidth >= 300, "The center pane must keep its minimum width.");
                Assert.False(double.IsNaN(view.DesiredSize.Width));

                var screenshotPath = CaptureScreenshot(
                    view,
                    "shell_high_dpi_200.png",
                    dpi: 192,
                    pixelWidth: 1800,
                    pixelHeight: 1200);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void UnifiedShell_LaysOutWithoutCollapseAtDpiScales(double scale)
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenShell(shell, 1400, 900);

            try
            {
                view.LayoutTransform = new ScaleTransform(scale, scale);
                view.Measure(new Size(1400, 900));
                view.Arrange(new Rect(0, 0, 1400, 900));
                view.UpdateLayout();

                var left = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("LeftPane"));
                var center = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("CenterPane"));
                var right = Assert.IsAssignableFrom<FrameworkElement>(view.FindName("RightPane"));

                Assert.True(left.ActualWidth > 0);
                Assert.True(center.ActualWidth > 0);
                Assert.Equal(view.ActualWidth >= 1100, shell.IsInspectorDisplayed);
                Assert.True(shell.IsRightPaneVisible);
                if (shell.IsInspectorDisplayed) Assert.True(right.ActualWidth > 0);
                Assert.False(double.IsNaN(view.DesiredSize.Width));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UnifiedShell_BindsGlobalShortcutsAndKeyboardNavigation()
    {
        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();
        var shell = CreateShell(provider);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenShell(shell, 1280, 800);

            try
            {
                // Ctrl+K, Ctrl+P, Escape, F5 and Ctrl+1..Ctrl+0 are all bound on the shell itself.
                Assert.Equal(14, view.InputBindings.Count);

                var leftToggle = Assert.IsAssignableFrom<Button>(view.FindName("ToggleLeftPaneButton"));
                var rightToggle = Assert.IsAssignableFrom<Button>(view.FindName("ToggleRightPaneButton"));
                var paletteButton = Assert.IsAssignableFrom<Button>(view.FindName("OpenCommandPaletteButton"));

                Assert.True(leftToggle.Focusable);
                Assert.True(rightToggle.Focusable);
                Assert.True(paletteButton.Focusable);
                Assert.NotNull(leftToggle.Command);
                Assert.NotNull(paletteButton.Command);

                var navigationList = FindVisualDescendants<ListBox>(view).First();
                Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(navigationList));

                Assert.False(shell.CommandPalette.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static UnifiedWorkspaceShellViewModel CreateShell(ServiceProvider provider) =>
        new(
            provider.GetRequiredService<MainWindowViewModel>(),
            new StubLayoutPersistenceService());

    private static (Window Window, UnifiedWorkspaceShellView View) OpenShell(
        UnifiedWorkspaceShellViewModel viewModel,
        double width,
        double height)
    {
        var view = new UnifiedWorkspaceShellView { DataContext = viewModel };

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

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var visual in EnumerateVisuals(root))
        {
            if (visual is T typed)
            {
                yield return typed;
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
