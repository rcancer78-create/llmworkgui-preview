using System.Windows.Input;
using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed partial class AccountManagementViewModel
{
    private readonly IAdaptationAccountConfigurationService? _adaptationConfiguration;
    private AdaptationAccountConfiguration? _adaptationSnapshot;
    private string _enteredApiKey = "";
    private string _nativeProviderId = "";
    private string _adaptationStatus = "Обновите привязку выбранного аккаунта.";

    public ICommand RefreshAdaptationCommand { get; }
    public ICommand SaveAccountKeyCommand { get; }
    public ICommand BindNativeProviderCommand { get; }
    public ICommand ClearAccountKeyCommand { get; }
    public bool ShowAdaptationConfiguration => _adaptationConfiguration is not null && Profile?.Backend == BackendType.OpenCode && SelectedAccount is not null;
    public bool CanChangeAdaptationConfiguration => CanEdit && ShowAdaptationConfiguration && _adaptationSnapshot is { HasOwnedExecution: false }
        && _adaptationSnapshot.AccountId == SelectedAccount?.Value.Settings.Id && _adaptationSnapshot.ProviderProfileId == Profile?.Id;
    public bool CanSaveAccountKey => CanChangeAdaptationConfiguration && !string.IsNullOrWhiteSpace(EnteredApiKey);
    public bool CanBindNativeProvider => CanChangeAdaptationConfiguration && _adaptationSnapshot!.IsKeyUsable && !string.IsNullOrWhiteSpace(NativeProviderId);
    public string AdaptationStatus { get => _adaptationStatus; private set => SetProperty(ref _adaptationStatus, value); }
    public string EnteredApiKey
    {
        get => _enteredApiKey;
        set { if ((!IsBusy || value.Length == 0) && SetProperty(ref _enteredApiKey, value)) NotifyAdaptationCommands(); }
    }
    public string NativeProviderId
    {
        get => _nativeProviderId;
        set { if (!IsBusy && SetProperty(ref _nativeProviderId, value)) NotifyAdaptationCommands(); }
    }
    public void ClearAccountKey() => EnteredApiKey = "";

    public Task RefreshAdaptationAsync()
    {
        if (!CanRead || !ShowAdaptationConfiguration) return Task.CompletedTask;
        var profile = Profile!.Id; var account = SelectedAccount!.Value.Settings.Id;
        return Run(async () => await LoadAdaptationAsync(profile, account), readOnly: true);
    }

    public Task SaveAccountKeyAsync()
    {
        if (!CanSaveAccountKey) return Task.CompletedTask;
        var expected = _adaptationSnapshot!; var key = EnteredApiKey; ClearAccountKey();
        return Run(async () =>
        {
            _adaptationSnapshot = null;
            AdaptationStatus = "Сохранение ключа выбранного аккаунта…";
            try
            {
                await _adaptationConfiguration!.SaveKeyAsync(expected, key);
                await Reload(expected.AccountId);
                await LoadAdaptationAsync(expected.ProviderProfileId, expected.AccountId);
                Message = "Ключ аккаунта сохранён. Проверьте привязку native-провайдера; авторизация не подтверждена.";
            }
            catch { AdaptationStatus = "Ключ или привязка изменились либо аккаунт занят. Обновите привязку перед следующим действием."; throw; }
            finally { ClearAccountKey(); NotifyAdaptationCommands(); }
        });
    }

    public Task BindNativeProviderAsync()
    {
        if (!CanBindNativeProvider) return Task.CompletedTask;
        var expected = _adaptationSnapshot!; var provider = NativeProviderId;
        return Run(async () =>
        {
            _adaptationSnapshot = null;
            AdaptationStatus = "Сохранение привязки выбранного аккаунта…";
            try
            {
                await _adaptationConfiguration!.ConfigureProviderAsync(expected, provider);
                await LoadAdaptationAsync(expected.ProviderProfileId, expected.AccountId);
                Message = "Native-провайдер привязан к ключу выбранного аккаунта. Это настройка; авторизация и источник ответа не подтверждены.";
            }
            catch { AdaptationStatus = "Привязка не подтверждена. Обновите данные аккаунта перед следующим действием."; throw; }
        });
    }

    private async Task LoadAdaptationAsync(string profile, string account)
    {
        _adaptationSnapshot = null; ClearAccountKey();
        AdaptationStatus = "Привязка ещё не подтверждена; данные обновляются…";
        AdaptationAccountConfiguration snapshot;
        try { snapshot = await _adaptationConfiguration!.ReadConfigurationAsync(profile, account); }
        catch { AdaptationStatus = "Данные привязки недоступны. Повторите обновление."; throw; }
        if (Profile?.Id != profile || SelectedAccount?.Value.Settings.Id != account) return;
        _adaptationSnapshot = snapshot; _nativeProviderId = snapshot.NativeProviderId ?? "";
        OnPropertyChanged(nameof(NativeProviderId));
        AdaptationStatus = snapshot.HasOwnedExecution ? "Аккаунт занят незавершённым выполнением. Замена ключа и привязки запрещена до подтверждённого освобождения."
            : !snapshot.IsKeyUsable ? "Для адаптации нужен отдельный активный API-ключ выбранного аккаунта. OAuth/plugin-контексты автоматически не копируются."
            : snapshot.IsMappingCurrent ? "Ключ сохранён, native-провайдер привязан. Авторизация и аккаунт, ответивший на запрос, не подтверждены."
            : "Ключ сохранён. Привяжите native-провайдера заново: текущая привязка отсутствует или относится к прежнему ключу.";
        NotifyAdaptationCommands();
    }

    private void ResetAdaptationEditor()
    {
        _adaptationSnapshot = null; ClearAccountKey(); _nativeProviderId = "";
        OnPropertyChanged(nameof(NativeProviderId)); AdaptationStatus = "Обновите привязку выбранного аккаунта.";
    }
    private void NotifyAdaptationCommands()
    {
        OnPropertyChanged(nameof(ShowAdaptationConfiguration)); OnPropertyChanged(nameof(CanChangeAdaptationConfiguration));
        OnPropertyChanged(nameof(CanSaveAccountKey)); OnPropertyChanged(nameof(CanBindNativeProvider)); RelayCommand.RaiseCanExecuteChanged();
    }
}
