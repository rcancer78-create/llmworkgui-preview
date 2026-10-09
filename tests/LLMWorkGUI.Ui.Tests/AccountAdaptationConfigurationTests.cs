using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("Account configuration UI isolation")]
public sealed class AccountAdaptationConfigurationTests
{
    [Fact]
    public void EnteredKeyIsCapturedOnceClearedBeforeAwaitAndNeverLoadedFromSavedStatus()
    {
        StaTestRunner.Run(() =>
        {
            var setup = new Setup(); var vm = Ready(setup);
            vm.EnteredApiKey = "synthetic-entered-key"; setup.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var save = vm.SaveAccountKeyAsync(); Assert.True(vm.IsBusy); Assert.Empty(vm.EnteredApiKey);
            vm.EnteredApiKey = "synthetic-late-key"; vm.Profile = vm.Profiles.Last();
            Pump(vm.SaveAccountKeyAsync()); Assert.Equal(1, setup.Saves); Assert.Equal("synthetic-entered-key", setup.CapturedKey);
            Assert.Empty(vm.EnteredApiKey); Assert.Equal("opencode", vm.Profile!.Id);
            setup.Pending.SetResult(); Pump(save);
            Assert.Empty(vm.EnteredApiKey); Assert.False(vm.IsBusy); Assert.Contains("авторизация не подтверждена", vm.Message);
            Assert.DoesNotContain("synthetic-entered-key", vm.AdaptationStatus); Assert.Equal(AuthState.Unknown, vm.SelectedAccount!.Value.AuthState);
        });
    }

    [Fact]
    public void ChangedSelectionClearsKeyProviderAndStaleSnapshot()
    {
        StaTestRunner.Run(() =>
        {
            var setup = new Setup(); var vm = Ready(setup); vm.EnteredApiKey = "synthetic-input"; vm.NativeProviderId = "openrouter";
            vm.SelectedAccount = vm.Accounts.Last();
            Assert.Empty(vm.EnteredApiKey); Assert.Empty(vm.NativeProviderId);
            Assert.False(vm.CanSaveAccountKey); Assert.False(vm.CanBindNativeProvider); Assert.Equal(0, setup.Saves);
        });
    }

    [Fact]
    public void ViewOnlyCanRefreshSavedBindingButCannotMutateIt()
    {
        StaTestRunner.Run(() =>
        {
            var setup = new Setup(); var vm = Ready(setup, new ViewOnly());
            Assert.True(vm.RefreshAdaptationCommand.CanExecute(null)); Assert.True(vm.ShowAdaptationConfiguration);
            vm.EnteredApiKey = "synthetic-refused"; vm.NativeProviderId = "openrouter";
            Pump(vm.SaveAccountKeyAsync()); Pump(vm.BindNativeProviderAsync());
            Assert.False(vm.CanSaveAccountKey); Assert.False(vm.CanBindNativeProvider); Assert.Equal(0, setup.Saves); Assert.Equal(0, setup.Bindings);
        });
    }

    [Fact]
    public void UnresolvedExecutionPreventsEditorWritesWithoutPreventingStatusRefresh()
    {
        StaTestRunner.Run(() =>
        {
            var setup = new Setup { Owned = true }; var vm = Ready(setup);
            vm.EnteredApiKey = "synthetic-held"; Pump(vm.SaveAccountKeyAsync()); Pump(vm.BindNativeProviderAsync());
            Assert.False(vm.CanChangeAdaptationConfiguration); Assert.Contains("незавершённым", vm.AdaptationStatus);
            Assert.Equal(0, setup.Saves); Assert.Equal(0, setup.Bindings); Assert.True(vm.RefreshAdaptationCommand.CanExecute(null));
        });
    }

    [Fact]
    public void UnsupportedBackendHidesThisBindingAndClearsTransientInput()
    {
        StaTestRunner.Run(() =>
        {
            var setup = new Setup(); var vm = Ready(setup); vm.EnteredApiKey = "synthetic-input";
            vm.Profile = vm.Profiles.Last(); vm.SelectedAccount = vm.Accounts.First();
            Assert.Empty(vm.EnteredApiKey); Assert.False(vm.ShowAdaptationConfiguration); Assert.False(vm.CanSaveAccountKey);
            Pump(vm.RefreshAdaptationAsync()); Assert.Equal(1, setup.Reads);
        });
    }

