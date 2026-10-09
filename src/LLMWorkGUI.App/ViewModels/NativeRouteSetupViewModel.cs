using System.Windows.Input;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>Explicit first-use setup; a native status check is not a verified model turn.</summary>
public sealed class NativeRouteSetupViewModel(
    INativeGatewayRouteActivationService? service, Func<Task> refresh, Func<bool>? ownerAvailable = null) : ObservableObject
{
    private long _generation;
    private ConfiguredRoute? _route;
    private NativeGatewayRouteActivationPreview? _preview;
    private string _email = "", _status = "";
    private bool _busy, _support, _unverified;
    private DataClassification _classification = DataClassification.PublicSource;
    private ICommand? _previewCommand, _activateCommand;

    public bool IsVisible { get; private set; }
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); Changed(); } }
    public bool CanEdit => !IsBusy;
    public string ExpectedEmail
    {
        get => _email;
        set { if (!IsBusy && SetProperty(ref _email, value)) Invalidate(); }
    }
    public DataClassification MaxDataClass
    {
        get => _classification;
        set
        {
            if (!IsBusy && SetProperty(ref _classification, value))
            {
                _support = _unverified = false;
                OnPropertyChanged(nameof(ConfirmModelSupport));
                OnPropertyChanged(nameof(AllowUnverifiedFirstRequest));
                Changed();
            }
        }
    }
    public bool ConfirmModelSupport
    {
        get => _support;
        set { if (!IsBusy && SetProperty(ref _support, value)) Changed(); }
    }
    public bool AllowUnverifiedFirstRequest
    {
        get => _unverified;
        set { if (!IsBusy && SetProperty(ref _unverified, value)) Changed(); }
    }
    public string StatusMessage { get => _status; private set => SetProperty(ref _status, value); }
    public string ObservedIdentity => _preview is null ? "Вход ещё не проверен."
        : $"Вход: {_preview.ActualEmail}\nID пользователя: {_preview.NativeUserId}\nМодель: {_preview.NativeModelId}";
    public bool CanPreview => !IsBusy && (ownerAvailable?.Invoke() ?? true) && IsVisible && service is not null && !string.IsNullOrWhiteSpace(ExpectedEmail);
    public bool CanActivate => !IsBusy && (ownerAvailable?.Invoke() ?? true) && IsVisible && service is not null && _preview is not null
        && _support && _unverified && _preview.ExpiresAtUtc > DateTimeOffset.UtcNow;
    public ICommand PreviewCommand => _previewCommand ??= new RelayCommand(() => _ = PreviewAsync(), () => CanPreview);
    public ICommand ActivateCommand => _activateCommand ??= new RelayCommand(() => _ = ActivateAsync(), () => CanActivate);

    public void SetRoute(ConfiguredRoute? route, ModelProfileOption? profile)
    {
        _route = route;
        IsVisible = route?.Backend == BackendType.NativeGateway && profile?.Backend == BackendType.NativeGateway;
        OnPropertyChanged(nameof(IsVisible));
        _classification = route?.MaxDataClass ?? DataClassification.PublicSource;
        OnPropertyChanged(nameof(MaxDataClass));
        Invalidate();
    }

    private void Invalidate()
    {
        _generation++;
        _preview = null;
        _support = _unverified = false;
        StatusMessage = "";
        OnPropertyChanged(nameof(ObservedIdentity));
        OnPropertyChanged(nameof(ConfirmModelSupport));
        OnPropertyChanged(nameof(AllowUnverifiedFirstRequest));
        Changed();
    }

    public async Task PreviewAsync()
    {
        if (!CanPreview) return;
        var routeId = _route!.Id;
        var email = ExpectedEmail.Trim();
        Invalidate();
        var generation = _generation;
        IsBusy = true;
        try
        {
            var preview = await service!.PreviewAsync(routeId, email, CancellationToken.None);
            if (generation != _generation || _route?.Id != routeId || ExpectedEmail.Trim() != email) return;
            _preview = preview;
            OnPropertyChanged(nameof(ObservedIdentity));
            StatusMessage = "Вход и наличие модели проверены. Маршрут ещё не включён. Проверьте сведения и подтверждения ниже.";
        }
        catch (InvalidOperationException error) { if (generation == _generation) StatusMessage = error.Message; }
        catch (Exception) { if (generation == _generation) StatusMessage = "Не удалось проверить вход Cursor. Маршрут не включён."; }
        finally { IsBusy = false; }
    }

    public async Task ActivateAsync()
    {
        if (!CanActivate) return;
        IsBusy = true;
        var success = false;
        try
        {
            await service!.ActivateAsync(_preview!.ObservationId, ConfirmModelSupport,
                AllowUnverifiedFirstRequest, MaxDataClass, CancellationToken.None);
            success = true;
        }
        catch (InvalidOperationException error) { Invalidate(); StatusMessage = error.Message; }
        catch (Exception) { Invalidate(); StatusMessage = "Включение не подтверждено. Обновите сохранённую конфигурацию перед повтором."; }
        finally { IsBusy = false; }
        if (success)
        {
            Invalidate();
            await refresh();
            StatusMessage = "Маршрут включён с неподтверждённым состоянием ForcedEnabled. В рабочей области обновите маршруты, выберите этот маршрут и отправьте первый запрос. Разрешения на передачу данных запрашиваются отдельно.";
        }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanActivate));
        RelayCommand.RaiseCanExecuteChanged();
    }
}
