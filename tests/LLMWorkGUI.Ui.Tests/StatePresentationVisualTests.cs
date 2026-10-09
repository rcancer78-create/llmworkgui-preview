using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.ViewModels.StateControls;
using LLMWorkGUI.App.Views.StateControls;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// QuickViewer-style WPF scenario of the standardized Empty, Loading and Error states (ROADMAP Phase 11).
/// All three shipped state views are rendered together in Dark and Light themes; screenshots are
/// persisted to the Screenshots folder.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class StatePresentationVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    [Fact]
    public void EmptyLoadingAndErrorStates_DarkTheme_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var clipboard = new RecordingClipboard();
        var (empty, loading, error) = CreateStateViewModels(clipboard);
        error.ToggleDetailsCommand.Execute(null);
        error.CopyDetailsCommand.Execute(null);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(empty, loading, error, 1280, 520);

            try
            {
                var texts = ReadTexts(view);

                // Empty state: icon, title, explanation and the optional call-to-action.
                Assert.Contains("No activity events yet", texts);
                Assert.Contains(
                    texts,
                    text => text.Contains("appear here as soon as they are reported", StringComparison.Ordinal));
                Assert.Contains("Reset filters", texts);

                // Loading state: operation text and the cancel action.
                Assert.Contains("Loading activity events…", texts);
                Assert.Contains("Отмена", texts);

                // Error state: title, message, expanded technical details and the copy notice.
                Assert.Contains("The activity query failed", texts);
                Assert.Contains("The repository is unavailable.", texts);
                Assert.Contains("Скрыть технические подробности", texts);
                Assert.Contains("Копировать подробности", texts);
                Assert.Contains(
                    texts,
                    text => text.Contains("System.InvalidOperationException: boom", StringComparison.Ordinal));
                Assert.Contains("Технические подробности скопированы в буфер обмена.", texts);

                Assert.Equal(error.TechnicalDetails, clipboard.LastText);

                var screenshotPath = CaptureScreenshot(view, "empty_loading_error_states.png", dpi: 96, 1280, 520);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EmptyLoadingAndErrorStates_LightTheme_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var clipboard = new RecordingClipboard();
        var (empty, loading, error) = CreateStateViewModels(clipboard);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Light);

            var (window, view) = OpenView(empty, loading, error, 1280, 520);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("No activity events yet", texts);
                Assert.Contains("Loading activity events…", texts);
                Assert.Contains("The activity query failed", texts);
                Assert.Contains("Показать технические подробности", texts);

                var screenshotPath = CaptureScreenshot(view, "empty_loading_error_states_light.png", dpi: 96, 1280, 520);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EmptyLoadingAndErrorStates_AtTwoHundredPercentDpi_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var clipboard = new RecordingClipboard();
        var (empty, loading, error) = CreateStateViewModels(clipboard);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(empty, loading, error, 1280, 520);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("No activity events yet", texts);
                Assert.Contains("Loading activity events…", texts);
                Assert.Contains("The activity query failed", texts);
                Assert.False(double.IsNaN(view.DesiredSize.Width));
                Assert.False(double.IsNaN(view.DesiredSize.Height));

                var screenshotPath = CaptureScreenshot(view, "empty_loading_error_states_200.png", dpi: 192, 2560, 1040);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static (EmptyStateViewModel Empty, LoadingStateViewModel Loading, ErrorStateViewModel Error)
        CreateStateViewModels(IClipboardService clipboard)
    {
        var empty = new EmptyStateViewModel(
            "No activity events yet",
            "Workflow runs, sessions, health transitions and user actions appear here as soon as they are reported.",
            "◻",
            "Reset filters",
            new RelayCommand(() => { }));

        var loading = new LoadingStateViewModel(
            "Loading activity events…",
            isCancellable: true,
            new RelayCommand(() => { }));

        var error = new ErrorStateViewModel(
            "The activity query failed",
            "The repository is unavailable.",
            "System.InvalidOperationException: boom\n   at ActivityCenterService.Query(...)",
            new RelayCommand(() => { }),
            clipboard);

        return (empty, loading, error);
    }

    private static (Window Window, Grid View) OpenView(
        EmptyStateViewModel empty,
        LoadingStateViewModel loading,
        ErrorStateViewModel error,
        double width,
        double height)
    {
        var grid = new Grid { Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var emptyView = new EmptyStateView { DataContext = empty };
        var loadingView = new LoadingStateView { DataContext = loading };
        var errorView = new ErrorStateView { DataContext = error };

        Grid.SetColumn(emptyView, 0);
        Grid.SetColumn(loadingView, 1);
        Grid.SetColumn(errorView, 2);

        grid.Children.Add(emptyView);
        grid.Children.Add(loadingView);
        grid.Children.Add(errorView);

        var window = new Window
        {
            Width = width,
            Height = height,
            Content = grid,
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

        grid.Measure(new Size(width, height));
        grid.Arrange(new Rect(0, 0, width, height));
        grid.UpdateLayout();

        return (window, grid);
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

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? LastText { get; private set; }

        public void SetText(string text)
        {
            LastText = text;
        }
    }
}
