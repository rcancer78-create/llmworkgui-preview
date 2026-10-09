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
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Observable QuickViewer-style WPF UI test for the Health Center (ТЗ §12.4.2, ROADMAP Phase 7). It
/// renders the shipped XAML DataTemplate over the real <see cref="HealthCenterService"/> on in-memory
/// stores, asserts what the operator actually sees and persists screenshots. No model quota is spent.
/// </summary>
[Trait("Category", "VisualUi")]
[Collection("Health visual isolation")]
public sealed class HealthCenterUiVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    private readonly InMemoryHealthStateStore _states = new();
    private readonly InMemoryHealthEventStore _events = new();
    private readonly HealthUiTimeProvider _time = new();

    [Fact]
    public void HealthCenter_ShowsAQuarantinedScopeAndItsRecoveryAudit_AndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var service = CreateService();
        var viewModel = CreateViewModel(service);
        var scope = HealthScope.ForAccount("openai-primary");

        // Drive the scope out of routing through observed steps only: failure -> cooldown -> probe -> fail.
        RunDeterministic(async () =>
        {
            await service.ReportFailureAsync(scope, HealthErrorClass.Provider4xx5xx);
            _time.Advance(TimeSpan.FromMinutes(10));
            await service.ExpireCooldownAsync(scope);
            await service.StartProbeAsync(scope);
            await service.CompleteProbeAsync(scope, succeeded: false);
            await viewModel.RefreshAsync();
        });

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenScreen(viewModel);

            try
            {
                var texts = ReadTextBlocks(root);

                // The operator sees the scope, its state and that it is out of routing.
                Assert.Contains("openai-primary", texts);
                Assert.Contains(nameof(HealthState.QuarantinedAuto), texts);
                Assert.Contains("Исключён из маршрутизации", texts);

                // The probe requirement is stated rather than implied.
                Assert.Contains(
                    texts,
                    text => text.Contains("прохождение проверки", StringComparison.OrdinalIgnoreCase));

                // The recovery audit is visible with the observed error class.
                Assert.Contains(nameof(HealthErrorClass.Provider4xx5xx), texts);
                Assert.Contains("Переход", texts);

                // Nothing claims a verified recovery, because no probe ever passed.
                Assert.DoesNotContain("Подтверждённое восстановление", texts);

                var screenshotPath = CaptureScreenshot(root, "health_center_quarantine.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HealthCenter_ShowsAForcedRouteAsUnverified_AndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var service = CreateService();
        var viewModel = CreateViewModel(service);
        var scope = HealthScope.ForRoute("route-gpt4o");

        RunDeterministic(async () =>
        {
            await service.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
            _time.Advance(TimeSpan.FromMinutes(10));
            await service.ExpireCooldownAsync(scope);
            await service.ForceEnableAsync(scope, "The operator accepted the risk during the incident.");
            await viewModel.RefreshAsync();
        });

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenScreen(viewModel);

            try
            {
                var texts = ReadTextBlocks(root);

                Assert.Contains("route-gpt4o", texts);
                Assert.Contains(nameof(HealthState.ForcedEnabled), texts);
                Assert.Contains("В маршрутизации", texts);

                // A forced route is visibly unverified, and the audit says so too.
                Assert.Contains(
                    texts,
                    text => text.Contains("не подтверждено", StringComparison.OrdinalIgnoreCase));
                Assert.Contains("Принудительно, без проверки", texts);

                // It must never be presented as a verified recovery.
                Assert.DoesNotContain("Подтверждённое восстановление", texts);

                var screenshotPath = CaptureScreenshot(root, "health_center_forced_enable.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HealthCenter_AfterAnExecutedProbe_ShowsTheObservedResult_AndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var service = CreateService();
        var probe = new StubHealthProbeService(service);
        var viewModel = CreateViewModel(service, probe);
        var scope = HealthScope.ForAccount("anthropic-main");

        RunDeterministic(async () =>
        {
            await service.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
            _time.Advance(TimeSpan.FromMinutes(10));
            await service.ExpireCooldownAsync(scope);
            await viewModel.RefreshAsync();

            // The pinned model probe is the only one that may verify a recovery, so the operator's
            // model and cost acknowledgement are required before it runs.
            viewModel.ModelIdToProbe = "mock-model";
            viewModel.CostPreviewAcknowledged = true;

            await viewModel.RunProbeAsync();
        });

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenScreen(viewModel);

            try
            {
                var texts = ReadTextBlocks(root);

                // The probe actually ran, so the recovery is verified and the route is back in routing.
                Assert.Contains("anthropic-main", texts);
                Assert.Contains(nameof(HealthState.Healthy), texts);
                Assert.Contains("В маршрутизации", texts);
                Assert.Contains("Подтверждённое восстановление", texts);

                // The observed probe result is visible to the operator.
                Assert.Contains(
                    texts,
                    text => text.Contains("provider.example", StringComparison.Ordinal));

                // The screen states which probe actually verifies a recovery.
                Assert.Contains(
                    texts,
                    text => text.Contains("закреплённая проверка", StringComparison.OrdinalIgnoreCase));

                var screenshotPath = CaptureScreenshot(root, "health_center_probe_executed.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HealthCenter_ShowsImpactedSessionsOfAnUnhealthyRoute_AndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        var service = CreateService();
        var sessions = new InMemorySessionStore();
        var viewModel = CreateViewModel(service, impactedSessions: new ImpactedSessionService(service, sessions));
        var scope = HealthScope.ForAccount("account-1");

        sessions.Add("session-running", SessionState.Active, activeExecutionId: "exec-live");
        sessions.Add("session-waiting", SessionState.Idle);

        RunDeterministic(async () =>
        {
            await service.ReportFailureAsync(scope, HealthErrorClass.Provider4xx5xx);
            await viewModel.RefreshAsync();
        });

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var (window, root) = OpenScreen(viewModel);

            try
            {
                var texts = ReadTextBlocks(root);

                Assert.Contains("Затронутые сессии", texts);

                // The operator sees which real work the unhealthy route has put at risk, and that
                // stopping the turn is a recommendation rather than an action taken here.
                Assert.Contains("session-running", texts);
                Assert.Contains(
                    "Выполняемый ход должен быть остановлен — замена сессии требует вашего подтверждения",
                    texts);

                // And which session is merely blocked, so nothing is lost there.
                Assert.Contains("session-waiting", texts);
                Assert.Contains("Следующий ход заблокирован", texts);

                // ТЗ §6.5: a replacement session is confirmed by the user, never created automatically.
                Assert.Contains(
                    texts,
                    text => text.Contains("never created automatically", StringComparison.OrdinalIgnoreCase));

                var screenshotPath = CaptureScreenshot(root, "health_center_impacted_sessions.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HealthCenter_WithoutTheService_ShowsTheUnavailableNoticeAndNoInventedScope()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

            var viewModel = CreateViewModel(healthCenter: null);
            var (window, root) = OpenScreen(viewModel);

            try
            {
                var texts = ReadTextBlocks(root);

                // A missing service is stated explicitly instead of being shown as health.
                Assert.Contains(viewModel.UnavailableNotice, texts);
                Assert.Contains(HealthCenterViewModel.UnavailableIndicator, texts);

                // No scope row and no invented state are rendered.
                Assert.DoesNotContain("В маршрутизации", texts);
                Assert.DoesNotContain(nameof(HealthState.Healthy), texts);

                var screenshotPath = CaptureScreenshot(root, "health_center_unavailable.png");
                Assert.True(new FileInfo(screenshotPath).Length > 0, "The screenshot must not be empty.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void HealthCenter_ShowsRecoveryFormAndRequiresCostConfirmation_AndGeneratesScreenshots(AppTheme theme)
    {
        StaTestRunner.EnsureApplication();
        var service = CreateService();
        var viewModel = CreateViewModel(service, new StubHealthProbeService(service));
        var scope = HealthScope.ForAccount("synthetic-recovery-account");
        RunDeterministic(async () =>
        {
            await service.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
            _time.Advance(TimeSpan.FromMinutes(10));
            await service.ExpireCooldownAsync(scope);
            await viewModel.RefreshAsync();
        });

        StaTestRunner.Run(() =>
        {
            var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
            var originalDictionaries = dictionaries.ToArray();
            Window? window = null;
            try
            {
                new ThemeResourceApplier().ApplyTheme(theme);
                var opened = OpenScreen(viewModel);
                window = opened.Window;
                window.SetResourceReference(Window.BackgroundProperty, "Theme.Background");
                var root = opened.Root;
                Assert.True(window.IsVisible);
                var button = Assert.Single(EnumerateVisuals(root).OfType<Button>(),
                    item => item.Content as string == "Проверить модель");
                var checkbox = Assert.Single(EnumerateVisuals(root).OfType<CheckBox>(),
                    item => item.Content as string == "Оценка стоимости подтверждена");
                var model = Assert.Single(EnumerateVisuals(root).OfType<TextBox>(), item => item.IsVisible
                    && item.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == "ModelIdToProbe");
                Assert.True(model.IsVisible);
                Assert.True(button.IsVisible);
                Assert.True(checkbox.IsVisible);
                Assert.False(button.IsEnabled);
                Assert.False(viewModel.CostPreviewAcknowledged);

                checkbox.IsChecked = true;
                checkbox.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!.UpdateSource();
                FlushCommandRequery();
                Assert.False(button.Command!.CanExecute(button.CommandParameter));
                Assert.False(button.IsEnabled);
                checkbox.IsChecked = false;
                checkbox.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!.UpdateSource();

                model.Text = "mock-model";
                model.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                FlushCommandRequery();
                root.UpdateLayout();
                Assert.Equal("mock-model", viewModel.ModelIdToProbe);
                Assert.False(button.Command!.CanExecute(button.CommandParameter));
                var before = CaptureScreenshot(window, "health_recovery_unconfirmed_" + theme + ".png");
                Assert.True(new FileInfo(before).Length > 0);

                checkbox.IsChecked = true;
                checkbox.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!
                    .UpdateSource();
                FlushCommandRequery();
                root.UpdateLayout();
                Assert.True(viewModel.CostPreviewAcknowledged);
                Assert.True(button.Command.CanExecute(button.CommandParameter));
                Assert.True(button.IsEnabled);
                Assert.DoesNotContain("Подтверждённое восстановление", ReadTextBlocks(root));
                var after = CaptureScreenshot(window, "health_recovery_confirmed_" + theme + ".png");
                Assert.True(new FileInfo(after).Length > 0);
                // Confirmation enables the real command but does not execute a model or fabricate recovery.
                Assert.Equal(HealthState.ProbeRequired, viewModel.SelectedScope!.State);
            }
            finally
            {
                window?.Close();
                dictionaries.Clear();
                foreach (var dictionary in originalDictionaries) { dictionaries.Add(dictionary); }
            }
        });
    }

    private static void FlushCommandRequery()
    {
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private HealthCenterViewModel CreateViewModel(
        IHealthCenterService? healthCenter,
        IHealthProbeService? probeService = null,
        IImpactedSessionService? impactedSessions = null) =>
        new(
            new CliStatusViewModel(FakeCliDetectionService.Degraded(), new FakeTimeProvider()),
            healthCenter,
            probeService,
            impactedSessions);

    private HealthCenterService CreateService() =>
        new(_states, _events, _time, new HealthPolicy { FailureThreshold = 1 });

    /// <summary>
    /// Runs the arrangement on deterministic in-memory doubles. Everything completes synchronously, so
    /// a pending task would mean a real asynchronous dependency slipped into the test.
    /// </summary>
    private static void RunDeterministic(Func<Task> arrange)
    {
        var task = arrange();

        Assert.True(task.IsCompleted, "Deterministic test doubles must complete synchronously.");

        task.GetAwaiter().GetResult();
    }

    private static (Window Window, FrameworkElement Root) OpenScreen(HealthCenterViewModel viewModel)
    {
        // The screen is rendered through the production DataTemplate, loaded exactly as MainWindow
        // loads it, so the test exercises the shipped XAML rather than a test-only copy.
        var host = new ContentControl { Content = viewModel };

        var window = new Window
        {
            Width = 1000,
            Height = 640,
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

        host.Measure(new Size(1000, 640));
        host.Arrange(new Rect(0, 0, 1000, 640));
        host.UpdateLayout();

        return (window, host);
    }

    private static string CaptureScreenshot(FrameworkElement root, string fileName)
    {
        var width = (int)Math.Max(1000, root.ActualWidth);
        var height = (int)Math.Max(640, root.ActualHeight);

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
        EnumerateVisuals(root)
            .OfType<System.Windows.Controls.TextBlock>()
            .Select(block => block.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
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

[CollectionDefinition("Health visual isolation", DisableParallelization = true)]
public sealed class HealthVisualIsolationCollection;
