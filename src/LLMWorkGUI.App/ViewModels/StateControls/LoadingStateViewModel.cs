using System.Windows.Input;

namespace LLMWorkGUI.App.ViewModels.StateControls;

/// <summary>
/// Standard loading state: the current operation plus an optional cancel action. The progress bar is
/// indeterminate because the observed operations do not report a reliable percentage.
/// </summary>
public sealed class LoadingStateViewModel
{
    public const string DefaultMessage = "Загрузка…";

    public LoadingStateViewModel(
        string? message = null,
        bool isCancellable = false,
        ICommand? cancelCommand = null)
    {
        Message = string.IsNullOrWhiteSpace(message) ? DefaultMessage : message;
        IsCancellable = isCancellable;
        CancelCommand = cancelCommand;
    }

    public string Message { get; }

    public bool IsCancellable { get; }

    public ICommand? CancelCommand { get; }

    public bool ShowCancel => IsCancellable && CancelCommand is not null;
}
