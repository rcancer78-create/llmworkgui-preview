using System.IO;
using System.Windows;
using System.Windows.Controls;
using LLMWorkGUI.App.DependencyInjection;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.DependencyInjection;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed partial class ModelsRoutesScreenTests
{
    [Fact]
    public void NativeEvidenceDoesNotClaimPersonalManualConfirmation()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var model=new ConfiguredModel("model","profile",BackendType.NativeGateway,"native","Native",CapabilityState.Unknown,ModelProvenance.PluginReported,true);
            var account=new ModelAccountOption("account","profile","Account",AuthState.Valid,true);
            var now=DateTimeOffset.UtcNow;
            var evidence=new ModelCapabilityEvidence(model.Id,account.Id,CapabilityState.Supported,ModelProvenance.PluginReported,
                now.AddSeconds(-1),now.AddMinutes(10),ModelCapabilityFlags.ReasoningVariants,["high"],[],[]) { DiscoverySource="Explicit native fixture" };
            var editor=new ModelCapabilitiesEditorViewModel(null,null,TimeProvider.System);
            editor.SetConfiguration(new ModelRouteConfiguration([],[account],[model],[]) { Capabilities=[evidence] },"profile");
            editor.Model=editor.Models.Single(); editor.Account=editor.Accounts.Single();
            Assert.False(editor.Confirmed); Assert.Equal("high",editor.Reasoning);
            Assert.Contains("Explicit native fixture",editor.SavedEvidence);
            Assert.False(editor.Chat); Assert.False(editor.Tools); Assert.False(editor.Vision);
        });
    }

    [Fact]
    public void NativeDiscoveryButtonPinsScopeAndDisablesSelectionUntilCompletion()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var directory=Directory.CreateTempSubdirectory("llm-native-discovery-ui-");
            ServiceProvider? provider=null;
            var discovery=new UiDiscoveryFixture();
            try
            {
                var services=new ServiceCollection();
                services.AddSingleton<IModelCapabilityDiscoveryService>(discovery);
                services.AddApplication(); services.AddInfrastructure(directory.FullName); services.AddAppUi();
                provider=services.BuildServiceProvider();
                Pump(provider.GetRequiredService<DatabaseMigrator>().MigrateAsync());
                var factory=new SqliteConnectionFactory(Path.Combine(directory.FullName,"llmworkgui.db"));
                Seed(factory);
                using(var connection=Pump(factory.OpenConnectionAsync()))
                using(var command=connection.CreateCommand())
                { command.CommandText="UPDATE ProviderProfiles SET Backend='NativeGateway'"; command.ExecuteNonQuery(); }
                var vm=provider.GetRequiredService<ModelsRoutesViewModel>();
                Pump(vm.RefreshAsync()); vm.Profile=vm.Profiles.Single();
                vm.NativeModelId="explicit-native-model"; vm.ModelName="Модель для discovery"; Pump(vm.SaveModelAsync());
                var editor=vm.CapabilitiesEditor;
                editor.Model=editor.Models.Single(); editor.Account=editor.Accounts.Single();
                new ThemeResourceApplier().ApplyTheme(AppTheme.Dark);
                var host=new ContentControl { Content=vm };
                var window=new Window { Content=host,Width=760,Height=680,ShowInTaskbar=false };
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("pack://application:,,,/LLMWorkGUI.App;component/Themes/Shared.xaml") });
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("pack://application:,,,/LLMWorkGUI.App;component/Views/ScreenTemplates.xaml") });
                window.Show(); Settle(window);
                try
                {
                    Descendants<TabControl>(host).Single().SelectedIndex=2; Settle(window);
                    var button=Find<Button>(host,"DiscoverModelCapabilitiesButton");
                    Assert.True(button.IsEnabled);
                    button.Command.Execute(null); Settle(window);
                    Assert.Equal(editor.Model.Id,discovery.Model); Assert.Equal(editor.Account.Id,discovery.Account);
                    Assert.True(editor.IsBusy); Assert.False(editor.CanSelect); Assert.False(button.IsEnabled);
                    var selected=editor.Model; editor.Model=null; Assert.Same(selected,editor.Model);
                    Assert.False(Find<ComboBox>(host,"CapabilitiesAccountPicker").IsEnabled);
                    Capture(host,"native_capability_discovery_busy.png");
                    discovery.Release.TrySetResult();
                    var until=DateTime.UtcNow.AddSeconds(10);
                    while(editor.IsBusy && DateTime.UtcNow<until) Settle(window);
                    Assert.False(editor.IsBusy); Assert.True(button.IsEnabled);
                    Assert.Contains("Неизвестные",editor.StatusMessage);
                    Assert.False(editor.Chat); Assert.False(editor.Tools); Assert.False(editor.Vision);
                    Capture(host,"native_capability_discovery_finished.png");
                }
                finally { discovery.Release.TrySetResult(); window.Close(); }
            }
            finally
            {
                provider?.Dispose(); SqliteConnection.ClearAllPools();
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),directory.FullName,StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith("llm-native-discovery-ui-",directory.Name,StringComparison.Ordinal);
                directory.Delete(true);
            }
        });
    }

    private sealed class UiDiscoveryFixture:IModelCapabilityDiscoveryService
    {
        public string? Model,Account;
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DiscoverAsync(string modelId,string accountId,CancellationToken token=default)
        { Model=modelId; Account=accountId; return Release.Task.WaitAsync(token); }
    }
}
