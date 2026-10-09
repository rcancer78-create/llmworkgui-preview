using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.ViewModels.Onboarding;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.App.Views.Onboarding;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Reproducible UiInspector runner for TASK-070. It renders the 19 primary screens of the shipped UI in
/// the Dark and Light themes at 100%, 150% and 200% DPI (114 combinations), writes the screenshots and
/// the machine-readable manifest into a <c>ui-inspector</c> subdirectory of the test screenshot output,
/// and asserts that no visible element
/// overflows the window bounds and that every visible string stays inside the closed English allowlist.
///
/// The runner renders the real view models over the clean-profile in-memory host (no disk database, no
/// developer <c>%LOCALAPPDATA%</c> state). Normal runs respect the configured screenshot output and
/// do not rewrite documentation screenshots or the accepted visual baseline.
/// </summary>
[Collection("UiInspector visual isolation")]
[Trait("Category", "VisualUi")]
public sealed class UiInspectorVisualTests
{
    private static readonly string HashA = "sha256:" + new string('a', 64);

    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly (AppTheme Theme, string ThemeKey)[] Themes =
    {
        (AppTheme.Dark, "dark"),
        (AppTheme.Light, "light")
    };

    private static readonly (double Dpi, string DpiKey)[] DpiScales =
    {
        (96.0, "100"),
        (144.0, "150"),
        (192.0, "200")
    };

    private static readonly string[] MainWindowScreenKeys =
    {
        "mainwindow_workspace",
        "mainwindow_projects",
        "mainwindow_providersaccounts",
        "mainwindow_models",
        "mainwindow_quotas",
        "mainwindow_sessions",
        "mainwindow_runs",
        "mainwindow_workflows",
        "mainwindow_healthcenter",
        "mainwindow_settingsdiagnostics"
    };

    [Fact]
    public void AllowedStatusPhrases_MatchLongestFirstWithoutFalseLeaks()
    {
        foreach (var status in new[] { "Not reported", "Not reported (stale)", "Not reported (error)", "LLMGateway" })
        {
            var leaks = new List<LeakFinding>();
            ScanForLeaks(status, leaks);
            Assert.Empty(leaks);
        }
    }

    [Fact]
    public void PathPrefixedEnglishLabel_IsStillReported()
    {
        var leaks = new List<LeakFinding>();
        ScanForLeaks(@"C:\projects\demo — Providers and accounts", leaks);
        Assert.Contains(leaks, leak => leak.Word == "Providers");
        Assert.Contains(leaks, leak => leak.Word == "accounts");
    }

    [Fact]
    public void VisualBaselineComparison_FailsWhenOnePixelChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "llmworkgui-pixel-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var baselinePath = Path.Combine(directory, "baseline.png");
        var changedPath = Path.Combine(directory, "changed.png");

