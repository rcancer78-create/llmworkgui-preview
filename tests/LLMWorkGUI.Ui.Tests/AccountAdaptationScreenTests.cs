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
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Trait("Category", "VisualUi")]
[Collection("Account configuration UI isolation")]
public sealed class AccountAdaptationScreenTests
{
    [Theory]
    [InlineData(AppTheme.Dark, 900, 750)]
    [InlineData(AppTheme.Light, 620, 700)]
    public void ShippedMaskedEditorSavesOnlyTheSelectedAccountKeyBindsProviderAndReopensWithoutLoadingPayload(AppTheme theme, int width, int height)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var directory = Directory.CreateTempSubdirectory("llm-account-key-ui-"); Window? window = null;
            ISqliteConnectionFactory? ownedFactory = null;
            try
            {
                var services = new ServiceCollection(); services.AddInfrastructure(directory.FullName); services.AddAppUi();
                services.AddSingleton<ISecretStore, MemoryKeys>();
                using var provider = services.BuildServiceProvider();
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync());
                ownedFactory = provider.GetRequiredService<ISqliteConnectionFactory>(); Pump(Seed(ownedFactory));
                var parent = provider.GetRequiredService<ProvidersAccountsViewModel>(); var vm = parent.AccountManagement;
                Pump(vm.RefreshAsync()); vm.Profile = vm.Profiles.Single(p => p.Id == "account-key-provider"); vm.SelectedAccount = Assert.Single(vm.Accounts);
                new ThemeResourceApplier().ApplyTheme(theme);
                var host = new ContentControl { Content = vm }; var scroll = new ScrollViewer { Content = host, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                window = new Window { Content = scroll, Width = width, Height = height, ShowInTaskbar = false };
                window.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
                window.Show(); Settle(window);
                Click(host, "Обновить привязку аккаунта"); Idle(vm, window);
                Assert.True(vm.CanChangeAdaptationConfiguration); Assert.False(vm.CanBindNativeProvider);
                var input = Descendants<PasswordBox>(host).Single(b => AutomationProperties.GetName(b) == "Новый API-ключ аккаунта");
                Assert.Empty(input.Password); input.Password = "synthetic-wpf-account-key"; Settle(window);
                Assert.Equal("synthetic-wpf-account-key", vm.EnteredApiKey);
                Click(host, "Сохранить ключ аккаунта"); Idle(vm, window);
                Assert.Empty(input.Password); Assert.Empty(vm.EnteredApiKey);
                var native = Descendants<TextBox>(host).Single(b => AutomationProperties.GetName(b) == "Native-провайдер аккаунта");
                native.Text = "openai"; Settle(window);
                Click(host, "Привязать native-провайдера"); Idle(vm, window);
                var setup = provider.GetRequiredService<IAdaptationAccountConfigurationService>();
                var read = Completed(setup.ReadConfigurationAsync("account-key-provider", "account-key-selected"));
                Assert.True(read.IsMappingCurrent); Assert.False(read.HasOwnedExecution);
                var account = Completed(provider.GetRequiredService<IAccountRepository>().GetByIdAsync("account-key-selected"));
                Assert.Equal(AuthState.Unknown, account!.AuthState);
                var other = Completed(provider.GetRequiredService<IAccountRepository>().GetByIdAsync("account-key-other"));
                Assert.Null(other!.SecretReference);
                Assert.DoesNotContain(Descendants<TextBlock>(host), b => b.Text.Contains("synthetic-wpf-account-key"));
                Assert.DoesNotContain(Descendants<TextBox>(host), b => b.Text.Contains("synthetic-wpf-account-key"));
                scroll.ScrollToBottom(); Settle(window);
                Assert.True(native.IsVisible); Assert.True(native.ActualWidth > 100);
                Capture(window, $"account_adaptation_{theme.ToString().ToLowerInvariant()}.png");
                var reopened = new AccountManagementViewModel(provider.GetRequiredService<IAccountConfigurationService>(), adaptationConfiguration: setup);
                host.Content = reopened; Pump(reopened.RefreshAsync()); reopened.Profile = reopened.Profiles.Single(p => p.Id == "account-key-provider"); reopened.SelectedAccount = Assert.Single(reopened.Accounts);
                Pump(reopened.RefreshAdaptationAsync()); Settle(window);
                Assert.Equal("openai", reopened.NativeProviderId); Assert.Empty(reopened.EnteredApiKey);
                Assert.Empty(Descendants<PasswordBox>(host).Single().Password);
                Assert.Contains("не подтверждены", reopened.AdaptationStatus);
                var revoke = provider.GetRequiredService<ISecretLifecycleService>().RevokeAccountSecretAsync("account-key-selected"); Pump(revoke);
                Assert.False(Directory.Exists(Path.Combine(directory.FullName, "runs"))); // No native process/LLM/probe launched by configuration.
            }
            finally
            {
                window?.Close();
                if (ownedFactory is not null)
                { using var connection = ownedFactory.CreateConnection(); Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection); }
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (!string.Equals(directory.Parent!.FullName,root,StringComparison.OrdinalIgnoreCase)
                    || !directory.Name.StartsWith("llm-account-key-ui-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path.");
                directory.Delete(true);
            }
        });
    }

    private static async Task Seed(ISqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync(); await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProviderProfiles(Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
            VALUES('account-key-provider','Synthetic OpenCode','OpenCode','PrivateSource',1,'2026-10-05','2026-10-05');
            INSERT INTO ProviderProfiles(Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
            VALUES('account-key-other-provider','Other profile','CursorAcp','PrivateSource',1,'2026-10-05','2026-10-05');
            INSERT INTO Accounts(Id,ProviderProfileId,DisplayName,AuthState,Health,IsEnabled,ManualPriority,MaxConcurrentExecutions,CreatedAtUtc,UpdatedAtUtc)
            VALUES('account-key-selected','account-key-provider','Selected account','Valid','Healthy',0,0,1,'2026-10-05','2026-10-05');
            INSERT INTO Accounts(Id,ProviderProfileId,DisplayName,AuthState,Health,IsEnabled,ManualPriority,MaxConcurrentExecutions,CreatedAtUtc,UpdatedAtUtc)
            VALUES('account-key-other','account-key-other-provider','Other account','Unknown','Healthy',0,0,1,'2026-10-05','2026-10-05');
            """;
        await command.ExecuteNonQueryAsync();
    }
    private sealed class MemoryKeys : ISecretStore
    {
        private readonly Dictionary<string,string> _payloads = new();
        public Task<string> SaveSecretAsync(string value, CancellationToken token = default)
        { var reference = SecretReference.Prefix + Guid.NewGuid().ToString("N"); _payloads.Add(reference,value); return Task.FromResult(reference); }
        public Task<string?> GetSecretAsync(string reference, CancellationToken token = default) => Task.FromResult(_payloads.GetValueOrDefault(reference));
        public Task<bool> DeleteSecretAsync(string reference, CancellationToken token = default) => Task.FromResult(_payloads.Remove(reference));
    }
    private static void Click(DependencyObject host, string label)
    { var b = Descendants<Button>(host).Single(b => Equals(b.Content,label)); Assert.True(b.IsEnabled, label + " is disabled"); Assert.True(b.Command.CanExecute(null), label + " is refused"); b.Command.Execute(null); }
    private static void Idle(AccountManagementViewModel vm, Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (vm.IsBusy && DateTime.UtcNow < deadline) Dispatcher.CurrentDispatcher.Invoke(() => {},DispatcherPriority.ApplicationIdle);
        Assert.False(vm.IsBusy); Settle(window);
    }
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Dispatcher.CurrentDispatcher.Invoke(() => {},DispatcherPriority.ApplicationIdle);
        Assert.True(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    private static T Completed<T>(Task<T> task) { Pump(task); return task.GetAwaiter().GetResult(); }
    private static void Settle(Window window) { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => {},DispatcherPriority.ApplicationIdle); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        { var child=VisualTreeHelper.GetChild(root,i); if(child is T found) yield return found; foreach(var nested in Descendants<T>(child)) yield return nested; }
    }
    private static void Capture(Window window, string name)
    {
        var output=Environment.GetEnvironmentVariable("LLMWORKGUI_SCREENSHOT_DIR") ?? Path.Combine("artifacts","project-closure-20261004","account-adaptation-tests","screenshots");
        Directory.CreateDirectory(output);
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file=File.Create(Path.Combine(output,name)); png.Save(file);
    }
}
