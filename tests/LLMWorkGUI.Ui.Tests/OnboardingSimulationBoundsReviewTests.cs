using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.ViewModels.Onboarding;
using LLMWorkGUI.App.Views.Onboarding;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Trait("Category", "VisualUi")]
public sealed class OnboardingSimulationBoundsReviewTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void ActualSimulationRowsKeepTheEntireSummaryAndSyntheticBadgeInsideTheirCards(int renderDpi)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
            // This production command creates the canonical local fixture; it does not load accounts or call providers.
            var model = new OnboardingViewModel();
            model.GoToStep(OnboardingStepKind.ReadySafeMode);
            model.StartSafeSimulation();
            Assert.Equal(5, model.SimulationSteps.Count);
            var view = new OnboardingView { DataContext = model };
            var window = new Window
            {
                Width = 1280, Height = 800, Content = view,
                WindowStyle = WindowStyle.None, ShowActivated = false
            };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml", UriKind.Absolute)
            });
            try
            {
                window.Show();
                view.Measure(new Size(1280, 800));
                view.Arrange(new Rect(0, 0, 1280, 800));
                view.UpdateLayout();
                // Same reviewed DIP geometry at both raster resolutions; save current pixels only after bounds pass.
                var pixels = new RenderTargetBitmap(1280 * renderDpi / 96, 800 * renderDpi / 96,
                    renderDpi, renderDpi, PixelFormats.Pbgra32);
                pixels.Render(view);
                var texts = Descendants<TextBlock>(view).ToArray();
                foreach (var step in model.SimulationSteps)
                {
                    var summary = Assert.Single(texts, text => ReferenceEquals(text.DataContext, step)
                        && text.Text == step.SummaryDisplay);
                    var badge = Assert.Single(texts, text => ReferenceEquals(text.DataContext, step)
                        && text.Text == step.SyntheticLabel);
                    Assert.True(summary.IsVisible && badge.IsVisible);
                    Assert.EndsWith("SYNTHETIC", summary.Text, StringComparison.Ordinal);
                    Assert.Equal("SYNTHETIC", badge.Text);
                    var card = NearestBorder(summary);
                    AssertInsidePaddedCard(summary, card, renderDpi);
                    AssertInsidePaddedCard(badge, card, renderDpi);

                    // Measure the COMPLETE, unchanged production text at its real allocated width.
                    // This also rejects height clipping or a repair that silently truncates the last SYNTHETIC token.
                    var required = new TextBlock
                    {
                        Text = summary.Text, FontFamily = summary.FontFamily, FontSize = summary.FontSize,
                        FontStyle = summary.FontStyle, FontWeight = summary.FontWeight, FontStretch = summary.FontStretch,
                        FlowDirection = summary.FlowDirection, Language = summary.Language,
                        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
                        LineHeight = summary.LineHeight, LineStackingStrategy = summary.LineStackingStrategy
                    };
                    TextOptions.SetTextFormattingMode(required, TextOptions.GetTextFormattingMode(summary));
                    required.Measure(new Size(summary.ActualWidth, double.PositiveInfinity));
                    Assert.True(summary.ActualHeight + 1 >= required.DesiredSize.Height,
                        $"Entire summary including its final SYNTHETIC token needs {required.DesiredSize.Height.ToString(CultureInfo.InvariantCulture)} DIP, but received {summary.ActualHeight.ToString(CultureInfo.InvariantCulture)} at {renderDpi} render DPI.");
                }
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(pixels));
                var fileName = renderDpi == 96
                    ? "onboarding_safe_simulation_bounds_dark_100.png"
                    : "onboarding_safe_simulation_bounds_dark_200.png";
                ScreenshotFile.Save(encoder, Path.Combine(ScreenshotFile.OutputDirectory, fileName));
            }
            finally { window.Close(); }
        });
    }

    private static void AssertInsidePaddedCard(FrameworkElement element, Border card, int renderDpi)
    {
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0);
        var bounds = element.TransformToAncestor(card).TransformBounds(new Rect(element.RenderSize));
        var left = card.Padding.Left + card.BorderThickness.Left;
        var right = card.ActualWidth - card.Padding.Right - card.BorderThickness.Right;
        var top = card.Padding.Top + card.BorderThickness.Top;
        var bottom = card.ActualHeight - card.Padding.Bottom - card.BorderThickness.Bottom;
        Assert.True(bounds.Left >= left - 1 && bounds.Right <= right + 1,
            $"Simulation text '{((TextBlock)element).Text}' extends horizontally beyond its own card: {bounds.Left}..{bounds.Right}, available {left}..{right} at {renderDpi} render DPI.");
        Assert.True(bounds.Top >= top - 1 && bounds.Bottom <= bottom + 1,
            $"Simulation text extends vertically beyond its own card at {renderDpi} render DPI.");
    }

    private static Border NearestBorder(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is Border border) return border;
        throw new InvalidOperationException("The real simulation summary must have its TimelineCard ancestor.");
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
