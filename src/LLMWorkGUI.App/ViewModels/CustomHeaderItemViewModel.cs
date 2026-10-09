namespace LLMWorkGUI.App.ViewModels;

public sealed class CustomHeaderItemViewModel : ObservableObject
{
    private string _name;
    private string _value;
    private bool _isSecret;

    public CustomHeaderItemViewModel(string name = "", string value = "", bool isSecret = false, string? secretReference = null)
    {
        _name = name;
        _value = value;
        _isSecret = isSecret;
        SecretReference = secretReference;
    }

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(IsSensitive)); }
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public bool IsSecret
    {
        get => _isSecret;
        set { if (SetProperty(ref _isSecret, value)) OnPropertyChanged(nameof(IsSensitive)); }
    }

    public string? SecretReference { get; }
    public bool IsSensitive => IsSecret || SecretReference is not null
        || LLMWorkGUI.Application.Security.CredentialTextRedactor.IsSensitiveHeaderName(Name);
    public string ValueWatermark => SecretReference is null ? "Значение" : "Секрет сохранён; введите новое значение для замены";
}