    [Fact]
    public void KeyFailureClearsInputAndInvalidatesSnapshotWithoutDisplayingRawException()
    {
        StaTestRunner.Run(() =>
        {
            var setup = new Setup { FailSave = true }; var vm = Ready(setup); vm.EnteredApiKey = "synthetic-input";
            Pump(vm.SaveAccountKeyAsync());
            Assert.Empty(vm.EnteredApiKey); Assert.False(vm.CanChangeAdaptationConfiguration); Assert.False(vm.IsBusy);
            Assert.DoesNotContain("synthetic-native-secret", vm.Message + vm.AdaptationStatus);
            Assert.Contains("Обновите", vm.AdaptationStatus);
            setup.FailSave = false; Pump(vm.RefreshAdaptationAsync()); Assert.True(vm.CanChangeAdaptationConfiguration);
        });
    }

    [Fact]
    public void FailedRefreshDisablesOldReadySnapshotAndNativeBindingCapturesChosenProvider()
    {
        StaTestRunner.Run(() =>
        {
            var setup = new Setup(); var vm = Ready(setup);
            vm.NativeProviderId = "openrouter"; Pump(vm.BindNativeProviderAsync());
            Assert.Equal("openrouter", setup.Provider); Assert.Equal(1, setup.Bindings);
            setup.FailRead = true; Pump(vm.RefreshAdaptationAsync());
            Assert.False(vm.CanBindNativeProvider); Assert.False(vm.CanChangeAdaptationConfiguration);
            Assert.DoesNotContain("synthetic-native-secret", vm.Message + vm.AdaptationStatus);
            Assert.Empty(vm.EnteredApiKey);
        });
    }

    private static AccountManagementViewModel Ready(Setup setup, IApplicationInstanceGuard? guard = null)
    {
        var vm = new AccountManagementViewModel(new Accounts(), guard, setup); Pump(vm.RefreshAsync());
        vm.SelectedAccount = vm.Accounts.First(); Pump(vm.RefreshAdaptationAsync()); return vm;
    }
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.True(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    private sealed class Setup : IAdaptationAccountConfigurationService
    {
        public bool Owned, FailSave, FailRead;
        public int Saves, Bindings, Reads;
        public string? CapturedKey;
        public string Provider = "openai";
        public TaskCompletionSource? Pending;
        public Task<AdaptationAccountConfiguration> ReadConfigurationAsync(string profile, string account, CancellationToken token = default)
        {
            Reads++; if (FailRead) throw new InvalidOperationException("Bearer synthetic-native-secret");
            return Task.FromResult(new AdaptationAccountConfiguration(account, profile, "urn:llmworkgui:secret:synthetic", SecretReferenceState.Active,
                true, Provider, "urn:llmworkgui:secret:synthetic", Owned));
        }
        public Task SaveKeyAsync(AdaptationAccountConfiguration expected, string key, CancellationToken token = default)
        { Saves++; CapturedKey = key; if (FailSave) throw new InvalidOperationException("Bearer synthetic-native-secret"); return Pending?.Task ?? Task.CompletedTask; }
        public Task ConfigureProviderAsync(AdaptationAccountConfiguration expected, string provider, CancellationToken token = default)
        { Bindings++; Provider = provider; return Task.CompletedTask; }
    }
    private sealed class Accounts : IAccountConfigurationService
    {
        public Task<AccountConfiguration> ReadAsync(CancellationToken token = default) => Task.FromResult(new AccountConfiguration(
            [new("opencode", "OpenCode", BackendType.OpenCode, DataClassification.PublicSource, true), new("cursor", "Cursor", BackendType.CursorAcp, DataClassification.PublicSource, true)],
            [Row("one","opencode"),Row("two","opencode"),Row("cursor-account","cursor")]));
        private static AccountConfigurationRow Row(string id, string profile) => new(new(id,profile,id,null,0,false,1,null),AuthState.Unknown,HealthState.Healthy,null,null);
        public Task<string> SaveAsync(SaveAccountSettings request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AccountImportCandidate>> DiscoverAsync(string profile, CancellationToken token = default) => throw new NotSupportedException();
        public Task<string> ImportAsync(string profile, AccountImportCandidate expected, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class ViewOnly : IApplicationInstanceGuard
    {
        public string InstanceId => "synthetic-view-only";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new InvalidOperationException();
        public void Dispose() { }
    }
}
