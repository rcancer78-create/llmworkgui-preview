using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("Account configuration UI isolation")]
public sealed class AccountManagementViewModelTests
{
    [Fact]
    public void ViewOnlyAccountEditor_CanReadButCannotSaveDiscoverOrImport()
    {
        StaTestRunner.Run(() =>
        {
            var service = new FakeService();
            var providers = new ProvidersAccountsViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                accountConfiguration: service, instanceGuard: new ViewOnlyGuard());
            var vm = providers.AccountManagement;
            Assert.True(vm.RefreshCommand.CanExecute(null));
            Pump(vm.RefreshAsync());
            Assert.Equal(2, vm.Profiles.Count);
            Assert.False(vm.CanEdit);
            Assert.False(vm.NewCommand.CanExecute(null));
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.False(vm.DiscoverCommand.CanExecute(null));
            Assert.False(vm.ImportCommand.CanExecute(null));
            vm.Name = "Refused"; Pump(vm.SaveAsync()); Pump(vm.DiscoverAsync()); Pump(vm.ImportAsync());
            Assert.Equal(0, service.SaveCount); Assert.Equal(0, service.DiscoverCount);
        });
    }

    private sealed class ViewOnlyGuard : LLMWorkGUI.Application.Concurrency.IApplicationInstanceGuard
    {
        public string InstanceId => "view-only-test";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new InvalidOperationException("View only");
        public void Dispose() { }
    }

    [Fact]
    public void AccountFromOtherProfile_IsRefusedWithoutReplacingEditorOrRequestedBinding()
    {
        StaTestRunner.Run(() =>
        {
            var service = new FakeService(); var vm = new AccountManagementViewModel(service);
            Pump(vm.RefreshAsync()); vm.Name = "Unsaved account";
            vm.SelectedAccount = new(new(new("other", "profile-2", "Wrong", null, 0, false, 1, null), AuthState.Unknown, HealthState.Healthy, null, null));
            Assert.Null(vm.SelectedAccount); Assert.Equal("Unsaved account", vm.Name); Assert.Equal("profile-1", vm.Profile!.Id);
            Assert.Contains("текущего профиля", vm.Message);
        });
    }

    [Fact]
    public void SessionBindingDisplay_SeparatesRequestedMetadataAndShowsBoundedCount()
    {
        var row = new AccountManagementRow(new(new("account", "profile", "Fixture", null, 0, false, 1, null), AuthState.Unknown, HealthState.Healthy, null, null)
        { SessionCount = 21, Sessions = [new("local-session", "project", BackendType.OpenCode, "local-model", "high", "fast", "plan", "native-session", SessionState.Ambiguous, "execution")] });
        Assert.Contains("запрошенные настройки", row.SessionBindings); Assert.Contains("не подтверждён", row.SessionBindings);
        Assert.Contains("local-session", row.SessionBindings); Assert.Contains("native-session", row.SessionBindings);
        Assert.Contains("Ambiguous", row.SessionBindings); Assert.Contains("последние 1 из 21", row.SessionBindings);
        Assert.Equal(AuthState.Unknown, row.Value.AuthState);
    }

    [Fact]
    public void BusySave_CapturesInputAndRejectsReentryAndProfileSwitch()
    {
        StaTestRunner.Run(() =>
        {
            var service = new FakeService(); var vm = new AccountManagementViewModel(service);
            Pump(vm.RefreshAsync()); vm.Name = "Captured";
            service.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = vm.SaveAsync(); Assert.True(vm.IsBusy);
            vm.Profile = vm.Profiles.Last(); vm.Name = "Later";
            Pump(vm.SaveAsync()); Pump(vm.DiscoverAsync());
            Assert.Equal("profile-1", vm.Profile!.Id); Assert.Equal(1, service.SaveCount); Assert.Equal(0, service.DiscoverCount);
            Assert.Equal("Captured", service.Request!.Name);
            service.Pending.SetResult("created"); Pump(first); Assert.False(vm.IsBusy);
        });
    }

    [Fact]
    public void ProviderChange_ClearsPreviousImportSelection()
    {
        StaTestRunner.Run(() =>
        {
            var service = new FakeService(); var vm = new AccountManagementViewModel(service);
            Pump(vm.RefreshAsync()); Pump(vm.DiscoverAsync());
            vm.SelectedImport = Assert.Single(vm.ImportCandidates);
            vm.Profile = vm.Profiles.Last(); Assert.Null(vm.SelectedImport); Assert.Empty(vm.ImportCandidates);
            Assert.False(vm.ImportCommand.CanExecute(null));
        });
    }

    [Fact]
    public void NativeFailure_DoesNotDisplayExceptionCredentialsOrLeaveBusy()
    {
        StaTestRunner.Run(() =>
        {
            var service = new FakeService { FailDiscovery = true }; var vm = new AccountManagementViewModel(service);
            Pump(vm.RefreshAsync()); Pump(vm.DiscoverAsync());
            Assert.False(vm.IsBusy); Assert.DoesNotContain("secret-fixture", vm.Message!); Assert.DoesNotContain("auth.json", vm.Message!);
            Assert.Empty(vm.ImportCandidates);
        });
    }

    [Theory]
    [InlineData("NaN")] [InlineData("Infinity")] [InlineData("1.1")] [InlineData("0,2")]
    public void InvalidReserve_IsRejectedBeforePersistence(string reserve)
    {
        StaTestRunner.Run(() =>
        {
            var service = new FakeService(); var vm = new AccountManagementViewModel(service);
            Pump(vm.RefreshAsync()); vm.Name = "Fixture"; vm.Reserve = reserve;
            Pump(vm.SaveAsync()); Assert.Equal(0, service.SaveCount); Assert.False(vm.IsBusy); Assert.NotNull(vm.Message);
        });
    }

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.True(task.IsCompleted); task.GetAwaiter().GetResult();
    }
    private sealed class FakeService : IAccountConfigurationService
    {
        public TaskCompletionSource<string>? Pending { get; set; }
        public SaveAccountSettings? Request { get; private set; }
        public bool FailDiscovery { get; set; }
        public int SaveCount { get; private set; }
        public int DiscoverCount { get; private set; }
        public Task<AccountConfiguration> ReadAsync(CancellationToken t = default) => Task.FromResult(new AccountConfiguration(
            [new ModelProfileOption("profile-1", "One", BackendType.OpenCode, DataClassification.PublicSource, true),
             new ModelProfileOption("profile-2", "Two", BackendType.OpenCode, DataClassification.PublicSource, true)], []));
        public Task<string> SaveAsync(SaveAccountSettings request, CancellationToken t = default)
        { SaveCount++; Request = request; return Pending?.Task ?? Task.FromResult("created"); }
        public Task<IReadOnlyList<AccountImportCandidate>> DiscoverAsync(string p, CancellationToken t = default)
        {
            DiscoverCount++;
            if (FailDiscovery) throw new InvalidOperationException("Bearer secret-fixture /auth.json");
            return Task.FromResult<IReadOnlyList<AccountImportCandidate>>([new("native-1", "Fixture", null)]);
        }
        public Task<string> ImportAsync(string p, AccountImportCandidate c, CancellationToken t = default) => Task.FromResult(c.Id);
    }
}

