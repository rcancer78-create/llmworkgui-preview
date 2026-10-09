using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// Observable QuickViewer-style WPF UI tests for the Quotas &amp; Routing Dashboard (ТЗ §12.4.2).
/// All scenarios run on deterministic test doubles and persist screenshots for the release report.
/// </summary>
[Trait("Category", "VisualUi")]
public sealed class QuotasUiVisualTests
{
    private static string ScreenshotOutputDir => ScreenshotFile.OutputDirectory;

    [Fact]
    public void QuotasDashboard_RendersFullLayout_WithDistinctBuckets_AndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            using var fixture = CreateFixture();
            var now = fixture.TimeProvider.GetUtcNow();

            fixture.Profiles.Save(new ProviderProfile(
                "prov-codex",
                "OpenAI Codex (multi-account)",
                BackendType.OpenCode,
                "https://api.openai.com/v1",
                executablePath: null,
                DataClassification.PrivateSource,
                isEnabled: true));

            // Class 1: trusted numeric (ExactProviderReported + PluginReported).
            fixture.Accounts.Save(NewAccount("acc-exact", "Codex Primary", priority: 10));
            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-exact",
                "acc-exact",
                QuotaProvenance.ExactProviderReported,
                now,
                new[] { new QuotaBucket("gpt-4o", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900, now.AddHours(12), hardReserve: 50) },
                "prov-codex",
                "gpt-4o",
                now.AddMinutes(10)));

            fixture.Accounts.Save(NewAccount("acc-plugin", "AGY Plugin Account", priority: 8));
            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-plugin",
                "acc-plugin",
                QuotaProvenance.PluginReported,
                now,
                new[] { new QuotaBucket("tokens", QuotaLimitUnit.Tokens, QuotaLimitWindow.PerMonth, 200000, 50000, 150000, now.AddDays(10)) },
                "prov-codex",
                "gpt-4o",
                now.AddMinutes(10)));

            // Class 2: estimated / locally calculated.
            fixture.Accounts.Save(NewAccount("acc-estimated", "Local Counter Account", priority: 5));
            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-estimated",
                "acc-estimated",
                QuotaProvenance.LocallyCalculated,
                now,
                new[] { new QuotaBucket("requests", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 500, 100, 400, now.AddHours(6), confidence: QuotaConfidence.Low) },
                "prov-codex",
                "gpt-4o",
                now.AddMinutes(10)));

            // Class 3: unknown / unsupported (must stay textual, never 0% / 100%).
            fixture.Accounts.Save(NewAccount("acc-unknown", "Hidden Quota Account", priority: 5));
            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-unknown",
                "acc-unknown",
                QuotaProvenance.Unknown,
                now,
                buckets: null,
                providerProfileId: "prov-codex",
                modelId: "gpt-4o"));

            fixture.Accounts.Save(NewAccount("acc-unsupported", "Cursor (No Quota API)", priority: 5));
            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-unsupported",
                "acc-unsupported",
                QuotaProvenance.Unsupported,
                now,
                buckets: null,
                providerProfileId: "prov-codex",
                modelId: "gpt-4o"));

            // Class 4: error / stale with warning badges and sanitized messages.
            fixture.Accounts.Save(NewAccount("acc-error", "Failing Quota Account", priority: 5));
            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-error",
                "acc-error",
                QuotaProvenance.Error,
                now,
                buckets: null,
                providerProfileId: "prov-codex",
                modelId: "gpt-4o",
                errorMessage: "Quota fetch failed: api_key=sk-supersecret123456789 with Bearer abcdefgh12345678"));

            fixture.Accounts.Save(NewAccount("acc-stale", "Stale Snapshot Account", priority: 5));
            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-stale",
                "acc-stale",
                QuotaProvenance.Stale,
                now.AddHours(-3),
                new[] { new QuotaBucket("requests", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 400, 600) },
                "prov-codex",
                "gpt-4o",
                now.AddHours(-2)));

            var vm = fixture.ViewModel;
            AwaitDeterministic(vm.LoadQuotasAsync());

            Assert.Equal(7, vm.Buckets.Count);
            Assert.True(vm.HasBuckets);
            Assert.Contains("Всего корзин: 7", vm.TotalBucketsDisplay);

            var exactBucket = vm.Buckets.Single(bucket => bucket.SnapshotId == "snap-exact");
            Assert.True(exactBucket.IsNumericQuota);
            Assert.True(exactBucket.CanCalculateScore);
            Assert.True(exactBucket.IsTrustedProvenance);
            Assert.Equal("900", exactBucket.RemainingDisplay);
            Assert.Equal("100", exactBucket.UsedDisplay);
            Assert.Equal("1000", exactBucket.TotalLimitDisplay);
            Assert.Equal("За день", exactBucket.LimitWindowDisplay);
            Assert.Equal("Точная", exactBucket.ConfidenceDisplay);

            var pluginBucket = vm.Buckets.Single(bucket => bucket.SnapshotId == "snap-plugin");
            Assert.True(pluginBucket.CanCalculateScore);
            Assert.Equal("ДАННЫЕ ПЛАГИНА", pluginBucket.ProvenanceBadgeText);

            var estimatedBucket = vm.Buckets.Single(bucket => bucket.SnapshotId == "snap-estimated");
            Assert.True(estimatedBucket.IsEstimatedLocal);
            Assert.Equal("ЛОКАЛЬНЫЙ РАСЧЁТ", estimatedBucket.ProvenanceBadgeText);
            Assert.False(estimatedBucket.CanCalculateScore);
            Assert.Equal("За день", estimatedBucket.LimitWindowDisplay);
            Assert.Equal("Низкая", estimatedBucket.ConfidenceDisplay);

            var unknownBucket = vm.Buckets.Single(bucket => bucket.SnapshotId == "snap-unknown");
            Assert.True(unknownBucket.IsUnknownOrUnsupported);
            Assert.False(unknownBucket.IsNumericQuota);
            Assert.False(unknownBucket.CanCalculateScore);
            Assert.Equal("Не сообщено", unknownBucket.RemainingDisplay);
            Assert.Equal("Не сообщено", unknownBucket.UsedDisplay);
            Assert.Equal("Не сообщено", unknownBucket.TotalLimitDisplay);
            Assert.Equal("Нет", unknownBucket.LimitWindowDisplay);
            Assert.Equal("Нет", unknownBucket.ConfidenceDisplay);
            Assert.Equal("Числовая оценка недоступна (неизвестно)", unknownBucket.ScoreEligibilityDisplay);

            var unsupportedBucket = vm.Buckets.Single(bucket => bucket.SnapshotId == "snap-unsupported");
            Assert.Equal("НЕ ПОДДЕРЖИВАЕТСЯ", unsupportedBucket.ProvenanceBadgeText);
            Assert.Equal("Не поддерживается", unsupportedBucket.RemainingDisplay);
            Assert.False(unsupportedBucket.CanCalculateScore);

            var errorBucket = vm.Buckets.Single(bucket => bucket.SnapshotId == "snap-error");
            Assert.True(errorBucket.HasError);
            Assert.Equal("ОШИБКА", errorBucket.ProvenanceBadgeText);
            Assert.DoesNotContain("sk-supersecret", errorBucket.ErrorDisplay);
            Assert.DoesNotContain("abcdefgh12345678", errorBucket.ErrorDisplay);
            Assert.Contains("***REDACTED***", errorBucket.ErrorDisplay);
            Assert.False(errorBucket.IsNumericQuota);

            var staleBucket = vm.Buckets.Single(bucket => bucket.SnapshotId == "snap-stale");
            Assert.True(staleBucket.IsErrorOrStale);
            Assert.False(staleBucket.IsNumericQuota);
            Assert.Equal("Нет актуальных данных", staleBucket.RemainingDisplay);
            Assert.Contains("устарел", staleBucket.ErrorDisplay);

            var (window, mainWindowVm) = OpenDashboard(fixture);
            Assert.Same(vm, mainWindowVm.CurrentScreen);

            var textBlocks = ReadTextBlocks(window);
            Assert.Contains("Квоты", textBlocks);
            Assert.Contains("Codex Primary", textBlocks);
            Assert.Contains("AGY Plugin Account", textBlocks);
            Assert.Contains("Local Counter Account", textBlocks);
            Assert.Contains("Hidden Quota Account", textBlocks);
            Assert.Contains("Cursor (No Quota API)", textBlocks);
            Assert.Contains("Failing Quota Account", textBlocks);
            Assert.Contains("Stale Snapshot Account", textBlocks);
            Assert.Contains("ТОЧНЫЕ", textBlocks);
            Assert.Contains("ДАННЫЕ ПЛАГИНА", textBlocks);
            Assert.Contains("ЛОКАЛЬНЫЙ РАСЧЁТ", textBlocks);
            Assert.Contains("НЕИЗВЕСТНО", textBlocks);
            Assert.Contains("НЕ ПОДДЕРЖИВАЕТСЯ", textBlocks);
            Assert.Contains("ОШИБКА", textBlocks);
            Assert.Contains("УСТАРЕЛ", textBlocks);
            Assert.Contains("Доступно числовое оценивание (актуальная доверенная квота)", textBlocks);
            Assert.Contains("Числовая оценка недоступна (неизвестно)", textBlocks);
            Assert.Contains("Числовая оценка недоступна (не поддерживается)", textBlocks);
            Assert.Contains("Окно:", textBlocks);
            Assert.Contains("За день", textBlocks);
            Assert.Contains("Достоверность:", textBlocks);
            Assert.Contains("Низкая", textBlocks);

            var screenshotPath = CaptureScreenshot(window, "quotas_dashboard_screen.png");
            Assert.True(new FileInfo(screenshotPath).Length > 1000, "Screenshot file is too small.");
        });
    }

    [Fact]
    public void QuotasDashboard_SimulatesMultiAccountRouting_DisplaysSynchronizedSnapshot_AndGeneratesScreenshot()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            using var fixture = CreateFixture();
            var now = fixture.TimeProvider.GetUtcNow();

            fixture.Profiles.Save(new ProviderProfile(
                "prov-codex",
                "OpenAI Codex (multi-account)",
                BackendType.OpenCode,
                "https://api.openai.com/v1",
                executablePath: null,
                DataClassification.PrivateSource,
                isEnabled: true));

            fixture.Accounts.Save(NewAccount("acc-alpha", "Codex Alpha", priority: 10));
            fixture.Accounts.Save(NewAccount("acc-beta", "Codex Beta", priority: 10));

            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-alpha",
                "acc-alpha",
                QuotaProvenance.ExactProviderReported,
                now,
                new[] { new QuotaBucket("gpt-4o", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900, now.AddHours(12), hardReserve: 50) },
                "prov-codex",
                "gpt-4o",
                now.AddMinutes(10)));

            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-beta",
                "acc-beta",
                QuotaProvenance.Unsupported,
                now,
                buckets: null,
                providerProfileId: "prov-codex",
                modelId: "gpt-4o"));

            var vm = fixture.ViewModel;
            AwaitDeterministic(vm.LoadQuotasAsync());

            vm.SelectedPolicy = RoutingPolicy.Balanced;
            vm.SimulationProviderProfileId = "prov-codex";
            vm.SimulationModelId = "gpt-4o";
            AwaitDeterministic(vm.SimulateRoutingAsync());

            Assert.True(vm.IsRoutingSuccess);
            Assert.NotNull(vm.ActiveRoutingDecision);
            Assert.Equal("acc-alpha", vm.ActiveRoutingDecision!.SelectedAccount!.Id);
            Assert.Equal("snap-alpha", vm.ActiveQuotaSnapshotId);
            Assert.Equal("snap-alpha", vm.ActiveQuotaSnapshotIdDisplay);
            Assert.StartsWith("Итого ", vm.RoutingScoreSummary, StringComparison.Ordinal);
            Assert.Contains("Квота ", vm.RoutingScoreSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("Total ", vm.RoutingScoreSummary, StringComparison.Ordinal);

            // Dashboard and router must reference the same quota snapshot ID (ТЗ §6.6).
            var synchronizedBucket = vm.Buckets.Single(bucket => bucket.SnapshotId == vm.ActiveQuotaSnapshotId);
            Assert.Equal("acc-alpha", synchronizedBucket.AccountId);

            Assert.Single(vm.CandidateScores);
            var candidateScore = vm.CandidateScores[0];
            Assert.Equal("ВЫБРАН", candidateScore.SelectionBadgeText);
            Assert.StartsWith("Итого ", candidateScore.TotalScoreDisplay, StringComparison.Ordinal);
            Assert.Contains("Квота ", candidateScore.ComponentBreakdownDisplay, StringComparison.Ordinal);
            Assert.StartsWith("Снимок ", candidateScore.QuotaSnapshotDisplay, StringComparison.Ordinal);
            Assert.Equal("acc-alpha", candidateScore.AccountId);
            Assert.True(candidateScore.TotalScore > 0.0);
            Assert.Equal("snap-alpha", candidateScore.QuotaSnapshotId);

            Assert.Single(vm.RejectedCandidates);
            Assert.Equal("acc-beta", vm.RejectedCandidates[0].AccountId);
            Assert.Contains("cannot be used for automatic scoring", vm.RejectedCandidates[0].Reason);

            var (window, _) = OpenDashboard(fixture);

            var textBlocks = ReadTextBlocks(window);
            Assert.Contains("МАРШРУТ ВЫБРАН", textBlocks);
            Assert.Contains("snap-alpha", textBlocks);
            Assert.Contains("Codex Alpha (acc-alpha)", textBlocks);
            Assert.Contains("Оценки кандидатов", textBlocks);
            Assert.Contains("ВЫБРАН", textBlocks);
            Assert.Contains("Отклонённые кандидаты", textBlocks);
            Assert.Contains("acc-beta", textBlocks);
            Assert.Contains("ОТКЛОНЁН", textBlocks);

            var screenshotPath = CaptureScreenshot(window, "quotas_routing_explanation.png");
            Assert.True(new FileInfo(screenshotPath).Length > 1000, "Screenshot file is too small.");
        });
    }

    [Fact]
    public void QuotasDashboard_PolicySwitching_And_AccountCooldown_UpdatesUiDeterministically()
    {
        StaTestRunner.EnsureApplication();

        StaTestRunner.Run(() =>
        {
            using var fixture = CreateFixture();
            var now = fixture.TimeProvider.GetUtcNow();

            fixture.Profiles.Save(new ProviderProfile(
                "prov-codex",
                "OpenAI Codex (multi-account)",
                BackendType.OpenCode,
                "https://api.openai.com/v1",
                executablePath: null,
                DataClassification.PrivateSource,
                isEnabled: true));

            fixture.Accounts.Save(NewAccount("acc-alpha", "Codex Alpha", priority: 10));
            fixture.Accounts.Save(NewAccount("acc-beta", "Codex Beta", priority: 10));
            fixture.Accounts.Save(NewAccount("acc-unknown", "Codex Unknown", priority: 10));

            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-alpha",
                "acc-alpha",
                QuotaProvenance.ExactProviderReported,
                now,
                new[] { new QuotaBucket("gpt-4o", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900, now.AddHours(12), hardReserve: 50) },
                "prov-codex",
                "gpt-4o",
                now.AddMinutes(10)));

            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-beta",
                "acc-beta",
                QuotaProvenance.ExactProviderReported,
                now,
                new[] { new QuotaBucket("gpt-4o", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 500, 500, now.AddHours(12), hardReserve: 50) },
                "prov-codex",
                "gpt-4o",
                now.AddMinutes(10)));

            fixture.Snapshots.Save(new QuotaSnapshot(
                "snap-unknown",
                "acc-unknown",
                QuotaProvenance.Unknown,
                now,
                buckets: null,
                providerProfileId: "prov-codex",
                modelId: "gpt-4o"));

            var vm = fixture.ViewModel;
            AwaitDeterministic(vm.LoadQuotasAsync());

            vm.SimulationProviderProfileId = "prov-codex";
            vm.SimulationModelId = "gpt-4o";
            Assert.NotNull(vm.RefreshAllCommand);
            Assert.NotNull(vm.RefreshAccountCommand);

            // 1. Pinned selects exactly the requested account.
            vm.SelectedPolicy = RoutingPolicy.Pinned;
            vm.PinnedAccountId = "acc-beta";
            AwaitDeterministic(vm.SimulateRoutingAsync());

            Assert.True(vm.IsRoutingSuccess);
            Assert.Equal("acc-beta", vm.ActiveRoutingDecision!.SelectedAccount!.Id);
            Assert.Equal("snap-beta", vm.ActiveQuotaSnapshotId);

            var (window, _) = OpenDashboard(fixture);
            var pinnedTexts = ReadTextBlocks(window);
            Assert.Contains("МАРШРУТ ВЫБРАН", pinnedTexts);
            Assert.Contains("Pinned", pinnedTexts);

            // 2. Cooldown on the pinned account stops the turn: no auto failover to acc-alpha.
            fixture.Accounts.Save(new Account(
                "acc-beta",
                "prov-codex",
                "Codex Beta",
                providerNativeId: null,
                AuthState.Valid,
                manualPriority: 10,
                isEnabled: true,
                HealthState.Healthy,
                cooldownUntil: now.AddMinutes(30),
                disabledUntil: null,
                maxConcurrentExecutions: 4,
                reserveThreshold: null));

            AwaitDeterministic(vm.SimulateRoutingAsync());

            Assert.False(vm.IsRoutingSuccess);
            Assert.Contains("Automatic failover is forbidden", vm.RoutingExplanation);
            Assert.Empty(vm.CandidateScores);

            Layout(window);
            var cooldownTexts = ReadTextBlocks(window);
            Assert.Contains("МАРШРУТ НЕ НАЙДЕН (выбор остановлен)", cooldownTexts);

            // 3. Balanced policy excludes the cooled-down account and never scores Unknown quota.
            vm.SelectedPolicy = RoutingPolicy.Balanced;
            AwaitDeterministic(vm.SimulateRoutingAsync());

            Assert.True(vm.IsRoutingSuccess);
            Assert.Equal("acc-alpha", vm.ActiveRoutingDecision!.SelectedAccount!.Id);
            Assert.Equal("snap-alpha", vm.ActiveQuotaSnapshotId);
            Assert.Single(vm.CandidateScores);
            Assert.Equal("acc-alpha", vm.CandidateScores[0].AccountId);

            Assert.Equal(2, vm.RejectedCandidates.Count);
            Assert.Contains(vm.RejectedCandidates, rejected =>
                rejected.AccountId == "acc-beta" && rejected.Reason.Contains("cooldown", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(vm.RejectedCandidates, rejected =>
                rejected.AccountId == "acc-unknown" && rejected.Reason.Contains("cannot be used for automatic scoring", StringComparison.Ordinal));
            Assert.DoesNotContain(vm.CandidateScores, score => score.AccountId == "acc-unknown");

            var unknownBucket = vm.Buckets.Single(bucket => bucket.AccountId == "acc-unknown");
            Assert.False(unknownBucket.IsNumericQuota);
            Assert.False(unknownBucket.CanCalculateScore);
            Assert.Equal("Не сообщено", unknownBucket.RemainingDisplay);
            Assert.Equal("Числовая оценка недоступна (неизвестно)", unknownBucket.ScoreEligibilityDisplay);

            Layout(window);
            var balancedTexts = ReadTextBlocks(window);
            Assert.Contains("МАРШРУТ ВЫБРАН", balancedTexts);
            Assert.Contains("Balanced", balancedTexts);
            Assert.Contains("acc-beta", balancedTexts);
            Assert.Contains("acc-unknown", balancedTexts);
            Assert.Contains("Числовая оценка недоступна (неизвестно)", balancedTexts);
        });
    }

    [Fact]
    public void QuotasDashboard_ManualRefresh_RespectsSchedulerRapidRefreshGuard()
    {
        var scheduler = new RecordingQuotaRefreshScheduler();
        var vm = new QuotasViewModel(refreshScheduler: scheduler);
        var now = DateTimeOffset.UtcNow;

        vm.ApplySnapshot(new QuotaSnapshot(
            "snap-guard",
            "acc-guard",
            QuotaProvenance.ExactProviderReported,
            now,
            new[] { new QuotaBucket("req", QuotaLimitUnit.Requests, QuotaLimitWindow.PerDay, 1000, 100, 900) },
            "prov-codex",
            "gpt-4o",
            now.AddMinutes(10)));

        var bucket = Assert.Single(vm.Buckets);
        AwaitDeterministic(vm.RefreshAccountAsync(bucket));

        Assert.True(scheduler.RefreshRequested);
        Assert.False(scheduler.LastForce, "Manual refresh must not bypass the MinRefreshInterval rapid-refresh guard.");
        Assert.Equal("acc-guard", scheduler.LastAccountId);
    }

    private static DashboardFixture CreateFixture()
    {
        return new DashboardFixture();
    }

    private static Account NewAccount(string id, string displayName, int priority)
    {
        return new Account(
            id,
            "prov-codex",
            displayName,
            providerNativeId: null,
            AuthState.Valid,
            priority,
            isEnabled: true,
            HealthState.Healthy,
            cooldownUntil: null,
            disabledUntil: null,
            maxConcurrentExecutions: 4,
            reserveThreshold: null);
    }

    private static (Window Window, MainWindowViewModel MainWindowViewModel) OpenDashboard(DashboardFixture fixture)
    {
        // Screenshots must not depend on the theme left behind by other UI test collections.
        new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);

        var mainWindowVm = fixture.MainWindowViewModel;
        mainWindowVm.NavigateCommand.Execute(ScreenId.Quotas);
        var window = new MainWindow(mainWindowVm);
        Layout(window);

        return (window, mainWindowVm);
    }

    private static void AwaitDeterministic(Task task)
    {
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException("Deterministic test doubles must complete synchronously.");
        }

        task.GetAwaiter().GetResult();
    }

    private static void Layout(Window window)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        root.Measure(new Size(1400, 900));
        root.Arrange(new Rect(0, 0, 1400, 900));
        root.UpdateLayout();
    }

    private static string CaptureScreenshot(Window window, string fileName)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var width = (int)Math.Max(1400, root.ActualWidth);
        var height = (int)Math.Max(900, root.ActualHeight);

        var renderBitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
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

    private static string[] ReadTextBlocks(Window window)
    {
        var root = Assert.IsAssignableFrom<DependencyObject>(window.Content);
        return FindVisualDescendants<TextBlock>(root)
            .Select(textBlock => textBlock.Text)
            .ToArray();
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

    private sealed class DashboardFixture : IDisposable
    {
        private readonly ServiceProvider _provider;

        public DashboardFixture()
        {
            TimeProvider = new FakeTimeProvider();
            Accounts = new InMemoryAccountRepository();
            Snapshots = new InMemoryQuotaSnapshotRepository();
            Profiles = new InMemoryProviderProfileRepository();

            RoutingEngine = new RoutingEngine(
                Accounts,
                Snapshots,
                weights: null,
                timeProvider: TimeProvider,
                providerProfileRepository: Profiles);
            ViewModel = new QuotasViewModel(
                Snapshots,
                refreshScheduler: null,
                RoutingEngine,
                Accounts,
                Profiles,
                TimeProvider);

            _provider = CreateProvider(ViewModel);
        }

        public FakeTimeProvider TimeProvider { get; }

        public InMemoryAccountRepository Accounts { get; }

        public InMemoryQuotaSnapshotRepository Snapshots { get; }

        public InMemoryProviderProfileRepository Profiles { get; }

        public RoutingEngine RoutingEngine { get; }

        public QuotasViewModel ViewModel { get; }

        public MainWindowViewModel MainWindowViewModel => _provider.GetRequiredService<MainWindowViewModel>();

        public void Dispose()
        {
            _provider.Dispose();
        }

        private static ServiceProvider CreateProvider(QuotasViewModel quotasViewModel)
        {
            var services = new ServiceCollection();

            services.AddApplication();
            services.AddSingleton<IApplicationSettingsRepository>(new InMemoryApplicationSettingsRepository());
            services.AddSingleton<IThemeResourceApplier>(new RecordingThemeResourceApplier());
            services.AddSingleton<ISystemThemeProvider>(new FakeSystemThemeProvider());
            services.AddSingleton<ICliDetectionService>(FakeCliDetectionService.Degraded());
            services.AddSingleton<TimeProvider>(new FakeTimeProvider());
            services.AddSingleton(new StorageOptions
            {
                AppDataDirectory = null,
                DatabaseFileName = StorageOptions.DefaultDatabaseFileName
            });

            services.AddSingleton(quotasViewModel);
            services.AddAppUi();

            return services.BuildServiceProvider();
        }
    }
}

