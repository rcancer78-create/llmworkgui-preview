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

[Trait("Category", "VisualUi")]
public sealed class ErrorStateActionBoundsReviewTests
{
    [Theory]
    [InlineData(false, 96)]
    [InlineData(true, 96)]
    [InlineData(false, 192)]
    [InlineData(true, 192)]
    public void AllErrorActionsRemainInsideTheRealCardAtTheReviewedWidth(bool expanded, int renderDpi)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            var model = new ErrorStateViewModel("The activity query failed", "The repository is unavailable.",
                "Owned synthetic technical details", new RelayCommand(() => { }), new OwnedClipboard());
            if (expanded)
            {
                model.ToggleDetailsCommand.Execute(null);
                model.CopyDetailsCommand.Execute(null);
            }
            var view = new ErrorStateView { DataContext = model };
            var window = new Window { Width = 1280d / 3, Height = 520, Content = view, WindowStyle = WindowStyle.None, ShowActivated = false };
            try
            {
                window.Show();
                view.Measure(new Size(1280d / 3, 520));
                view.Arrange(new Rect(0, 0, 1280d / 3, 520));
                view.UpdateLayout();
                // Render current geometry locally; this regression neither writes nor replaces reference screenshots.
                var pixels = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * renderDpi / 96d),
                    (int)Math.Ceiling(view.ActualHeight * renderDpi / 96d), renderDpi, renderDpi, PixelFormats.Pbgra32);
                pixels.Render(view);
                var root = Assert.IsType<Border>(view.FindName("StateRoot"));
                foreach (var name in new[] { "RetryButton", "DetailsButton", "CopyDetailsButton" })
                {
                    var button = Assert.IsType<Button>(view.FindName(name));
                    Assert.True(button.IsVisible);
                    var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(bounds.Left >= root.BorderThickness.Left + root.Padding.Left - 1,
                        $"{name} starts outside the error card's content area: {bounds.Left}.");
                    Assert.True(bounds.Right <= root.ActualWidth - root.BorderThickness.Right - root.Padding.Right + 1,
                        $"{name} extends beyond the error card's visible content area: {bounds.Right} against {root.ActualWidth} at {renderDpi} render DPI.");
                }
            }
            finally { window.Close(); }
        });
    }

    private sealed class OwnedClipboard : IClipboardService
    {
        public void SetText(string text) { }
    }
}
