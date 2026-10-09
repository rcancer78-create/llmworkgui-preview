using System.IO;
using System.Windows.Controls;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.App.Views;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ProviderHeaderEditorTests
{
    [Fact]
    public async Task SavedProvider_EditorBindingIsEnabledAfterSaveCompletes()
    {
        StaTestRunner.EnsureApplication();
        var root = Path.Combine(Path.GetTempPath(), "LLMWorkGUI-provider-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "test.db"));
        try
        {
            await new DatabaseMigrator(factory).MigrateAsync();
            await StaTestRunner.Run(async () =>
            {
                var vm = new ProvidersAccountsViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                    profileRepository: new SqliteProviderProfileRepository(factory));
                vm.EditingProviderId = "provider";
                vm.EditingDisplayName = "Local test provider";
                vm.EditingBaseUrl = "http://127.0.0.1:1/v1";
                var editor = new StackPanel { DataContext = vm };
                editor.SetBinding(System.Windows.UIElement.IsEnabledProperty,
                    new System.Windows.Data.Binding(nameof(vm.CanConfigureProvider)));
                Assert.True(editor.IsEnabled);
                await vm.ExecuteConfirmAndSaveAsync();
                Assert.Contains("успешно", vm.StatusMessage);
                Assert.False(vm.IsBusy);
                Assert.True(vm.CanConfigureProvider);
                Assert.True(editor.IsEnabled);
            });
        }
        finally
        {
            using var pool = factory.CreateConnection();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "plain")]
    [InlineData(true, "plain")]
    [InlineData(false, "key")]
    [InlineData(true, "key")]
    [InlineData(false, "header")]
    [InlineData(true, "header")]
    public async Task RepeatedSave_UsesCommittedRevisionAndPreservesModels(bool existing, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMWorkGUI-repeat-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "test.db"));
        try
        {
            await new DatabaseMigrator(factory).MigrateAsync();
            var repo = new SqliteProviderProfileRepository(factory);
            if (existing)
                await repo.UpsertAsync(new("provider", "Existing", LLMWorkGUI.Domain.Enums.BackendType.OpenCode,
                    "https://provider.test", null, LLMWorkGUI.Domain.Enums.DataClassification.PublicSource, true));
            var store = new DpapiSecretStore(Path.Combine(root, "secrets"));
            using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(factory), repo);
            var vm = new ProvidersAccountsViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                profileRepository: repo, secretStore: store, secretLifecycle: lifecycle,
                modelRefreshService: new OwnedConfirmedRefresh());
            if (existing)
            {
                await vm.LoadProvidersAsync();
                vm.SelectedProvider = Assert.Single(vm.Providers);
            }
            else
            {
                vm.EditingProviderId = "provider";
                vm.EditingBaseUrl = "https://provider.test";
            }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                vm.EditingDisplayName = "Saved " + attempt;
                if (mode == "key") vm.EditingApiKey = "synthetic-key-" + attempt;
                if (mode == "header")
                {
                    vm.EditingHeaders.Clear();
                    vm.EditingHeaders.Add(new("X-Token", "synthetic-header-" + attempt));
                }
                await vm.ExecuteRefreshModelsAsync();
                await vm.ExecuteConfirmAndSaveAsync();
                Assert.Contains("успешно", vm.StatusMessage);
                var saved = (await repo.GetByIdAsync("provider"))!;
                Assert.Equal("Saved " + attempt, saved.DisplayName);
                Assert.Equal((long)(attempt + (existing ? 1 : 0)), saved.Revision);
                Assert.Equal(saved.Revision, vm.SelectedProvider?.Revision);
                Assert.Single(vm.DiscoveredModels);
                Assert.Empty(vm.EditingApiKey);
            }
        }
        finally
        {
            using var pool = factory.CreateConnection();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_ListRefreshDoesNotAdoptConcurrentWritersRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMWorkGUI-save-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "test.db"));
        try
        {
            await new DatabaseMigrator(factory).MigrateAsync();
            var repo = new SqliteProviderProfileRepository(factory);
            var catalog = new CallbackModelCatalog(async () =>
            {
                var current = (await repo.GetByIdAsync("provider"))!;
                await repo.UpsertAsync(new(current.Id, "Concurrent writer", current.Backend, current.BaseUrl,
                    current.ExecutablePath, current.MaxDataClass, current.IsEnabled, revision: current.Revision));
            });
            var vm = new ProvidersAccountsViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                profileRepository: repo, modelCatalogWriter: catalog);
            vm.EditingProviderId = "provider";
            vm.EditingDisplayName = "My form";
            vm.EditingBaseUrl = "https://provider.test";
            vm.DiscoveredModels.Add(new ModelItemViewModel(ModelCapabilityDetector.DetectCapabilities("model")));
            await vm.ExecuteConfirmAndSaveAsync();
            Assert.Contains("успешно", vm.StatusMessage);
            Assert.Equal("My form", vm.EditingDisplayName);
            await vm.ExecuteConfirmAndSaveAsync();
            Assert.Contains("Сбой", vm.StatusMessage);
            Assert.Equal("Concurrent writer", (await repo.GetByIdAsync("provider"))!.DisplayName);
            Assert.Equal(1L, (await repo.GetByIdAsync("provider"))!.Revision);
        }
        finally
        {
            using var pool = factory.CreateConnection();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SecondaryInstance_DisablesProviderCommandsAndRetainsUnsavedInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMWorkGUI-header-guard-" + Guid.NewGuid().ToString("N"));
        using var first = new LLMWorkGUI.Infrastructure.Concurrency.ApplicationInstanceGuard(root);
        using var second = new LLMWorkGUI.Infrastructure.Concurrency.ApplicationInstanceGuard(root);
        Assert.True(second.IsViewOnly);
        var vm = new ProvidersAccountsViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
            instanceGuard: second);
        vm.EditingApiKey = "synthetic-unsaved";
        vm.EditingHeaders.Add(new("X-Token", "synthetic-header", true));
        Assert.False(vm.CanConfigureProvider);
        Assert.False(vm.ConfirmAndSaveCommand.CanExecute(null));
        Assert.False(vm.DeleteProviderCommand.CanExecute(null));
        Assert.False(vm.NewProviderCommand.CanExecute(null));
        vm.NewProviderCommand.Execute(null);
        await vm.ExecuteConfirmAndSaveAsync();
        await vm.ExecuteDeleteProviderAsync();
        Assert.Equal("synthetic-unsaved", vm.EditingApiKey);
        Assert.Equal("synthetic-header", Assert.Single(vm.EditingHeaders).Value);
    }

    [Fact]
    public async Task SaveAndReopen_PreservesHeadersWithoutLoadingSecretText_AndFailedSaveRetainsInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMWorkGUI-header-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(Path.Combine(root, "test.db"));
            await new DatabaseMigrator(factory).MigrateAsync();
            var repo = new SqliteProviderProfileRepository(factory);
            var store = new DpapiSecretStore(Path.Combine(root, "secrets"));
            using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(factory), repo);
            ProvidersAccountsViewModel Open() => new(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                profileRepository: repo, secretStore: store, secretLifecycle: lifecycle);
            var first = Open();
            first.EditingProviderId = "provider";
            first.EditingDisplayName = "Provider";
            first.EditingBaseUrl = "https://provider.test";
            first.EditingHeaders.Add(new("X-Region", "east"));
            first.EditingHeaders.Add(new("X-Token", "synthetic-ui-header-canary"));
            await first.ExecuteConfirmAndSaveAsync();
            Assert.Contains("успешно", first.StatusMessage);
            Assert.Empty(first.EditingHeaders[1].Value);
            var reopened = Open();
            await reopened.LoadProvidersAsync();
            reopened.SelectedProvider = Assert.Single(reopened.Providers);
            Assert.Equal("east", reopened.EditingHeaders[0].Value);
            var secret = reopened.EditingHeaders[1];
            Assert.Empty(secret.Value);
            Assert.True(secret.IsSensitive);
            var originalRef = secret.SecretReference!;
            Assert.Equal("synthetic-ui-header-canary", await store.GetSecretAsync(originalRef));
            await reopened.ExecuteConfirmAndSaveAsync();
            Assert.Equal(originalRef, reopened.EditingHeaders[1].SecretReference);
            await using var connection = await factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_save BEFORE UPDATE ON ProviderProfiles BEGIN SELECT RAISE(ABORT,'synthetic fault'); END;";
            await command.ExecuteNonQueryAsync();
            reopened.EditingHeaders[1].Value = "synthetic-replacement";
            await reopened.ExecuteConfirmAndSaveAsync();
            Assert.Equal("synthetic-replacement", reopened.EditingHeaders[1].Value);
            Assert.Contains("Сбой", reopened.StatusMessage);
            Assert.DoesNotContain("synthetic-replacement", reopened.StatusMessage);
            Assert.Equal("synthetic-ui-header-canary", await store.GetSecretAsync(originalRef));
            command.CommandText = "DROP TRIGGER fail_save;";
            await command.ExecuteNonQueryAsync();
            var ancillaryFailure = new ProvidersAccountsViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                profileRepository: repo, secretStore: store, secretLifecycle: lifecycle, modelCatalogWriter: new FailingModelCatalog());
            await ancillaryFailure.LoadProvidersAsync();
            ancillaryFailure.SelectedProvider = Assert.Single(ancillaryFailure.Providers);
            ancillaryFailure.EditingHeaders[1].Value = "committed-header-value";
            ancillaryFailure.DiscoveredModels.Add(new ModelItemViewModel(ModelCapabilityDetector.DetectCapabilities("model")));
            await ancillaryFailure.ExecuteConfirmAndSaveAsync();
            Assert.Contains("Профиль и секреты сохранены", ancillaryFailure.StatusMessage);
            Assert.Empty(ancillaryFailure.EditingHeaders[1].Value);
            Assert.Equal("committed-header-value", await store.GetSecretAsync((await repo.GetByIdAsync("provider"))!.CustomHeaders![1].SecretReference!));
            var withKey = Open();
            await withKey.LoadProvidersAsync();
            withKey.SelectedProvider = Assert.Single(withKey.Providers);
            withKey.EditingApiKey = "synthetic-later-missing";
            await withKey.ExecuteConfirmAndSaveAsync();
            await store.DeleteSecretAsync((await repo.GetApiKeySecretReferenceAsync("provider"))!);
            var missingKey = Open();
            await missingKey.LoadProvidersAsync();
            missingKey.SelectedProvider = Assert.Single(missingKey.Providers);
            await missingKey.ExecuteConfirmAndSaveAsync();
            Assert.Contains("доступность ключа не проверена", missingKey.StatusMessage);
            Assert.DoesNotContain("Ключ сохранён в хранилище", missingKey.StatusMessage);
            var stale = Open();
            await stale.LoadProvidersAsync();
            stale.SelectedProvider = Assert.Single(stale.Providers);
            var fresh = Open();
            await fresh.LoadProvidersAsync();
            fresh.SelectedProvider = Assert.Single(fresh.Providers);
            fresh.EditingDisplayName = "New committed name";
            await fresh.ExecuteConfirmAndSaveAsync();
            stale.EditingDisplayName = "Stale editor name";
            await stale.ExecuteConfirmAndSaveAsync();
            Assert.Contains("Сбой", stale.StatusMessage);
            Assert.Equal("New committed name", (await repo.GetByIdAsync("provider"))!.DisplayName);
            await stale.ExecuteDeleteProviderAsync();
            Assert.Contains("не выполнено", stale.StatusMessage);
            Assert.NotNull(await repo.GetByIdAsync("provider"));
        }
        finally
        {
            using var pool = new SqliteConnectionFactory(Path.Combine(root, "test.db")).CreateConnection();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SecretEditor_MasksSensitiveNamesAndCheckedValues_AndClearsWithModel()
    {
        StaTestRunner.EnsureApplication();
        StaTestRunner.Run(() =>
        {
            var model = new CustomHeaderItemViewModel("X-Token", "synthetic-secret");
            var editor = new ProviderHeaderValueBox { DataContext = model };
            var password = Assert.IsType<PasswordBox>(editor.Content);
            Assert.Equal("synthetic-secret", password.Password);
            password.Password = "new-secret";
            Assert.Equal("new-secret", model.Value);
            model.Value = "";
            Assert.Empty(password.Password);
            model.Name = "X-Region";
            model.Value = "east";
            Assert.Equal("east", Assert.IsType<TextBox>(editor.Content).Text);
            model.IsSecret = true;
            Assert.Equal("east", Assert.IsType<PasswordBox>(editor.Content).Password);
            editor.DataContext = new CustomHeaderItemViewModel("X-Token", "", true, "urn:llmworkgui:secret:saved");
            Assert.Empty(Assert.IsType<PasswordBox>(editor.Content).Password);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("X-Header")]
    public void InvalidHeaderPreview_IsRefusedWithoutThrowingOrOpeningDialog(string name)
    {
        var model = new ProvidersAccountsViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
            configService: new LLMWorkGUI.Infrastructure.Providers.OpenCodeConfigService());
        model.EditingHeaders.Add(new(name, name.Length == 0 ? "value" : new string('x', 32769)));
        model.ExecutePreviewConfig();
        Assert.False(model.PreviewDialog.IsVisible);
        Assert.Contains("проверьте", model.StatusMessage);
    }

    [Theory]
    [InlineData("new", false)]
    [InlineData("new", true)]
    [InlineData("selection", false)]
    [InlineData("selection", true)]
    public async Task BusySaveCannotResetOrSwitchTheProviderForm(string action, bool failCatalog)
    {
        StaTestRunner.EnsureApplication();
        var root = Path.Combine(Path.GetTempPath(), "LLMWorkGUI-provider-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "test.db"));
        try
        {
            await new DatabaseMigrator(factory).MigrateAsync();
            var repo = new SqliteProviderProfileRepository(factory);
            await repo.UpsertAsync(new("provider", "Original", LLMWorkGUI.Domain.Enums.BackendType.OpenCode,
                "https://original.test", null, LLMWorkGUI.Domain.Enums.DataClassification.PublicSource, true));
            await repo.UpsertAsync(new("other", "Other", LLMWorkGUI.Domain.Enums.BackendType.OpenCode,
                "https://other.test", null, LLMWorkGUI.Domain.Enums.DataClassification.PublicSource, true));
            await StaTestRunner.Run(async () =>
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var catalog = new CallbackModelCatalog(async () =>
                {
                    entered.TrySetResult(); await release.Task;
                    if (failCatalog) throw new IOException("synthetic catalog fault");
                });
                var vm = new ProvidersAccountsViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                    profileRepository: repo, modelCatalogWriter: catalog);
                await vm.LoadProvidersAsync();
                var original = vm.Providers.Single(p => p.Id == "provider");
                var other = vm.Providers.Single(p => p.Id == "other");
                vm.SelectedProvider = original;
                vm.EditingDisplayName = "Saved original";
                vm.DiscoveredModels.Add(new ModelItemViewModel(ModelCapabilityDetector.DetectCapabilities("model")));
                var selection = new ListBox { DataContext = vm };
                selection.SetBinding(System.Windows.UIElement.IsEnabledProperty,
                    new System.Windows.Data.Binding(nameof(vm.CanSelectProvider)));
                Assert.True(selection.IsEnabled);
                var save = vm.ExecuteConfirmAndSaveAsync();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(vm.IsBusy);
                    Assert.False(selection.IsEnabled);
                    // Exercise direct Execute/setter too, not merely the disabled button.
                    if (action == "new") vm.NewProviderCommand.Execute(null);
                    else vm.SelectedProvider = other;
                    Assert.Equal("provider", vm.EditingProviderId);
                    Assert.Equal("Saved original", vm.EditingDisplayName);
                    Assert.Same(original, vm.SelectedProvider);
                    Assert.Single(vm.DiscoveredModels);
                    Assert.False(vm.NewProviderCommand.CanExecute(null));
                }
                finally { release.TrySetResult(); await save.WaitAsync(TimeSpan.FromSeconds(5)); }
                Assert.False(vm.IsBusy); Assert.True(vm.NewProviderCommand.CanExecute(null));
                Assert.True(selection.IsEnabled);
                Assert.Equal("Saved original", (await repo.GetByIdAsync("provider"))!.DisplayName);
                Assert.Equal("Other", (await repo.GetByIdAsync("other"))!.DisplayName);
                Assert.Contains(failCatalog ? "Не удалось" : "успешно", vm.StatusMessage);
                vm.SelectedProvider = vm.Providers.Single(p => p.Id == "other");
                Assert.Equal("other", vm.EditingProviderId);
            });
        }
        finally
        {
            using var pool = factory.CreateConnection();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("reload-failure")]
    [InlineData("delete-failure")]
    public async Task CommittedDeletionClearsTheEditorEvenWhenListRefreshFails(string outcome)
    {
        var root = Path.Combine(Path.GetTempPath(), "LLMWorkGUI-provider-delete-form-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "test.db"));
        try
        {
            await new DatabaseMigrator(factory).MigrateAsync();
            var actual = new SqliteProviderProfileRepository(factory);
            await actual.UpsertAsync(new("deleted-provider", "Delete this", LLMWorkGUI.Domain.Enums.BackendType.OpenCode,
                "https://provider.test", null, LLMWorkGUI.Domain.Enums.DataClassification.PublicSource, true));
            var read = new FaultingListRepository(actual);
            var store = new DpapiSecretStore(Path.Combine(root, "secrets"));
            using var lifecycle = new SecretLifecycleService(store, new SqliteSecretReferenceRepository(factory), actual);
            var vm = new ProvidersAccountsViewModel(new CliStatusViewModel(FakeCliDetectionService.Degraded(), TimeProvider.System),
                profileRepository: read, secretStore: store, secretLifecycle: lifecycle);
            await vm.LoadProvidersAsync(); vm.SelectedProvider = Assert.Single(vm.Providers);
            vm.EditingApiKey = "synthetic-unsaved-delete-key";
            vm.EditingHeaders.Add(new("X-Region", "synthetic-unsaved-delete-header"));
            vm.DiscoveredModels.Add(new ModelItemViewModel(ModelCapabilityDetector.DetectCapabilities("model")));
            read.FailList = outcome == "reload-failure";
            if (outcome == "delete-failure")
            {
                await using var connection = await factory.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER reject_delete BEFORE DELETE ON ProviderProfiles BEGIN SELECT RAISE(ABORT,'synthetic fault'); END;";
                await command.ExecuteNonQueryAsync();
            }
            await vm.ExecuteDeleteProviderAsync();
            Assert.False(vm.IsBusy);
            if (outcome == "delete-failure")
            {
                Assert.NotNull(await actual.GetByIdAsync("deleted-provider"));
                Assert.Equal("deleted-provider", vm.SelectedProvider?.Id);
                Assert.Equal("synthetic-unsaved-delete-key", vm.EditingApiKey);
                Assert.Single(vm.EditingHeaders); Assert.Single(vm.DiscoveredModels);
                Assert.Contains("не выполнено", vm.StatusMessage);
            }
            else
            {
                Assert.Null(await actual.GetByIdAsync("deleted-provider"));
                Assert.Null(vm.SelectedProvider);
                Assert.NotEqual("deleted-provider", vm.EditingProviderId);
                Assert.Empty(vm.EditingApiKey); Assert.Null(vm.EditingApiKeySecretRef);
                Assert.Empty(vm.EditingHeaders); Assert.Empty(vm.DiscoveredModels);
                Assert.Contains("удалён", vm.StatusMessage);
                read.FailList = false;
                // Save on the cleared form may create a new profile, never recreate the deleted ID.
                var newId = vm.EditingProviderId;
                await vm.ExecuteConfirmAndSaveAsync();
                Assert.Contains("успешно", vm.StatusMessage);
                Assert.Null(await actual.GetByIdAsync("deleted-provider"));
                Assert.NotNull(await actual.GetByIdAsync(newId));
                Assert.Null(await actual.GetApiKeySecretReferenceAsync(newId));
            }
        }
        finally
        {
            using var pool = factory.CreateConnection();
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FaultingListRepository(LLMWorkGUI.Application.Repositories.IProviderProfileRepository inner)
        : LLMWorkGUI.Application.Repositories.IProviderProfileRepository
    {
        public bool FailList { get; set; }
        public Task<IReadOnlyList<LLMWorkGUI.Domain.Entities.ProviderProfile>> ListAsync(CancellationToken cancellationToken = default) =>
            FailList ? throw new IOException("synthetic list refresh fault") : inner.ListAsync(cancellationToken);
        public Task<LLMWorkGUI.Domain.Entities.ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);
        public Task UpsertAsync(LLMWorkGUI.Domain.Entities.ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default) => inner.UpsertAsync(profile, apiKeySecretReference, cancellationToken);
        public Task<long> UpsertReturningRevisionAsync(LLMWorkGUI.Domain.Entities.ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default) => inner.UpsertReturningRevisionAsync(profile, apiKeySecretReference, cancellationToken);
        public Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken cancellationToken = default) => inner.GetApiKeySecretReferenceAsync(id, cancellationToken);
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
    }

    private sealed class FailingModelCatalog : IProviderModelCatalogWriter
    {
        public Task SaveModelsAsync(ProviderModelCatalogQuery query, IReadOnlyList<ProviderModelDescriptor> models, CancellationToken cancellationToken = default) =>
            throw new IOException("synthetic catalog write fault");
        public Task ClearModelsAsync(ProviderModelCatalogQuery query, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OwnedConfirmedRefresh : IModelRefreshService
    {
        public Task<IReadOnlyList<DiscoveredModelDetails>> RefreshModelsAsync(CustomProviderSettings settings,
            string? explicitApiKey = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiscoveredModelDetails>>([new DiscoveredModelDetails("model", "Owned model")]);
    }

    private sealed class CallbackModelCatalog(Func<Task> save) : IProviderModelCatalogWriter
    {
        public Task SaveModelsAsync(ProviderModelCatalogQuery query, IReadOnlyList<ProviderModelDescriptor> models,
            CancellationToken cancellationToken = default) => save();
        public Task ClearModelsAsync(ProviderModelCatalogQuery query, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
