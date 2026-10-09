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
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// QuickViewer-style WPF scenarios of the Phase 11B Diff / Artifact Viewer. The shipped
/// <see cref="DiffArtifactViewerView"/> is rendered with an inline diff, a side-by-side diff and an
/// artifact payload; screenshots are persisted to the Screenshots folder.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class DiffArtifactViewerVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    private const string SampleDiff =
        "diff --git a/app.txt b/app.txt\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/app.txt\n" +
        "+++ b/app.txt\n" +
        "@@ -1,4 +1,5 @@\n" +
        " line one\n" +
        "-line two\n" +
        "+line two changed\n" +
        "+line three added\n" +
        " line four";

    [Fact]
    public void DiffViewer_InlineDarkTheme_ShowsAdditionsAndDeletions_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = new DiffArtifactViewerViewModel(new SilentClipboard());
        viewModel.LoadDiff(SampleDiff, title: "app.txt diff");

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel, 1280, 720);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("app.txt diff", texts);
                Assert.Contains("Линейный", texts);
                Assert.Contains("Бок о бок", texts);
                Assert.Contains("Копировать diff", texts);
                Assert.Contains("+2 -1 · 1 hunk(s) · 2 context line(s)", texts);
                Assert.Contains(
                    texts,
                    text => text.Contains("line two changed", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.Contains("line three added", StringComparison.Ordinal));
                Assert.Contains("-", texts);
                Assert.Contains("+", texts);

                // The color marking comes from the shipped item container style: added rows carry a
                // green-tinted background, deleted rows a red-tinted one.
                var list = Assert.IsAssignableFrom<ListBox>(view.FindName("UnifiedLinesList"));
                var additionContainer = FindContainerForText(list, "line three added");
                var deletionContainer = FindContainerForText(list, "line two");
                Assert.NotNull(additionContainer);
                Assert.NotNull(deletionContainer);
                Assert.NotEqual(additionContainer!.Background.ToString(), deletionContainer!.Background.ToString());

                var screenshotPath = CaptureScreenshot(view, "diff_viewer_inline_dark.png", dpi: 96, 1280, 720);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DiffViewer_SideBySideDarkTheme_AlignsBothColumns_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = new DiffArtifactViewerViewModel(new SilentClipboard());
        viewModel.LoadDiff(SampleDiff, title: "app.txt diff");
        viewModel.UseSideBySideLayoutCommand.Execute(null);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel, 1280, 720);

            try
            {
                var texts = ReadTexts(view);

                Assert.True(viewModel.IsSideBySideMode);
                Assert.Contains(
                    texts,
                    text => text.Contains("line two changed", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.Contains("line three added", StringComparison.Ordinal));

                var sideBySideList = Assert.IsAssignableFrom<ListBox>(view.FindName("SideBySideLinesList"));
                Assert.True(sideBySideList.ActualWidth > 0);
                Assert.False(double.IsNaN(view.DesiredSize.Width));

                var screenshotPath = CaptureScreenshot(view, "diff_viewer_side_by_side_dark.png", dpi: 96, 1280, 720);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DiffViewer_InlineLightTheme_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = new DiffArtifactViewerViewModel(new SilentClipboard());
        viewModel.LoadDiff(SampleDiff, title: "app.txt diff");

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Light);

            var (window, view) = OpenView(viewModel, 1280, 720);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("app.txt diff", texts);
                Assert.Contains(
                    texts,
                    text => text.Contains("line three added", StringComparison.Ordinal));

                var screenshotPath = CaptureScreenshot(view, "diff_viewer_inline_light.png", dpi: 96, 1280, 720);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DiffViewer_InlineAtTwoHundredPercentDpi_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = new DiffArtifactViewerViewModel(new SilentClipboard());
        viewModel.LoadDiff(SampleDiff, title: "app.txt diff");

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel, 1280, 720);

            try
            {
                var list = Assert.IsAssignableFrom<ListBox>(view.FindName("UnifiedLinesList"));

                Assert.True(list.ActualWidth > 0, "The diff list must not collapse at 200% DPI.");
                Assert.True(list.ActualHeight > 0, "The diff list must keep its height at 200% DPI.");
                Assert.False(double.IsNaN(view.DesiredSize.Width));
                Assert.False(double.IsNaN(view.DesiredSize.Height));

                var screenshotPath = CaptureScreenshot(view, "diff_viewer_inline_dark_200.png", dpi: 192, 2560, 1440);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DiffViewer_ArtifactPane_ShowsContentSizeHashAndStatus_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = new DiffArtifactViewerViewModel(new SilentClipboard());
        viewModel.LoadArtifact(
            "plan.json",
            "{\n  \"stage\": \"plan\",\n  \"role\": \"Architect\"\n}",
            changeStatus: ArtifactChangeStatus.Added);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel, 1280, 720);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("Имя:", texts);
                Assert.Contains("Размер:", texts);
                Assert.Contains("Изменение:", texts);
                Assert.Contains("SHA-256:", texts);
                Assert.Contains("plan.json", texts);
                Assert.Contains("Добавлен", texts);
                Assert.Contains(
                    texts,
                    text => text.StartsWith("sha256:", StringComparison.Ordinal));
                Assert.Contains(
                    texts,
                    text => text.Contains("\"stage\": \"plan\"", StringComparison.Ordinal));

                var screenshotPath = CaptureScreenshot(view, "diff_viewer_artifact_dark.png", dpi: 96, 1280, 720);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ListBoxItem? FindContainerForText(ListBox list, string textFragment)
    {
        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.Items[index] is DiffLineViewModel line
                && line.Text.Contains(textFragment, StringComparison.Ordinal))
            {
                return list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            }
        }

        return null;
    }

    private static (Window Window, DiffArtifactViewerView View) OpenView(
        DiffArtifactViewerViewModel viewModel,
        double width,
        double height)
    {
        var view = new DiffArtifactViewerView { DataContext = viewModel };

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

    private sealed class SilentClipboard : IClipboardService
    {
        public void SetText(string text)
        {
        }
    }
}
