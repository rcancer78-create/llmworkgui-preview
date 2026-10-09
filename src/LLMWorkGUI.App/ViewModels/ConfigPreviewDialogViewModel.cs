using System.Windows.Input;

namespace LLMWorkGUI.App.ViewModels;

public sealed class ConfigPreviewDialogViewModel : ObservableObject
{
    private bool _isVisible;
    private string _diffText = string.Empty;
    private string _redactedJson = string.Empty;
    private bool _hasExistingConfig;
    private bool _hasChanges;
    private Action? _onConfirmed;
    private Action? _onCancelled;

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public string DiffText
    {
        get => _diffText;
        set => SetProperty(ref _diffText, value);
    }

    public string RedactedJson
    {
        get => _redactedJson;
        set => SetProperty(ref _redactedJson, value);
    }

    public bool HasExistingConfig
    {
        get => _hasExistingConfig;
        set => SetProperty(ref _hasExistingConfig, value);
    }

    public bool HasChanges
    {
        get => _hasChanges;
        set => SetProperty(ref _hasChanges, value);
    }

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public ConfigPreviewDialogViewModel()
    {
        ConfirmCommand = new RelayCommand(ExecuteConfirm);
        CancelCommand = new RelayCommand(ExecuteCancel);
    }

    public void Show(
        string diffText,
        string redactedJson,
        bool hasExistingConfig,
        bool hasChanges,
        Action onConfirmed,
        Action? onCancelled = null)
    {
        DiffText = diffText;
        RedactedJson = redactedJson;
        HasExistingConfig = hasExistingConfig;
        HasChanges = hasChanges;
        _onConfirmed = onConfirmed;
        _onCancelled = onCancelled;
        IsVisible = true;
    }

    public void Close()
    {
        IsVisible = false;
        _onConfirmed = null;
        _onCancelled = null;
    }

    private void ExecuteConfirm()
    {
        var action = _onConfirmed;
        Close();
        action?.Invoke();
    }

    private void ExecuteCancel()
    {
        var action = _onCancelled;
        Close();
        action?.Invoke();
    }
}
