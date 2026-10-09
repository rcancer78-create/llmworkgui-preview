using System.IO;
using System.Windows;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.Shell;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Data;
using LLMWorkGUI.Application.Diagnostics;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Composition evidence for TASK-071-B4: the production host resolves the unified workspace shell and
/// the Phase 12 hardening services, the UI-only host keeps the hardening diagnostics surface
/// unavailable, and <see cref="MainWindow"/> mounts the shell without breaking the baseline layout.
/// </summary>
public sealed class UnifiedWorkspaceShellCompositionTests
{
    [Fact]
    public void ShellProviderDisposalStopsAcceptingActivityQueries()
    {
        var services = new ServiceCollection();
        services.AddAppUi();
        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<BoundedActivityQueryExecutor>();
        Assert.Same(executor, provider.GetRequiredService<IActivityQueryExecutor>());

        provider.Dispose();
        var acceptedBefore = executor.EnqueuedCount;
        executor.Enqueue(() => throw new InvalidOperationException("Disposed executor accepted work."));
        Assert.Equal(acceptedBefore, executor.EnqueuedCount);
        executor.Dispose();
    }

    [Fact]
    public async Task ProductionHost_ResolvesShellAndHardeningServices()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "llmworkgui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        IHost? host = null;

        try
        {
            var builder = HostBootstrapper.CreateHostBuilder(appDataDirectory: tempDir);
            builder.ConfigureServices((_, services) =>
            {
                services.AddAppUi();
                services.AddUnifiedWorkspaceShell();
            });

            host = builder.Build();
            await HostBootstrapper.InitializeAsync(host);

            Assert.NotNull(host.Services.GetRequiredService<UnifiedWorkspaceShellViewModel>());

            Assert.NotNull(host.Services.GetRequiredService<IDatabaseBackupService>());
            Assert.NotNull(host.Services.GetRequiredService<IDiagnosticBundleService>());
            Assert.NotNull(host.Services.GetRequiredService<IAppCrashRecoveryService>());
            Assert.NotNull(host.Services.GetRequiredService<IRetentionCleanupService>());
            Assert.NotNull(host.Services.GetRequiredService<ILongRunningExecutionService>());
            Assert.NotNull(host.Services.GetRequiredService<IQuotaPollingSoakRunner>());

            var viewModel = host.Services.GetRequiredService<HardeningDiagnosticsViewModel>();

            Assert.True(viewModel.IsAvailable);
        }
        finally
        {
            host?.Dispose();
            SqliteConnection.ClearAllPools();

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void UiOnlyHost_HardeningDiagnosticsSurfaceReportsUnavailable()
    {
        using var provider = UiTestHost.CreateProvider();

        var viewModel = provider.GetRequiredService<HardeningDiagnosticsViewModel>();

        Assert.False(viewModel.IsAvailable);
    }

    [Fact]
    public void MainWindow_WithShellViewModel_MountsAndDisplaysShell()
    {
        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            var mainViewModel = provider.GetRequiredService<MainWindowViewModel>();
            var shellViewModel = provider.GetRequiredService<UnifiedWorkspaceShellViewModel>();

            var window = new MainWindow(mainViewModel, shellViewModel);

            var shellView = Assert.IsType<UnifiedWorkspaceShellView>(window.FindName("UnifiedWorkspaceShell"));

            Assert.Equal(Visibility.Visible, shellView.Visibility);
            Assert.Same(shellViewModel, shellView.DataContext);

            var topBar = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("TopBar"));
            Assert.Equal(Visibility.Collapsed, topBar.Visibility);

            var banner = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("DegradedBanner"));
            Assert.Equal(Visibility.Collapsed, banner.Visibility);

            var panes = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ShellPanes"));
            Assert.Equal(Visibility.Collapsed, panes.Visibility);

            var statusBar = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("GlobalStatusBar"));
            Assert.Equal(Visibility.Collapsed, statusBar.Visibility);
        });
    }

    [Fact]
    public void MainWindow_WithoutShellViewModel_DisplaysBaselineLayout()
    {
        StaTestRunner.Run(() =>
        {
            using var provider = UiTestHost.CreateProvider();
            var mainViewModel = provider.GetRequiredService<MainWindowViewModel>();

            var window = new MainWindow(mainViewModel);
            Layout(window);

            var shellView = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("UnifiedWorkspaceShell"));
            Assert.Equal(Visibility.Collapsed, shellView.Visibility);
            Assert.Null(shellView.DataContext);

            var topBar = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("TopBar"));
            Assert.Equal(Visibility.Visible, topBar.Visibility);

            var panes = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ShellPanes"));
            Assert.Equal(Visibility.Visible, panes.Visibility);

            var statusBar = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("GlobalStatusBar"));
            Assert.Equal(Visibility.Visible, statusBar.Visibility);

            var banner = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("DegradedBanner"));
            Assert.Equal(Visibility.Collapsed, banner.Visibility);

            Assert.Equal(12, window.InputBindings.Count);
        });
    }

    private static void Layout(Window window)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        root.Measure(new Size(1400, 900));
        root.Arrange(new Rect(0, 0, 1400, 900));
        root.UpdateLayout();
    }
}
