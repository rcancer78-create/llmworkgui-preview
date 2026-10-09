using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed class ProviderItemViewModel : ObservableObject
{
    private string _displayName;
    private string _baseUrl;
    private bool _isEnabled;

    public string Id { get; }
    public BackendType Backend { get; }
    public bool HasLimitations => Id == LLMWorkGUI.Application.Providers.GrokBotRestrictions.ProviderProfileId;
    public string Limitations => HasLimitations ? LLMWorkGUI.Application.Providers.GrokBotRestrictions.Notice : string.Empty;
    public string BackendDisplay => Backend switch
    {
        BackendType.CursorAcp => "Cursor ACP",
        BackendType.NativeGateway => "LLMGateway",
        _ => Backend.ToString()
    };
    public DataClassification MaxDataClass { get; }
    public IReadOnlyList<ProviderHeader> CustomHeaders { get; }
    public long? Revision { get; }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string? SecretReference { get; }
    public bool HasSecretRef => !string.IsNullOrWhiteSpace(SecretReference);

    public ProviderItemViewModel(ProviderProfile profile, string? secretReference = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Id = profile.Id;
        Backend = profile.Backend;
        MaxDataClass = profile.MaxDataClass;
        _displayName = profile.DisplayName;
        _baseUrl = profile.BaseUrl ?? string.Empty;
        _isEnabled = profile.IsEnabled;
        SecretReference = secretReference;
        CustomHeaders = profile.CustomHeaders ?? [];
        Revision = profile.Revision;
    }
}
