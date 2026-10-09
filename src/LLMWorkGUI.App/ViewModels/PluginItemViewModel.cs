using LLMWorkGUI.Application.Providers;

namespace LLMWorkGUI.App.ViewModels;

public sealed class PluginItemViewModel : ObservableObject
{
    private bool _isEnabled;

    public string Name { get; }
    public string? Version { get; }
    public string? Description { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public PluginItemViewModel(PluginInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        Name = info.Name;
        Version = info.Version;
        Description = info.Description;
        _isEnabled = info.Enabled;
    }
}