/// <summary>
/// Deterministic in-memory account repository used by the observable Quotas UI tests.
/// </summary>
internal sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly List<Account> _accounts = new();

    public void Save(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _accounts.RemoveAll(existing => existing.Id == account.Id);
        _accounts.Add(account);
    }

    public Task<Account?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_accounts.FirstOrDefault(account => account.Id == id));
    }

    public Task<IReadOnlyList<Account>> ListByProviderProfileIdAsync(string providerProfileId, CancellationToken cancellationToken = default)
    {
        var list = _accounts.Where(account => account.ProviderProfileId == providerProfileId).ToList();
        return Task.FromResult<IReadOnlyList<Account>>(list);
    }

    public Task<IReadOnlyList<Account>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<Account>>(_accounts.ToList());
    }

    public Task SaveAsync(Account account, CancellationToken cancellationToken = default)
    {
        Save(account);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _accounts.RemoveAll(account => account.Id == id);
        return Task.CompletedTask;
    }

    public Task UpdateAuthStateAsync(string id, AuthState authState, CancellationToken cancellationToken = default)
    {
        var account = _accounts.FirstOrDefault(existing => existing.Id == id);
        if (account is not null)
        {
            _accounts.Remove(account);
            _accounts.Add(account.WithAuthState(authState));
        }

        return Task.CompletedTask;
    }

    public Task UpdateCooldownAsync(string id, DateTimeOffset? cooldownUntil, CancellationToken cancellationToken = default)
    {
        var account = _accounts.FirstOrDefault(existing => existing.Id == id);
        if (account is not null)
        {
            _accounts.Remove(account);
            _accounts.Add(account.WithCooldown(cooldownUntil));
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetSecretReferenceAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = _accounts.FirstOrDefault(existing => existing.Id == accountId);
        return Task.FromResult(account?.SecretReference);
    }
}

