using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public partial class ProvidersAccountsViewModelTests
{
    [Fact]
    public async Task SavingNativeCursorProfile_DoesNotRequireAnHttpUrl()
    {
        var repository = new FakeProviderRepository();
        await repository.UpsertAsync(new ProviderProfile("cursor", "Cursor", BackendType.CursorAcp,
            null, "cursor-agent.exe", DataClassification.PrivateSource, true));
        var model = CreateViewModel(provRepo: repository);
        await model.LoadProvidersAsync();
        model.SelectedProvider = Assert.Single(model.Providers);
        model.EditingDisplayName = "Renamed Cursor";
        await model.ExecuteConfirmAndSaveAsync();
        var saved = await repository.GetByIdAsync("cursor");
        Assert.Equal("Renamed Cursor", saved!.DisplayName);
        Assert.Null(saved.BaseUrl);
        Assert.Equal("cursor-agent.exe", saved.ExecutablePath);
    }

    [Fact]
    public async Task SavingExistingProvider_PreservesBackendExecutableAndGatewayIdentity()
    {
        var repository = new FakeProviderRepository();
        await repository.UpsertAsync(new ProviderProfile("provider", "Provider", BackendType.CursorAcp,
            "https://example.test", "cursor-agent.exe", DataClassification.PrivateSource, true, "native-id"));
        var model = CreateViewModel(provRepo: repository);
        model.EditingProviderId = "provider";
        model.EditingDisplayName = "Renamed";
        model.EditingBaseUrl = "https://example.test";
        await model.ExecuteConfirmAndSaveAsync();
        var saved = await repository.GetByIdAsync("provider");
        Assert.Equal(BackendType.CursorAcp, saved!.Backend);
        Assert.Equal("cursor-agent.exe", saved.ExecutablePath);
        Assert.Equal("native-id", saved.GatewayNativeId);
    }

    [Fact]
    public async Task MissingSecretStorage_DoesNotDiscardEnteredKeyOrClaimSave()
    {
        var repository = new FakeProviderRepository();
        var model = new ProvidersAccountsViewModel(new CliStatusViewModel(new FakeCliDetectionService(), TimeProvider.System),
            profileRepository: repository, configService: new FakeConfigService());
        model.EditingProviderId = "provider";
        model.EditingDisplayName = "Provider";
        model.EditingBaseUrl = "https://example.test";
        model.EditingApiKey = "unsaved-key";
        await model.ExecuteConfirmAndSaveAsync();
        Assert.Null(await repository.GetByIdAsync("provider"));
        Assert.Equal("unsaved-key", model.EditingApiKey);
        Assert.DoesNotContain("unsaved-key", model.StatusMessage);
    }
    [Fact]
    public async Task FailedProfileWrite_PreservesEnteredKeyAndRemovesNewUnboundPayload()
    {
        var repository = new FakeProviderRepository { UpsertFailure = new System.IO.IOException("synthetic failure") };
        var store = new FakeSecretStore();
        var model = CreateViewModel(provRepo: repository, secretStore: store);
        model.EditingProviderId = "provider";
        model.EditingDisplayName = "Provider";
        model.EditingBaseUrl = "https://example.test";
        model.EditingApiKey = "unsaved-key";
        await model.ExecuteConfirmAndSaveAsync();
        Assert.Equal("unsaved-key", model.EditingApiKey);
        Assert.Null(model.EditingApiKeySecretRef);
        Assert.Equal(0, store.Count);
        Assert.Null(await repository.GetByIdAsync("provider"));
    }

    private class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _store = new();
        public int Count => _store.Count;

        public Task<string> SaveSecretAsync(string secret, CancellationToken cancellationToken = default)
        {
            var reference = $"urn:llmworkgui:secret:{Guid.NewGuid():N}";
            _store[reference] = secret;
            return Task.FromResult(reference);
        }

        public Task<string?> GetSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            _store.TryGetValue(secretReference, out var secret);
            return Task.FromResult(secret);
        }

        public Task<bool> DeleteSecretAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_store.Remove(secretReference));
        }
    }

    private class FakeProviderRepository : IProviderProfileRepository
    {
        private readonly Dictionary<string, (ProviderProfile Profile, string? SecretRef)> _profiles = new();
        public Exception? DeleteFailure { get; set; }
        public Exception? UpsertFailure { get; set; }

        public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ProviderProfile>>(_profiles.Values.Select(v => v.Profile).ToList());
        }

        public Task<ProviderProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            _profiles.TryGetValue(id, out var tuple);
            return Task.FromResult<ProviderProfile?>(tuple.Profile);
        }

        public Task UpsertAsync(ProviderProfile profile, string? apiKeySecretReference = null, CancellationToken cancellationToken = default)
        {
            if (UpsertFailure is not null) throw UpsertFailure;
            _profiles[profile.Id] = (profile, apiKeySecretReference);
            return Task.CompletedTask;
        }

        public Task<string?> GetApiKeySecretReferenceAsync(string id, CancellationToken cancellationToken = default)
        {
            _profiles.TryGetValue(id, out var tuple);
            return Task.FromResult(tuple.SecretRef);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            if (DeleteFailure is not null) throw DeleteFailure;
            return Task.FromResult(_profiles.Remove(id));
        }
    }

    private class FakeApprovalRuleRepository : IApprovalRuleRepository
    {
        private readonly Dictionary<string, ApprovalRule> _rules = new();
        public Exception? DeleteFailure { get; set; }

        public Task<IReadOnlyList<ApprovalRule>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ApprovalRule>>(_rules.Values.ToList());
        }

        public Task<IReadOnlyList<ApprovalRule>> ListByProjectIdAsync(string projectId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ApprovalRule>>(_rules.Values.Where(r => r.ProjectId == projectId).ToList());
        }

        public Task<ApprovalRule?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
        {
            _rules.TryGetValue(id, out var rule);
            return Task.FromResult<ApprovalRule?>(rule);
        }

        public Task UpsertAsync(ApprovalRule rule, CancellationToken cancellationToken = default)
        {
            _rules[rule.Id] = rule;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            if (DeleteFailure is not null) throw DeleteFailure;
            return Task.FromResult(_rules.Remove(id));
        }
    }

    private class FakeConnectionTestService : IProviderConnectionTestService
    {
        public Func<Task<ProviderConnectionTestResult>> OnTest = () =>
            Task.FromResult(ProviderConnectionTestResult.CreateSuccess(
                "http://127.0.0.1:11434/v1/models",
                42,
                new[] { new DiscoveredModelInfo("mock-model-1", "Mock Model 1") }));

        public Task<ProviderConnectionTestResult> TestConnectionAsync(
            CustomProviderSettings settings,
            string? explicitApiKey = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            return OnTest();
        }
    }

    private class FakeConfigService : IOpenCodeConfigService
    {
        public UrlValidationResult ValidateBaseUrl(string? baseUrl) => ProviderUrlValidator.Validate(baseUrl);

        public string GenerateProviderConfigJson(CustomProviderSettings settings, string? resolvedApiKey = null, bool redactSecrets = false) => "{}";

        public ConfigPreviewResult GeneratePreview(CustomProviderSettings settings, string? existingConfigContent = null, string? resolvedApiKey = null)
        {
            var maskedApiKey = redactSecrets(resolvedApiKey);
            return new ConfigPreviewResult(
                GeneratedJson: $"{{\"apiKey\": \"{resolvedApiKey}\"}}",
                RedactedJson: $"{{\"apiKey\": \"{maskedApiKey}\"}}",
                DiffText: $"+ apiKey: \"{maskedApiKey}\"",
                HasChanges: true,
                HasExistingConfig: false);
        }

        public string MergeConfig(string existingConfigContent, CustomProviderSettings settings, string? resolvedApiKey = null) => "{}";

        public async Task<string> CreateUnboundApiKeyReferenceAsync(
            string rawApiKey,
            ISecretStore secretStore,
            ISecretLifecycleService? secretLifecycle = null,
            CancellationToken cancellationToken = default)
        {
            if (secretLifecycle is not null)
            {
                var status = await secretLifecycle.CreateAsync(rawApiKey, cancellationToken: cancellationToken);
                return status.Reference;
            }

            return await secretStore.SaveSecretAsync(rawApiKey, cancellationToken);
        }

        private static string redactSecrets(string? val) => string.IsNullOrEmpty(val) ? "" : "***REDACTED***";
    }

    private class FakeDiagnosticExportService : IProviderDiagnosticExportService
    {
        public Exception? Failure { get; init; }
        public Task<ProviderDiagnosticReport> GenerateExportAsync(
            CustomProviderSettings settings,
            ProviderConnectionTestResult? connectionResult = null,
            IReadOnlyList<PluginInfo>? plugins = null,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            var report = new ProviderDiagnosticReport
            {
                ProviderId = settings.ProviderId,
                DisplayName = settings.DisplayName,
                SanitizedBaseUrl = settings.BaseUrl,
                SanitizedHeaders = new[] { new CustomProviderHeader("Authorization", "***REDACTED***", true) }
            };
            return Task.FromResult(report);
        }
    }

    private class FakeCliDetectionService : ICliDetectionService
    {
        public Task<CliDetectionSnapshot> DetectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CliDetectionSnapshot.AllNotDetected(DateTimeOffset.UtcNow));
        }
    }

    private ProvidersAccountsViewModel CreateViewModel(
        FakeProviderRepository? provRepo = null,
        FakeApprovalRuleRepository? ruleRepo = null,
        FakeSecretStore? secretStore = null,
        ISecretLifecycleService? secretLifecycle = null,
        IProviderModelCatalogWriter? modelCatalogWriter = null,
        IProjectRepository? projectRepository = null,
        IProviderConnectionTestService? connectionTestService = null,
        IModelRefreshService? modelRefreshService = null,
        IProviderDiagnosticExportService? exportService = null)
    {
        var cliStatus = new CliStatusViewModel(new FakeCliDetectionService(), TimeProvider.System);
        var profileRepository = provRepo ?? new FakeProviderRepository();
        if (secretLifecycle is RecordingSecretLifecycle recording) recording.Profiles = profileRepository;
        return new ProvidersAccountsViewModel(
            cliStatus,
            profileRepository: profileRepository,
            approvalRuleRepository: ruleRepo ?? new FakeApprovalRuleRepository(),
            configService: new FakeConfigService(),
            connectionTestService: connectionTestService ?? new FakeConnectionTestService(),
            modelRefreshService: modelRefreshService,
            pluginInventoryService: null,
            exportService: exportService ?? new FakeDiagnosticExportService(),
            secretStore: secretStore ?? new FakeSecretStore(),
            secretLifecycle: secretLifecycle,
            modelCatalogWriter: modelCatalogWriter,
            projectRepository: projectRepository);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("delete")]
    [InlineData("remove-rule")]
    [InlineData("refresh")]
    [InlineData("export")]
    public async Task CommandFailure_IsHandledAndDoesNotExposePrivateDiagnostics(string operation)
    {
        var failure = new InvalidOperationException("synthetic-private-provider-value");
        var providers = new FakeProviderRepository();
        var rules = new FakeApprovalRuleRepository();
        var vm = CreateViewModel(provRepo: providers, ruleRepo: rules,
            connectionTestService: new FakeConnectionTestService { OnTest = () => throw failure },
            modelRefreshService: new ThrowingModelRefresh(failure),
            exportService: new FakeDiagnosticExportService { Failure = failure });
        vm.EditingProviderId = "review-provider";
        vm.EditingDisplayName = "Review provider";
        vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";
        await vm.ExecuteConfirmAndSaveAsync();
        vm.SelectedProvider = Assert.Single(vm.Providers);
        Func<Task> action;
        if (operation == "connection") action = vm.ExecuteTestConnectionAsync;
        else if (operation == "refresh") action = vm.ExecuteRefreshModelsAsync;
        else if (operation == "export") action = vm.ExecuteExportDiagnosticsAsync;
        else if (operation == "delete")
        {
            providers.DeleteFailure = failure;
            action = vm.ExecuteDeleteProviderAsync;
        }
        else
        {
            vm.NewRuleKind = NormalizedApprovalKind.ReadFile;
            vm.NewRuleOperation = "read-config";
            await vm.ExecuteAddApprovalRuleAsync();
            rules.DeleteFailure = failure;
            action = () => vm.ExecuteRemoveApprovalRuleAsync(Assert.Single(vm.ApprovalRules));
        }

        Assert.Null(await Record.ExceptionAsync(action));
        Assert.False(vm.IsBusy);
        Assert.DoesNotContain("synthetic-private-provider-value", vm.StatusMessage + vm.ConnectionTestMessage);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public async Task LocalCredentialRefusal_KeepsExistingModelsAndExplainsRefreshFailure()
    {
        var vm = CreateViewModel(modelRefreshService: new ThrowingModelRefresh(new ProviderCredentialUnavailableException()));
        vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";
        vm.DiscoveredModels.Add(new ModelItemViewModel(ModelCapabilityDetector.DetectCapabilities("existing")));
        await vm.ExecuteRefreshModelsAsync();
        Assert.Equal("existing", Assert.Single(vm.DiscoveredModels).Id);
        Assert.Contains("секрет недоступен", vm.StatusMessage);
        Assert.DoesNotContain("Обновлено моделей", vm.StatusMessage);
    }

    private sealed class ThrowingModelRefresh(Exception failure) : IModelRefreshService
    {
        public Task<IReadOnlyList<DiscoveredModelDetails>> RefreshModelsAsync(CustomProviderSettings settings,
            string? explicitApiKey = null, CancellationToken cancellationToken = default) => throw failure;
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-provider")]
    public async Task ApprovalRuleForMissingProvider_DoesNotCreateAProviderOrRule(string providerId)
    {
        var providers = new FakeProviderRepository();
        var rules = new FakeApprovalRuleRepository();
        var vm = CreateViewModel(provRepo: providers, ruleRepo: rules);
        vm.EditingProviderId = providerId;
        vm.NewRuleKind = NormalizedApprovalKind.ReadFile;
        vm.NewRuleOperation = "read-config";
        await vm.ExecuteAddApprovalRuleAsync();
        Assert.Empty(await providers.ListAsync());
        Assert.Empty(await rules.ListAsync());
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public void SelectingSavedCredential_ReportsReferencePresenceWithoutClaimingVerifiedPayload()
    {
        var vm = CreateViewModel();
        var profile = new ProviderProfile("stored", "Stored", BackendType.OpenCode,
            "https://example.invalid/v1", null, DataClassification.PublicSource, true);
        var empty = vm.ApiKeySecretStateText;
        vm.SelectedProvider = new ProviderItemViewModel(profile, "urn:llmworkgui:secret:synthetic");
        Assert.NotEqual(empty, vm.ApiKeySecretStateText);
        Assert.DoesNotContain("urn:", vm.ApiKeySecretStateText);
        Assert.Empty(vm.EditingApiKey);
        vm.SelectedProvider = new ProviderItemViewModel(profile);
        Assert.Equal(empty, vm.ApiKeySecretStateText);
    }

    [Fact]
    public async Task ApprovalRuleRequiresExistingProjectAndAcceptsExplicitSelection()
    {
        using var services = TestSupport.UiTestHost.CreateProvider();
        var projects = (IProjectRepository)services.GetService(typeof(IProjectRepository))!;
        var providers = new FakeProviderRepository();
        await providers.UpsertAsync(new ProviderProfile("rule-provider", "Rule provider", BackendType.OpenCode,
            "https://example.invalid/v1", null, DataClassification.PublicSource, true));
        var vm = CreateViewModel(provRepo: providers, projectRepository: projects);
        vm.EditingProviderId = "rule-provider";
        await vm.InitializeAsync();
        await vm.ExecuteAddApprovalRuleAsync();
        Assert.Empty(vm.ApprovalRules);
        Assert.Contains("Выберите существующий проект", vm.StatusMessage);
        await projects.UpsertAsync(new Project("rule-project", "Rule project", @"D:\test", null,
            false, false, null, null, DataClassification.PublicSource));
        await vm.InitializeAsync();
        Assert.Single(vm.RuleProjects);
        vm.NewRuleProjectId = "rule-project";
        await vm.ExecuteAddApprovalRuleAsync();
        Assert.Single(vm.ApprovalRules);
    }


    [Fact]
    public void EditingBaseUrl_ReactiveValidation_UpdatesStatusAndIsUrlValid()
    {
        var vm = CreateViewModel();

        // 1. Empty URL
        vm.EditingBaseUrl = "";
        Assert.False(vm.IsUrlValid);
        Assert.Equal(UrlClassification.InvalidFormat, vm.UrlClassification);

        // 2. Valid loopback
        vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";
        Assert.True(vm.IsUrlValid);
        Assert.Equal(UrlClassification.ValidLoopbackHttp, vm.UrlClassification);
        Assert.Contains("Loopback", vm.UrlValidationStatus);

        // 3. Valid remote HTTPS
        vm.EditingBaseUrl = "https://api.openai.com/v1";
        Assert.True(vm.IsUrlValid);
        Assert.Equal(UrlClassification.ValidRemoteHttps, vm.UrlClassification);
        Assert.Contains("HTTPS", vm.UrlValidationStatus);

        // 4. Insecure remote HTTP
        vm.EditingBaseUrl = "http://api.remote-llm.com/v1";
        Assert.False(vm.IsUrlValid);
        Assert.Equal(UrlClassification.InsecureRemoteHttp, vm.UrlClassification);
        Assert.Contains("Disallowed", vm.UrlValidationStatus);

        // 5. Malformed
        vm.EditingBaseUrl = "not-a-valid-uri";
        Assert.False(vm.IsUrlValid);
        Assert.Equal(UrlClassification.InvalidFormat, vm.UrlClassification);
    }

    [Fact]
    public async Task TestConnectionCommand_UpdatesStatusAndDiscoveredModels()
    {
        var vm = CreateViewModel();
        vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";

        await vm.ExecuteTestConnectionAsync();

        Assert.Equal(ProviderConnectionStatus.Success, vm.ConnectionTestStatus);
        Assert.Equal(42, vm.ConnectionTestLatencyMs);
        Assert.Single(vm.DiscoveredModels);
        Assert.Equal("mock-model-1", vm.DiscoveredModels[0].Id);
    }

    [Fact]
    public void PreviewConfigCommand_ShowsDialogWithRedactedSecrets()
    {
        var vm = CreateViewModel();
        vm.EditingProviderId = "test-preview-prov";
        vm.EditingDisplayName = "Preview Prov";
        vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";
        vm.EditingApiKey = "sk-super-confidential-token-999";

        vm.ExecutePreviewConfig();

        Assert.True(vm.PreviewDialog.IsVisible);
        Assert.Contains("***REDACTED***", vm.PreviewDialog.DiffText);
        Assert.Contains("***REDACTED***", vm.PreviewDialog.RedactedJson);
        Assert.DoesNotContain("sk-super-confidential-token-999", vm.PreviewDialog.DiffText);
        Assert.DoesNotContain("sk-super-confidential-token-999", vm.PreviewDialog.RedactedJson);
    }

    [Fact]
    public async Task ConfirmAndSaveCommand_BindsSecretToStoreAndSavesProvider()
    {
        var provRepo = new FakeProviderRepository();
        var secretStore = new FakeSecretStore();
        var vm = CreateViewModel(provRepo: provRepo, secretStore: secretStore);

        vm.EditingProviderId = "new-ollama";
        vm.EditingDisplayName = "Local Ollama";
        vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";
        vm.EditingApiKey = "sk-ollama-secret-token";

        await vm.ExecuteConfirmAndSaveAsync();

        // Plaintext API key is cleared from memory
        Assert.Empty(vm.EditingApiKey);

        // URN reference was generated and bound
        Assert.NotNull(vm.EditingApiKeySecretRef);
        Assert.StartsWith("urn:llmworkgui:secret:", vm.EditingApiKeySecretRef);

        // Secret was saved to store
        var storedSecret = await secretStore.GetSecretAsync(vm.EditingApiKeySecretRef!);
        Assert.Equal("sk-ollama-secret-token", storedSecret);

        // Profile exists in repository
        var profile = await provRepo.GetByIdAsync("new-ollama");
        Assert.NotNull(profile);
        Assert.Equal("Local Ollama", profile!.DisplayName);
        Assert.Equal(DataClassification.PublicSource, profile.MaxDataClass);
    }

    [Fact]
    public async Task ConfirmAndSaveCommand_PersistsMaximumDataClassFromTheForm()
    {
        var provRepo = new FakeProviderRepository();
        var vm = CreateViewModel(provRepo: provRepo);

        Assert.Equal(DataClassification.PublicSource, vm.EditingMaxDataClass);

        vm.EditingProviderId = "classified-provider";
        vm.EditingDisplayName = "Classified Provider";
        vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";
        vm.EditingMaxDataClass = DataClassification.PrivateSource;

        await vm.ExecuteConfirmAndSaveAsync();

        var profile = await provRepo.GetByIdAsync("classified-provider");
        Assert.NotNull(profile);
        Assert.Equal(DataClassification.PrivateSource, profile!.MaxDataClass);

        await vm.LoadProvidersAsync();
        vm.SelectedProvider = Assert.Single(vm.Providers);
        Assert.Equal(DataClassification.PrivateSource, vm.EditingMaxDataClass);
    }

    [Fact]
    public async Task ApprovalRules_CanAddAndRemove_BlocksUnknownHighRisk()
    {
        var ruleRepo = new FakeApprovalRuleRepository();
        var providers = new FakeProviderRepository();
        await providers.UpsertAsync(new ProviderProfile("rule-provider", "Rule provider", BackendType.OpenCode,
            "https://example.invalid/v1", null, DataClassification.PublicSource, true));
        var vm = CreateViewModel(provRepo: providers, ruleRepo: ruleRepo);
        vm.EditingProviderId = "rule-provider";

        // 1. Attempt to add UnknownHighRisk rule -> BLOCKED
        vm.NewRuleKind = NormalizedApprovalKind.UnknownHighRisk;
        vm.NewRuleOperation = "dangerous-rm";
        await vm.ExecuteAddApprovalRuleAsync();

        Assert.Contains("UnknownHighRisk", vm.StatusMessage ?? "");
        Assert.Empty(vm.ApprovalRules);

        // 2. Add ReadFile rule -> SUCCEEDS
        vm.NewRuleKind = NormalizedApprovalKind.ReadFile;
        vm.NewRuleOperation = "read-config";
        vm.NewRulePathScope = "config/**";
        await vm.ExecuteAddApprovalRuleAsync();

        Assert.Single(vm.ApprovalRules);
        Assert.Equal(NormalizedApprovalKind.ReadFile, vm.ApprovalRules[0].Kind);
        Assert.Equal("read-config", vm.ApprovalRules[0].Operation);

        // 3. Remove rule -> SUCCEEDS
        await vm.ExecuteRemoveApprovalRuleAsync(vm.ApprovalRules[0]);
        Assert.Empty(vm.ApprovalRules);
    }

    [Fact]
    public async Task ExportDiagnosticsCommand_RedactsSecretHeaders()
    {
        var vm = CreateViewModel();
        vm.EditingProviderId = "test-export";
        vm.EditingDisplayName = "Test Export";
        vm.EditingBaseUrl = "https://api.provider.com/v1";

        await vm.ExecuteExportDiagnosticsAsync();

        Assert.NotNull(vm.LastExportedDiagnosticJson);
        Assert.Contains("***REDACTED***", vm.LastExportedDiagnosticJson);
    }

    [Fact]
    public async Task ConfirmAndSaveCommand_WithSecretLifecycle_UsesTheCommittedReferenceAndReportsTheState()
    {
        var provRepo = new FakeProviderRepository();
        var secretStore = new FakeSecretStore();
        var lifecycle = new RecordingSecretLifecycle();
        var vm = CreateViewModel(provRepo: provRepo, secretStore: secretStore, secretLifecycle: lifecycle);

        vm.EditingProviderId = "lifecycle-provider";
        vm.EditingDisplayName = "Lifecycle Provider";
        vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";
        vm.EditingApiKey = "sk-first-token";

        await vm.ExecuteConfirmAndSaveAsync();

        const string firstReference = "urn:llmworkgui:secret:first-token";

        Assert.Equal(firstReference, vm.EditingApiKeySecretRef);
        Assert.Equal(firstReference, await provRepo.GetApiKeySecretReferenceAsync("lifecycle-provider"));
        Assert.Contains("активен", vm.ApiKeySecretStateText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("активен", vm.StatusMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // The lifecycle returns the fresh reference committed with the replacement value.
        vm.EditingApiKey = "sk-second-token";
        await vm.ExecuteConfirmAndSaveAsync();

        Assert.NotEqual(firstReference, vm.EditingApiKeySecretRef);
        Assert.Equal(vm.EditingApiKeySecretRef, await provRepo.GetApiKeySecretReferenceAsync("lifecycle-provider"));
        Assert.Equal(2, lifecycle.SaveProviderApiKeyCallCount);

        // The UI did not bypass coordinated persistence with a standalone Create call.
        Assert.Empty(lifecycle.CreatedReferences);
    }

    [Fact]
    public async Task ConfirmAndSaveCommand_WithSecretLifecycle_SurfacesARevokedCredential()
    {
        var provRepo = new FakeProviderRepository();
        var lifecycle = new RecordingSecretLifecycle
        {
            State = SecretReferenceState.Revoked
        };
        var vm = CreateViewModel(provRepo: provRepo, secretLifecycle: lifecycle);

        vm.EditingProviderId = "revoked-provider";
        vm.EditingDisplayName = "Revoked Provider";
        vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";
        vm.EditingApiKey = "sk-replacement-token";

        await vm.ExecuteConfirmAndSaveAsync();

        Assert.Contains("отозван", vm.ApiKeySecretStateText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-replacement-token", vm.ApiKeySecretStateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteProviderCommand_UsesCoordinatedLifecycleDeletion()
    {
        var provRepo = new FakeProviderRepository();
        var lifecycle = new RecordingSecretLifecycle();
        var vm = CreateViewModel(provRepo: provRepo, secretLifecycle: lifecycle);

        vm.EditingProviderId = "delete-provider";
        vm.EditingDisplayName = "Delete Provider";
        vm.EditingBaseUrl = "http://127.0.0.1:11434/v1";
        vm.EditingApiKey = "sk-value";
        await vm.ExecuteConfirmAndSaveAsync();

        await vm.LoadProvidersAsync();
        vm.SelectedProvider = Assert.Single(vm.Providers);
        Assert.Equal("urn:llmworkgui:secret:first-token", vm.SelectedProvider.SecretReference);

        await vm.ExecuteDeleteProviderAsync();

        Assert.Equal("delete-provider", lifecycle.DeletedProviderId);
        Assert.Null(lifecycle.RevokedProviderId);
        Assert.Null(await provRepo.GetByIdAsync("delete-provider"));
    }

    [Fact]
    public async Task ConfirmAndSaveCommand_RecordsTheConfirmedModelListAsTheProfilesLocalModels()
    {
        var writer = new RecordingProviderModelCatalogWriter();
        var vm = CreateViewModel(modelCatalogWriter: writer);

        vm.EditingProviderId = "space-bunny";
        vm.EditingDisplayName = "Space Bunny";
        vm.EditingBaseUrl = "http://127.0.0.1:4096";

        // The models the operator confirmed in this screen are what a later route can name.
        vm.DiscoveredModels.Add(new ModelItemViewModel(ModelCapabilityDetector.DetectCapabilities("opencode/space-bunny-free")));
        vm.DiscoveredModels.Add(new ModelItemViewModel(ModelCapabilityDetector.DetectCapabilities("opencode/space-bunny-pro")));

        await vm.ExecuteConfirmAndSaveAsync();

        var save = Assert.Single(writer.SavedModels);

        // The account/profile/backend relation is preserved, so the model can never be reused elsewhere.
        Assert.Equal("space-bunny", save.Query.ProviderProfileId);
        Assert.Equal(BackendType.OpenCode, save.Query.Backend);
        Assert.Equal(
            new[] { "opencode/space-bunny-free", "opencode/space-bunny-pro" },
            save.Models.Select(model => model.ModelId).ToArray());
    }

    [Fact]
    public async Task ConfirmAndSaveCommand_WithoutDiscoveredModels_LeavesTheStoredModelListAlone()
    {
        var writer = new RecordingProviderModelCatalogWriter();
        var vm = CreateViewModel(modelCatalogWriter: writer);

        vm.EditingProviderId = "space-bunny";
        vm.EditingDisplayName = "Space Bunny";
        vm.EditingBaseUrl = "http://127.0.0.1:4096";

        await vm.ExecuteConfirmAndSaveAsync();

        // Nothing was confirmed, so nothing is recorded — an empty list never clears a good one.
        Assert.Empty(writer.SavedModels);
    }

    private sealed class RecordingProviderModelCatalogWriter : IProviderModelCatalogWriter
    {
        public List<(ProviderModelCatalogQuery Query, IReadOnlyList<ProviderModelDescriptor> Models)> SavedModels { get; } = new();

        public Task SaveModelsAsync(
            ProviderModelCatalogQuery query,
            IReadOnlyList<ProviderModelDescriptor> models,
            CancellationToken cancellationToken = default)
        {
            SavedModels.Add((query, models));
            return Task.CompletedTask;
        }

        public Task ClearModelsAsync(ProviderModelCatalogQuery query, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Records what the view model asked the lifecycle to do and answers with a fixed state, so the
    /// wiring is verified without a real secret store.
    /// </summary>
    private sealed class RecordingSecretLifecycle : ISecretLifecycleService    {
        private string Reference => SaveProviderApiKeyCallCount <= 1
            ? "urn:llmworkgui:secret:first-token"
            : "urn:llmworkgui:secret:replacement-" + SaveProviderApiKeyCallCount;
        public IProviderProfileRepository Profiles { get; set; } = null!;

        public SecretReferenceState State { get; set; } = SecretReferenceState.Active;

        public int SaveProviderApiKeyCallCount { get; private set; }

        public string? RevokedProviderId { get; private set; }
        public string? DeletedProviderId { get; private set; }

        public async Task<ProviderDeletionResult> DeleteProviderConfigurationAsync(string providerId, long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            var deleted = await Profiles.DeleteAsync(providerId, cancellationToken);
            DeletedProviderId = providerId;
            return new ProviderDeletionResult(deleted, 0);
        }

        public List<string> CreatedReferences { get; } = new();

        public Task<SecretReferenceStatus> GetStatusAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretReferenceStatus
            {
                Reference = reference,
                State = State,
                IsRegistered = true
            });

        public Task<SecretReferenceStatus> CreateAsync(string rawSecret, SecretReferenceKind kind = SecretReferenceKind.ProviderApiKey, SecretReferenceOwnerBinding? owner = null, CancellationToken cancellationToken = default)
        {
            CreatedReferences.Add(Reference);

            return Task.FromResult(new SecretReferenceStatus
            {
                Reference = Reference,
                State = State,
                IsRegistered = true
            });
        }

        public Task<SecretReferenceStatus> RotateAsync(string reference, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> RevokeAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretReferenceStatus> BindAsync(string reference, SecretReferenceOwnerBinding owner, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<SecretReferenceStatus> SaveProviderApiKeyAsync(ProviderProfile profile, string rawSecret, CancellationToken cancellationToken = default)
        {
            SaveProviderApiKeyCallCount++;
            // The production lifecycle owns profile persistence, not merely secret allocation.
            await Profiles.UpsertAsync(profile, Reference, cancellationToken);

            return new SecretReferenceStatus
            {
                Reference = Reference,
                State = State,
                IsRegistered = true
            };
        }

        public async Task<ProviderConfigurationSaveResult> SaveProviderConfigurationAsync(ProviderProfile profile,
            IReadOnlyList<CustomProviderHeader> headers, string? rawApiKey = null, CancellationToken cancellationToken = default)
        {
            Assert.Empty(headers);
            if (rawApiKey is not null)
                await SaveProviderApiKeyAsync(profile, rawApiKey, cancellationToken);
            else
                await Profiles.UpsertAsync(profile, cancellationToken: cancellationToken);
            var saved = new ProviderProfile(profile.Id, profile.DisplayName, profile.Backend, profile.BaseUrl,
                profile.ExecutablePath, profile.MaxDataClass, profile.IsEnabled, profile.GatewayNativeId,
                profile.CustomHeaders, (profile.Revision ?? -1) + 1);
            return new ProviderConfigurationSaveResult(saved, rawApiKey is null ? null : Reference);
        }

        public Task RevokeProviderApiKeyAsync(string providerProfileId, CancellationToken cancellationToken = default)
        {
            RevokedProviderId = providerProfileId;

            return Task.CompletedTask;
        }

        public Task<SecretReferenceStatus> SaveAccountSecretAsync(string accountId, string rawSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RevokeAccountSecretAsync(string accountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
