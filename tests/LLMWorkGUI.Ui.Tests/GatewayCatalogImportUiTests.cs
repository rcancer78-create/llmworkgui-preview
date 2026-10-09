using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LLMGateway.Core;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("Account configuration UI isolation")]
public sealed class GatewayCatalogImportUiTests
{
    [Fact]
    public void ImportRunsOffDispatcher_RejectsReentry_AndCancels()
    {
        StaTestRunner.Run(() =>
        {
            var uiThread = Environment.CurrentManagedThreadId;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var workerThread = 0;
            var service = new FakeImport(token =>
            {
                workerThread = Environment.CurrentManagedThreadId;
                entered.Set(); release.Wait(token);
                return Task.FromResult(Empty());
            });
            var refreshed = false;
            var vm = new GatewayCatalogImportViewModel(service, () => true, () => { refreshed = true; return Task.CompletedTask; });
            var first = vm.ImportAsync();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.NotEqual(uiThread, workerThread);
                Assert.True(vm.IsBusy); Assert.False(vm.ImportCommand.CanExecute(null));
                Pump(vm.ImportAsync()); Assert.Equal(1, service.Calls);
                Assert.True(vm.CancelCommand.CanExecute(null));
                vm.CancelCommand.Execute(null); Assert.False(vm.CancelCommand.CanExecute(null));
                Pump(first);
                Assert.False(refreshed); Assert.False(vm.IsBusy); Assert.True(vm.CanImport);
                Assert.Equal("Импорт отменён.", vm.Message);
            }
            finally { release.Set(); Pump(first); }
        });
    }

    [Fact]
    public void FailureDoesNotExposeNativeError_AndAllowsRetry()
    {
        StaTestRunner.Run(() =>
        {
            var service = new FakeImport(_ => throw new InvalidOperationException("Bearer fixture-private-value C:\\private\\auth.json"));
            var vm = new GatewayCatalogImportViewModel(service, () => true, () => Task.CompletedTask);
            Pump(vm.ImportAsync());
            Assert.False(vm.IsBusy); Assert.True(vm.CanImport);
            Assert.DoesNotContain("fixture-private-value", vm.Message);
            Assert.DoesNotContain("auth.json", vm.Message);
            Assert.Contains("Не удалось", vm.Message);
        });
    }

    [Fact]
    public void PersistedImportWithRefreshFailure_IsNotReportedAsRolledBack()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new GatewayCatalogImportViewModel(new FakeImport(_ => Task.FromResult(Empty())), () => true,
                () => throw new IOException("fixture-private-value"));
            Pump(vm.ImportAsync());
            Assert.Contains("Каталог сохранён", vm.Message);
            Assert.Contains("списки не удалось обновить", vm.Message);
            Assert.DoesNotContain("fixture-private-value", vm.Message);
            Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public void CommitDisablesCancellationWhileUiRefreshIsPending()
    {
        StaTestRunner.Run(() =>
        {
            var refresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var vm = new GatewayCatalogImportViewModel(new FakeImport(_ => Task.FromResult(Empty())), () => true,
                () => { refreshEntered.SetResult(); return refresh.Task; });
            var import = vm.ImportAsync(); Pump(refreshEntered.Task);
            Assert.False(vm.CanCancel); vm.CancelCommand.Execute(null);
            refresh.SetResult(); Pump(import);
            Assert.Contains("Каталог сохранён", vm.Message);
        });
    }

    [Fact]
    public void MissingServiceOrBlockedHost_DisablesImportWithoutCallingService()
    {
        StaTestRunner.Run(() =>
        {
            var missing = new GatewayCatalogImportViewModel(null, () => true, () => Task.CompletedTask);
            Assert.False(missing.ImportCommand.CanExecute(null)); Pump(missing.ImportAsync());
            var service = new FakeImport(_ => Task.FromResult(Empty()));
            var blocked = new GatewayCatalogImportViewModel(service, () => false, () => Task.CompletedTask);
            Assert.False(blocked.CanImport); Pump(blocked.ImportAsync()); Assert.Equal(0, service.Calls);
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, 1280, 900)]
    [InlineData(AppTheme.Light, 1000, 720)]
    public void ShippedImportButton_PersistsCatalog_RefreshesScreen_AndProtectsNativeProfile(AppTheme theme, int width, int height)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var directory = Directory.CreateTempSubdirectory("llm-gateway-ui-");
            ServiceProvider? provider = null;
            ISqliteConnectionFactory? factory = null;
            Window? window = null;
            try
            {
                var services = new ServiceCollection();
                services.AddApplication(); services.AddInfrastructure(directory.FullName); services.AddAppUi();
                var gateway = new FixtureGateway();
                services.AddSingleton<ILlmGateway>(gateway);
                services.AddSingleton<ICliDetectionService>(FakeCliDetectionService.Degraded());
                provider = services.BuildServiceProvider(validateScopes: true);
                factory = provider.GetRequiredService<ISqliteConnectionFactory>();
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync());
                var vm = provider.GetRequiredService<ProvidersAccountsViewModel>();
                Pump(vm.InitializeAsync()); Assert.Equal(0, gateway.Calls);
                var host = new ContentControl { Content = vm };
                window = new Window { Content = host, Width = width, Height = height, ShowInTaskbar = false };
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/LLMWorkGUI.App;component/Themes/{theme}.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
                window.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
                window.Show(); Settle(window);
                var button = Descendants<Button>(host).Single(b => AutomationProperties.GetName(b) == "Импортировать каталог LLMGateway");
                Assert.True(button.IsVisible); Assert.True(button.IsEnabled);
                Assert.NotNull(button.Command); button.Command.Execute(null);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (vm.GatewayImport.IsBusy && DateTime.UtcNow < deadline) Settle(window);
                Assert.False(vm.GatewayImport.IsBusy); Assert.Equal(4, gateway.Calls);
                Assert.Contains("моделей 1, маршрутов 1", vm.GatewayImport.Message);
                var native = Assert.Single(vm.Providers);
                Assert.Equal(BackendType.NativeGateway, native.Backend);
                Assert.Equal("LLMGateway", native.BackendDisplay);
                var account = Assert.Single(vm.AccountManagement.Accounts).Value;
                Assert.Equal(AuthState.Unknown, account.AuthState); Assert.False(account.Settings.IsEnabled);
                Assert.Equal("Список аккаунтов обновлён.", vm.AccountManagement.Message);
                vm.SelectedProvider = native; Settle(window);
                Assert.False(vm.ConfirmAndSaveCommand.CanExecute(null));
                Assert.False(vm.DeleteProviderCommand.CanExecute(null));
                Assert.False(vm.TestConnectionCommand.CanExecute(null));
                vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";
                Pump(vm.ExecuteConfirmAndSaveAsync()); Pump(vm.ExecuteDeleteProviderAsync());
                var stored = Pump(provider.GetRequiredService<IProviderProfileRepository>().GetByIdAsync(native.Id));
                Assert.Equal(BackendType.NativeGateway, stored!.Backend);
                var expander = Descendants<Expander>(host).Single(e => AutomationProperties.GetName(e) == "Управление аккаунтами");
                Assert.True(expander.IsEnabled);
                Assert.True(expander.IsExpanded);
                Assert.False(Descendants<Button>(host).Single(b => Equals(b.Content, "Сохранить провайдера")).IsVisible);
                var status = Descendants<TextBlock>(host).Single(b => AutomationProperties.GetName(b) == "Состояние импорта LLMGateway");
                Assert.True(status.IsVisible);
                Assert.True(status.TranslatePoint(new Point(0, status.ActualHeight), window).Y < window.ActualHeight);
                Capture(window, $"gateway_import_{theme.ToString().ToLowerInvariant()}.png");

                // A caller with a stale/empty UI list cannot repurpose the saved profile as OpenCode.
                vm.SelectedProvider = null; vm.Providers.Clear(); vm.EditingProviderId = native.Id;
                Pump(vm.ExecuteConfirmAndSaveAsync());
                stored = Pump(provider.GetRequiredService<IProviderProfileRepository>().GetByIdAsync(native.Id));
                Assert.Equal(BackendType.NativeGateway, stored!.Backend);
                Assert.Contains("каталог нативных клиентов", vm.StatusMessage);
            }
            finally
            {
                window?.Close(); provider?.Dispose();
                using var connection = factory?.CreateConnection();
                if (connection is not null) Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
                Directory.Delete(directory.FullName, true);
            }
        });
    }

    private static GatewayCatalogSnapshot Empty() => new([], [], [], [], []);
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(task.IsCompleted, "UI task timeout"); task.GetAwaiter().GetResult();
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Settle(Window window) { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static void Capture(Window window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(ScreenshotFile.OutputDirectory);
        using var stream = File.Create(Path.Combine(ScreenshotFile.OutputDirectory, name)); encoder.Save(stream);
    }

    private sealed class FakeImport(Func<CancellationToken, Task<GatewayCatalogSnapshot>> action) : IGatewayCatalogImportService
    {
        public int Calls { get; private set; }
        public Task<GatewayCatalogSnapshot> ImportAsync(CancellationToken cancellationToken = default)
        { Calls++; return action(cancellationToken); }
    }

    private sealed class FixtureGateway : ILlmGateway
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<ProviderInfo>>([new(ProviderKind.Codex, "Codex", "codex", true, null, new(false, "native", MultiAccountSupport.Isolated, "", null, null, false, 1000))]); }
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<AccountInfo>>([new("work", "Рабочий аккаунт", ProviderKind.Codex, true, true, AccountAvailability.Ready, null, null, null, AccountAuthMode.NativeLogin, null, null, null, null, null)]); }
        public Task<IReadOnlyList<GatewayModel>> GetModelsAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<GatewayModel>>([new("codex/work/model", "model", "Тестовая модель", ProviderKind.Codex, "work", true)]); }
        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotasAsync(bool refresh = false, string? accountId = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]); }
        public Task<AccountInfo> AddAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountInfo> UpdateAccountAsync(AccountProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountInfo> SelectAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountInfo> CheckAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartNativeLoginAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatUpdate> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
