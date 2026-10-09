namespace LLMWorkGUI.App.ViewModels;

public abstract class ScreenViewModel : ObservableObject
{
    protected ScreenViewModel(ScreenId id, string title, string shortcut, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcut);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Id = id;
        Title = title;
        Shortcut = shortcut;
        Description = description;
    }

    public ScreenId Id { get; }

    public string Title { get; }

    public string Shortcut { get; }

    public string Description { get; }
}
