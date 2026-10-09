using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Trait("Category", "VisualUi")]
public sealed partial class ModelsRoutesScreenTests
{
    [Theory]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'; UPDATE Accounts SET AuthState='Valid'")]
    [InlineData("UPDATE ProviderProfiles SET BaseUrl='https://changed.example/v1'")]
    [InlineData("UPDATE Models SET ProviderModelId='changed-model'")]
    public void EmptyCapabilitiesEditorRefusesChangedContextUntilReload(string change)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var directory = Directory.CreateTempSubdirectory("llm-capability-context-");
            ServiceProvider? provider = null;
            try
            {
                var services = new ServiceCollection();
                services.AddApplication(); services.AddInfrastructure(directory.FullName); services.AddAppUi();
                provider = services.BuildServiceProvider();
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync());
                var factory = new SqliteConnectionFactory(Path.Combine(directory.FullName, "llmworkgui.db"));
                Seed(factory);
                using var connection = Pump(factory.OpenConnectionAsync());
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE ProviderProfiles SET Backend='OpenCode'"; command.ExecuteNonQuery();
                var vm = provider.GetRequiredService<ModelsRoutesViewModel>();
                Pump(vm.RefreshAsync()); vm.Profile = vm.Profiles.Single();
                vm.NativeModelId = "fixture-context"; vm.ModelName = "Context";
                vm.Capability = CapabilityState.Supported; Pump(vm.SaveModelAsync());
                var editor = vm.CapabilitiesEditor;
                editor.Model = editor.Models.Single(); editor.Account = editor.Accounts.Single();
                editor.Confirmed = true; editor.Chat = true;
                command.CommandText = change; command.ExecuteNonQuery();
                Pump(editor.SaveAsync());
                Assert.Contains("контекст", editor.StatusMessage);
                var configuration = provider.GetRequiredService<IModelRouteConfigurationService>();
                Assert.Empty(Pump(configuration.ReadAsync()).Capabilities);
                Pump(vm.RefreshAsync()); editor.Confirmed = true; editor.Chat = true;
                Pump(editor.SaveAsync());
                Assert.Contains("сохранена", editor.StatusMessage);
                Assert.Equal(CapabilityState.Supported, Assert.Single(Pump(configuration.ReadAsync()).Capabilities).State);
            }
            finally
            {
                provider?.Dispose(); SqliteConnection.ClearAllPools();
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), directory.FullName, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith("llm-capability-context-", directory.Name, StringComparison.Ordinal);
                directory.Delete(true);
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, 900, 700)]
    [InlineData(AppTheme.Light, 620, 570)]
    public void ShippedCapabilitiesEditor_SavesReopensAndKeepsCursorReadOnly(AppTheme theme, int width, int height)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var directory = Directory.CreateTempSubdirectory("llm-model-capabilities-");
            ServiceProvider? provider = null;
            try
            {
                var services = new ServiceCollection();
                services.AddApplication(); services.AddInfrastructure(directory.FullName); services.AddAppUi();
                provider = services.BuildServiceProvider();
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync());
                var factory = new SqliteConnectionFactory(Path.Combine(directory.FullName, "llmworkgui.db"));
                Seed(factory);
                using (var connection = Pump(factory.OpenConnectionAsync()))
                using (var command = connection.CreateCommand())
                { command.CommandText = "UPDATE ProviderProfiles SET Backend='OpenCode',DisplayName='OpenCode · тестовый профиль'"; command.ExecuteNonQuery(); }
                var vm = provider.GetRequiredService<ModelsRoutesViewModel>();
                Pump(vm.RefreshAsync()); vm.Profile = vm.Profiles.Single();
                vm.NativeModelId = "fixture-capabilities"; vm.ModelName = "Модель для ревью";
                vm.Capability = CapabilityState.Supported; Pump(vm.SaveModelAsync());
                var editor = vm.CapabilitiesEditor;
                new ThemeResourceApplier().ApplyTheme(theme);
                var host = new ContentControl { Content = vm };
                var window = new Window { Content = host, Width = width, Height = height, ShowInTaskbar = false };
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
                window.Show(); Settle(window);
                try
                {
                    Descendants<TabControl>(host).Single().SelectedIndex = 2; Settle(window);
                    Assert.True(vm.CanChooseProfile);
                    Find<ComboBox>(host, "CapabilitiesModelPicker").SelectedItem = editor.Models.Single();
                    Find<ComboBox>(host, "CapabilitiesAccountPicker").SelectedItem = editor.Accounts.Single();
                    Settle(window);
                    Find<CheckBox>(host, "CapabilitiesConfirmed").IsChecked = true;
                    Find<CheckBox>(host, "CapabilityChat").IsChecked = true;
                    Find<TextBox>(host, "CapabilityReasoningInput").Text = "high, max";
                    Find<TextBox>(host, "CapabilitySpeedInput").Text = "fast";
                    Find<TextBox>(host, "CapabilityModesInput").Text = "chat";
                    Find<TextBox>(host, "CapabilityContextInput").Text = "64000";
                    Find<TextBox>(host, "CapabilityValidityInput").Text = "2";
                    Settle(window); Assert.True(editor.CanSave);
                    Capture(host, $"model_capabilities_{theme}_top.png");
                    var scroll = Descendants<ScrollViewer>(host).First(item => item.Content is StackPanel);
                    scroll.ScrollToEnd(); Settle(window);
                    var saveButton = Descendants<Button>(host).Single(button => Equals(button.Content, "Сохранить возможности"));
                    Assert.InRange(saveButton.TransformToAncestor(host).Transform(new Point()).Y, 0, host.ActualHeight - saveButton.ActualHeight);
                    Click(host, "Сохранить возможности");
                    var until = DateTime.UtcNow.AddSeconds(10);
                    while (editor.IsBusy && DateTime.UtcNow < until) Settle(window);
                    Assert.False(editor.IsBusy); Assert.Contains("сохранена", editor.StatusMessage);
                    Settle(window);
                    Capture(host, $"model_capabilities_{theme}_saved.png");
                    Pump(vm.RefreshAsync());
                    Assert.True(editor.Confirmed); Assert.True(editor.Chat);
                    Assert.Equal("2", editor.ValidityHours);
                    Assert.Equal("high, max", editor.Reasoning); Assert.Equal("64000", editor.ContextLimit);
                    var evidence = Assert.Single(Pump(provider.GetRequiredService<IModelRouteConfigurationService>().ReadAsync()).Capabilities);
                    Assert.Equal(ModelProvenance.UserDefined, evidence.Provenance);
                    Assert.InRange((evidence.ExpiresAtUtc - evidence.ObservedAtUtc).TotalHours, 1.99, 2.01);

                    editor.Reasoning = "high, high"; Pump(editor.SaveAsync());
                    Assert.Contains("уникальных", editor.StatusMessage);
                    Assert.Equal(evidence.ObservedAtUtc, Assert.Single(Pump(provider.GetRequiredService<IModelRouteConfigurationService>().ReadAsync()).Capabilities).ObservedAtUtc);
                    editor.Account = null; Assert.Empty(editor.Reasoning); Assert.False(editor.Confirmed); Assert.False(editor.CanSave);
                    editor.Account = editor.Accounts.Single(); Assert.Equal("high, max", editor.Reasoning); Assert.True(editor.Confirmed);

                    using (var connection = Pump(factory.OpenConnectionAsync()))
                    using (var command = connection.CreateCommand())
                    { command.CommandText = "UPDATE ProviderProfiles SET Backend='CursorAcp'; UPDATE Models SET Backend='CursorAcp'"; command.ExecuteNonQuery(); }
                    Pump(vm.RefreshAsync()); Settle(window);
                    Assert.False(editor.CanSave); Assert.Contains("discovery", editor.ScopeNote);
                    Assert.False(Find<TextBox>(host, "CapabilityReasoningInput").IsEnabled);
                }
                finally { window.Close(); }
            }
            finally
            {
                provider?.Dispose(); SqliteConnection.ClearAllPools();
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), directory.FullName, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith("llm-model-capabilities-", directory.Name, StringComparison.Ordinal);
                directory.Delete(true);
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark, 900, 700)]
    [InlineData(AppTheme.Light, 620, 570)]
    public void ShippedScreen_CreatesEditsAndReopensAModelAndRoute(AppTheme theme, int width, int height)
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var directory = Directory.CreateTempSubdirectory("llm-model-routes-");
            var factory = new SqliteConnectionFactory(Path.Combine(directory.FullName, "llmworkgui.db"));
            ServiceProvider? provider = null;
            try
            {
                var services = new ServiceCollection();
                services.AddApplication(); services.AddInfrastructure(directory.FullName); services.AddAppUi();
                provider = services.BuildServiceProvider();
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync());
                Seed(factory);
                var main = provider.GetRequiredService<MainWindowViewModel>();
                var vm = Assert.IsType<ModelsRoutesViewModel>(main.Screens.Single(s => s.Id == ScreenId.Models));
                Assert.Same(provider.GetRequiredService<ModelsRoutesViewModel>(), vm);
                Pump(vm.RefreshAsync());
                new ThemeResourceApplier().ApplyTheme(theme);
                var host = new ContentControl { Content = vm };
                var window = new Window { Content = host, Width = width, Height = height, ShowInTaskbar = false };
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
                window.Show(); Settle(window);
                try
                {
                    Find<ComboBox>(host, "ModelProfilePicker").SelectedItem = vm.Profiles.Single(); Settle(window);
                    Find<TextBox>(host, "NativeModelIdInput").Text = "fixture-model";
                    Find<TextBox>(host, "ModelNameInput").Text = "Тестовая модель";
                    Find<ComboBox>(host, "ModelCapabilityPicker").SelectedValue = CapabilityState.Supported;
                    Settle(window);
                    Click(host, "Сохранить модель"); WaitIdle(vm, window);
                    var model = Assert.Single(vm.Models).Value;
                    Assert.Equal("fixture-model", model.NativeModelId);
                    Assert.Equal(ModelProvenance.UserDefined, model.Provenance);
                    Assert.False(Find<TextBox>(host, "NativeModelIdInput").IsEnabled);
                    Capture(host, $"models_routes_{theme}_models.png");

                    Descendants<TabControl>(host).Single().SelectedIndex = 1; Settle(window);
                    Click(host, "Новый маршрут"); Settle(window);
                    Find<ComboBox>(host, "RouteAccountPicker").SelectedItem = Assert.Single(vm.Accounts);
                    Find<ComboBox>(host, "RouteModelPicker").SelectedItem = Assert.Single(vm.RouteModels);
                    Find<ComboBox>(host, "RouteModePicker").SelectedValue = "ask";
                    Find<TextBox>(host, "RoutePriorityInput").Text = "3"; Settle(window);
                    Assert.Equal("ask", vm.RouteMode);
                    Click(host, "Сохранить маршрут"); WaitIdle(vm, window);
                    var route = Assert.Single(vm.Routes).Value;
                    Assert.Equal(model.Id, route.ModelId); Assert.Equal("ask", route.Mode); Assert.Equal(3, route.Priority);
                    Assert.False(Find<ComboBox>(host, "RouteAccountPicker").IsEnabled);
                    Capture(host, $"models_routes_{theme}_routes.png");
                    var routeScroll = Descendants<ScrollViewer>(host).First(s => s.Content is StackPanel);
                    routeScroll.ScrollToEnd(); Settle(window);
                    var saveButton = Descendants<Button>(host).Single(b => Equals(b.Content, "Сохранить маршрут"));
                    var position = saveButton.TransformToAncestor(host).Transform(new Point());
                    Assert.InRange(position.Y, 0, host.ActualHeight - saveButton.ActualHeight);
                    Capture(host, $"models_routes_{theme}_routes_scrolled.png");

                    var journal = provider.GetRequiredService<ICursorAcpExecutionJournal>();
                    Assert.Equal(route.Id, Assert.Single(Pump(journal.ListRoutesAsync())).Id);
                    vm.RouteEnabled = false; Click(host, "Сохранить маршрут"); WaitIdle(vm, window);
                    Assert.Empty(Pump(journal.ListRoutesAsync()));
                    vm.RouteEnabled = true; Click(host, "Сохранить маршрут"); WaitIdle(vm, window);
                    var reopened = new ModelsRoutesViewModel(provider.GetRequiredService<IModelRouteConfigurationService>());
                    Pump(reopened.RefreshAsync()); reopened.Profile = Assert.Single(reopened.Profiles);
                    Assert.Equal(route.Id, Assert.Single(reopened.Routes).Value.Id);
                    Assert.Equal(model.Id, Assert.Single(reopened.Models).Value.Id);
                    vm.SearchText = "zz-missing"; Settle(window); Assert.True(vm.IsEmpty);
                    Assert.True(double.IsFinite(host.ActualWidth));
                }
                finally { window.Close(); }
            }
            finally
            {
                if (provider is not null) Pump(provider.DisposeAsync().AsTask());
                using (var connection = factory.CreateConnection()) SqliteConnection.ClearPool(connection);
                directory.Delete(true);
            }
        });
    }

    [Fact]
    public async Task MissingServiceDisablesWritesAndExplainsWhy()
    {
        var vm = new ModelsRoutesViewModel(); await vm.RefreshAsync();
        Assert.False(vm.CanEdit); Assert.False(vm.SaveModelCommand.CanExecute(null));
        Assert.Contains("недоступно", vm.StatusMessage);
    }

    private static void Seed(SqliteConnectionFactory factory)
    {
        using var connection = Pump(factory.OpenConnectionAsync()); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProviderProfiles (Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
            VALUES ('fixture-profile','Cursor · тестовый профиль','CursorAcp','PrivateSource',1,'2026-10-02T00:00:00Z','2026-10-02T00:00:00Z');
            INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,AuthState,Health,CreatedAtUtc,UpdatedAtUtc)
            VALUES ('fixture-account','fixture-profile','Тестовый аккаунт','Valid','Healthy','2026-10-02T00:00:00Z','2026-10-02T00:00:00Z');
            """;
        command.ExecuteNonQuery();
    }
    private static void Click(DependencyObject host, string content)
    {
        var button = Descendants<Button>(host).Single(b => Equals(b.Content, content));
        Assert.True(button.IsEnabled); Assert.NotNull(button.Command);
        button.Command.Execute(button.CommandParameter);
    }
    private static void WaitIdle(ModelsRoutesViewModel vm, Window window)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (vm.IsBusy && DateTime.UtcNow < until) Settle(window);
        Assert.False(vm.IsBusy); Settle(window);
        Assert.DoesNotContain("Не удалось", vm.StatusMessage);
    }
    private static void Settle(Window window) { window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants<T>(root).Single(x => x.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Capture(FrameworkElement root, string filename)
    {
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        ScreenshotFile.Save(encoder, Path.Combine(ScreenshotFile.OutputDirectory, filename));
    }
}
