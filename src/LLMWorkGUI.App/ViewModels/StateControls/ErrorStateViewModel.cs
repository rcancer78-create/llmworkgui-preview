using System.Windows.Input;
using LLMWorkGUI.App.Services;

namespace LLMWorkGUI.App.ViewModels.StateControls;

/// <summary>
/// Standard error state: a human-readable message, an expandable technical details block with a copy
/// action and an optional retry action. Secrets in the details are expected to be redacted upstream.
/// </summary>
public sealed class ErrorStateViewModel : ObservableObject
{
    private readonly IClipboardService? _clipboard;

    private bool _isDetailsExpanded;
    private string _copyNotice = string.Empty;

    public ErrorStateViewModel(
        string title,
        string message,
        string? technicalDetails = null,
        ICommand? retryCommand = null,
        IClipboardService? clipboard = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Title = title;
        Message = message;
        TechnicalDetails = technicalDetails ?? string.Empty;
        RetryCommand = retryCommand;
        _clipboard = clipboard;

        ToggleDetailsCommand = new RelayCommand(ToggleDetails);
        CopyDetailsCommand = new RelayCommand(CopyDetails);
    }

    public string Title { get; }

    public string Message { get; }

    public string TechnicalDetails { get; }

    public bool HasDetails => !string.IsNullOrWhiteSpace(TechnicalDetails);

    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        private set
        {
            if (SetProperty(ref _isDetailsExpanded, value))
            {
                OnPropertyChanged(nameof(DetailsToggleLabel));
            }
        }
    }

    public string DetailsToggleLabel => IsDetailsExpanded ? "Скрыть технические подробности" : "Показать технические подробности";

    public ICommand ToggleDetailsCommand { get; }

    public ICommand CopyDetailsCommand { get; }

    public ICommand? RetryCommand { get; }

    public bool HasRetry => RetryCommand is not null;

    public string CopyNotice
    {
        get => _copyNotice;
        private set
        {
            if (SetProperty(ref _copyNotice, value))
            {
                OnPropertyChanged(nameof(HasCopyNotice));
            }
        }
    }

    public bool HasCopyNotice => !string.IsNullOrWhiteSpace(CopyNotice);

    public void ToggleDetails()
    {
        if (HasDetails)
        {
            IsDetailsExpanded = !IsDetailsExpanded;
        }
    }

    public void CopyDetails()
    {
        if (!HasDetails || _clipboard is null)
        {
            return;
        }

        _clipboard.SetText(TechnicalDetails);
        CopyNotice = "Технические подробности скопированы в буфер обмена.";
    }
}
