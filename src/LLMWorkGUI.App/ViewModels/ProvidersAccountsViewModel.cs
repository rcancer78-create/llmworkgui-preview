using System.Collections.ObjectModel;
using System.Windows.Input;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed class ProvidersAccountsViewModel : ScreenViewModel
{
    private readonly IProviderProfileRepository? _profileRepository;
    private readonly IProjectRepository? _projectRepository;
    public ObservableCollection<Project> RuleProjects { get; } = new();
    private readonly IApprovalRuleRepository? _approvalRuleRepository;
    private readonly IOpenCodeConfigService? _configService;
    private readonly IProviderConnectionTestService? _connectionTestService;
    private readonly IModelRefreshService? _modelRefreshService;
    private readonly IPluginInventoryService? _pluginInventoryService;
    private readonly IProviderDiagnosticExportService? _exportService;
    private readonly ISecretStore? _secretStore;
    private readonly ISecretLifecycleService? _secretLifecycle;
    private readonly LLMWorkGUI.Application.Concurrency.IApplicationInstanceGuard? _instanceGuard;
    private readonly IProviderModelCatalogWriter? _modelCatalogWriter;

    private ProviderItemViewModel? _selectedProvider;
    private string? _editingSnapshotId;
    private long? _editingSnapshotRevision;
    private long _discoveryVersion;
    private bool _modelsConfirmed;
    private bool _normalizingHeaders;
    private readonly HashSet<CustomHeaderItemViewModel> _observedHeaders = [];
    private ApprovalRuleItemViewModel? _selectedApprovalRule;

    private string _editingProviderId = string.Empty;
    private string _editingDisplayName = string.Empty;
    private string _editingBaseUrl = string.Empty;
    private string _editingApiKey = string.Empty;
    private string? _editingApiKeySecretRef;
    private bool _editingIsEnabled = true;
    private DataClassification _editingMaxDataClass = DataClassification.PublicSource;

    private string _urlValidationStatus = "URL не указан";
    private bool _isUrlValid;
    private UrlClassification _urlClassification = UrlClassification.InvalidFormat;

    private ProviderConnectionStatus? _connectionTestStatus;
    private string? _connectionTestMessage;
    private long? _connectionTestLatencyMs;
    private bool _isBusy;
    private int _saveInProgress;
    private string? _statusMessage;
    private string? _lastExportedDiagnosticJson;
    private string _apiKeySecretStateText = "Ключ провайдера не задан.";

    // Approval rule creation inputs
    private string _newRuleOperation = "read";
    private NormalizedApprovalKind _newRuleKind = NormalizedApprovalKind.ReadFile;
    private string? _newRulePathScope;
    private string _newRuleProjectId = "default-project";
    private BackendType _newRuleBackend = BackendType.OpenCode;

    public CliStatusViewModel CliStatus { get; }
    public ConfigPreviewDialogViewModel PreviewDialog { get; } = new();
    public AccountManagementViewModel AccountManagement { get; }
    public GatewayCatalogImportViewModel GatewayImport { get; }
    public bool IsGatewayImportIdle => !GatewayImport.IsBusy;
    public bool CanSelectProvider => !IsBusy && IsGatewayImportIdle;
    public bool CanCreateProvider => CanSelectProvider && _instanceGuard?.IsViewOnly != true;
    public bool CanModifyApprovalRules => CanSelectProvider && _instanceGuard?.IsViewOnly != true;
    public bool IsNativeGatewayProvider => SelectedProvider?.Backend == BackendType.NativeGateway
        || Providers.Any(p => p.Id == EditingProviderId && p.Backend == BackendType.NativeGateway);
    public bool CanConfigureProvider => CanSelectProvider && !IsNativeGatewayProvider
        && _instanceGuard?.IsViewOnly != true;
    public bool CanDeleteProvider => CanConfigureProvider && SelectedProvider is not null;
    public bool IsProviderIdReadOnly => _editingSnapshotId is not null;

    /// <summary>
    /// Redacted state of the saved credential for the provider being edited. The value is never
    /// shown: only whether the key is active, missing or revoked (ADR-0005 §2.4).
    /// </summary>
    public string ApiKeySecretStateText
    {
        get => _apiKeySecretStateText;
        private set => SetProperty(ref _apiKeySecretStateText, value);
    }


    public ObservableCollection<ProviderItemViewModel> Providers { get; } = new();
    public ObservableCollection<CustomHeaderItemViewModel> EditingHeaders { get; } = new();
    public ObservableCollection<ModelItemViewModel> DiscoveredModels { get; } = new();
    public ObservableCollection<PluginItemViewModel> InstalledPlugins { get; } = new();
    public ObservableCollection<ApprovalRuleItemViewModel> ApprovalRules { get; } = new();

    public ProviderItemViewModel? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (CanSelectProvider) SetSelectedProvider(value);
        }
    }

    private void SetSelectedProvider(ProviderItemViewModel? value)
    {
        if (!SetProperty(ref _selectedProvider, value, nameof(SelectedProvider))) return;
        if (value is not null) LoadProviderIntoForm(value);
        UpdateProviderCommands();
    }

    public ApprovalRuleItemViewModel? SelectedApprovalRule
    {
        get => _selectedApprovalRule;
        set => SetProperty(ref _selectedApprovalRule, value);
    }

    public string EditingProviderId
    {
        get => _editingProviderId;
        set
        {
            if (SetProperty(ref _editingProviderId, value))
            {
                InvalidateDiscovery();
                UpdateProviderCommands();
            }
        }
    }

    public string EditingDisplayName
    {
        get => _editingDisplayName;
        set => SetProperty(ref _editingDisplayName, value);
    }

    public string EditingBaseUrl
    {
        get => _editingBaseUrl;
        set
        {
            if (SetProperty(ref _editingBaseUrl, value))
            {
                InvalidateDiscovery();
                UpdateUrlValidation();
            }
        }
    }

    public string EditingApiKey
    {
        get => _editingApiKey;
        set { if (SetProperty(ref _editingApiKey, value)) InvalidateDiscovery(); }
    }

    public string? EditingApiKeySecretRef
    {
        get => _editingApiKeySecretRef;
        set { if (SetProperty(ref _editingApiKeySecretRef, value)) InvalidateDiscovery(); }
    }

    public bool EditingIsEnabled
    {
        get => _editingIsEnabled;
        set => SetProperty(ref _editingIsEnabled, value);
    }

    public DataClassification EditingMaxDataClass
    {
        get => _editingMaxDataClass;
        set => SetProperty(ref _editingMaxDataClass, value);
    }

    public string UrlValidationStatus
    {
        get => _urlValidationStatus;
        private set => SetProperty(ref _urlValidationStatus, value);
    }

    public bool IsUrlValid
    {
        get => _isUrlValid;
        private set => SetProperty(ref _isUrlValid, value);
    }

    public UrlClassification UrlClassification
    {
        get => _urlClassification;
        private set => SetProperty(ref _urlClassification, value);
    }

    public ProviderConnectionStatus? ConnectionTestStatus
    {
        get => _connectionTestStatus;
        private set => SetProperty(ref _connectionTestStatus, value);
    }

    public string? ConnectionTestMessage
    {
        get => _connectionTestMessage;
        private set => SetProperty(ref _connectionTestMessage, value);
    }

    public long? ConnectionTestLatencyMs
    {
        get => _connectionTestLatencyMs;
        private set => SetProperty(ref _connectionTestLatencyMs, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) UpdateProviderCommands(); }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string? LastExportedDiagnosticJson
    {
        get => _lastExportedDiagnosticJson;
        set => SetProperty(ref _lastExportedDiagnosticJson, value);
    }

    public string NewRuleOperation
    {
        get => _newRuleOperation;
        set => SetProperty(ref _newRuleOperation, value);
    }

    public NormalizedApprovalKind NewRuleKind
    {
        get => _newRuleKind;
        set => SetProperty(ref _newRuleKind, value);
    }

    public string? NewRulePathScope
    {
        get => _newRulePathScope;
        set => SetProperty(ref _newRulePathScope, value);
    }

    public string NewRuleProjectId
    {
        get => _newRuleProjectId;
        set => SetProperty(ref _newRuleProjectId, value);
    }

    public BackendType NewRuleBackend
    {
        get => _newRuleBackend;
        set => SetProperty(ref _newRuleBackend, value);
    }

    public ICommand NewProviderCommand { get; }
    public ICommand AddHeaderCommand { get; }
    public ICommand RemoveHeaderCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand RefreshModelsCommand { get; }
    public ICommand PreviewConfigCommand { get; }
    public ICommand ConfirmAndSaveCommand { get; }
    public ICommand DeleteProviderCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand AddApprovalRuleCommand { get; }
    public ICommand RemoveApprovalRuleCommand { get; }

    public ProvidersAccountsViewModel(
        CliStatusViewModel cliStatus,
        IProviderProfileRepository? profileRepository = null,
        IApprovalRuleRepository? approvalRuleRepository = null,
        IOpenCodeConfigService? configService = null,
        IProviderConnectionTestService? connectionTestService = null,
        IModelRefreshService? modelRefreshService = null,
        IPluginInventoryService? pluginInventoryService = null,
        IProviderDiagnosticExportService? exportService = null,
        ISecretStore? secretStore = null,
        ISecretLifecycleService? secretLifecycle = null,
        IProviderModelCatalogWriter? modelCatalogWriter = null,
        IProjectRepository? projectRepository = null,
        LLMWorkGUI.Application.Accounts.IAccountConfigurationService? accountConfiguration = null,
        IGatewayCatalogImportService? gatewayCatalogImport = null,
        LLMWorkGUI.Application.Concurrency.IApplicationInstanceGuard? instanceGuard = null,
        LLMWorkGUI.Application.Accounts.IAdaptationAccountConfigurationService? adaptationAccountConfiguration = null)
        : base(
            ScreenId.ProvidersAccounts,
            "Провайдеры и аккаунты",
            "Ctrl+3",
            "Настройка провайдеров, обнаружение, состояние авторизации и плагины (ТЗ §7.2).")
    {
        ArgumentNullException.ThrowIfNull(cliStatus);

        CliStatus = cliStatus;
        _instanceGuard = instanceGuard;
        AccountManagement = new(accountConfiguration, instanceGuard, adaptationAccountConfiguration);
        GatewayImport = new(gatewayCatalogImport,
            () => !IsBusy && !AccountManagement.IsBusy && instanceGuard?.IsViewOnly != true,
            RefreshGatewayCatalogAsync);
        GatewayImport.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(GatewayCatalogImportViewModel.IsBusy)) return;
            OnPropertyChanged(nameof(IsGatewayImportIdle));
            UpdateProviderCommands();
        };
        _profileRepository = profileRepository;
        _projectRepository = projectRepository;
        _approvalRuleRepository = approvalRuleRepository;
        _configService = configService;
        _connectionTestService = connectionTestService;
        _modelRefreshService = modelRefreshService;
        _pluginInventoryService = pluginInventoryService;
        _exportService = exportService;
        _secretStore = secretStore;
        _secretLifecycle = secretLifecycle;
        _modelCatalogWriter = modelCatalogWriter;
        EditingHeaders.CollectionChanged += (_, _) =>
        {
            foreach (var header in _observedHeaders) header.PropertyChanged -= OnDiscoveryHeaderChanged;
            _observedHeaders.Clear();
            foreach (var header in EditingHeaders)
                if (_observedHeaders.Add(header)) header.PropertyChanged += OnDiscoveryHeaderChanged;
            if (!_normalizingHeaders) InvalidateDiscovery();
        };

        NewProviderCommand = new RelayCommand(ExecuteNewProvider, () => CanCreateProvider);
        AddHeaderCommand = new RelayCommand(ExecuteAddHeader);
        RemoveHeaderCommand = new RelayCommand(p => ExecuteRemoveHeader(p as CustomHeaderItemViewModel));
        TestConnectionCommand = new RelayCommand(async () => await ExecuteTestConnectionAsync(), () => CanConfigureProvider);
        RefreshModelsCommand = new RelayCommand(async () => await ExecuteRefreshModelsAsync(), () => CanConfigureProvider);
        PreviewConfigCommand = new RelayCommand(ExecutePreviewConfig, () => CanConfigureProvider);
        ConfirmAndSaveCommand = new RelayCommand(async () => await ExecuteConfirmAndSaveAsync(), () => CanConfigureProvider);
        DeleteProviderCommand = new RelayCommand(async () => await ExecuteDeleteProviderAsync(), () => CanDeleteProvider);
        ExportDiagnosticsCommand = new RelayCommand(async () => await ExecuteExportDiagnosticsAsync(), () => CanConfigureProvider);
        AddApprovalRuleCommand = new RelayCommand(async () => await ExecuteAddApprovalRuleAsync(), () => CanModifyApprovalRules);
        RemoveApprovalRuleCommand = new RelayCommand(async p => await ExecuteRemoveApprovalRuleAsync(p as ApprovalRuleItemViewModel), _ => CanModifyApprovalRules);

        UpdateUrlValidation();
    }

    private void UpdateProviderCommands()
    {
        OnPropertyChanged(nameof(IsNativeGatewayProvider));
        OnPropertyChanged(nameof(CanConfigureProvider));
        OnPropertyChanged(nameof(CanDeleteProvider));
        OnPropertyChanged(nameof(IsProviderIdReadOnly));
        OnPropertyChanged(nameof(CanSelectProvider));
        OnPropertyChanged(nameof(CanCreateProvider));
        OnPropertyChanged(nameof(CanModifyApprovalRules));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private async Task RefreshGatewayCatalogAsync()
    {
        var selectedId = SelectedProvider?.Id;
        await LoadProvidersAsync();
        // This is a trusted refresh after a committed import; user selection remains blocked.
        SetSelectedProvider(Providers.FirstOrDefault(p => p.Id == selectedId));
        await AccountManagement.RefreshAfterCatalogImportAsync();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await LoadProvidersAsync(cancellationToken);
            await LoadApprovalRulesAsync(cancellationToken);
            if (_projectRepository is not null)
            {
                RuleProjects.Clear();
                foreach (var project in await _projectRepository.ListAsync(cancellationToken)) RuleProjects.Add(project);
                if (!RuleProjects.Any(x => x.Id == NewRuleProjectId)) NewRuleProjectId = string.Empty;
            }
            LoadPlugins();
            await AccountManagement.RefreshAsync();
        }
        catch (Exception)
        {
            StatusMessage = "Не удалось загрузить провайдеров и проекты. Повторите открытие раздела.";
        }
        finally { IsBusy = false; }
    }

    public async Task LoadProvidersAsync(CancellationToken cancellationToken = default)
    {
        if (_profileRepository == null) return;

        Providers.Clear();
        var list = await _profileRepository.ListAsync(cancellationToken);
        foreach (var profile in list)
        {
            var secretRef = await _profileRepository.GetApiKeySecretReferenceAsync(profile.Id, cancellationToken);
            Providers.Add(new ProviderItemViewModel(profile, secretRef));
        }
    }

    public async Task LoadApprovalRulesAsync(CancellationToken cancellationToken = default)
    {
        if (_approvalRuleRepository == null) return;

        ApprovalRules.Clear();
        var rules = await _approvalRuleRepository.ListAsync(cancellationToken);
        foreach (var rule in rules)
        {
            ApprovalRules.Add(new ApprovalRuleItemViewModel(rule));
        }
    }

    private void LoadPlugins()
    {
        if (_pluginInventoryService == null) return;

        InstalledPlugins.Clear();
        var sampleConfig = "{}";
        var plugins = _pluginInventoryService.GetInstalledPlugins(sampleConfig);
        foreach (var p in plugins)
        {
            InstalledPlugins.Add(new PluginItemViewModel(p));
        }
    }

    private void LoadProviderIntoForm(ProviderItemViewModel item)
    {
        _editingSnapshotId = item.Id;
        _editingSnapshotRevision = item.Revision;
        EditingProviderId = item.Id;
        EditingDisplayName = item.DisplayName;
        EditingBaseUrl = item.BaseUrl;
        EditingApiKey = string.Empty; // Never populate plaintext API key
        EditingApiKeySecretRef = item.SecretReference;
        ApiKeySecretStateText = item.HasSecretRef
            ? "Ссылка на сохранённый ключ задана; доступность секрета не проверена."
            : "Ключ провайдера не задан.";
        EditingIsEnabled = item.IsEnabled;
        EditingMaxDataClass = item.MaxDataClass;
        EditingHeaders.Clear();
        foreach (var header in item.CustomHeaders)
            EditingHeaders.Add(new CustomHeaderItemViewModel(header.Name, header.Value ?? string.Empty,
                header.SecretReference is not null, header.SecretReference));
        DiscoveredModels.Clear();
        ConnectionTestStatus = null;
        ConnectionTestMessage = null;
        ConnectionTestLatencyMs = null;
    }

    private void ExecuteNewProvider()
    {
        if (!CanCreateProvider) return;
        ResetProviderForm();
    }

    // Used after a durable deletion too, while the UI is still busy and user commands are gated.
    private void ResetProviderForm()
    {
        _editingSnapshotId = null;
        _editingSnapshotRevision = null;
        _selectedProvider = null;
        OnPropertyChanged(nameof(SelectedProvider));

        EditingProviderId = "custom-" + Guid.NewGuid().ToString("N")[..8];
        EditingDisplayName = "New Provider";
        EditingBaseUrl = "http://127.0.0.1:11434/v1";
        EditingApiKey = string.Empty;
        EditingApiKeySecretRef = null;
        ApiKeySecretStateText = "Ключ провайдера не задан.";
        EditingIsEnabled = true;
        EditingMaxDataClass = DataClassification.PublicSource;
        EditingHeaders.Clear();
        DiscoveredModels.Clear();
        ConnectionTestStatus = null;
        ConnectionTestMessage = null;
        ConnectionTestLatencyMs = null;
        UpdateProviderCommands();
    }

    private void ExecuteAddHeader()
    {
        EditingHeaders.Add(new CustomHeaderItemViewModel("X-Custom-Header", "Value"));
    }

    private void ExecuteRemoveHeader(CustomHeaderItemViewModel? header)
    {
        if (header != null)
        {
            EditingHeaders.Remove(header);
        }
    }

    private void UpdateUrlValidation()
    {
        if (string.IsNullOrWhiteSpace(EditingBaseUrl))
        {
            UrlValidationStatus = "URL не указан";
            IsUrlValid = false;
            UrlClassification = UrlClassification.InvalidFormat;
            return;
        }

        var result = ProviderUrlValidator.Validate(EditingBaseUrl);
        IsUrlValid = result.IsValid;
        UrlClassification = result.Classification;

        UrlValidationStatus = result.Classification switch
        {
            UrlClassification.ValidLoopbackHttp => "Valid Local Loopback (HTTP)",
            UrlClassification.ValidRemoteHttps => "Valid Secure Remote (HTTPS)",
            UrlClassification.InsecureRemoteHttp => "Insecure Remote HTTP (Disallowed)",
            _ => result.ErrorMessage ?? "Invalid URL Format"
        };
    }

    public async Task ExecuteTestConnectionAsync()
    {
        if (!CanConfigureProvider) return;
        if (_connectionTestService == null) return;

        IsBusy = true;
        ConnectionTestStatus = null;
        ConnectionTestMessage = "Проверка подключения...";
        ConnectionTestLatencyMs = null;

        try
        {
            var discoveryVersion = _discoveryVersion;
            var headers = BuildCustomHeaders();
            var settings = new CustomProviderSettings(
                string.IsNullOrWhiteSpace(EditingProviderId) ? "test" : EditingProviderId,
                string.IsNullOrWhiteSpace(EditingDisplayName) ? "Test" : EditingDisplayName,
                EditingBaseUrl,
                apiKeySecretRef: EditingApiKeySecretRef,
                customHeaders: headers,
                validateUrl: false);

            var result = await _connectionTestService.TestConnectionAsync(
                settings,
                explicitApiKey: string.IsNullOrWhiteSpace(EditingApiKey) ? null : EditingApiKey);

            if (discoveryVersion != _discoveryVersion) return;

            ConnectionTestStatus = result.Status;
            ConnectionTestLatencyMs = result.LatencyMs;
            ConnectionTestMessage = result.IsSuccessful
                ? $"Подключение успешно установлено за {result.LatencyMs} мс. Найдено моделей: {result.DiscoveredModels.Count}."
                : result.ErrorMessage ?? $"Сбой подключения со статусом {result.Status}.";

            DiscoveredModels.Clear();
            foreach (var m in result.DiscoveredModels)
            {
                DiscoveredModels.Add(new ModelItemViewModel(ModelCapabilityDetector.DetectCapabilities(m.Id, m.Name, m.OwnedBy)));
            }
            _modelsConfirmed = result.IsSuccessful;
        }
        catch (Exception exception)
        {
            ConnectionTestStatus = ProviderConnectionStatus.UnknownError;
            ConnectionTestMessage = "Проверка подключения завершилась ошибкой (" + exception.GetType().Name + ").";
            StatusMessage = ConnectionTestMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExecuteRefreshModelsAsync()
    {
        if (!CanConfigureProvider) return;
        if (_modelRefreshService == null) return;

        IsBusy = true;
        StatusMessage = "Обновление моделей...";

        try
        {
            var discoveryVersion = _discoveryVersion;
            var headers = BuildCustomHeaders();
            var settings = new CustomProviderSettings(
                string.IsNullOrWhiteSpace(EditingProviderId) ? "refresh" : EditingProviderId,
                string.IsNullOrWhiteSpace(EditingDisplayName) ? "Refresh" : EditingDisplayName,
                EditingBaseUrl,
                apiKeySecretRef: EditingApiKeySecretRef,
                customHeaders: headers,
                validateUrl: false);

            var models = await _modelRefreshService.RefreshModelsAsync(
                settings,
                explicitApiKey: string.IsNullOrWhiteSpace(EditingApiKey) ? null : EditingApiKey);

            if (discoveryVersion != _discoveryVersion) return;

            DiscoveredModels.Clear();
            foreach (var m in models)
            {
                DiscoveredModels.Add(new ModelItemViewModel(m));
            }
            _modelsConfirmed = true;

            StatusMessage = $"Обновлено моделей: {models.Count}.";
        }
        catch (ProviderCredentialUnavailableException)
        {
            StatusMessage = "Обновление моделей не выполнено: секрет недоступен или не принадлежит этому провайдеру. Проверьте ключ и секретные заголовки.";
        }
        catch (ProviderModelDiscoveryUnavailableException)
        {
            StatusMessage = "Не удалось подтвердить список моделей провайдера. Сохранённый каталог не заменён. Повторите обновление после проверки подключения.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Сбой обновления: {UiErrorMessage.Describe(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ExecutePreviewConfig()
    {
        if (!CanConfigureProvider) return;
        if (_configService == null) return;

        try
        {
            var headers = BuildCustomHeaders();
            var models = DiscoveredModels.Select(m => new CustomProviderModelSettings(m.Id, m.Name)).ToArray();
            var settings = new CustomProviderSettings(
                string.IsNullOrWhiteSpace(EditingProviderId) ? "preview" : EditingProviderId,
                string.IsNullOrWhiteSpace(EditingDisplayName) ? "Preview" : EditingDisplayName,
                EditingBaseUrl,
                apiKeySecretRef: EditingApiKeySecretRef,
                customHeaders: headers,
                models: models,
                isEnabled: EditingIsEnabled,
                validateUrl: false);

            var preview = _configService.GeneratePreview(
                settings,
                existingConfigContent: null,
                resolvedApiKey: string.IsNullOrWhiteSpace(EditingApiKey) ? null : EditingApiKey);

            PreviewDialog.Show(
                preview.DiffText,
                preview.RedactedJson,
                preview.HasExistingConfig,
                preview.HasChanges,
                onConfirmed: () => _ = ExecuteConfirmAndSaveAsync());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            PreviewDialog.Close();
            StatusMessage = "Невозможно показать конфигурацию: проверьте имена и значения заголовков.";
        }
    }

    public async Task ExecuteConfirmAndSaveAsync()
    {
        if (Interlocked.CompareExchange(ref _saveInProgress, 1, 0) != 0) return;
        try { await ExecuteConfirmAndSaveCoreAsync(); }
        finally { Volatile.Write(ref _saveInProgress, 0); }
    }

    private async Task ExecuteConfirmAndSaveCoreAsync()
    {
        if (!CanConfigureProvider) return;
        if (_profileRepository == null) return;

        if (_editingSnapshotId is not null && !string.Equals(_editingSnapshotId, EditingProviderId, StringComparison.Ordinal))
        {
            StatusMessage = "ID существующего провайдера изменить нельзя. Для нового профиля нажмите «Новый провайдер».";
            return;
        }
        if (string.IsNullOrWhiteSpace(EditingProviderId) || string.IsNullOrWhiteSpace(EditingDisplayName))
        {
            StatusMessage = "ID провайдера и отображаемое имя не могут быть пустыми.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Сохранение конфигурации провайдера...";
        string? unboundPayload = null;
        var profileCommitted = false;

        try
        {
            var confirmedModels = DiscoveredModels.Select(model => new ProviderModelDescriptor(model.Id, model.Name)).ToArray();
            var modelsConfirmed = _modelsConfirmed || confirmedModels.Length > 0;
            var discoveredProviderId = EditingProviderId;
            var discoveredBaseUrl = EditingBaseUrl;
            var existing = await _profileRepository.GetByIdAsync(EditingProviderId);
            if (existing?.Backend == BackendType.NativeGateway)
            {
                StatusMessage = "Профиль LLMGateway управляется через каталог нативных клиентов.";
                return;
            }
            // A native ACP executable has no HTTP endpoint. Require a URL for HTTP profiles and
            // still validate one when supplied; never invent a URL just to save a native profile.
            var nativeWithoutUrl = existing?.Backend == BackendType.CursorAcp && string.IsNullOrWhiteSpace(EditingBaseUrl);
            UpdateUrlValidation();
            if (!nativeWithoutUrl && !IsUrlValid)
            {
                StatusMessage = $"Невозможно сохранить: {UrlValidationStatus}";
                return;
            }
            string? secretRef = existing is null ? EditingApiKeySecretRef
                : await _profileRepository.GetApiKeySecretReferenceAsync(existing.Id);
            var profilePersisted = false;
            var refreshApiKeyStatus = false;
            long? committedRevision = null;

            var profile = new ProviderProfile(
                EditingProviderId,
                EditingDisplayName,
                existing?.Backend ?? BackendType.OpenCode,
                nativeWithoutUrl ? null : EditingBaseUrl,
                executablePath: existing?.ExecutablePath,
                maxDataClass: EditingMaxDataClass,
                isEnabled: EditingIsEnabled,
                gatewayNativeId: existing?.GatewayNativeId,
                revision: _editingSnapshotId == EditingProviderId ? _editingSnapshotRevision ?? existing?.Revision ?? -1 : -1);

            // A replacement key gets a fresh URN. The lifecycle retains the previous payload until
            // the profile commit succeeds, then retires it only when no other binding still needs it.
            var headers = BuildCustomHeaders();
            if (headers.Count > 0 || existing?.CustomHeaders?.Count > 0)
            {
                if (_secretLifecycle is null)
                    throw new InvalidOperationException("The secret lifecycle is required to persist provider headers.");
                var saved = await _secretLifecycle.SaveProviderConfigurationAsync(profile, headers, EditingApiKey);
                profile = saved.Profile;
                committedRevision = profile.Revision;
                profileCommitted = true;
                profilePersisted = true;
                secretRef = saved.ApiKeySecretReference;
                ApiKeySecretStateText = string.IsNullOrWhiteSpace(secretRef)
                    ? "Ключ провайдера не задан."
                    : string.IsNullOrWhiteSpace(EditingApiKey)
                        ? "Ссылка на прежний ключ сохранена; доступность ключа не проверена."
                        : "Ключ сохранён в хранилище секретов.";
                _normalizingHeaders = true;
                try
                {
                    EditingHeaders.Clear();
                    foreach (var header in profile.CustomHeaders ?? [])
                        EditingHeaders.Add(new CustomHeaderItemViewModel(header.Name, header.Value ?? string.Empty,
                            header.SecretReference is not null, header.SecretReference));
                }
                finally { _normalizingHeaders = false; }
            }
            else if (!string.IsNullOrWhiteSpace(EditingApiKey))
            {
                if (_secretLifecycle is not null)
                {
                    var saved = await _secretLifecycle.SaveProviderConfigurationAsync(profile, headers, EditingApiKey);
                    profile = saved.Profile;
                    committedRevision = profile.Revision;
                    profileCommitted = true;
                    profilePersisted = true;
                    secretRef = saved.ApiKeySecretReference;
                    refreshApiKeyStatus = true;
                }
                else if (_configService is not null && _secretStore is not null)
                {
                    secretRef = await _configService.CreateUnboundApiKeyReferenceAsync(
                        EditingApiKey,
                        _secretStore,
                        _secretLifecycle);
                    unboundPayload = secretRef;
                }
                else
                {
                    StatusMessage = "Хранилище секретов недоступно. Ключ и профиль не сохранены.";
                    return;
                }

            }

            if (!profilePersisted)
                committedRevision = await _profileRepository.UpsertReturningRevisionAsync(profile, secretRef);
            profileCommitted = true;
            unboundPayload = null;
            // Keep the version returned by our commit, even if a concurrent editor writes before
            // the list reload. Reading the latest version here would authorize stale form data.
            _editingSnapshotId = profile.Id;
            _editingSnapshotRevision = committedRevision;
            // Normalizing the successfully saved credential does not change the configuration
            // that produced the current discovery. User edits still invalidate it in the setters.
            SetProperty(ref _editingApiKeySecretRef, secretRef, nameof(EditingApiKeySecretRef));
            SetProperty(ref _editingApiKey, string.Empty, nameof(EditingApiKey));
            if (refreshApiKeyStatus && secretRef is not null)
                ApiKeySecretStateText = DescribeSecretState(await _secretLifecycle!.GetStatusAsync(secretRef));

            // The confirmed model list is recorded locally: it is the only place a real backend model id
            // for this profile comes from, because an OpenCode account record carries no model id. The
            // writer drops anything that is not a model id, so nothing else can become a route.
            if (_modelCatalogWriter is not null)
            {
                var query = new ProviderModelCatalogQuery(profile.Id, profile.Backend);
                if (modelsConfirmed && string.Equals(profile.Id, discoveredProviderId, StringComparison.Ordinal)
                    && string.Equals(profile.BaseUrl ?? string.Empty, discoveredBaseUrl, StringComparison.Ordinal))
                {
                    await _modelCatalogWriter.SaveModelsAsync(query, confirmedModels);
                }
            }

            await LoadProvidersAsync();

            // Rebind selection without reloading the form or clearing its confirmed model list.
            _selectedProvider = Providers.FirstOrDefault(item => item.Id == profile.Id && item.Revision == committedRevision);
            OnPropertyChanged(nameof(SelectedProvider));
            UpdateProviderCommands();

            StatusMessage = string.IsNullOrWhiteSpace(EditingApiKeySecretRef)
                ? "Профиль провайдера успешно сохранён."
                : $"Профиль провайдера успешно сохранён. {ApiKeySecretStateText}";
        }
        catch (Exception)
        {
            if (unboundPayload is not null && _secretStore is not null)
            {
                try { await _secretStore.DeleteSecretAsync(unboundPayload, CancellationToken.None); }
                catch (Exception) { /* Preserve the failed save and the entered key for correction. */ }
            }
            StatusMessage = profileCommitted
                ? "Профиль и секреты сохранены. Не удалось обновить список провайдеров или моделей; повторно откройте раздел."
                : "Сбой сохранения конфигурации провайдера. Повторите попытку.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InvalidateDiscovery()
    {
        _discoveryVersion++;
        _modelsConfirmed = false;
        DiscoveredModels.Clear();
    }

    private void OnDiscoveryHeaderChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (!_normalizingHeaders) InvalidateDiscovery();
    }

    /// <summary>
    /// Redacted, non-technical description of the saved credential state. Only the state reaches the
    /// UI: neither the URN's value nor the secret itself is ever shown or logged.
    /// </summary>
    private static string DescribeSecretState(SecretReferenceStatus status) =>
        status.State switch
        {
            SecretReferenceState.Active => "Секрет активен в хранилище приложения.",
            SecretReferenceState.Missing =>
                "Секрет отсутствует (Missing): сохранённое значение больше не читается, введите ключ заново.",
            SecretReferenceState.Revoked =>
                "Секрет отозван (Revoked): введите ключ заново, чтобы создать новую ссылку.",
            _ => "Состояние секрета неизвестно."
        };

    public async Task ExecuteDeleteProviderAsync()
    {
        if (!CanDeleteProvider) return;
        if (_profileRepository == null || SelectedProvider == null) return;

        IsBusy = true;
        var committed = false;
        try
        {
            var providerId = SelectedProvider.Id;
            var lifecycle = _secretLifecycle ?? throw new InvalidOperationException("Coordinated provider deletion is unavailable.");
            var revision = SelectedProvider.Revision
                ?? (await _profileRepository.GetByIdAsync(providerId))?.Revision
                ?? throw new InvalidOperationException("Reload the provider before deletion.");
            var result = await lifecycle.DeleteProviderConfigurationAsync(providerId, revision);
            committed = true;
            // Clear stale identity/secrets before the fallible list refresh, so even a refresh
            // failure cannot leave Save targeting the deleted profile.
            ResetProviderForm();
            await LoadProvidersAsync();
            StatusMessage = $"Провайдер «{providerId}» удалён."
                + (result.PendingSecretCleanup != 0 ? " Очистка секретов ожидает повторной попытки при запуске приложения." : "");
        }
        catch (Exception exception)
        {
            StatusMessage = committed ? "Провайдер удалён. Не удалось обновить список; откройте экран заново."
                : "Удаление провайдера не выполнено (" + exception.GetType().Name + ").";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExecuteExportDiagnosticsAsync()
    {
        if (!CanConfigureProvider) return;
        if (_exportService == null) return;

        IsBusy = true;
        try
        {
            var headers = BuildCustomHeaders();
            var settings = new CustomProviderSettings(
                EditingProviderId,
                EditingDisplayName,
                EditingBaseUrl,
                apiKeySecretRef: EditingApiKeySecretRef,
                customHeaders: headers,
                validateUrl: false);

            var report = await _exportService.GenerateExportAsync(settings);
            LastExportedDiagnosticJson = report.ToJson();
            StatusMessage = "Диагностический экспорт успешно сформирован (секреты скрыты).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Сбой экспорта: {UiErrorMessage.Describe(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExecuteAddApprovalRuleAsync()
    {
        if (!CanModifyApprovalRules) return;
        IsBusy = true;
        try
        {
            _instanceGuard?.EnsureSupervisorPermitted();
            await AddApprovalRuleCoreAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = "Не удалось добавить правило подтверждения (" + exception.GetType().Name + ").";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddApprovalRuleCoreAsync()
    {
        if (_approvalRuleRepository == null) return;

        if (_projectRepository is not null &&
            (string.IsNullOrWhiteSpace(NewRuleProjectId) || await _projectRepository.GetByIdAsync(NewRuleProjectId) is null))
        {
            StatusMessage = "Выберите существующий проект для правила подтверждения.";
            return;
        }

        // Invariant ТЗ §6.7: UnknownHighRisk never receives a persistent rule!
        if (NewRuleKind == NormalizedApprovalKind.UnknownHighRisk)
        {
            StatusMessage = "Ошибка: операции UnknownHighRisk не могут иметь постоянных правил подтверждения.";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewRuleOperation))
        {
            StatusMessage = "Имя операции не может быть пустым.";
            return;
        }

        var providerId = EditingProviderId;
        if (string.IsNullOrWhiteSpace(providerId) || _profileRepository is null
            || await _profileRepository.GetByIdAsync(providerId) is null)
        {
            StatusMessage = "Выберите существующего провайдера для правила подтверждения.";
            return;
        }

        var rule = new ApprovalRule(
            "rule-" + Guid.NewGuid().ToString("N")[..8],
            NewRuleBackend,
            providerId,
            NewRuleProjectId,
            NewRulePathScope,
            NewRuleKind,
            NewRuleOperation,
            expiresAt: null,
            createdBy: "user",
            createdAt: DateTimeOffset.UtcNow);

        var committed = false;
        try
        {
            _instanceGuard?.EnsureSupervisorPermitted();
            await _approvalRuleRepository.UpsertAsync(rule);
            committed = true;
            // Retire this successfully committed draft, without erasing a different draft edited
            // during the await. A reload-only retry must not create a second rule identity.
            if (NewRuleOperation == rule.Operation && NewRuleProjectId == rule.ProjectId
                && NewRuleBackend == rule.Backend && NewRuleKind == rule.Kind && NewRulePathScope == rule.PathScope)
            {
                NewRuleOperation = string.Empty;
                NewRulePathScope = null;
            }
            await LoadApprovalRulesAsync();
            StatusMessage = $"Правило подтверждения для '{rule.Operation}' добавлено.";
        }
        catch (Exception)
        {
            StatusMessage = committed
                ? "Правило подтверждения добавлено. Не удалось обновить список; повторно откройте раздел."
                : "Не удалось сохранить правило. Проверьте проект и провайдера и повторите попытку.";
        }
    }

    public async Task ExecuteRemoveApprovalRuleAsync(ApprovalRuleItemViewModel? rule)
    {
        if (!CanModifyApprovalRules || _approvalRuleRepository == null) return;

        var target = rule ?? SelectedApprovalRule;
        if (target == null) return;

        IsBusy = true;
        var settled = false;
        var deleted = false;
        try
        {
            _instanceGuard?.EnsureSupervisorPermitted();
            deleted = await _approvalRuleRepository.DeleteAsync(target.Id);
            settled = true;
            if (SelectedApprovalRule?.Id == target.Id) SelectedApprovalRule = null;
            await LoadApprovalRulesAsync();
            StatusMessage = deleted ? $"Правило подтверждения '{target.Id}' удалено."
                : "Правило подтверждения уже отсутствует.";
        }
        catch (Exception exception)
        {
            StatusMessage = settled
                ? (deleted ? "Правило подтверждения удалено." : "Правило подтверждения уже отсутствует.")
                    + " Не удалось обновить список; повторно откройте раздел."
                : "Не удалось удалить правило подтверждения (" + exception.GetType().Name + ").";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private List<CustomProviderHeader> BuildCustomHeaders()
    {
        return EditingHeaders
            .Select(h => new CustomProviderHeader(h.Name, h.Value, h.IsSensitive,
                string.IsNullOrEmpty(h.Value) ? h.SecretReference : null))
            .ToList();
    }
}
