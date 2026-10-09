namespace LLMWorkGUI.App.ViewModels;

public sealed class NavigationItemViewModel : ObservableObject
{
    private bool _isSelected;

    public NavigationItemViewModel(ScreenViewModel screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        Screen = screen;
    }

    public ScreenViewModel Screen { get; }

    public ScreenId Id => Screen.Id;

    public string Title => Screen.Title;

    public string Shortcut => Screen.Shortcut;

    public string Icon => Id switch
    {
        ScreenId.Workspace => "\uE7F4",
        ScreenId.Projects => "\uE8B7",
        ScreenId.ProvidersAccounts => "\uE716",
        ScreenId.Models => "\uE950",
        ScreenId.Quotas => "\uE9D9",
        ScreenId.Sessions => "\uE8F2",
        ScreenId.Runs => "\uE768",
        ScreenId.Workflows => "\uE8AB",
        ScreenId.HealthCenter => "\uE9D9",
        _ => "\uE713"
    };

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}