/// <summary>
/// Deterministic in-memory quota snapshot repository used by the observable Quotas UI tests.
/// </summary>
internal sealed class InMemoryQuotaSnapshotRepository : IQuotaSnapshotRepository
{
    private readonly List<QuotaSnapshot> _snapshots = new();

    public void Save(QuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots.RemoveAll(existing => existing.Id == snapshot.Id);
        _snapshots.Add(snapshot);
    }

    public Task<QuotaSnapshot?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_snapshots.FirstOrDefault(snapshot => snapshot.Id == id));
    }

    public Task<QuotaSnapshot?> GetLatestForAccountAsync(string accountId, string? modelId = null, CancellationToken cancellationToken = default)
    {
        var found = _snapshots
            .Where(snapshot => snapshot.AccountId == accountId && (modelId is null || snapshot.ModelId == modelId))
            .OrderByDescending(snapshot => snapshot.CapturedAt)
            .FirstOrDefault();

        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<QuotaSnapshot>> ListLatestByAccountIdAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var list = _snapshots
            .Where(snapshot => snapshot.AccountId == accountId)
            .OrderByDescending(snapshot => snapshot.CapturedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<QuotaSnapshot>>(list);
    }

    public Task<IReadOnlyList<QuotaSnapshot>> ListAllLatestAsync(CancellationToken cancellationToken = default)
    {
        var list = _snapshots
            .GroupBy(snapshot => snapshot.AccountId)
            .Select(group => group.OrderByDescending(snapshot => snapshot.CapturedAt).First())
            .ToList();

        return Task.FromResult<IReadOnlyList<QuotaSnapshot>>(list);
    }

    public Task SaveAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Save(snapshot);
        return Task.CompletedTask;
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var removed = _snapshots.RemoveAll(snapshot => snapshot.CapturedAt < cutoff);
        return Task.FromResult(removed);
    }
}

