using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
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

/// <summary>
/// QuickViewer-style WPF scenarios of the Phase 11C frictionless onboarding. The shipped
/// <see cref="OnboardingView"/> is rendered over the built-in catalog and local CLI detection in Dark
/// and Light themes at 100% and 200% DPI; no model or network call is involved. Screenshots are
/// persisted for the required artifact set.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class OnboardingVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Onboarding_LargeWindow_PreservesCenteredCardSize()
    {
        StaTestRunner.EnsureApplication();
        var viewModel = await CreateViewModelAsync();
        StaTestRunner.Run(() =>
        {
            var (window, view) = OpenView(viewModel, 1280, 800);
            try
            {
                var card = FindByName<Border>(view, "OnboardingCardHost");
                var bounds = card.TransformToAncestor(view).TransformBounds(new Rect(card.RenderSize));
                Assert.Equal(980, card.ActualWidth, 1);
                Assert.Equal(680, card.ActualHeight, 1);
                Assert.Equal((view.ActualWidth - card.ActualWidth) / 2, bounds.Left, 1);
                Assert.Equal((view.ActualHeight - card.ActualHeight) / 2, bounds.Top, 1);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public async Task Onboarding_NarrowWindow_KeepsCardAndNavigationInsideViewport(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();
        var viewModel = await CreateViewModelAsync();
        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(theme);
            var (window, view) = OpenView(viewModel, 900, 560);
            try
            {
                for (var step = 0; step < viewModel.Steps.Count; step++)
                {
                    viewModel.GoToStep(step);
                    view.UpdateLayout();
                    AssertCardFits(view);
                    foreach (var name in new[] { "OnboardingCardHost", "DismissOnboardingButton",
                        "BackStepButton", "NextStepButton" }.Concat(step == viewModel.Steps.Count - 1
                            ? new[] { "CompleteOnboardingButton" } : Array.Empty<string>()))
                    {
                        var element = FindByName<FrameworkElement>(view, name);
                        var bounds = element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
                        Assert.True(bounds.Left >= 0 && bounds.Top >= 0
                            && bounds.Right <= view.ActualWidth && bounds.Bottom <= view.ActualHeight,
                            $"{name} extends outside the narrow viewport: {bounds}.");
                    }
                    CaptureScreenshot(view, $"onboarding_narrow_{theme}_{step + 1}.png", 96, 900, 560);
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Onboarding_NarrowWindow_LongCatalogCanScrollWithoutMovingNavigation()
    {
        StaTestRunner.EnsureApplication();
        var viewModel = await CreateViewModelAsync();
        StaTestRunner.Run(() =>
        {
            for (var index = 0; index < 50; index++)
                viewModel.DocumentTemplateNames.Add($"Тестовый документ {index}");
            viewModel.GoToStep(2);
            var (window, view) = OpenView(viewModel, 900, 560);
            try
            {
                var scroll = FindByName<ScrollViewer>(view, "OnboardingStepScrollViewer");
                Assert.True(scroll.ScrollableHeight > 0);
                scroll.ScrollToEnd();
                view.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                var next = FindByName<Button>(view, "NextStepButton");
                var bounds = next.TransformToAncestor(view).TransformBounds(new Rect(next.RenderSize));
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= view.ActualHeight);
                CaptureScreenshot(view, "onboarding_narrow_long_catalog.png", 96, 900, 560);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Onboarding_WelcomeStep_DarkTheme100_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = await CreateViewModelAsync();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel, 1280, 800);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("Первый запуск", texts);
                Assert.Contains(OnboardingViewModel.NoPaidCallNotice, texts);
                Assert.Contains("Рабочий каталог проекта", texts);
                Assert.Contains("Скрыть", texts);

                // Accessibility: the interactive elements carry explicit automation names.
                Assert.Equal(
                    "Скрыть онбординг без блокировки интерфейса",
                    AutomationProperties.GetName(FindByName<Button>(view, "DismissOnboardingButton")));
                Assert.Equal(
                    "Подтвердить рабочий каталог",
                    AutomationProperties.GetName(FindByName<Button>(view, "ConfirmWorkspaceButton")));
                Assert.False(string.IsNullOrWhiteSpace(
                    AutomationProperties.GetName(FindByName<Button>(view, "NextStepButton"))));

                AssertCardFits(view);

                var screenshotPath = CaptureScreenshot(
                    view,
                    "onboarding_dark_100.png",
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
    public async Task Onboarding_LocalCliDetectionStep_LightTheme_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = await CreateViewModelAsync();
        viewModel.GoToStep(OnboardingStepKind.LocalCliDetection);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Light);

            var (window, view) = OpenView(viewModel, 1280, 800);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("Обнаружение локальных CLI и бэкендов", texts);
                Assert.Contains("Повторить локальное обнаружение", texts);
                Assert.True(viewModel.IsCliStep);

                AssertCardFits(view);

                var screenshotPath = CaptureScreenshot(
                    view,
                    "onboarding_step2_cli_light.png",
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
    public async Task Onboarding_CatalogAndRoleMatrixStep_LightTheme_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = await CreateViewModelAsync();
        viewModel.GoToStep(OnboardingStepKind.WorkflowCatalogDiscovery);

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Light);

            var (window, view) = OpenView(viewModel, 1280, 800);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("Готовые шаблоны сценариев", texts);
                Assert.Contains("Матрица ролей", texts);
                Assert.Contains("Шаблоны документов", texts);
                Assert.Contains(
                    texts,
                    text => text.Contains("встроенный", StringComparison.Ordinal));
                Assert.True(viewModel.HasCatalogTemplates);
                Assert.True(viewModel.HasRoleMatrix);

                AssertCardFits(view);

                var screenshotPath = CaptureScreenshot(
                    view,
                    "onboarding_light.png",
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
    public async Task Onboarding_ReadySafeSimulationStep_DarkTheme200_GeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = await CreateViewModelAsync();
        viewModel.GoToStep(OnboardingStepKind.ReadySafeMode);
        viewModel.StartSafeSimulation();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            // A 1280x800 device-independent window rendered as a 2560x1600 raster at 200% DPI.
            var (window, view) = OpenView(viewModel, 1280, 800);

            try
            {
                var texts = ReadTexts(view);

                Assert.Contains("Безопасная симуляция", texts);
                Assert.Contains(OnboardingViewModel.SafeSimulationNotice, texts);
                Assert.Contains("SYNTHETIC", texts);
                Assert.Contains(
                    texts,
                    text => text.Contains("Coordinator", StringComparison.Ordinal));
                Assert.Contains("Открыть рабочую область", texts);
                Assert.Equal(
                    "Запустить безопасную synthetic-симуляцию",
                    AutomationProperties.GetName(FindByName<Button>(view, "StartSafeSimulationButton")));
                Assert.Equal(
                    "Завершить онбординг и открыть рабочую область",
                    AutomationProperties.GetName(FindByName<Button>(view, "OpenWorkspaceButton")));

                AssertCardFits(view);

                var screenshotPath = CaptureScreenshot(
                    view,
                    "onboarding_dark_200.png",
                    dpi: 192,
                    pixelWidth: 2560,
                    pixelHeight: 1600);
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Onboarding_RendersWithoutClippingAtAllSupportedDpiRasters()
    {
        StaTestRunner.EnsureApplication();

        var viewModel = await CreateViewModelAsync();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, view) = OpenView(viewModel, 1280, 800);

            try
            {
                // 100%, 150% and 200% DPI rasters over the same device-independent layout.
                foreach (var dpi in new[] { 96.0, 144.0, 192.0 })
                {
                    var raster = new RenderTargetBitmap(
                        (int)Math.Ceiling(1280 * dpi / 96.0),
                        (int)Math.Ceiling(800 * dpi / 96.0),
                        dpi,
                        dpi,
                        PixelFormats.Pbgra32);

                    raster.Render(view);

                    Assert.True(raster.PixelWidth > 0 && raster.PixelHeight > 0);
                    AssertCardFits(view);
                    Assert.False(double.IsNaN(view.DesiredSize.Width));
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static async Task<OnboardingViewModel> CreateViewModelAsync()
    {
        var cliStatus = new CliStatusViewModel(
            FakeCliDetectionService.AllDetected(Now),
            new FixedTimeProvider(Now));

        var viewModel = new OnboardingViewModel(
            cliStatus,
            new WorkflowLibraryViewModel(),
            new InMemoryApplicationSettingsRepository(),
            new InMemoryProjectRepository(),
            new FixedTimeProvider(Now));

        await viewModel.InitializeAsync();
        return viewModel;
    }

    private static (Window Window, OnboardingView View) OpenView(
        OnboardingViewModel viewModel,
        double width,
        double height)
    {
        var view = new OnboardingView { DataContext = viewModel };

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

    private static void AssertCardFits(FrameworkElement root)
    {
        var card = FindByName<Border>(root, "OnboardingCardHost");

        Assert.True(card.ActualWidth > 0, "The onboarding card must be laid out.");
        Assert.True(card.ActualHeight > 0, "The onboarding card must be laid out.");
        Assert.True(
            card.ActualWidth <= root.ActualWidth,
            "The onboarding card must not be clipped horizontally.");
        Assert.True(
            card.ActualHeight <= root.ActualHeight,
            "The onboarding card must not be clipped vertically.");
    }

    private static T FindByName<T>(FrameworkElement root, string name)
        where T : FrameworkElement
    {
        var element = root.FindName(name);
        return Assert.IsAssignableFrom<T>(element);
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
}
