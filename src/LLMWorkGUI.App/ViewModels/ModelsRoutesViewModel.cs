using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed record ConfigurationChoice<T>(T Value, string Label);
public sealed record ModelConfigurationRow(ConfiguredModel Value, string Display);
public sealed record RouteConfigurationRow(ConfiguredRoute Value, string Display);

public sealed class ModelsRoutesViewModel : ScreenViewModel
{
    private readonly IModelRouteConfigurationService? _service;
    private ModelRouteConfiguration _snapshot = new([], [], [], []);
    private ModelProfileOption? _profile;
    private ModelConfigurationRow? _selectedModel;
    private RouteConfigurationRow? _selectedRoute;
    private ModelAccountOption? _account;
    private ModelConfigurationRow? _routeModel;
    private string _nativeId = "", _modelName = "", _priority = "0", _search = "", _status = "";
    private CapabilityState _capability = CapabilityState.Unknown;
    private DataClassification _classification = DataClassification.PublicSource;
    private string? _mode;
    private bool _modelEnabled = true, _routeEnabled = true, _busy;
    private int _selectedTabIndex;

    public ModelsRoutesViewModel(IModelRouteConfigurationService? service = null,
        IModelCapabilityEvidenceStore? capabilityStore = null, TimeProvider? clock = null,
        IModelCapabilityDiscoveryService? capabilityDiscovery = null,
        INativeGatewayRouteActivationService? nativeRouteActivation = null)
        : base(ScreenId.Models, "Модели", "Ctrl+4", "Сохранённые модели и маршруты: профиль, аккаунт и режим отправки.")
    {
        _service = service;
        CapabilitiesEditor = new(service, capabilityStore, clock ?? TimeProvider.System, capabilityDiscovery);
        NativeRouteSetup = new(nativeRouteActivation, RefreshAsync, () => !IsBusy && !CapabilitiesEditor.IsBusy);
        NativeRouteSetup.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NativeRouteSetup.IsBusy)) Changed();
        };
        CapabilitiesEditor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CapabilitiesEditor.IsBusy)) Changed();
            if (args.PropertyName == nameof(CapabilitiesEditor.StatusMessage) && CapabilitiesEditor.StatusMessage.Length > 0) StatusMessage = "";
        };
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy && !NativeRouteSetup.IsBusy);
        NewModelCommand = new RelayCommand(NewModel, () => CanEdit);
        NewRouteCommand = new RelayCommand(NewRoute, () => CanEdit);
        SaveModelCommand = new RelayCommand(() => _ = SaveModelAsync(), () => CanEdit && Profile is not null
            && !string.IsNullOrWhiteSpace(NativeModelId) && !string.IsNullOrWhiteSpace(ModelName));
        SaveRouteCommand = new RelayCommand(() => _ = SaveRouteAsync(), () => CanEdit && Profile is not null
            && Account is not null && RouteModel is not null);
    }

    public ObservableCollection<ModelProfileOption> Profiles { get; } = new();
    public ModelCapabilitiesEditorViewModel CapabilitiesEditor { get; }
    public NativeRouteSetupViewModel NativeRouteSetup { get; }
    public ObservableCollection<ModelAccountOption> Accounts { get; } = new();
    public ObservableCollection<ModelConfigurationRow> Models { get; } = new();
    public ObservableCollection<ModelConfigurationRow> RouteModels { get; } = new();
    public ObservableCollection<RouteConfigurationRow> Routes { get; } = new();
    public IReadOnlyList<ConfigurationChoice<CapabilityState>> CapabilityChoices { get; } =
    [new(CapabilityState.Unknown, "Не проверена"), new(CapabilityState.Supported, "Поддержка подтверждена пользователем"), new(CapabilityState.Unsupported, "Не поддерживается")];
    public IReadOnlyList<ConfigurationChoice<DataClassification>> ClassificationChoices { get; } =
    [new(DataClassification.PublicSource, "Открытые данные"), new(DataClassification.PrivateSource, "Приватные данные"), new(DataClassification.Restricted, "Ограниченные данные")];
    private static readonly IReadOnlyList<ConfigurationChoice<string?>> CursorModes =
        [new(null, "Любой доступный режим"), new("ask", "Вопрос (ask)"), new("plan", "План (plan)"), new("agent", "Агент (agent)")];
    private static readonly IReadOnlyList<ConfigurationChoice<string?>> DefaultModes = [new(null, "По умолчанию")];
    public IReadOnlyList<ConfigurationChoice<string?>> ModeChoices => Profile?.Backend == BackendType.CursorAcp ? CursorModes : DefaultModes;
    public ICommand RefreshCommand { get; }
    public ICommand NewModelCommand { get; }
    public ICommand NewRouteCommand { get; }
    public ICommand SaveModelCommand { get; }
    public ICommand SaveRouteCommand { get; }
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); Changed(); } }
    public bool CanEdit => !IsBusy && !CapabilitiesEditor.IsBusy && !NativeRouteSetup.IsBusy && _service is not null;
    public bool CanChooseProfile => CanEdit && (SelectedTabIndex == 2 || SelectedModel is null && SelectedRoute is null);
    public int SelectedTabIndex { get => _selectedTabIndex; set { if (!IsBusy && !CapabilitiesEditor.IsBusy && SetProperty(ref _selectedTabIndex, value)) Changed(); } }
    public bool CanEditModelIdentity => CanEdit && SelectedModel is null;
    public bool CanEditRouteBinding => CanEdit && SelectedRoute is null;
    public bool IsEmpty => Models.Count == 0 && Routes.Count == 0;
    public string StatusMessage { get => _status; private set => SetProperty(ref _status, value); }
    public string ProfileNote => Profile is null ? "Выберите сохранённый профиль. Настройка профилей доступна в разделе «Провайдеры и аккаунты»."
        : $"Бэкенд: {Profile.Backend}. " + (Accounts.Count == 0 ? "В профиле нет аккаунтов: маршрут пока создать нельзя." : "Авторизация и доступность проверяются перед отправкой.");
    public string ModelOrigin => SelectedModel?.Value.Provenance switch
    { ModelProvenance.ProviderReported => "Источник: провайдер", ModelProvenance.PluginReported => "Источник: плагин", _ => "Источник: ручная настройка. Она не подтверждает фактическую модель ответа." };
    public string AccountNote => Account is null ? "Выберите аккаунт этого профиля." : Account.AuthState == AuthState.Valid
        ? "В хранилище аккаунт отмечен как авторизованный; доступность будет проверена при отправке."
        : "Авторизация аккаунта не подтверждена. Сохранение маршрута не разрешает отправку.";

    public ModelProfileOption? Profile
    {
        get => _profile;
        set
        {
            if (!CanChooseProfile || value is not null && !Profiles.Contains(value) || !SetProperty(ref _profile, value)) return;
            ResetDrafts(); ApplyLists(); Changed();
            CapabilitiesEditor.SetConfiguration(_snapshot, _profile?.Id);
        }
    }
    public ModelConfigurationRow? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (IsBusy || NativeRouteSetup.IsBusy || value is not null && !Models.Contains(value)) return;
            LoadModel(value); Changed();
        }
    }
    public RouteConfigurationRow? SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            if (IsBusy || NativeRouteSetup.IsBusy || value is not null && !Routes.Contains(value)) return;
            LoadRoute(value); Changed();
        }
    }
    public ModelAccountOption? Account { get => _account; set { if (CanEditRouteBinding && (value is null || Accounts.Contains(value))) { SetProperty(ref _account, value); Changed(); } } }
    public ModelConfigurationRow? RouteModel { get => _routeModel; set { if (CanEditRouteBinding && (value is null || RouteModels.Contains(value))) { SetProperty(ref _routeModel, value); Changed(); } } }
    public string NativeModelId { get => _nativeId; set { SetProperty(ref _nativeId, value); Changed(); } }
    public string ModelName { get => _modelName; set { SetProperty(ref _modelName, value); Changed(); } }
    public CapabilityState Capability { get => _capability; set => SetProperty(ref _capability, value); }
    public bool ModelEnabled { get => _modelEnabled; set => SetProperty(ref _modelEnabled, value); }
    public string? RouteMode { get => _mode; set => SetProperty(ref _mode, value); }
    public DataClassification MaxDataClass { get => _classification; set => SetProperty(ref _classification, value); }
    public bool RouteEnabled { get => _routeEnabled; set => SetProperty(ref _routeEnabled, value); }
    public string Priority { get => _priority; set => SetProperty(ref _priority, value); }
    public string SearchText { get => _search; set { if (SetProperty(ref _search, value)) ApplyLists(); } }

    public async Task RefreshAsync()
    {
        if (IsBusy || CapabilitiesEditor.IsBusy || NativeRouteSetup.IsBusy) return;
        await RunAsync(async () => { await ReloadAsync(); StatusMessage = "Сохранённая конфигурация обновлена."; });
    }
    public async Task SaveModelAsync()
    {
        if (!SaveModelCommand.CanExecute(null)) return;
        var request = new SaveModelConfiguration(Profile!.Id, NativeModelId, ModelName, Capability, ModelEnabled, SelectedModel?.Value);
        await RunAsync(async () =>
        {
            var id = await _service!.SaveModelAsync(request);
            await ReloadAsync(); LoadModel(Models.FirstOrDefault(m => m.Value.Id == id));
            StatusMessage = "Модель сохранена. Для отправки настройте маршрут на вкладке «Маршруты».";
        });
    }
    public async Task SaveRouteAsync()
    {
        if (!SaveRouteCommand.CanExecute(null)) return;
        if (!int.TryParse(Priority, NumberStyles.None, CultureInfo.InvariantCulture, out var priority))
        { StatusMessage = "Приоритет должен быть целым неотрицательным числом."; return; }
        var request = new SaveRouteConfiguration(Profile!.Id, Account!.Id, RouteModel!.Value.Id, RouteMode,
            MaxDataClass, RouteEnabled, priority, SelectedRoute?.Value);
        await RunAsync(async () =>
        {
            var id = await _service!.SaveRouteAsync(request);
            await ReloadAsync(); LoadRoute(Routes.FirstOrDefault(r => r.Value.Id == id));
            StatusMessage = "Маршрут сохранён. В рабочей области обновите список маршрутов и выберите его.";
        });
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_service is null) { StatusMessage = "Хранилище настройки моделей недоступно."; return; }
        IsBusy = true;
        try { await action(); }
        catch (InvalidOperationException error) { StatusMessage = error.Message; }
        catch (Exception) { StatusMessage = "Не удалось прочитать или сохранить конфигурацию. Обновите список и повторите действие."; }
        finally { IsBusy = false; }
    }
    private async Task ReloadAsync()
    {
        var profileId = _profile?.Id; var modelId = _selectedModel?.Value.Id; var routeId = _selectedRoute?.Value.Id;
        _snapshot = await _service!.ReadAsync();
        Profiles.Clear(); foreach (var profile in _snapshot.Profiles) Profiles.Add(profile);
        _profile = Profiles.FirstOrDefault(p => p.Id == profileId);
        ResetDrafts(); ApplyLists();
        LoadModel(Models.FirstOrDefault(m => m.Value.Id == modelId));
        LoadRoute(Routes.FirstOrDefault(r => r.Value.Id == routeId)); Changed();
        CapabilitiesEditor.SetConfiguration(_snapshot, _profile?.Id);
    }
    private void ApplyLists()
    {
        var query = SearchText.Trim();
        bool Matches(string value) => value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        Models.Clear(); Routes.Clear(); Accounts.Clear(); RouteModels.Clear();
        foreach (var account in _snapshot.Accounts.Where(a => a.ProfileId == _profile?.Id)) Accounts.Add(account);
        foreach (var model in _snapshot.Models.Where(m => m.ProfileId == _profile?.Id))
        {
            var row = new ModelConfigurationRow(model, $"{model.Name} · {model.NativeModelId}" + (model.IsEnabled ? "" : " · отключена"));
            RouteModels.Add(row); if (Matches(row.Display)) Models.Add(row);
        }
        foreach (var route in _snapshot.Routes.Where(r => r.ProfileId == _profile?.Id))
        {
            var model = _snapshot.Models.FirstOrDefault(m => m.Id == route.ModelId);
            var account = _snapshot.Accounts.FirstOrDefault(a => a.Id == route.AccountId);
            var display = $"{model?.Name ?? route.ModelId} · {account?.Name ?? route.AccountId} · {route.Mode ?? "по умолчанию"}" + (route.IsEnabled ? "" : " · отключён");
            if (Matches(display)) Routes.Add(new(route, display));
        }
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(ProfileNote));
    }
    private void LoadModel(ModelConfigurationRow? row)
    {
        _selectedModel = row;
        NativeModelId = row?.Value.NativeModelId ?? ""; ModelName = row?.Value.Name ?? "";
        Capability = row?.Value.Capability ?? CapabilityState.Unknown; ModelEnabled = row?.Value.IsEnabled ?? true;
        OnPropertyChanged(nameof(SelectedModel)); OnPropertyChanged(nameof(ModelOrigin));
    }
    private void LoadRoute(RouteConfigurationRow? row)
    {
        NativeRouteSetup.SetRoute(row?.Value, _profile);
        _selectedRoute = row; _account = Accounts.FirstOrDefault(a => a.Id == row?.Value.AccountId);
        _routeModel = RouteModels.FirstOrDefault(m => m.Value.Id == row?.Value.ModelId);
        RouteMode = row?.Value.Mode; MaxDataClass = row?.Value.MaxDataClass ?? DataClassification.PublicSource;
        Priority = (row?.Value.Priority ?? 0).ToString(CultureInfo.InvariantCulture); RouteEnabled = row?.Value.IsEnabled ?? true;
        OnPropertyChanged(nameof(SelectedRoute)); OnPropertyChanged(nameof(Account)); OnPropertyChanged(nameof(RouteModel)); OnPropertyChanged(nameof(AccountNote));
    }
    private void ResetDrafts() { LoadModel(null); LoadRoute(null); }
    private void NewModel() { if (CanEdit) { ResetDrafts(); StatusMessage = "Введите ID и название новой модели."; Changed(); } }
    private void NewRoute() { if (CanEdit) { ResetDrafts(); StatusMessage = "Выберите аккаунт и сохранённую модель."; Changed(); } }
    private void Changed()
    {
        foreach (var name in new[] { nameof(CanEdit), nameof(CanChooseProfile), nameof(CanEditModelIdentity), nameof(CanEditRouteBinding), nameof(Profile), nameof(ProfileNote), nameof(AccountNote), nameof(ModeChoices) }) OnPropertyChanged(name);
        RelayCommand.RaiseCanExecuteChanged();
    }
}
