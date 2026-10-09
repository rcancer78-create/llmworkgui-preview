using System.Windows.Input;

namespace LLMWorkGUI.App.ViewModels.StateControls;

/// <summary>
/// Standard empty state: an icon, a title, an explanation and an optional call-to-action. Screens use
/// it instead of inventing their own placeholder prose, so "nothing here yet" always looks the same.
/// </summary>
public sealed class EmptyStateViewModel
{
    public const string DefaultIconGlyph = "◻";

    public EmptyStateViewModel(
        string title,
        string description,
        string iconGlyph = DefaultIconGlyph,
        string? actionLabel = null,
        ICommand? actionCommand = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Title = title;
        Description = description;
        IconGlyph = string.IsNullOrWhiteSpace(iconGlyph) ? DefaultIconGlyph : iconGlyph;
        ActionLabel = actionLabel ?? string.Empty;
        ActionCommand = actionCommand;
    }

    public string IconGlyph { get; }

    public string Title { get; }

    public string Description { get; }

    public string ActionLabel { get; }

    public ICommand? ActionCommand { get; }

    public bool HasAction => ActionCommand is not null && !string.IsNullOrWhiteSpace(ActionLabel);
}
