using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Observable QuickViewer-style WPF UI test for the Cursor ACP panel (ТЗ §12.4.2). It renders the
/// real XAML DataTemplate on deterministic doubles, asserts what the user actually sees and persists
/// a screenshot for the release report. No model quota is spent.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class CursorWorkspaceUiVisualTests
{
    private const string SessionId = "cursor-session-visual";

    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    [Fact]
    public void CursorPanel_RendersObservedEvidence_AndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var lifecycle = new FakeCursorAcpSessionLifecycleService
            {
                StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart,
                SessionHandler = () =>
                    CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = SessionId })
            };

            var viewModel = new CursorWorkspaceViewModel(lifecycle, new CursorAcpModePolicy());

            AwaitDeterministic(viewModel.StartBackendAsync());
            AwaitDeterministic(viewModel.CreateSessionAsync(Path.GetTempPath()));

            lifecycle.RaiseStreamEvent(new CursorAcpStreamEvent.TextChunk
            {
                Method = "session/update",
                SessionId = SessionId,
                Text = "Analyzing the failing assertion."
            });

            lifecycle.RaisePermissionRequest(new CursorAcpStreamEvent.PermissionRequest
            {
                Method = "session/request_permission",
                SessionId = SessionId,
                RequestId = "permission-visual",
                Description = "Write to src/Parser.cs"
            });

            var (window, root) = OpenPanel(viewModel);

            try
            {
                var texts = ReadTextBlocks(root);

                // The user sees the confirmed native session and the ready protocol version.
                Assert.Contains(SessionId, texts);
                Assert.Contains("1", texts);
                Assert.Contains("Ready", texts);

                // The approval is visible and explicitly high risk.
                Assert.Contains("Требуется разрешение", texts);
                Assert.Contains("Write to src/Parser.cs", texts);
                Assert.Contains("UnknownHighRisk", texts);

                // The observed stream event is rendered.
                Assert.Contains("Analyzing the failing assertion.", texts);

                var screenshotPath = CaptureScreenshot(root, "cursor_workspace_panel.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CursorPanel_WithoutBackend_ShowsUnavailableNoticeAndNotReported()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var viewModel = new CursorWorkspaceViewModel(lifecycle: null, new CursorAcpModePolicy());
            var (window, root) = OpenPanel(viewModel);

            try
            {
                var texts = ReadTextBlocks(root);

                // A missing backend is stated explicitly instead of being hidden.
                Assert.Contains(viewModel.UnavailableNotice, texts);

                // No invented session evidence is shown.
                Assert.DoesNotContain(SessionId, texts);

                var screenshotPath = CaptureScreenshot(root, "cursor_workspace_panel_unavailable.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static (Window Window, FrameworkElement Root) OpenPanel(CursorWorkspaceViewModel viewModel)
    {
        // The panel is rendered through the production DataTemplate, loaded exactly as MainWindow
        // loads it, so the test exercises the shipped XAML rather than a test-only copy.
        var host = new ContentControl { Content = viewModel };

        var window = new Window
        {
            Width = 900,
            Height = 620,
            Content = host,
            ShowActivated = false,
            WindowStyle = WindowStyle.None
        };

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml",
                UriKind.Absolute)
        });

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml",
                UriKind.Absolute)
        });

        window.Show();

        host.Measure(new Size(900, 620));
        host.Arrange(new Rect(0, 0, 900, 620));
        host.UpdateLayout();

        return (window, host);
    }

    private static void AwaitDeterministic(Task task)
    {
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException("Deterministic test doubles must complete synchronously.");
        }

        task.GetAwaiter().GetResult();
    }

    private static string CaptureScreenshot(FrameworkElement root, string fileName)
    {
        var width = (int)Math.Max(900, root.ActualWidth);
        var height = (int)Math.Max(620, root.ActualHeight);

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

    private static string[] ReadTextBlocks(DependencyObject root) =>
        FindVisualDescendants<TextBlock>(root)
            .Select(textBlock => textBlock.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
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
}
