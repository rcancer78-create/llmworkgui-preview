using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Trait("Category", "VisualUi")]
[Collection("Account configuration UI isolation")]
public sealed class AccountManagementScreenTests
{
    [Theory]
    [InlineData(AppTheme.Dark, 900, 750)]
    [InlineData(AppTheme.Light, 620, 700)]
    public void ShippedForm_CreatesEditsImportsAndReopensUnknownAccounts(AppTheme theme, int width, int height)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var directory = Directory.CreateTempSubdirectory("llm-account-ui-");
            Window? window = null;
            try
            {
                var services = new ServiceCollection();
                services.AddApplication(); services.AddInfrastructure(directory.FullName); services.AddAppUi();
                // Explicit native-discovery fixture; the production OpenCode bridge has no discovery API.
                services.RemoveAll<IAccountBridge>(); services.AddSingleton<IAccountBridge>(new FixtureDiscoveryBridge());
                using var provider = services.BuildServiceProvider();
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync());
                var factory = provider.GetRequiredService<ISqliteConnectionFactory>();
                Pump(Seed(factory));
                var parent = provider.GetRequiredService<ProvidersAccountsViewModel>();
                Pump(parent.LoadProvidersAsync());
                parent.SelectedProvider = Assert.Single(parent.Providers);
                var vm = parent.AccountManagement;
                Pump(vm.RefreshAsync()); Assert.Single(vm.Profiles);
                new ThemeResourceApplier().ApplyTheme(theme);
                var host = new ContentControl { Content = vm };
                window = new Window { Content = new ScrollViewer { Content = host }, Width = width, Height = height, ShowInTaskbar = false };
                window.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
                window.Show(); Settle(window);
                Input(host, "Имя аккаунта").Text = "WPF fixture account";
                Input(host, "Приоритет аккаунта").Text = "3";
                Input(host, "Лимит выполнений аккаунта").Text = "2";
                Input(host, "Резерв аккаунта").Text = "0.25";
                Click(host, "Сохранить настройки аккаунта"); Idle(vm, window);
                var account = Assert.Single(vm.Accounts).Value;
                Assert.Equal(AuthState.Unknown, account.AuthState); Assert.False(account.Settings.IsEnabled);
                Assert.Equal(2, account.Settings.MaxConcurrentExecutions); Assert.Equal(0.25, account.Settings.ReserveThreshold);
                Input(host, "Имя аккаунта").Text = "Renamed through WPF";
                Click(host, "Сохранить настройки аккаунта"); Idle(vm, window);
                Assert.Equal("Renamed through WPF", Assert.Single(vm.Accounts).Value.Settings.Name);
                Pump(SeedSessionBinding(factory, account.Settings.Id, directory.FullName));
                Click(host, "Обнаружить контексты"); Idle(vm, window);
                var picker = Descendants<ComboBox>(host).Single(c => AutomationProperties.GetName(c) == "Нативный контекст для импорта");
                picker.SelectedItem = Assert.Single(vm.ImportCandidates); Settle(window);
                Click(host, "Импортировать выбранный контекст"); Idle(vm, window);
                Assert.Equal(2, vm.Accounts.Count);
                Assert.All(vm.Accounts, a => { Assert.Equal(AuthState.Unknown, a.Value.AuthState); Assert.False(a.Value.Settings.IsEnabled); });
                var reopened = new AccountManagementViewModel(provider.GetRequiredService<IAccountConfigurationService>());
                Pump(reopened.RefreshAsync()); Assert.Equal(2, reopened.Accounts.Count);
                vm.SelectedAccount = vm.Accounts.Single(a => a.Value.Settings.Id == account.Settings.Id); Settle(window);
                var bindingText = Descendants<TextBlock>(host).Single(t => AutomationProperties.GetName(t) == "Локальные привязки сессий аккаунта");
                Assert.Contains("synthetic-local-session", bindingText.Text); Assert.Contains("synthetic-native-session", bindingText.Text);
                Assert.Contains("запрошенные настройки", bindingText.Text); Assert.Contains("не подтверждён", bindingText.Text);
                var bindings = Descendants<TextBox>(host).Select(b => b.Text).ToArray();
                Assert.DoesNotContain(bindings, text => text.Contains("urn:llmworkgui:secret:"));
                Capture(window, $"accounts_management_{theme.ToString().ToLowerInvariant()}.png");
                host.Content = parent; window.Content = host; window.Width = 1280; window.Height = 900; Settle(window);
                var expander = Descendants<Expander>(host).Single(e => AutomationProperties.GetName(e) == "Управление аккаунтами");
                expander.IsExpanded = true; Settle(window);
                var formScroll = Descendants<ScrollViewer>(host).Single(s => Descendants<Expander>(s).Contains(expander));
                formScroll.ScrollToBottom(); Settle(window);
                Assert.True(Input(host, "Имя аккаунта").IsVisible);
                Capture(window, $"providers_accounts_expanded_{theme.ToString().ToLowerInvariant()}.png");
                // Exercise the shipped template, not just command CanExecute: an ancestor must
                // not disable Refresh, selection and details together with the mutation editor.
                var readOnly = new AccountManagementViewModel(provider.GetRequiredService<IAccountConfigurationService>(), new ViewOnlyGuard());
                Pump(readOnly.RefreshAsync()); host.Content = readOnly; Settle(window);
                var list = Descendants<ListBox>(host).Single(b => AutomationProperties.GetName(b) == "Сохранённые аккаунты");
                Assert.True(list.IsEnabled);
                list.SelectedItem = readOnly.Accounts[0]; Settle(window);
                Assert.NotNull(readOnly.SelectedAccount);
                var refresh = Descendants<Button>(host).Single(b => Equals(b.Content, "Обновить аккаунты"));
                Assert.True(refresh.IsEnabled); Click(host, "Обновить аккаунты"); Idle(readOnly, window);
                Assert.False(Input(host, "Имя аккаунта").IsEnabled);
                Assert.False(Descendants<Button>(host).Single(b => Equals(b.Content, "Сохранить настройки аккаунта")).IsEnabled);
                Assert.False(Descendants<Button>(host).Single(b => Equals(b.Content, "Обнаружить контексты")).IsEnabled);
            }
            finally
            {
                window?.Close();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(directory.FullName, true);
            }
        });
    }

    private static async Task Seed(ISqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ProviderProfiles (Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc) VALUES ('account-ui-provider','Account UI fixture','OpenCode','PublicSource',1,'2026-10-02','2026-10-02')";
        await command.ExecuteNonQueryAsync();
    }
    private static async Task SeedSessionBinding(ISqliteConnectionFactory factory, string accountId, string root)
    {
        await using var connection = await factory.OpenConnectionAsync(); await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Models (Id,Backend,ProviderProfileId,ProviderModelId,DisplayName,CapabilityState,Provenance,IsEnabled,Health,DiscoveredAtUtc)
                VALUES ('binding-model','OpenCode','account-ui-provider','synthetic/model','Synthetic','Supported','UserDefined',1,'Healthy','2026-10-03');
            INSERT INTO Projects (Id,DisplayName,RootPath,DataClassification,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('binding-project','Synthetic',$root,'PublicSource','2026-10-03','2026-10-03');
            INSERT INTO Sessions (Id,ProjectId,Backend,ProviderProfileId,AccountId,ModelId,WorkspaceRootPath,NativeSessionId,
                ExecutionMode,State,ReconciliationOutcome,CloseReason,CreatedAtUtc,LastEventAtUtc)
                VALUES ('synthetic-local-session','binding-project','OpenCode','account-ui-provider',$account,'binding-model',$root,
                    'synthetic-native-session','plan','Idle','None','None','2026-10-03','2026-10-03');
            """;
        command.Parameters.AddWithValue("$account", accountId); command.Parameters.AddWithValue("$root", root);
        await command.ExecuteNonQueryAsync();
    }
    private static TextBox Input(DependencyObject host, string name) => Descendants<TextBox>(host).Single(b => AutomationProperties.GetName(b) == name);
    private static void Click(DependencyObject host, string label)
    {
        var button = Descendants<Button>(host).Single(b => Equals(b.Content, label));
        Assert.True(button.Command.CanExecute(button.CommandParameter)); button.Command.Execute(button.CommandParameter);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static void Settle(Window window) { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(task.IsCompleted, "UI task timeout"); task.GetAwaiter().GetResult();
    }
    private static void Idle(AccountManagementViewModel vm, Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.IsBusy && DateTime.UtcNow < deadline) Settle(window);
        Assert.False(vm.IsBusy); Settle(window);
    }
    private static void Capture(Window window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(ScreenshotFile.OutputDirectory);
        using var stream = File.Create(Path.Combine(ScreenshotFile.OutputDirectory, name)); encoder.Save(stream);
    }

    private sealed class FixtureDiscoveryBridge : IAccountBridge
    {
        public string BackendId => "opencode";
        public bool SupportsPinning => false;
        public bool SupportsObservedRoute => false;
        public Task<IReadOnlyList<DiscoveredAccountInfo>> DiscoverAccountsAsync(string profile, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<DiscoveredAccountInfo>>([new("fixture-native-account", "Native discovery fixture", "fixture-context", AuthState.Valid)]);
        public Task<AccountPinResult> PinAccountAsync(string p, string a, SessionBinding b, CancellationToken t = default) => throw new InvalidOperationException("Unexpected native mutation");
        public Task<AccountAuthProbeResult> ProbeAuthAsync(string p, string a, CancellationToken t = default) => throw new InvalidOperationException("Unexpected authentication probe");
    }

    private sealed class ViewOnlyGuard : LLMWorkGUI.Application.Concurrency.IApplicationInstanceGuard
    {
        public string InstanceId => "view-only-template-test";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new InvalidOperationException("View only");
        public void Dispose() { }
    }
}

[CollectionDefinition("Account configuration UI isolation", DisableParallelization = true)]
public sealed class AccountConfigurationUiIsolationCollection;
