using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Workflows;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Composition-root evidence for the workflow library. A green view-model test is meaningless if the
/// shipped XAML never receives the screen, so the production registration and template are asserted here
/// instead of being assumed.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class WorkflowLibraryCompositionTests
{
    private static readonly string HashA = "sha256:" + new string('a', 64);
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnavailableLibraryNotice_IsNotCoveredByTheEmptyBrowser()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var library = new WorkflowLibraryViewModel();
            var (window, root) = OpenPanel(library);
            try
            {
                var notice = Assert.Single(FindVisualDescendants<TextBlock>(root)
                    .Where(text => text.Text == library.LibraryUnavailableNotice && text.IsVisible));
                Assert.True(notice.IsVisible);
                var point = notice.TranslatePoint(new Point(notice.ActualWidth / 2, notice.ActualHeight / 2), root);
                Assert.Same(notice, root.InputHitTest(point));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async System.Threading.Tasks.Task AdaptationBeforeSession_UsesTheAvailableDialogWidth()
    {
        StaTestRunner.EnsureApplication();
        await StaTestRunner.Run(async () =>
        {
            var library = new WorkflowLibraryViewModel();
            var package = new WorkflowPackageItemViewModel(new WorkflowPackage(
                "pkg-layout", "Layout", "Synthetic layout fixture", Array.Empty<string>(),
                WorkflowSourceType.ZipArchive, HashA, HashA, Now, Now));
            var version = new WorkflowVersionItemViewModel(new WorkflowVersion(
                "ver-layout", "pkg-layout", 1, HashA, HashA, WorkflowSourceType.ZipArchive,
                null, null, null, null, null, Now, null));
            var project = new Project("project-layout", "Layout", @"D:\work\layout-fixture", null,
                false, true, null, null, DataClassification.PrivateSource);
            await library.AdaptationDialog.OpenForVersionAsync(version, package, project);
            var (window, root) = OpenPanel(library);
            try
            {
                var state = Assert.Single(FindVisualDescendants<Grid>(root).Where(grid =>
                    System.Windows.Data.BindingOperations.GetBinding(grid, UIElement.VisibilityProperty) is { } binding
                    && binding.Path.Path == "AdaptationDialog.IsSessionActive"
                    && binding.Converter is LLMWorkGUI.App.Converters.InverseBooleanToVisibilityConverter));
                var parent = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetParent(state));
                Assert.True(state.IsVisible);
                Assert.InRange(parent.ActualWidth - state.ActualWidth, -1, 1);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AddAppUi_RegistersTheWorkflowLibraryScreenAndShowsItInTheShell()
    {
        using var provider = UiTestHost.CreateProvider();

        var library = provider.GetRequiredService<WorkflowLibraryViewModel>();
        var mainWindow = provider.GetRequiredService<MainWindowViewModel>();

        // The shell must show the real screen, not the catalog placeholder.
        Assert.Same(library, mainWindow.Screens.Single(screen => screen.Id == ScreenId.Workflows));

        mainWindow.NavigateTo(ScreenId.Workflows);

        Assert.Same(library, mainWindow.CurrentScreen);
        Assert.Equal("Процессы", library.Title);
        Assert.Equal("Ctrl+8", library.Shortcut);
    }

    [Fact]
    public void AddAppUi_WithoutWorkflowServices_ReportsTheLibraryUnavailable()
    {
        using var provider = UiTestHost.CreateProvider();

        var library = provider.GetRequiredService<WorkflowLibraryViewModel>();

        // The UI-only graph has no workflow infrastructure, so the screen must degrade honestly.
        Assert.False(library.IsLibraryAvailable);
        Assert.False(library.IsPreviewAvailable);
        Assert.False(library.IsBindingAvailable);
        Assert.False(library.IsImportAvailable);
        Assert.False(library.IsExportAvailable);
        Assert.False(library.IsAdaptationAvailable);
        Assert.False(library.IsActivationAvailable);
        Assert.False(library.AdaptationDialog.IsVisible);
    }

    [Fact]
    public void MainWindowViewModel_WithoutTheWorkflowLibrary_KeepsTheCatalogScreen()
    {
        using var provider = UiTestHost.CreateProvider();

        var mainWindow = new MainWindowViewModel(
            provider.GetRequiredService<WorkspaceViewModel>(),
            provider.GetRequiredService<ProvidersAccountsViewModel>(),
            provider.GetRequiredService<QuotasViewModel>(),
            provider.GetRequiredService<HealthCenterViewModel>(),
            provider.GetRequiredService<SettingsDiagnosticsViewModel>(),
            provider.GetRequiredService<StatusBarViewModel>(),
            provider.GetRequiredService<CliStatusViewModel>(),
            provider.GetRequiredService<ThemeSelectorViewModel>());

        Assert.IsType<CatalogScreenViewModel>(
            mainWindow.Screens.Single(screen => screen.Id == ScreenId.Workflows));
    }

    [Fact]
    public void AddAppUi_ConsumesTheRegisteredWorkflowServices()
    {
        using var provider = CreateProviderWithWorkflowServices();

        var library = provider.GetRequiredService<WorkflowLibraryViewModel>();

        Assert.True(library.IsLibraryAvailable);
        Assert.True(library.IsPreviewAvailable);
        Assert.True(library.IsBindingAvailable);
        Assert.True(library.IsImportAvailable);
        Assert.True(library.IsExportAvailable);
        Assert.True(library.IsProjectSelectionAvailable);
        Assert.True(library.IsAdaptationAvailable);
        Assert.True(library.IsActivationAvailable);
        Assert.False(library.AdaptationDialog.IsVisible);
    }

    [Fact]
    public void ShippedXaml_RendersTheWorkflowLibraryThroughTheProductionTemplate()
    {
        var package = new WorkflowPackage(
            "pkg-1",
            "Release Workflow",
            "Coordinates release tasks",
            new[] { "release" },
            WorkflowSourceType.ZipArchive,
            HashA,
            HashA,
            Now,
            Now);

        var library = new WorkflowLibraryViewModel(
            new InMemoryWorkflowPackageRepository(),
            new InMemoryWorkflowVersionRepository(),
            new InMemoryWorkflowBindingRepository());
        library.Packages.Add(new WorkflowPackageItemViewModel(package));

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenPanel(library);

            try
            {
                var texts = ReadTextBlocks(root);

                Assert.Contains("Процессы", texts);
                Assert.Contains(library.LibraryNote, texts);
                Assert.Contains("Release Workflow", texts);

                // Markdown is rendered as read-only text, never through an HTML/WebView component.
                Assert.Contains(FindVisualDescendants<TextBox>(root), textBox => textBox.IsReadOnly);

                // The file list is virtualized, so large archives do not materialize every row.
                Assert.NotEmpty(FindVisualDescendants<VirtualizingStackPanel>(root));

                // The adaptation and rollback actions are shipped in the project binding panel, and the
                // adaptation dialog overlay exists in the production template.
                var buttons = FindVisualDescendants<Button>(root)
                    .Select(button => button.Content?.ToString() ?? string.Empty)
                    .ToArray();

                Assert.Contains("Адаптировать", buttons);
                Assert.Contains("Откатить", buttons);
                Assert.Contains("Адаптация процесса (ТЗ §6.14)", texts);
                Assert.Contains(
                    texts,
                    text => text.Contains("Каждый шаг является явной командой", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WorkflowLibrary_WithoutAnyService_StillRendersTheUnavailableState()
    {
        var library = new WorkflowLibraryViewModel();

        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenPanel(library);

            try
            {
                var texts = ReadTextBlocks(root);

                Assert.Contains("Процессы", texts);
                Assert.Contains(library.LibraryUnavailableNotice, texts);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ServiceProvider CreateProviderWithWorkflowServices()
    {
        var packages = new InMemoryWorkflowPackageRepository();
        var versions = new InMemoryWorkflowVersionRepository();

        var services = new ServiceCollection();

        services.AddApplication();
        services.AddSingleton<IApplicationSettingsRepository>(new InMemoryApplicationSettingsRepository());
        services.AddSingleton<IAccountRepository>(new InMemoryAccountRepository());
        services.AddSingleton<IQuotaSnapshotRepository>(new InMemoryQuotaSnapshotRepository());
        services.AddSingleton<IThemeResourceApplier>(new RecordingThemeResourceApplier());
        services.AddSingleton<ISystemThemeProvider>(new FakeSystemThemeProvider());
        services.AddSingleton<ICliDetectionService>(FakeCliDetectionService.Degraded());
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddSingleton(new StorageOptions
        {
            AppDataDirectory = null,
            DatabaseFileName = StorageOptions.DefaultDatabaseFileName
        });

        var bindings = new InMemoryWorkflowBindingRepository();

        services.AddSingleton<IWorkflowPackageRepository>(packages);
        services.AddSingleton<IWorkflowVersionRepository>(versions);
        services.AddSingleton<IWorkflowBindingRepository>(bindings);
        services.AddSingleton<IWorkflowBindingService>(serviceProvider => new WorkflowBindingService(
            serviceProvider.GetRequiredService<IWorkflowBindingRepository>(),
            serviceProvider.GetRequiredService<IWorkflowPackageRepository>(),
            serviceProvider.GetRequiredService<IWorkflowVersionRepository>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IWorkflowPreviewService>(new StubWorkflowPreviewService());
        services.AddSingleton<IWorkflowImportService>(new StubWorkflowImportService(packages, versions));
        services.AddSingleton<IWorkflowExportService>(new StubWorkflowExportService());
        services.AddSingleton<IProjectRepository>(new InMemoryProjectRepository());
        services.AddSingleton<ISanitizedCatalogProvider>(new LibraryStubCatalogProvider());
        services.AddSingleton<IWorkflowAdaptationService>(new LibraryStubWorkflowAdaptationService());
        services.AddSingleton<IWorkflowActivationService>(
            new LibraryStubWorkflowActivationService(bindings, versions));

        services.AddAppUi();

        return services.BuildServiceProvider();
    }

    private static (Window Window, FrameworkElement Root) OpenPanel(WorkflowLibraryViewModel viewModel)
    {
        var host = new ContentControl { Content = viewModel };

        var window = new Window
        {
            Width = 1280,
            Height = 760,
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

        host.Measure(new Size(1280, 760));
        host.Arrange(new Rect(0, 0, 1280, 760));
        host.UpdateLayout();

        return (window, host);
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