/// <summary>
/// Deterministic in-memory provider profile repository used by the observable Quotas UI tests.
/// </summary>
internal sealed class InMemoryProviderProfileRepository : IProviderProfileRepository
{
    private readonly Dictionary<string, ProviderProfile> _profiles = new(StringComparer.Ordinal);

    public void Save(ProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profiles[profile.Id] = profile;
    }

    public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<ProviderProfile>>(_profiles.Values.ToList());
    }

    public Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        _profiles.TryGetValue(id, out var profile);
        return Task.FromResult(profile);
    }

    public Task UpsertAsync(ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default)
    {
        Save(profile);
        return Task.CompletedTask;
    }

    public Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_profiles.Remove(id));
    }
}

/// <summary>
/// Deterministic scheduler double that records the rapid-refresh guard flag passed by the dashboard.
/// </summary>
internal sealed class RecordingQuotaRefreshScheduler : IQuotaRefreshScheduler
{
    public event EventHandler<QuotaRefreshedEventArgs>? QuotaRefreshed
    {
        add { }
        remove { }
    }

    public bool RefreshRequested { get; private set; }

    public bool LastForce { get; private set; }

    public string? LastAccountId { get; private set; }

    public Task<QuotaSnapshot> RefreshAccountNowAsync(
        string providerProfileId,
        string accountId,
        string? modelId = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        RefreshRequested = true;
        LastForce = force;
        LastAccountId = accountId;
        return Task.FromResult(QuotaSnapshot.CreateUnknown(accountId, providerProfileId, modelId));
    }

    public Task RefreshAllEligibleAccountsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public AccountQuotaRefreshStatus? GetStatus(string accountId) => null;

    public IReadOnlyList<AccountQuotaRefreshStatus> GetAllStatuses() => Array.Empty<AccountQuotaRefreshStatus>();

    public void ScheduleNextRefresh(
        string accountId,
        string providerProfileId,
        DateTimeOffset? expiresAt = null,
        bool isFailure = false)
    {
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
