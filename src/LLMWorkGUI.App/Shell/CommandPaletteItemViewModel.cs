using System.Windows.Input;

namespace LLMWorkGUI.App.Shell;

/// <summary>
/// One executable entry of the command palette. The item carries its own command, so the palette can
/// execute it without knowing what the command does (navigation, theme, CLI refresh, workflow action).
/// </summary>
public sealed class CommandPaletteItemViewModel
{
    public CommandPaletteItemViewModel(
        string id,
        string title,
        string description,
        string category,
        string shortcutDisplay,
        ICommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(command);

        Id = id;
        Title = title;
        Description = description ?? string.Empty;
        Category = category;
        ShortcutDisplay = shortcutDisplay ?? string.Empty;
        Command = command;
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>One of the <see cref="CommandPaletteCategories"/> values.</summary>
    public string Category { get; }

    /// <summary>Human-readable shortcut, for example <c>Ctrl+9</c>. Empty when the item has none.</summary>
    public string ShortcutDisplay { get; }

    public ICommand Command { get; }
}

/// <summary>Stable palette categories used by the default command set and by the search filter.</summary>
public static class CommandPaletteCategories
{
    public const string Navigation = "Навигация";
    public const string Actions = "Действия";
    public const string Theme = "Тема";
    public const string Workflow = "Процессы";
}
