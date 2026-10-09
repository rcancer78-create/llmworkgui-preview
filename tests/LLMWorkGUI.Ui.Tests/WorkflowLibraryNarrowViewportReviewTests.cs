using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Trait("Category", "VisualUi")]
public sealed class WorkflowLibraryNarrowViewportReviewTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void ActualMinimumWidthLibraryCanBringEveryProjectBindingActionIntoItsOwnViewport(int renderDpi)
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        var library = new WorkflowLibraryViewModel(new InMemoryWorkflowPackageRepository(),
            new InMemoryWorkflowVersionRepository(), new InMemoryWorkflowBindingRepository());
        Assert.True(library.IsLibraryAvailable);
        var main = new MainWindowViewModel(provider.GetRequiredService<WorkspaceViewModel>(),
            provider.GetRequiredService<ProvidersAccountsViewModel>(), provider.GetRequiredService<QuotasViewModel>(),
            provider.GetRequiredService<HealthCenterViewModel>(), provider.GetRequiredService<SettingsDiagnosticsViewModel>(),
            provider.GetRequiredService<StatusBarViewModel>(), provider.GetRequiredService<CliStatusViewModel>(),
            provider.GetRequiredService<ThemeSelectorViewModel>(), library);
        main.NavigateTo(ScreenId.Workflows);
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            var window = new MainWindow(main) { Width = 900, Height = 590, WindowStyle = WindowStyle.None, ShowActivated = false };
            try
            {
                window.Show(); window.UpdateLayout();
                Assert.Equal(window.MinWidth, window.ActualWidth);
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var center = Assert.Single(Descendants<Border>(root), item => item.Name == "CenterPanel");
                var browser = Assert.Single(Descendants<ScrollViewer>(root), item => item.Name == "LibraryBrowserScroll");
                Assert.True(browser.IsVisible && browser.ActualWidth > 0);
                var viewport = Assert.Single(Descendants<ScrollContentPresenter>(browser),
                    item => ReferenceEquals(item.TemplatedParent, browser));
                foreach (var name in new[] { "LibraryBindButton", "LibrarySetActiveVersionButton", "LibraryAdaptButton",
                    "LibraryRollbackButton", "LibraryUnbindButton", "LibraryRefreshButton" })
                {
                    var button = Assert.Single(Descendants<Button>(browser), item => item.Name == name);
                    Assert.True(button.IsVisible && button.ActualWidth > 0 && button.ActualHeight > 0);
                    button.BringIntoView(); window.UpdateLayout();
                    var bounds = button.TransformToAncestor(viewport).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1,
                        $"{name} spans {bounds.Left}..{bounds.Right} beyond actual browser viewport 0..{viewport.ActualWidth} at {renderDpi} render DPI.");
                    Assert.True(bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1,
                        $"{name} must be reachable through actual browser scrolling, including its complete button rectangle.");
                    var centerBounds = button.TransformToAncestor(center).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(centerBounds.Left >= -1 && centerBounds.Right <= center.ActualWidth + 1
                        && centerBounds.Top >= -1 && centerBounds.Bottom <= center.ActualHeight + 1,
                        $"{name} spans {centerBounds} beyond its visible CenterPanel 0..{center.ActualWidth},0..{center.ActualHeight} at {renderDpi} render DPI.");
                    var windowBounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(windowBounds.Left >= -1 && windowBounds.Right <= root.ActualWidth + 1
                        && windowBounds.Top >= -1 && windowBounds.Bottom <= root.ActualHeight + 1,
                        $"{name} must fit the complete visible window after BringIntoView, rather than only an offscreen nested viewport.");
                }
                var pixels = new RenderTargetBitmap(900 * renderDpi / 96, 590 * renderDpi / 96, renderDpi, renderDpi, PixelFormats.Pbgra32);
                pixels.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixels));
                ScreenshotFile.Save(encoder, Path.Combine(ScreenshotFile.OutputDirectory,
                    $"workflow_library_owned_narrow_{renderDpi * 100 / 96}percent.png"));
            }
            finally { window.Close(); }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
