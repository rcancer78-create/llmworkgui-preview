using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Trait("Category", "VisualUi")]
public sealed class AdaptationExpandedScopeCaptionBoundsReviewTests
{
    private const string Label = "Разрешить расширенную семантическую область";

    [Theory]
    [InlineData(AppTheme.Dark, 96)]
    [InlineData(AppTheme.Dark, 144)]
    [InlineData(AppTheme.Dark, 192)]
    [InlineData(AppTheme.Light, 96)]
    [InlineData(AppTheme.Light, 144)]
    [InlineData(AppTheme.Light, 192)]
    public async Task EntireExpandedScopeCaptionFitsTheActualShippedCheckbox(AppTheme theme, int renderDpi)
    {
        StaTestRunner.EnsureApplication();
        using var provider = UiTestHost.CreateProvider();
        var main = provider.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var library = provider.GetRequiredService<WorkflowLibraryViewModel>();
        var now = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        var hash = "sha256:" + new string('a', 64);
        var version = new WorkflowVersionItemViewModel(new WorkflowVersion(
            "owned-caption-version", "owned-caption-package", 1, hash, hash,
            WorkflowSourceType.ZipArchive, null, null, null, null, null, now, null));
        var package = new WorkflowPackageItemViewModel(new WorkflowPackage(
            "owned-caption-package", "Пакет проверки интерфейса", null, Array.Empty<string>(),
            WorkflowSourceType.ZipArchive, hash, hash, now, now));
        // Display-only project; this UI graph has no adaptation service and never reads this path.
        var project = new Project("owned-caption-project", "Проект проверки", @"C:\owned-caption-fixture",
            null, false, true, null, null, DataClassification.PrivateSource);
        await library.AdaptationDialog.OpenForVersionAsync(version, package, project);
        main.NavigateTo(ScreenId.Workflows);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var window = new MainWindow(main)
            {
                Width = 1280, Height = 800, WindowStyle = WindowStyle.None, ShowActivated = false
            };
            try
            {
                window.Show();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                root.Measure(new Size(1280, 800));
                root.Arrange(new Rect(0, 0, 1280, 800));
                root.UpdateLayout();
                var pixels = new RenderTargetBitmap(1280 * renderDpi / 96, 800 * renderDpi / 96,
                    renderDpi, renderDpi, PixelFormats.Pbgra32);
                pixels.Render(root);
                var checkbox = Assert.Single(Descendants<CheckBox>(root), box =>
                    Equals(box.Content, Label) || box.Content is TextBlock text && text.Text == Label);
                Assert.True(checkbox.IsVisible && checkbox.ActualWidth > 0);
                var caption = Assert.Single(Descendants<TextBlock>(checkbox), text => text.Text == Label);
                Assert.True(caption.ActualWidth > 0 && caption.ActualHeight > 0);
                var bounds = caption.TransformToAncestor(checkbox).TransformBounds(new Rect(caption.RenderSize));
                Assert.True(bounds.Left >= -1 && bounds.Right <= checkbox.ActualWidth + 1,
                    $"Complete caption including final ь occupies {bounds.Left}..{bounds.Right}; checkbox allows 0..{checkbox.ActualWidth} at {renderDpi} render DPI.");
                Assert.True(bounds.Top >= -1 && bounds.Bottom <= checkbox.ActualHeight + 1);
                var complete = new TextBlock
                {
                    Text = Label, FontFamily = caption.FontFamily, FontSize = caption.FontSize,
                    FontStyle = caption.FontStyle, FontWeight = caption.FontWeight, FontStretch = caption.FontStretch,
                    FlowDirection = caption.FlowDirection, Language = caption.Language,
                    TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
                    LineHeight = caption.LineHeight, LineStackingStrategy = caption.LineStackingStrategy
                };
                TextOptions.SetTextFormattingMode(complete, TextOptions.GetTextFormattingMode(caption));
                complete.Measure(new Size(caption.ActualWidth, double.PositiveInfinity));
                Assert.True(caption.ActualHeight + 1 >= complete.DesiredSize.Height,
                    "Complete nonempty caption must have enough height for all wrapped lines.");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(pixels));
                ScreenshotFile.Save(encoder, Path.Combine(ScreenshotFile.OutputDirectory,
                    $"adaptation_expanded_scope_caption_{theme.ToString().ToLowerInvariant()}_{renderDpi * 100 / 96}percent.png"));
            }
            finally { window.Close(); }
        });
        library.AdaptationDialog.CloseDialog();
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
