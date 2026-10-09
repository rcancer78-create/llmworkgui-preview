using System.Windows.Input;

namespace LLMWorkGUI.App.Help;

public static class HelpCommands
{
    public static RoutedUICommand OpenGuide { get; } = new("Руководство пользователя и администратора", nameof(OpenGuide), typeof(HelpCommands));
}
