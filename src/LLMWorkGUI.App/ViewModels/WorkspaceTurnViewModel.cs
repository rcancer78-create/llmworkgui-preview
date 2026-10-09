using LLMWorkGUI.Application.Concurrency;

namespace LLMWorkGUI.App.ViewModels;

public sealed class WorkspaceTurnViewModel : ObservableObject
{
    private string _role = string.Empty;
    private string _promptText = string.Empty;
    private string _responseText = string.Empty;
    private string _status = string.Empty;

    public string Role
    {
        get => _role;
        set => SetProperty(ref _role, value);
    }

    public string PromptText
    {
        get => _promptText;
        set => SetProperty(ref _promptText, value);
    }

    public string ResponseText
    {
        get => _responseText;
        set => SetProperty(ref _responseText, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string ToolCallsDisplay { get; set; } = string.Empty;
}
