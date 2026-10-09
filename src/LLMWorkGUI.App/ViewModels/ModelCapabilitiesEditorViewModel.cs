using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed class ModelCapabilitiesEditorViewModel(
    IModelRouteConfigurationService? configuration,
    IModelCapabilityEvidenceStore? store,
    TimeProvider clock, IModelCapabilityDiscoveryService? discovery = null) : ObservableObject
{
    private ModelRouteConfiguration _snapshot = new([], [], [], []);
    private string? _profileId;
    private ConfiguredModel? _model;
    private ModelAccountOption? _account;
    private ModelCapabilityEvidence? _expected;
    private bool _busy, _confirmed, _chat, _vision, _tools;
    private string _reasoning = "", _speed = "", _modes = "", _context = "", _hours = "24", _status = "";
    private ICommand? _saveCommand;
    private ICommand? _discoverCommand;

    public ObservableCollection<ConfiguredModel> Models { get; } = new();
    public ObservableCollection<ModelAccountOption> Accounts { get; } = new();
    public ICommand SaveCommand => _saveCommand ??= new RelayCommand(() => _ = SaveAsync(), () => CanSave);
    public ICommand DiscoverCommand => _discoverCommand ??= new RelayCommand(() => _ = DiscoverAsync(), () => CanDiscover);
    public bool CanDiscover => !IsBusy && discovery is not null && configuration is not null
        && Model?.Backend == BackendType.NativeGateway && Account is not null;
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); Changed(); } }
    public bool CanSelect => !IsBusy;
    public bool CanSave => !IsBusy && configuration is not null && store is not null && Model is not null
        && Account is not null && Model.Backend != BackendType.CursorAcp;
    public ConfiguredModel? Model
    {
        get => _model;
        set { if (!IsBusy && (value is null || Models.Contains(value)) && SetProperty(ref _model, value)) Load(); }
    }
    public ModelAccountOption? Account
    {
        get => _account;
        set { if (!IsBusy && (value is null || Accounts.Contains(value)) && SetProperty(ref _account, value)) Load(); }
    }
    public string ScopeNote => Model?.Backend == BackendType.CursorAcp
        ? "Возможности Cursor принимаются из discovery. Ручное изменение недоступно."
        : Model?.Backend == BackendType.NativeGateway
        ? "Получите доступные параметры из авторизованного нативного клиента или укажите известные вам возможности вручную."
        : "Объявите известные вам возможности модели для этого аккаунта. Источник сохранения — ручная настройка.";
    public string SavedEvidence => _expected is null ? "Подтверждение для этой модели и аккаунта отсутствует."
        : $"Источник: {_expected.DiscoverySource ?? SourceName(_expected.Provenance)}. Состояние: {StateName(_expected)}. "
            + $"Записано {_expected.ObservedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}; действует до {_expected.ExpiresAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}.";
    public bool Confirmed { get => _confirmed; set => SetProperty(ref _confirmed, value); }
    public bool Chat { get => _chat; set => SetProperty(ref _chat, value); }
    public bool Vision { get => _vision; set => SetProperty(ref _vision, value); }
    public bool Tools { get => _tools; set => SetProperty(ref _tools, value); }
    public string Reasoning { get => _reasoning; set => SetProperty(ref _reasoning, value); }
    public string Speed { get => _speed; set => SetProperty(ref _speed, value); }
    public string Modes { get => _modes; set => SetProperty(ref _modes, value); }
    public string ContextLimit { get => _context; set => SetProperty(ref _context, value); }
    public string ValidityHours { get => _hours; set => SetProperty(ref _hours, value); }
    public string StatusMessage { get => _status; private set => SetProperty(ref _status, value); }

    public void SetConfiguration(ModelRouteConfiguration snapshot, string? profileId)
    {
        var modelId = _model?.Id; var accountId = _account?.Id;
        _snapshot = snapshot; _profileId = profileId;
        Models.Clear(); Accounts.Clear();
        foreach (var model in snapshot.Models.Where(item => item.ProfileId == profileId)) Models.Add(model);
        foreach (var account in snapshot.Accounts.Where(item => item.ProfileId == profileId)) Accounts.Add(account);
        _model = Models.FirstOrDefault(item => item.Id == modelId);
        _account = Accounts.FirstOrDefault(item => item.Id == accountId);
        OnPropertyChanged(nameof(Model)); OnPropertyChanged(nameof(Account)); Load();
    }

    private void Load()
    {
        _expected = _snapshot.Capabilities.SingleOrDefault(item => item.ModelId == Model?.Id && item.AccountId == Account?.Id);
        Confirmed = _expected?.Provenance == ModelProvenance.UserDefined && _expected.IsCurrent(clock.GetUtcNow());
        var flags = _expected?.Flags ?? ModelCapabilityFlags.None;
        Chat = flags.HasFlag(ModelCapabilityFlags.Chat); Vision = flags.HasFlag(ModelCapabilityFlags.Vision);
        Tools = flags.HasFlag(ModelCapabilityFlags.ToolCalling);
        Reasoning = string.Join(", ", _expected?.ReasoningEfforts ?? []);
        Speed = string.Join(", ", _expected?.SpeedModes ?? []);
        Modes = string.Join(", ", _expected?.ExecutionModes ?? []);
        ContextLimit = _expected?.ContextLimit?.ToString(CultureInfo.InvariantCulture) ?? "";
        ValidityHours = _expected is null ? "24"
            : Math.Clamp(Math.Ceiling((_expected.ExpiresAtUtc - _expected.ObservedAtUtc).TotalHours), 1, 8760)
                .ToString(CultureInfo.InvariantCulture);
        StatusMessage = ""; Changed();
    }

    public async Task SaveAsync()
    {
        if (!CanSave) return;
        if (!int.TryParse(ValidityHours, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) || hours is < 1 or > 8760)
        { StatusMessage = "Срок должен быть от 1 до 8760 часов."; return; }
        int? context = null;
        if (!string.IsNullOrWhiteSpace(ContextLimit))
        {
            if (!int.TryParse(ContextLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            { StatusMessage = "Размер контекста должен быть положительным целым числом или оставаться пустым."; return; }
            context = number;
        }
        var reasoning = Values(Reasoning); var speed = Values(Speed); var modes = Values(Modes);
        var flags = (Chat ? ModelCapabilityFlags.Chat : 0) | (Vision ? ModelCapabilityFlags.Vision : 0)
            | (Tools ? ModelCapabilityFlags.ToolCalling : 0) | (reasoning.Length > 0 ? ModelCapabilityFlags.ReasoningVariants : 0);
        var now = clock.GetUtcNow();
        var declaration = new ModelCapabilityEvidence(Model!.Id, Account!.Id,
            Confirmed ? CapabilityState.Supported : CapabilityState.Unknown, ModelProvenance.UserDefined,
            now, now.AddHours(hours), flags, reasoning, speed, modes, context);
        if (!declaration.IsWellFormed)
        { StatusMessage = "Проверьте списки: до 64 уникальных значений по 128 символов, без пустых элементов и управляющих символов."; return; }
        var expected = _expected;
        IsBusy = true;
        try
        {
            await store!.SaveUserDeclarationAsync(declaration, expected,
                new ModelCapabilityContext(Model.Id, Account.Id, Model.CapabilityRevision, Account.CapabilityRevision));
            SetConfiguration(await configuration!.ReadAsync(), _profileId);
            StatusMessage = "Ручная настройка возможностей сохранена для выбранной модели и аккаунта.";
        }
        catch (InvalidOperationException error) { StatusMessage = error.Message; }
        catch (ArgumentException) { StatusMessage = "Не удалось сохранить: проверьте параметры и отсутствие секретов в значениях."; }
        catch (Exception) { StatusMessage = "Не удалось сохранить возможности. Обновите данные и повторите действие."; }
        finally { IsBusy = false; }
    }

    public async Task DiscoverAsync()
    {
        if (!CanDiscover) return;
        var modelId = Model!.Id; var accountId = Account!.Id;
        IsBusy = true;
        try
        {
            await discovery!.DiscoverAsync(modelId, accountId);
            SetConfiguration(await configuration!.ReadAsync(), _profileId);
            StatusMessage = "Получены возможности выбранной модели из авторизованного нативного клиента. Неизвестные возможности не подтверждены.";
        }
        catch (Exception) { StatusMessage = "Не удалось получить возможности: проверьте авторизацию и поддержку discovery выбранным клиентом."; }
        finally { IsBusy = false; }
    }

    private static string[] Values(string text) => string.IsNullOrWhiteSpace(text) ? []
        : text.Split(',', StringSplitOptions.None).Select(value => value.Trim()).ToArray();
    private string StateName(ModelCapabilityEvidence evidence) => evidence.State == CapabilityState.Supported
        ? evidence.IsCurrent(clock.GetUtcNow()) ? "подтверждено" : "срок действия неактуален"
        : evidence.State switch { CapabilityState.Unsupported => "не поддерживается", CapabilityState.Error => "ошибка",
            CapabilityState.Stale => "устарело", _ => "не подтверждено" };
    private static string SourceName(ModelProvenance source) => source switch
    { ModelProvenance.ProviderReported => "провайдер", ModelProvenance.PluginReported => "плагин", _ => "ручная настройка" };
    private void Changed()
    {
        foreach (var name in new[] { nameof(CanSelect), nameof(CanSave), nameof(CanDiscover), nameof(ScopeNote), nameof(SavedEvidence) }) OnPropertyChanged(name);
        RelayCommand.RaiseCanExecuteChanged();
    }
}