        try
        {
            StaTestRunner.Run(() =>
            {
                WriteOnePixelPng(baselinePath, new byte[] { 0, 0, 0, 255 });
                WriteOnePixelPng(changedPath, new byte[] { 255, 0, 0, 255 });
                Assert.Equal(0, CountChangedPixels(baselinePath, baselinePath));
                Assert.Equal(1, CountChangedPixels(changedPath, baselinePath));
            });
        }
        finally
        {
            File.Delete(baselinePath);
            File.Delete(changedPath);
            Directory.Delete(directory);
        }
    }

    private static void WriteOnePixelPng(string path, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    [Fact]
    public async Task UiInspector_AllNineteenScreensAcrossThemesAndDpi_ReportsCleanEvidence()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("LLMWORKGUI_UI_INSPECTOR_CHILD"),
                "1",
                StringComparison.Ordinal))
        {
            await RunVisualInspectorInIsolatedTestHostAsync();
            return;
        }

        StaTestRunner.EnsureApplication();

        using var provider = UiTestHost.CreateProvider();

        var catalog = new InspectorSanitizedCatalogProvider();
        var adaptationLibrary = new WorkflowLibraryViewModel(catalogProvider: catalog);
        var mainWindowViewModel = CreateMainWindowViewModel(provider, adaptationLibrary);

        await mainWindowViewModel.InitializeAsync();

        var onboardingViewModel = await CreateOnboardingViewModelAsync();
        var commandPalette = CommandPaletteViewModel.CreateDefault(
            new InspectorPaletteCommandHost(mainWindowViewModel));
        var shell = new UnifiedWorkspaceShellViewModel(
            mainWindowViewModel,
            new StubLayoutPersistenceService());

        var version = CreateInspectorVersion();
        var package = CreateInspectorPackage();
        var project = CreateInspectorProject();

        var specs = BuildScreenSpecs(
            mainWindowViewModel,
            onboardingViewModel,
            commandPalette,
            shell,
            adaptationLibrary,
            version,
            package,
            project);

        var repositoryRoot = FindRepositoryRoot();
        var screenshotsDirectory = Path.Combine(ScreenshotFile.OutputDirectory, "ui-inspector");
        var baselineDirectory = Path.Combine(repositoryRoot, "docs", "acceptance", "visual-baseline");
        var manifestPath = Path.Combine(screenshotsDirectory, "inspection-manifest.json");
        Directory.CreateDirectory(screenshotsDirectory);

        var records = new List<CombinationRecord>();
        var contentStrings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var failures = new List<string>();

        StaTestRunner.Run(() =>
        {
            var application = System.Windows.Application.Current
                ?? throw new InvalidOperationException("The UI test application is not initialized.");
            var previousResources = application.Resources;
            application.Resources = new ResourceDictionary();

            try
            {
                foreach (var (theme, themeKey) in Themes)
                {
                    new ThemeResourceApplier().ApplyTheme(theme);

                    foreach (var spec in specs)
                    {
                    using var surface = spec.Open();

                    Layout(surface.Root, spec.DipWidth, spec.DipHeight);

                    var navSubtree = surface.NavigationSubtree ?? FindNavigationListBox(surface.Root, mainWindowViewModel);
                    var screenTexts = CollectVisibleTexts(surface.Root, navSubtree);
                    var areaTexts = ReferenceEquals(surface.ContentArea, surface.Root)
                        ? screenTexts
                        : CollectVisibleTexts(surface.ContentArea, null);

                    if (!contentStrings.ContainsKey(spec.Key))
                    {
                        contentStrings[spec.Key] = DistinctRussianStrings(areaTexts);
                    }

                    foreach (var (dpi, dpiKey) in DpiScales)
                    {
                        var pixelWidth = (int)Math.Ceiling(spec.DipWidth * dpi / 96.0);
                        var pixelHeight = (int)Math.Ceiling(spec.DipHeight * dpi / 96.0);

                        var raster = new RenderTargetBitmap(
                            pixelWidth,
                            pixelHeight,
                            dpi,
                            dpi,
                            PixelFormats.Pbgra32);
                        raster.Render(surface.Root);

                        var fileName = string.Create(
                            CultureInfo.InvariantCulture,
                            $"{spec.Key}_{themeKey}_{dpiKey}dpi.png");
                        var filePath = Path.Combine(screenshotsDirectory, fileName);
                        SavePng(raster, filePath);
                        // The inspected .NET 10 revision retains all original .NET 8 references.
                        // Missing revisions fail in CountChangedPixels; no auto-capture or fallback.
                        var baselinePath = Path.Combine(
                            baselineDirectory, "net10-guide-20261008",
                            fileName);
                        var changedPixels = CountChangedPixels(filePath, baselinePath);
                        if (changedPixels != 0)
                        {
                            failures.Add($"{fileName}: {changedPixels} pixels differ from the accepted baseline.");
                        }

                        foreach (var legacyStem in spec.LegacyFileStems)
                        {
                            var legacyFileName = string.Create(
                                CultureInfo.InvariantCulture,
                                $"{legacyStem}_{themeKey}_{dpiKey}dpi.png");
                            File.Copy(
                                filePath,
                                Path.Combine(screenshotsDirectory, legacyFileName),
                                overwrite: true);
                        }

                        var metrics = Measure(surface.Root);

                        if (metrics.Clipped.Count > 0)
                        {
                            failures.Add(BuildClippedReport(spec, themeKey, dpiKey, metrics));
                        }

                        if (metrics.Leaks.Count > 0)
                        {
                            failures.Add(BuildLeakReport(spec, themeKey, dpiKey, metrics));
                        }

                        records.Add(new CombinationRecord(
                            spec.Key,
                            themeKey,
                            dpiKey,
                            metrics.VisualElements,
                            metrics.TextBlocks,
                            metrics.Buttons,
                            metrics.Clipped.Count,
                            metrics.Leaks.Count,
                            changedPixels,
                            metrics.Leaks));
                    }
                    }
                }
            }
            finally
            {
                application.Resources = previousResources;
            }

            Assert.Equal(114, records.Count);
            Assert.Equal(19, contentStrings.Count);
            AssertMainWindowContentSetsAreDistinct(contentStrings);
            AssertOnboardingContentSetsAreDistinct(contentStrings);

            WriteManifest(manifestPath, specs, records, contentStrings);
            var failuresPath = Path.Combine(screenshotsDirectory, "inspection-failures.txt");
            if (failures.Count > 0)
            {
                File.WriteAllLines(failuresPath, failures);
            }
            else if (File.Exists(failuresPath))
            {
                File.Delete(failuresPath);
            }

            Assert.True(
                failures.Count == 0,
                string.Join(Environment.NewLine, failures.Take(200)));
        });
    }

    private static async Task RunVisualInspectorInIsolatedTestHostAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testProject = Path.Combine(repositoryRoot, "tests", "LLMWorkGUI.Ui.Tests", "LLMWorkGUI.Ui.Tests.csproj");
        var resultsDirectory = Path.Combine(repositoryRoot, "TestResults", "UiInspector");
        Directory.CreateDirectory(resultsDirectory);
        var trxPath = Path.Combine(resultsDirectory, "isolated-" + Guid.NewGuid().ToString("N") + ".trx");

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "test", testProject, "-c", "Release", "--no-build", "--nologo", "--verbosity", "quiet",
            "--filter", "FullyQualifiedName=LLMWorkGUI.Ui.Tests.UiInspectorVisualTests.UiInspector_AllNineteenScreensAcrossThemesAndDpi_ReportsCleanEvidence",
            "--logger", "trx;LogFileName=" + Path.GetFileName(trxPath),
            "--results-directory", resultsDirectory
        })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["LLMWORKGUI_UI_INSPECTOR_CHILD"] = "1";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The isolated UiInspector testhost could not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The isolated UiInspector testhost exceeded two minutes.");
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, output + Environment.NewLine + error);
        Assert.True(File.Exists(trxPath), "The isolated UiInspector testhost did not write a TRX result.");

        var trx = XDocument.Load(trxPath);
        var results = trx.Descendants()
            .Where(element => element.Name.LocalName == "UnitTestResult"
                && element.Attribute("testName")?.Value.Contains(
                    nameof(UiInspector_AllNineteenScreensAcrossThemesAndDpi_ReportsCleanEvidence),
                    StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Single(results);
        Assert.Equal("Passed", results[0].Attribute("outcome")?.Value);
    }

    private static MainWindowViewModel CreateMainWindowViewModel(
        ServiceProvider provider,
        WorkflowLibraryViewModel workflowLibrary)
    {
        return new MainWindowViewModel(
            provider.GetRequiredService<WorkspaceViewModel>(),
            provider.GetRequiredService<ProvidersAccountsViewModel>(),
            provider.GetRequiredService<QuotasViewModel>(),
            provider.GetRequiredService<HealthCenterViewModel>(),
            CreateSettingsDiagnosticsViewModel(provider),
            provider.GetRequiredService<StatusBarViewModel>(),
            provider.GetRequiredService<CliStatusViewModel>(),
            provider.GetRequiredService<ThemeSelectorViewModel>(),
            workflowLibrary,
            projects: provider.GetService<IProjectRepository>(),
            sessions: provider.GetService<ISessionRepository>(),
            executions: provider.GetService<IExecutionRepository>(),
            models: provider.GetService<ISanitizedCatalogProvider>());
    }

    // The settings/diagnostics screen renders the StorageOptions app-data root verbatim. Resolving it
    // from the live profile made the pixel gate depend on the Windows account that runs the test: the
    // accepted baselines were captured with the display-only root below, so any other account failed
    // the six settings/diagnostics comparisons on the user name alone. The inspector pins the same
    // display-only root to keep the accepted baselines valid and the visual gate reproducible on
    // every host; the product still shows the real per-user path outside the inspector.
    private const string InspectorAppDataDirectory = @"C:\Users\AgyProfile2\AppData\Local\LLMWorkGUI";

    private static SettingsDiagnosticsViewModel CreateSettingsDiagnosticsViewModel(ServiceProvider provider)
    {
        return new SettingsDiagnosticsViewModel(
            provider.GetRequiredService<ThemeSelectorViewModel>(),
            provider.GetRequiredService<CliStatusViewModel>(),
            new StorageOptions
            {
                AppDataDirectory = InspectorAppDataDirectory,
                DatabaseFileName = StorageOptions.DefaultDatabaseFileName
            });
    }

    private static async Task<OnboardingViewModel> CreateOnboardingViewModelAsync()
    {
        var cliStatus = new CliStatusViewModel(
            FakeCliDetectionService.AllDetected(Now),
            new FixedTimeProvider(Now));

        var onboarding = new OnboardingViewModel(
            cliStatus,
            new WorkflowLibraryViewModel(),
            new InMemoryApplicationSettingsRepository(),
            new InMemoryProjectRepository(),
            new FixedTimeProvider(Now));

        await onboarding.InitializeAsync();
        return onboarding;
    }

    private static WorkflowVersionItemViewModel CreateInspectorVersion() =>
        new(new WorkflowVersion(
            "ver-inspector",
            "pkg-inspector",
            1,
            HashA,
            HashA,
            WorkflowSourceType.ZipArchive,
            null,
            null,
            null,
            null,
            null,
            Now,
            null));

    private static WorkflowPackageItemViewModel CreateInspectorPackage() =>
        new(new WorkflowPackage(
            "pkg-inspector",
            "Пакет проверки интерфейса",
            "Пакет для визуальной проверки диалога адаптации",
            new[] { "проверка" },
            WorkflowSourceType.ZipArchive,
            HashA,
            HashA,
            Now,
            Now));

    private static Project CreateInspectorProject() =>
        new(
            "project-inspector",
            "Проект проверки",
            @"C:\work\inspector",
            null,
            isDirty: false,
            hasRequiredInstructions: true,
            defaultWorkflowId: null,
            defaultRoutePolicyId: null,
            DataClassification.PrivateSource);

    private static IReadOnlyList<ScreenSpec> BuildScreenSpecs(
        MainWindowViewModel mainWindow,
        OnboardingViewModel onboarding,
        CommandPaletteViewModel commandPalette,
        UnifiedWorkspaceShellViewModel shell,
        WorkflowLibraryViewModel adaptationLibrary,
        WorkflowVersionItemViewModel version,
        WorkflowPackageItemViewModel package,
        Project project)
    {
        var specs = new List<ScreenSpec>
        {
            MainWindowScreen("mainwindow_workspace", "Рабочая область", ScreenId.Workspace, mainWindow),
            MainWindowScreen("mainwindow_projects", "Проекты", ScreenId.Projects, mainWindow),
            MainWindowScreen(
                "mainwindow_providersaccounts",
                "Провайдеры и аккаунты",
                ScreenId.ProvidersAccounts,
                mainWindow),
            MainWindowScreen("mainwindow_models", "Модели", ScreenId.Models, mainWindow),
            MainWindowScreen("mainwindow_quotas", "Квоты", ScreenId.Quotas, mainWindow),
            MainWindowScreen("mainwindow_sessions", "Сессии", ScreenId.Sessions, mainWindow),
            MainWindowScreen("mainwindow_runs", "Запуски", ScreenId.Runs, mainWindow),
            MainWindowScreen("mainwindow_workflows", "Процессы", ScreenId.Workflows, mainWindow),
            MainWindowScreen(
                "mainwindow_healthcenter",
                "Центр здоровья",
                ScreenId.HealthCenter,
                mainWindow),
            MainWindowScreen(
                "mainwindow_settingsdiagnostics",
                "Настройки / Диагностика",
                ScreenId.SettingsDiagnostics,
                mainWindow),
            new ScreenSpec(
                "mainwindow_workflowadaptationdialog",
                "Диалог адаптации процесса",
                1280,
                800,
                () =>
                {
                    var openTask = adaptationLibrary.AdaptationDialog.OpenForVersionAsync(
                        version,
                        package,
                        project);
                    if (!openTask.IsCompleted)
                    {
                        throw new InvalidOperationException(
                            "The inspector catalog stub must complete synchronously so the STA dispatcher " +
                            "is never blocked by the adaptation dialog setup.");
                    }

                    openTask.GetAwaiter().GetResult();

                    return OpenMainWindow(
                        mainWindow,
                        ScreenId.Workflows,
                        adaptationLibrary.AdaptationDialog.CloseDialog);
                },
                Array.Empty<string>()),
            OnboardingScreen(
                "onboarding_step1_welcome",
                "Онбординг: Приветствие и рабочая область",
                OnboardingStepKind.WelcomeAndWorkspace,
                onboarding,
                Array.Empty<string>()),
            OnboardingScreen(
                "onboarding_step2_clidetection",
                "Онбординг: Обнаружение локальных CLI и бэкендов",
                OnboardingStepKind.LocalCliDetection,
                onboarding,
                Array.Empty<string>()),
            OnboardingScreen(
                "onboarding_step3_catalogdiscovery",
                "Онбординг: Каталог шаблонов сценариев и матрица ролей",
                OnboardingStepKind.WorkflowCatalogDiscovery,
                onboarding,
                new[] { "onboarding_step2_catalogdiscovery" }),
            OnboardingScreen(
                "onboarding_step4_safemode",
                "Онбординг: Готовность и безопасная симуляция",
                OnboardingStepKind.ReadySafeMode,
                onboarding,
                new[] { "onboarding_step3_safemode" }),
            CommandPaletteScreen(
                "commandpalette_empty",
                "Палитра команд: Начальный вид",
                query: null,
                commandPalette),
            CommandPaletteScreen(
                "commandpalette_match",
                "Палитра команд: Фильтр с результатами",
                "проект",
                commandPalette),
            CommandPaletteScreen(
                "commandpalette_nomatch",
                "Палитра команд: Без результатов",
                "несуществующийзапрос123",
                commandPalette),
            new ScreenSpec(
                "unifiedshell_threepane",
                "Единая трёхпанельная оболочка",
                1400,
                900,
                () =>
                {
                    var view = new UnifiedWorkspaceShellView { DataContext = shell };
                    var surface = OpenInWindow(view, 1400, 900);
                    var centerPane = view.FindName("CenterPane") as FrameworkElement;
                    return new ScreenSurface(surface.Window, surface.Root, centerPane ?? view, null, surface.Dispose);
                },
                Array.Empty<string>())
        };

        Assert.Equal(19, specs.Count);
        return specs;
    }

    private static ScreenSpec MainWindowScreen(
        string key,
        string title,
        ScreenId screenId,
        MainWindowViewModel mainWindow) =>
        new(
            key,
            title,
            1280,
            800,
            () => OpenMainWindow(mainWindow, screenId),
            Array.Empty<string>());

    private static ScreenSpec OnboardingScreen(
        string key,
        string title,
        OnboardingStepKind stepKind,
        OnboardingViewModel onboarding,
        IReadOnlyList<string> legacyFileStems) =>
        new(
            key,
            title,
            1280,
            800,
            () =>
            {
                onboarding.GoToStep(stepKind);
                var view = new OnboardingView { DataContext = onboarding };
                var surface = OpenInWindow(view, 1280, 800);
                var stepHost = ResolveOnboardingStepHost(view);
                return new ScreenSurface(surface.Window, surface.Root, stepHost ?? view, null, surface.Dispose);
            },
            legacyFileStems);

    private static ScreenSpec CommandPaletteScreen(
        string key,
        string title,
        string? query,
        CommandPaletteViewModel commandPalette) =>
        new(
            key,
            title,
            900,
            600,
            () =>
            {
                commandPalette.Open();
                commandPalette.SearchQuery = query ?? string.Empty;
                return OpenInWindow(new CommandPaletteView { DataContext = commandPalette }, 900, 600);
            },
            Array.Empty<string>());

    private static ScreenSurface OpenMainWindow(
        MainWindowViewModel viewModel,
        ScreenId screenId,
        Action? cleanup = null)
    {
        viewModel.NavigateTo(screenId);

        var window = new MainWindow(viewModel);
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var contentArea = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("CenterPanel"));
        var navigationSubtree = (DependencyObject?)window.FindName("NavigationListBox")
            ?? FindNavigationListBox(root, viewModel);

        return new ScreenSurface(window, root, contentArea, navigationSubtree, cleanup);
    }

    private static FrameworkElement? ResolveOnboardingStepHost(FrameworkElement view)
    {
        if (view.FindName("OnboardingCardHost") is Border cardHost && cardHost.Child is Grid grid)
        {
            return grid.Children.OfType<Grid>().FirstOrDefault(child => Grid.GetRow(child) == 2);
        }

        return null;
    }

    private static ScreenSurface OpenInWindow(
        FrameworkElement view,
        double width,
        double height,
        Action? cleanup = null)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = view,
            ShowActivated = false,
            IsHitTestVisible = false,
            WindowStyle = WindowStyle.None
        };

        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml",
                UriKind.Absolute)
        });

        window.Show();

        return new ScreenSurface(window, view, view, navigationSubtree: null, cleanup);
    }

    private static DependencyObject? FindNavigationListBox(
        FrameworkElement root,
        MainWindowViewModel viewModel)
    {
        return EnumerateVisuals(root)
            .OfType<ListBox>()
            .FirstOrDefault(listBox => ReferenceEquals(listBox.ItemsSource, viewModel.NavigationItems));
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static ScreenMetrics Measure(FrameworkElement root)
    {
        var visualElements = 0;
        var textBlocks = 0;
        var buttons = 0;
        var clipped = new List<string>();
        var leaks = new List<LeakFinding>();

        foreach (var node in EnumerateVisuals(root))
        {
            visualElements++;

            switch (node)
            {
                case TextBlock textBlock:
                    textBlocks++;
                    if (IsLaidOutVisible(textBlock) && !string.IsNullOrWhiteSpace(textBlock.Text))
                    {
                        ScanForLeaks(textBlock.Text, leaks);
                    }

                    break;
                case TextBox textBox when IsLaidOutVisible(textBox) && !string.IsNullOrWhiteSpace(textBox.Text):
                    ScanForLeaks(textBox.Text, leaks);
                    break;
            }

            if (node is Button)
            {
                buttons++;
            }

            if (node is FrameworkElement element
                && !ReferenceEquals(element, root)
                && IsLaidOutVisible(element)
                && IsClipped(element, root))
            {
                clipped.Add(DescribeElement(element, root));
            }
        }

        return new ScreenMetrics(visualElements, textBlocks, buttons, clipped, leaks);
    }

    private static bool IsLaidOutVisible(FrameworkElement element)
    {
        if (element.Visibility != Visibility.Visible
            || element.ActualWidth <= 0
            || element.ActualHeight <= 0)
        {
            return false;
        }

        // A collapsed or hidden ancestor keeps the previous layout of its subtree, so the element is
        // only genuinely rendered when every ancestor is visible as well.
        var current = VisualTreeHelper.GetParent(element);

        while (current is not null)
        {
            if (current is FrameworkElement parent && parent.Visibility != Visibility.Visible)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    private static bool IsClipped(FrameworkElement element, FrameworkElement root)
    {
        // The deliberate scroll and overlay containers clip their own content; an element that is only
        // outside the viewport of one of those containers is not a layout clipping defect.
        if (HasScrollableAncestor(element, root))
        {
            return false;
        }

        try
        {
            var bounds = element
                .TransformToAncestor(root)
                .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
            var rootBounds = new Rect(new Point(0, 0), root.RenderSize);

            const double tolerance = 1.0;

            return bounds.Left < -tolerance
                || bounds.Top < -tolerance
                || bounds.Right > rootBounds.Right + tolerance
                || bounds.Bottom > rootBounds.Bottom + tolerance;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasScrollableAncestor(FrameworkElement element, FrameworkElement root)
    {
        DependencyObject? current = element;

        while (current is not null && !ReferenceEquals(current, root))
        {
            if (current is ScrollViewer)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static string DescribeElement(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element
            .TransformToAncestor(root)
            .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

        var text = element switch
        {
            TextBlock textBlock => Truncate(textBlock.Text),
            Button button => Truncate(button.Content?.ToString()),
            _ => string.Empty
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{element.GetType().Name}('{element.Name}', {bounds.Left:F1},{bounds.Top:F1},{bounds.Right:F1},{bounds.Bottom:F1}) {text}");
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
    }

    private static List<string> CollectVisibleTexts(DependencyObject root, DependencyObject? skipSubtree)
    {
        var texts = new List<string>();

        foreach (var node in EnumerateVisuals(root, skipSubtree))
        {
            if (node is not FrameworkElement element || !IsLaidOutVisible(element))
            {
                continue;
            }

            switch (element)
            {
                case TextBlock { Text: { } text } when !string.IsNullOrWhiteSpace(text):
                    texts.Add(text);
                    break;
                case TextBox { Text: { } boxText } when !string.IsNullOrWhiteSpace(boxText):
                    texts.Add(boxText);
                    break;
            }
        }

        return texts;
    }

    private static List<string> DistinctRussianStrings(IEnumerable<string> texts)
    {
        return texts
            .Select(text => text.Trim())
            .Where(text => text.Length > 0 && text.Any(IsCyrillic))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsCyrillic(char value) => value >= '\u0400' && value <= '\u04FF';

    private static IEnumerable<DependencyObject> EnumerateVisuals(
        DependencyObject root,
        DependencyObject? skipSubtree = null)
    {
        if (ReferenceEquals(root, skipSubtree))
        {
            yield break;
        }

        yield return root;

        var childCount = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < childCount; index++)
        {
            foreach (var descendant in EnumerateVisuals(VisualTreeHelper.GetChild(root, index), skipSubtree))
            {
                yield return descendant;
            }
        }
    }

    private static void AssertMainWindowContentSetsAreDistinct(
        IReadOnlyDictionary<string, List<string>> contentStrings)
    {
        for (var left = 0; left < MainWindowScreenKeys.Length; left++)
        {
            for (var right = left + 1; right < MainWindowScreenKeys.Length; right++)
            {
                var leftKey = MainWindowScreenKeys[left];
                var rightKey = MainWindowScreenKeys[right];
                var leftSet = contentStrings[leftKey].ToHashSet(StringComparer.Ordinal);
                var rightSet = contentStrings[rightKey].ToHashSet(StringComparer.Ordinal);

                Assert.False(
                    leftSet.SetEquals(rightSet),
                    $"The content string sets of '{leftKey}' and '{rightKey}' are identical.");
                Assert.NotEqual(contentStrings[leftKey], contentStrings[rightKey]);
            }
        }

        Assert.All(
            MainWindowScreenKeys,
            key => Assert.NotEmpty(contentStrings[key]));
    }

    private static void AssertOnboardingContentSetsAreDistinct(
        IReadOnlyDictionary<string, List<string>> contentStrings)
    {
        var stepKeys = new[]
        {
            "onboarding_step1_welcome",
            "onboarding_step2_clidetection",
            "onboarding_step3_catalogdiscovery",
            "onboarding_step4_safemode"
        };

        foreach (var key in stepKeys)
        {
            Assert.True(
                contentStrings.TryGetValue(key, out var list) && list.Count >= 4,
                $"Onboarding step '{key}' must expose at least 4 distinct body strings.");
        }

        var step1 = contentStrings["onboarding_step1_welcome"];
        var step2 = contentStrings["onboarding_step2_clidetection"];
        var step3 = contentStrings["onboarding_step3_catalogdiscovery"];
        var step4 = contentStrings["onboarding_step4_safemode"];

        Assert.Contains(step1, s => s.Contains("Рабочий каталог проекта", StringComparison.Ordinal));
        Assert.Contains(step2, s => s.Contains("Повторить локальное обнаружение", StringComparison.Ordinal));
        Assert.Contains(step3, s => s.Contains("Каталог шаблонов", StringComparison.OrdinalIgnoreCase) || s.Contains("матрица ролей", StringComparison.OrdinalIgnoreCase) || s.Contains("Шаблоны", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(step4, s => s.Contains("симуляци", StringComparison.OrdinalIgnoreCase));

        for (var left = 0; left < stepKeys.Length; left++)
        {
            for (var right = left + 1; right < stepKeys.Length; right++)
            {
                var leftSet = contentStrings[stepKeys[left]].ToHashSet(StringComparer.Ordinal);
                var rightSet = contentStrings[stepKeys[right]].ToHashSet(StringComparer.Ordinal);

                Assert.False(
                    leftSet.SetEquals(rightSet),
                    $"Onboarding steps '{stepKeys[left]}' and '{stepKeys[right]}' have identical content string sets.");
            }
        }
    }

    private static void WriteManifest(
        string manifestPath,
        IReadOnlyList<ScreenSpec> specs,
        IReadOnlyList<CombinationRecord> records,
        IReadOnlyDictionary<string, List<string>> contentStrings)
    {
        var screens = new List<ManifestScreen>(specs.Count);

        foreach (var spec in specs)
        {
            var screenRecords = records
                .Where(record => string.Equals(record.ScreenKey, spec.Key, StringComparison.Ordinal))
                .ToList();

            var samples = BuildSampleRussianStrings(contentStrings[spec.Key]);

            Assert.True(
                samples.Count >= 4,
                $"The screen '{spec.Key}' must expose at least 4 distinct Russian content strings, " +
                $"but produced {samples.Count}.");

            screens.Add(new ManifestScreen
            {
                ScreenKey = spec.Key,
                Title = spec.Title,
                Combinations = screenRecords.Count,
                VisualElements = screenRecords.Sum(record => record.VisualElements),
                TextBlocks = screenRecords.Sum(record => record.TextBlocks),
                Buttons = screenRecords.Sum(record => record.Buttons),
                ClippedCount = screenRecords.Sum(record => record.ClippedCount),
                LeaksCount = screenRecords.Sum(record => record.LeaksCount),
                ChangedPixels = screenRecords.Sum(record => record.ChangedPixels),
                SampleRussianStrings = samples
            });
        }

        var manifest = new InspectionManifest
        {
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Runner = "LLMWorkGUI.Ui.Tests.UiInspectorVisualTests",
            TotalCombinations = records.Count,
            TotalVisualElements = records.Sum(record => record.VisualElements),
            TotalTextBlocks = records.Sum(record => record.TextBlocks),
            TotalButtons = records.Sum(record => record.Buttons),
            ClippedCount = records.Sum(record => record.ClippedCount),
            LeaksCount = records.Sum(record => record.LeaksCount),
            ChangedPixels = records.Sum(record => record.ChangedPixels),
            Screens = screens
        };

        var json = JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        File.WriteAllText(manifestPath, json);
    }

    private static List<string> BuildSampleRussianStrings(
        IReadOnlyList<string> contentStrings)
    {
        const int sampleSize = 8;
        return contentStrings.Take(sampleSize).ToList();
    }

    private static void SavePng(RenderTargetBitmap raster, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(raster));

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        const int maxAttempts = 10;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = File.Create(path);
                encoder.Save(stream);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }

        throw new IOException(
            $"The inspection screenshot '{path}' could not be written after {maxAttempts} attempts.");
    }

    private static long CountChangedPixels(string screenshotPath, string baselinePath)
    {
        if (!File.Exists(baselinePath))
        {
            throw new FileNotFoundException("The visual regression baseline is missing.", baselinePath);
        }

        var baseline = ReadPngPixels(baselinePath);
        var actual = ReadPngPixels(screenshotPath);
        if (baseline.PixelWidth != actual.PixelWidth || baseline.PixelHeight != actual.PixelHeight)
        {
            throw new InvalidDataException($"Visual baseline dimensions differ for '{Path.GetFileName(screenshotPath)}': "
                + $"actual {actual.PixelWidth}x{actual.PixelHeight}, "
                + $"baseline {baseline.PixelWidth}x{baseline.PixelHeight}.");
        }

        var stride = actual.PixelWidth * 4;
        var baselinePixels = new byte[stride * actual.PixelHeight];
        var actualPixels = new byte[baselinePixels.Length];
        baseline.CopyPixels(baselinePixels, stride, 0);
        actual.CopyPixels(actualPixels, stride, 0);

        long changed = 0;
        for (var offset = 0; offset < actualPixels.Length; offset += 4)
        {
            if (actualPixels[offset] != baselinePixels[offset]
                || actualPixels[offset + 1] != baselinePixels[offset + 1]
                || actualPixels[offset + 2] != baselinePixels[offset + 2]
                || actualPixels[offset + 3] != baselinePixels[offset + 3])
            {
                changed++;
            }
        }

        return changed;
    }

    private static BitmapSource ReadPngPixels(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LLMWorkGUI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "The repository root (LLMWorkGUI.sln) was not found above the test output directory.");
    }

    private static string BuildClippedReport(
        ScreenSpec spec,
        string themeKey,
        string dpiKey,
        ScreenMetrics metrics)
    {
        var lines = metrics.Clipped.Take(15).Select(entry => "  - " + entry);

        return $"'{spec.Key}' ({themeKey}, {dpiKey}dpi) reported {metrics.Clipped.Count} clipped " +
            "element(s):" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static string BuildLeakReport(
        ScreenSpec spec,
        string themeKey,
        string dpiKey,
        ScreenMetrics metrics)
    {
        var lines = metrics.Leaks
            .Take(30)
            .Select(leak => $"  - '{leak.Word}' in \"{Truncate(leak.Text)}\"");

        return $"'{spec.Key}' ({themeKey}, {dpiKey}dpi) reported {metrics.Leaks.Count} unallowed " +
            "English token(s):" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    // -------------------------------------------------------------------------------------------------
    // Closed English allowlist for the inspected UI states. Multiword phrases must match as complete
    // phrases; their individual words are not globally permitted. This gate does not inspect hidden
    // or non-default states, which require separate tests.
    // -------------------------------------------------------------------------------------------------
    private static readonly Regex LatinWordRegex = new("[A-Za-z]+", RegexOptions.Compiled);

    private static readonly (HashSet<string> Words, Regex[] Phrases) AllowedLatin = BuildAllowedLatin();

    private static readonly Regex UrlRegex = new(
        @"[A-Za-z][A-Za-z0-9+.\-]*://\S*",
        RegexOptions.Compiled);

    private static readonly Regex InlineDrivePathRegex = new(
        @"[A-Za-z]:[\\/][^\s,;)]*",
        RegexOptions.Compiled);

    private static readonly Regex TimestampRegex = new(
        @"\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?",
        RegexOptions.Compiled);

    private static readonly Regex VersionRegex = new(
        @"\bv?\d+(?:\.\d+)+[A-Za-z0-9\-]*\b|\bv\d+\b",
        RegexOptions.Compiled);

    private static readonly Regex HexBlobRegex = new(
        @"\b[0-9a-fA-F]{12,}\b",
        RegexOptions.Compiled);

    private static (HashSet<string> Words, Regex[] Phrases) BuildAllowedLatin()
    {
        var phrases = new[]
        {
            // TASK-070 §2.A.3 tokens.
            "Not reported", "Not reported (stale)", "Not reported (error)", "Not supported",
            "Unavailable", "None", "Not probed",
            "SYNTHETIC", "Architect", "Implementer", "Reviewer", "UiReviewer", "Tester", "Coordinator",
            "OpenCode", "Cursor ACP", "Mirasim", "LLMGateway", "gpt-4o", "claude-3-5-sonnet", "deepseek-v4.1-flash",
            "grok-4.7-high", "star-cliproxy", "LocalMockAiServer",
            "Workflow Studio", "Workflow Activity Monitor", "Activity Monitor", "Workflow Console",
            "LLM Work GUI",
            "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7", "Ctrl+8", "Ctrl+9",
            "Ctrl+0", "Ctrl+K", "Ctrl+P", "Ctrl+Shift+P", "Enter", "Esc", "Tab", "F5",
            "Windows", "x64", ".NET", "8.0", "WPF", "AppData", "Local", "SQLite", "DPAPI", "JSON-RPC",
            "stdio", "SHA-256", "GUID", "PID", "ID", "ms", "s", "КиБ", "МиБ", "ГиБ",
            "DefaultRootDirectory",

            // Shipped wire/protocol identifiers displayed on screen.
            "CLI", "PATH", "URL", "Diff", "Markdown", "BLOB", "ADR", "GUI", "Microsoft", "HTTP",
            "Cursor Agent", "Pinned", "SessionSticky", "QuotaFirst", "PriorityFirst", "Balanced",
            "ManualOnly", "CostSaving", "Quality", "Speed", "TechnicalWriter", "Approver", "Executor",
            "Succeeded", "API", "OpenAI", "Unknown", "UI",
            "LLMWorkGUI",
            // Built-in Workflow Studio template title; the onboarding role-matrix label is localized.
            "Standard development workflow", "codex",
            "UnknownHighRisk", "bypassPermissions", "fail-closed", "route-opencode",
            "read"
        };

        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exactPhrases = new List<Regex>();

        foreach (var phrase in phrases)
        {
            var matches = LatinWordRegex.Matches(phrase);
            if (matches.Count > 1 || phrase.Any(character => !char.IsLetter(character)))
            {
                exactPhrases.Add(new Regex($@"(?<![A-Za-z]){Regex.Escape(phrase)}(?![A-Za-z])",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase));
                continue;
            }

            foreach (Match match in matches)
            {
                words.Add(match.Value);
            }
        }

        return (words, exactPhrases.OrderByDescending(phrase => phrase.ToString().Length).ToArray());
    }

    private static void ScanForLeaks(string text, List<LeakFinding> leaks)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var value = text.Trim();

        value = UrlRegex.Replace(value, " ");
        value = InlineDrivePathRegex.Replace(value, " ");
        value = TimestampRegex.Replace(value, " ");
        value = VersionRegex.Replace(value, " ");
        value = HexBlobRegex.Replace(value, " ");

        foreach (var phrase in AllowedLatin.Phrases)
        {
            value = phrase.Replace(value, " ");
        }

        foreach (Match match in LatinWordRegex.Matches(value))
        {
            if (!AllowedLatin.Words.Contains(match.Value))
            {
                leaks.Add(new LeakFinding(match.Value, text.Trim()));
            }
        }
    }

    private sealed record ScreenSpec(
        string Key,
        string Title,
        int DipWidth,
        int DipHeight,
        Func<ScreenSurface> Open,
        IReadOnlyList<string> LegacyFileStems);

    private sealed record CombinationRecord(
        string ScreenKey,
        string ThemeKey,
        string DpiKey,
        int VisualElements,
        int TextBlocks,
        int Buttons,
        int ClippedCount,
        int LeaksCount,
        long ChangedPixels,
        IReadOnlyList<LeakFinding> Leaks);

    private sealed record LeakFinding(string Word, string Text);

    private sealed class ScreenMetrics
    {
        public ScreenMetrics(
            int visualElements,
            int textBlocks,
            int buttons,
            IReadOnlyList<string> clipped,
            IReadOnlyList<LeakFinding> leaks)
        {
            VisualElements = visualElements;
            TextBlocks = textBlocks;
            Buttons = buttons;
            Clipped = clipped;
            Leaks = leaks;
        }

        public int VisualElements { get; }

        public int TextBlocks { get; }

        public int Buttons { get; }

        public IReadOnlyList<string> Clipped { get; }

        public IReadOnlyList<LeakFinding> Leaks { get; }
    }

    private sealed class ScreenSurface : IDisposable
    {
        private readonly Action? _cleanup;

        public ScreenSurface(
            Window window,
            FrameworkElement root,
            FrameworkElement contentArea,
            DependencyObject? navigationSubtree,
            Action? cleanup)
        {
            Window = window;
            Root = root;
            ContentArea = contentArea;
            NavigationSubtree = navigationSubtree;
            _cleanup = cleanup;
        }

        public Window Window { get; }

        public FrameworkElement Root { get; }

        public FrameworkElement ContentArea { get; }

        public DependencyObject? NavigationSubtree { get; }

        public void Dispose()
        {
            Window.Close();
            _cleanup?.Invoke();
        }
    }

    private sealed class InspectorSanitizedCatalogProvider : ISanitizedCatalogProvider
    {
        public Task<SanitizedCapabilityCatalog> GetSanitizedCatalogAsync(
            CancellationToken cancellationToken = default)
        {
            var models = new[]
            {
                new SanitizedModelInfo(
                    "gpt-4o",
                    "gpt-4o",
                    ModelCapabilityFlags.Chat,
                    Array.Empty<string>(),
                    new[] { "balanced" },
                    ContextWindow: 128000,
                    HealthState.Healthy,
                    IsRoutable: true)
                {
                    // A route is only offered with a complete identity and a backend-native model id.
                    AccountId = "acct-gpt-4o",
                    ProviderProfileId = "prov-1",
                    Backend = BackendType.OpenCode,
                    BackendModelId = "gpt-4o"
                }
            };

            return Task.FromResult(new SanitizedCapabilityCatalog(
                Array.Empty<SanitizedProviderInfo>(),
                models,
                Now));
        }
    }

    private sealed class InspectorPaletteCommandHost : ICommandPaletteCommandHost
    {
        private readonly MainWindowViewModel _mainWindow;

        public InspectorPaletteCommandHost(MainWindowViewModel mainWindow)
        {
            _mainWindow = mainWindow;
        }

        public IReadOnlyList<ScreenViewModel> Screens => _mainWindow.Screens;

        public void NavigateTo(ScreenId screenId) => _mainWindow.NavigateTo(screenId);

        public void SetTheme(AppTheme theme)
        {
        }

        public void RefreshCli()
        {
        }

        public void OpenWorkflowStudio()
        {
        }

        public void OpenWorkflowActivityMonitor()
        {
        }

        public void ImportWorkflow()
        {
        }

        public void ExportWorkflow()
        {
        }
    }

    private sealed class StubLayoutPersistenceService : ILayoutPersistenceService
    {
        public ShellLayoutState State { get; set; } = new() { ActiveScreen = string.Empty };

        public ShellLayoutState Load() => State;

        public void Save(ShellLayoutState state) => State = state;
    }

    private sealed class InspectionManifest
    {
        public string GeneratedAt { get; init; } = string.Empty;

        public string Runner { get; init; } = string.Empty;

        public int TotalCombinations { get; init; }

        public int TotalVisualElements { get; init; }

        public int TotalTextBlocks { get; init; }

        public int TotalButtons { get; init; }

        public int ClippedCount { get; init; }

        public int LeaksCount { get; init; }

        public long ChangedPixels { get; init; }

        public List<ManifestScreen> Screens { get; init; } = new();
    }

    private sealed class ManifestScreen
    {
        public string ScreenKey { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public int Combinations { get; init; }

        public int VisualElements { get; init; }

        public int TextBlocks { get; init; }

        public int Buttons { get; init; }

        public int ClippedCount { get; init; }

        public int LeaksCount { get; init; }

        public long ChangedPixels { get; init; }

        public List<string> SampleRussianStrings { get; init; } = new();
    }
}

[CollectionDefinition("UiInspector visual isolation", DisableParallelization = true)]
public sealed class UiInspectorVisualIsolationCollection
{
}
