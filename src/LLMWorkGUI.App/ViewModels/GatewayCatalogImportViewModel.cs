using System.Windows.Input;
using LLMWorkGUI.Application.Providers;

namespace LLMWorkGUI.App.ViewModels;

public sealed class GatewayCatalogImportViewModel(
    IGatewayCatalogImportService? service, Func<bool> canStart, Func<Task> refreshCatalog) : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    private bool _isBusy;
    private bool _committed;
    private bool _cancelRequested;
    private string _message = service is null ? "Импорт LLMGateway недоступен." :
        "Загрузите каталог настроенных нативных клиентов. Новые подключения будут отключены до проверки.";

    public ICommand ImportCommand => _importCommand ??= new RelayCommand(async () => await ImportAsync(), () => CanImport);
    public ICommand CancelCommand => _cancelCommand ??= new RelayCommand(Cancel, () => CanCancel);
    private ICommand? _importCommand;
    private ICommand? _cancelCommand;
    public bool CanImport => service is not null && !IsBusy && canStart();
    public bool CanCancel => IsBusy && !_committed && !_cancelRequested;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) UpdateCommands(); } }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public async Task ImportAsync()
    {
        if (!CanImport) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _cancellation = cancellation;
        _committed = false;
        _cancelRequested = false;
        IsBusy = true;
        Message = "Чтение каталога LLMGateway…";
        try
        {
            // Native discovery and synchronous SQLite work must not occupy the WPF dispatcher.
            var snapshot = await Task.Run(() => service!.ImportAsync(cancellation.Token), cancellation.Token);
            _committed = true;
            UpdateCommands();
            try { await refreshCatalog(); }
            catch (Exception)
            {
                Message = "Каталог сохранён, но списки не удалось обновить. Откройте раздел заново.";
                return;
            }
            Message = $"Каталог сохранён: провайдеров {snapshot.Providers.Count}, аккаунтов {snapshot.Accounts.Count}, " +
                $"моделей {snapshot.Models.Count}, маршрутов {snapshot.Routes.Count}. " +
                "Новые записи отключены; существующие настройки сохранены. Авторизация требует отдельной проверки.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Message = _cancelRequested ? "Импорт отменён." : "Время ожидания каталога истекло. Повторите импорт.";
        }
        catch (Exception)
        {
            // Native errors can contain profile paths, tokens, or process arguments.
            Message = "Не удалось импортировать каталог. Проверьте доступность нативных клиентов и повторите попытку.";
        }
        finally { _cancellation = null; IsBusy = false; }
    }

    private void Cancel()
    {
        if (!CanCancel) return;
        _cancelRequested = true;
        Message = "Отмена импорта…";
        UpdateCommands();
        _cancellation?.Cancel();
    }

    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(CanCancel));
        RelayCommand.RaiseCanExecuteChanged();
    }
}
