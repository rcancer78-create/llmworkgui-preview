using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Concurrency;

namespace LLMWorkGUI.App.ViewModels;

public sealed record AccountManagementRow(AccountConfigurationRow Value)
{
    public string Display => $"{Value.Settings.Name} · {Value.AuthState} · {Value.Health}" + (Value.Settings.IsEnabled ? "" : " · отключён");
    public string Details => $"ID: {Value.Settings.Id}\nАвторизация: {Value.AuthState}; здоровье: {Value.Health}\n" +
        $"Пауза до: {Value.CooldownUntil ?? "—"}; отключён до: {Value.DisabledUntil ?? "—"}";
    public string SessionBindings => Value.SessionCount == 0 ? "Текущих локальных привязок сессий нет."
        : $"Локальные привязки сессий: {Value.SessionCount}. Это запрошенные настройки; аккаунт, ответивший на запрос, не подтверждён.\n"
          + string.Join("\n", Value.Sessions.Select(s =>
              $"Local: {s.LocalSessionId}; проект: {s.ProjectId}; {s.Backend}; {s.State}\n" +
              $"Модель ID: {s.ModelId}; effort: {s.ReasoningEffort ?? "—"}; speed: {s.SpeedMode ?? "—"}; mode: {s.ExecutionMode ?? "—"}\n" +
              $"Native session: {s.NativeSessionId ?? "не подтверждена"}; активное выполнение: {s.ActiveExecutionId ?? "—"}"))
          + (Value.SessionCount > Value.Sessions.Count ? $"\nПоказаны последние {Value.Sessions.Count} из {Value.SessionCount}." : "");
}

public sealed partial class AccountManagementViewModel : ObservableObject
{
    private readonly IAccountConfigurationService? _service;
    private readonly IApplicationInstanceGuard? _instanceGuard;
    private AccountConfiguration _snapshot = new([], []);
    private ModelProfileOption? _profile;
    private AccountManagementRow? _selectedAccount;
    private AccountImportCandidate? _selectedImport;
    private bool _busy;
    private string? _message;
    private string _name = "";
    private string _priority = "0";
    private string _limit = "1";
    private string _reserve = "";
    private bool _enabled;

