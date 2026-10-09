using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ShellSmokeTests
{
    [Fact]
    public void MainWindow_InitializesThreePanelsStatusBarAndTenScreens()
    {
        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            var viewModel = provider.GetRequiredService<MainWindowViewModel>();

            var window = new MainWindow(viewModel);

            Assert.NotNull(window.FindName("ShellPanes"));
            Assert.NotNull(window.FindName("LeftPanel"));
            Assert.NotNull(window.FindName("CenterPanel"));
            Assert.NotNull(window.FindName("RightPanel"));
            Assert.NotNull(window.FindName("GlobalStatusBar"));
            Assert.NotNull(window.FindName("DegradedBanner"));
            Assert.Equal(10, viewModel.NavigationItems.Count);
            Assert.Same(viewModel, window.DataContext);
        });
    }

    [Fact]
    public async Task MainWindow_ShowsDegradedBannerWhenNoBackendCliIsDetected()
    {
        using var provider = UiTestHost.CreateProvider(FakeCliDetectionService.Degraded());
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        await viewModel.InitializeAsync();

        StaTestRunner.Run(() =>
        {
            var window = new MainWindow(viewModel);
            Layout(window);

            var banner = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("DegradedBanner"));

            Assert.Equal(Visibility.Visible, banner.Visibility);
        });

        Assert.True(viewModel.StatusBar.IsDegraded);
        Assert.Equal("Ограниченный режим", viewModel.StatusBar.DegradedIndicatorText);
    }

    [Fact]
    public async Task MainWindow_HidesDegradedBannerWhenBackendsAreDetected()
    {
        using var provider = UiTestHost.CreateProvider(FakeCliDetectionService.AllDetected());
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        await viewModel.InitializeAsync();

        StaTestRunner.Run(() =>
        {
            var window = new MainWindow(viewModel);
            Layout(window);

            var banner = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("DegradedBanner"));

            Assert.Equal(Visibility.Collapsed, banner.Visibility);
        });

        Assert.False(viewModel.StatusBar.IsDegraded);
        Assert.Equal("Режим в норме", viewModel.StatusBar.DegradedIndicatorText);
    }

    [Fact]
    public void MainWindow_RendersSyntheticWorkspaceTimelineWithBadge()
    {
        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            var viewModel = provider.GetRequiredService<MainWindowViewModel>();
            var workspace = provider.GetRequiredService<WorkspaceViewModel>();

            var fixture = new SyntheticRunSequenceFixture();
            workspace.LoadTimeline(fixture.Canonical.Steps.Select(step => step.Projection).ToArray());

            var window = new MainWindow(viewModel);
            Layout(window);

            Assert.Same(workspace, viewModel.CurrentScreen);
            Assert.True(workspace.HasSyntheticEvidence);
            Assert.True(workspace.HasTimeline);
            Assert.Equal(fixture.Canonical.Steps.Count, workspace.TimelineItems.Count);
            Assert.Equal(SyntheticRunSequenceFixture.CanonicalStageOrder.Count, workspace.Stages.Count);
            Assert.All(workspace.TimelineItems, item => Assert.True(item.IsSynthetic));

            var renderedTexts = FindVisualDescendants<TextBlock>((DependencyObject)window.Content)
                .Select(textBlock => textBlock.Text)
                .ToArray();

            Assert.Contains("SYNTHETIC", renderedTexts);
            Assert.Contains("Хронология активности / ролей", renderedTexts);
        });
    }

    [Fact]
    public void MainWindow_BindsNavigationPaletteAndHelpShortcuts()
    {
        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            var viewModel = provider.GetRequiredService<MainWindowViewModel>();

            var window = new MainWindow(viewModel);

            Assert.Equal(12, window.InputBindings.Count);
        });
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void MainWindow_LaysOutWithoutCollapseAtDpiScales(double scale)
    {
        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            var viewModel = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow(viewModel);

            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            root.LayoutTransform = new ScaleTransform(scale, scale);
            root.Measure(new Size(1400, 900));
            root.Arrange(new Rect(0, 0, 1400, 900));
            root.UpdateLayout();

            var left = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("LeftPanel"));
            var center = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("CenterPanel"));
            var right = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("RightPanel"));
            var statusBar = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("GlobalStatusBar"));

            Assert.True(left.ActualWidth > 0);
            Assert.True(center.ActualWidth > 0);
            Assert.True(right.ActualWidth > 0);
            Assert.True(statusBar.ActualHeight > 0);
            Assert.True(root.RenderSize.Width > 0);
            Assert.False(double.IsNaN(root.DesiredSize.Width));
        });
    }

    private static void Layout(Window window)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        root.Measure(new Size(1400, 900));
        root.Arrange(new Rect(0, 0, 1400, 900));
        root.UpdateLayout();
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
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