    public AccountManagementViewModel(IAccountConfigurationService? service = null, IApplicationInstanceGuard? instanceGuard = null,
        IAdaptationAccountConfigurationService? adaptationConfiguration = null)
    {
        _service = service;
        _instanceGuard = instanceGuard;
        _adaptationConfiguration = adaptationConfiguration;
        RefreshAdaptationCommand = new RelayCommand(async () => await RefreshAdaptationAsync(), () => CanRead && ShowAdaptationConfiguration);
        SaveAccountKeyCommand = new RelayCommand(async () => await SaveAccountKeyAsync(), () => CanSaveAccountKey);
        BindNativeProviderCommand = new RelayCommand(async () => await BindNativeProviderAsync(), () => CanBindNativeProvider);
        ClearAccountKeyCommand = new RelayCommand(ClearAccountKey, () => EnteredApiKey.Length > 0);
        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => CanRead);
        NewCommand = new RelayCommand(() => SelectedAccount = null, () => CanEdit && Profile is not null);
        SaveCommand = new RelayCommand(async () => await SaveAsync(), () => CanEdit && Profile is not null);
        DiscoverCommand = new RelayCommand(async () => await DiscoverAsync(), () => CanEdit && Profile is not null);
        ImportCommand = new RelayCommand(async () => await ImportAsync(), () => CanEdit && SelectedImport is not null);
    }
    public ObservableCollection<ModelProfileOption> Profiles { get; } = new();
    public ObservableCollection<AccountManagementRow> Accounts { get; } = new();
    public ObservableCollection<AccountImportCandidate> ImportCandidates { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DiscoverCommand { get; }
    public ICommand ImportCommand { get; }
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanRead)); NotifyAdaptationCommands(); } } }
    public bool CanRead => !IsBusy && _service is not null;
    public bool CanEdit => CanRead && _instanceGuard?.IsViewOnly != true;
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }
    public ModelProfileOption? Profile
    {
        get => _profile;
        set { if (!IsBusy && SetProperty(ref _profile, value)) { FilterAccounts(); RelayCommand.RaiseCanExecuteChanged(); } }
    }
    public AccountManagementRow? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (IsBusy) return;
            if (value is not null && (!Accounts.Contains(value) || value.Value.Settings.ProfileId != Profile?.Id))
            { Message = "Выберите аккаунт текущего профиля из списка."; return; }
            LoadEditor(value);
        }
    }
    public AccountImportCandidate? SelectedImport
    {
        get => _selectedImport;
        set { if (!IsBusy && SetProperty(ref _selectedImport, value)) RelayCommand.RaiseCanExecuteChanged(); }
    }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Priority { get => _priority; set => SetProperty(ref _priority, value); }
    public string Limit { get => _limit; set => SetProperty(ref _limit, value); }
    public string Reserve { get => _reserve; set => SetProperty(ref _reserve, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

    public Task RefreshAsync() => Run(async () => { await Reload(); Message = Accounts.Count == 0 ? "В профиле нет аккаунтов. Создайте запись или обнаружьте нативные контексты." : "Аккаунты загружены."; }, readOnly: true);
    public async Task RefreshAfterCatalogImportAsync()
    {
        if (_service is null) return;
        if (IsBusy) throw new InvalidOperationException("Account operation is already running.");
        IsBusy = true;
        try
        {
            await Reload(SelectedAccount?.Value.Settings.Id);
            Message = "Список аккаунтов обновлён.";
        }
        finally { IsBusy = false; }
    }
    public Task SaveAsync()
    {
        if (!CanEdit || Profile is null) return Task.CompletedTask;
        if (!int.TryParse(Priority, NumberStyles.None, CultureInfo.InvariantCulture, out var priority)
            || !int.TryParse(Limit, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            || !TryReserve(out var reserve))
        { Message = "Приоритет и лимит — целые числа; резерв — число от 0 до 1 (например, 0.2)."; return Task.CompletedTask; }
        var request = new SaveAccountSettings(Profile.Id, Name, priority, Enabled, limit, reserve, SelectedAccount?.Value.Settings);
        return Run(async () => { var id = await _service!.SaveAsync(request); await Reload(id); Message = "Настройки сохранены. Состояние авторизации не изменено."; });
    }
    public Task DiscoverAsync()
    {
        if (!CanEdit || Profile is null) return Task.CompletedTask;
        var profileId = Profile.Id;
        return Run(async () =>
        {
            ImportCandidates.Clear(); _selectedImport = null; OnPropertyChanged(nameof(SelectedImport));
            foreach (var item in await _service!.DiscoverAsync(profileId)) ImportCandidates.Add(item);
            Message = ImportCandidates.Count == 0 ? "Нативные контексты не обнаружены." : "Выберите контекст для импорта. Обнаружение не подтверждает авторизацию.";
        });
    }
    public Task ImportAsync()
    {
        if (!CanEdit || Profile is null || SelectedImport is null) return Task.CompletedTask;
        var profileId = Profile.Id; var candidate = SelectedImport;
        return Run(async () => { var id = await _service!.ImportAsync(profileId, candidate); await Reload(id); Message = "Контекст импортирован отключённым, авторизация Unknown. Нативные файлы не копировались."; });
    }
    private async Task Run(Func<Task> action, bool readOnly = false)
    {
        if (readOnly ? !CanRead : !CanEdit) return;
        IsBusy = true;
        try { await action(); }
        // Adapter/SQLite exceptions may contain native paths or credentials; never expose their messages.
        catch (Exception) { Message = "Действие не выполнено. Проверьте поля и доступность контекста, затем обновите список. Устаревшие или повторные записи отклоняются."; }
        finally { IsBusy = false; }
    }
    private async Task Reload(string? selectedId = null)
    {
        var profileId = _profile?.Id;
        _snapshot = await _service!.ReadAsync();
        Profiles.Clear(); foreach (var item in _snapshot.Profiles) Profiles.Add(item);
        _profile = Profiles.FirstOrDefault(p => p.Id == profileId) ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(Profile)); FilterAccounts();
        LoadEditor(Accounts.FirstOrDefault(a => a.Value.Settings.Id == selectedId));
    }
    private void FilterAccounts()
    {
        Accounts.Clear(); foreach (var item in _snapshot.Accounts.Where(a => a.Settings.ProfileId == _profile?.Id)) Accounts.Add(new(item));
        ImportCandidates.Clear(); _selectedImport = null; OnPropertyChanged(nameof(SelectedImport)); LoadEditor(null);
    }
    private void LoadEditor(AccountManagementRow? row)
    {
        ResetAdaptationEditor();
        _selectedAccount = row; OnPropertyChanged(nameof(SelectedAccount));
        var settings = row?.Value.Settings;
        Name = settings?.Name ?? ""; Priority = settings?.Priority.ToString(CultureInfo.InvariantCulture) ?? "0";
        Limit = settings?.MaxConcurrentExecutions.ToString(CultureInfo.InvariantCulture) ?? "1";
        Reserve = settings?.ReserveThreshold?.ToString(CultureInfo.InvariantCulture) ?? ""; Enabled = settings?.IsEnabled ?? false;
        NotifyAdaptationCommands();
    }
    private bool TryReserve(out double? reserve)
    {
        reserve = null;
        if (string.IsNullOrWhiteSpace(Reserve)) return true;
        if (!double.TryParse(Reserve, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0 || value > 1) return false;
        reserve = value; return true;
    }
}
