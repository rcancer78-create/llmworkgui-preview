using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LLMWorkGUI.VisibleWorkflowHarness;

/// <summary>Finds and drives the shipped controls of the shown window by their <c>x:Name</c>.</summary>
internal static class UiProbe
{
    public static IReadOnlyList<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<T>();
        Collect(root, found);
        return found;
    }

    public static T Require<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        var matches = Descendants<T>(root).Where(element => element.Name == name).ToArray();

        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one control named '{name}' on the shown window, found {matches.Length}.");
        }

        return matches[0];
    }

    public static T RequireButtonByCommand<T>(DependencyObject root, ICommand command, string description)
        where T : ButtonBase
    {
        ArgumentNullException.ThrowIfNull(command);

        var matches = Descendants<T>(root)
            .Where(button => ReferenceEquals(button.Command, command))
            .ToArray();

        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one {description} bound to that command on the shown window, "
                + $"found {matches.Length}.");
        }

        return matches[0];
    }

    /// <summary>
    /// Executes the command the shipped button is bound to, after checking the button is enabled. This is
    /// the same <see cref="ICommand"/> a mouse click on that button runs; no view model method is called
    /// directly.
    /// </summary>
    public static void Invoke(ButtonBase button)
    {
        ArgumentNullException.ThrowIfNull(button);

        if (!button.IsEnabled)
        {
            throw new InvalidOperationException($"The shipped control '{button.Name}' is not enabled.");
        }

        var command = button.Command
            ?? throw new InvalidOperationException($"The shipped control '{button.Name}' has no command.");

        if (!command.CanExecute(button.CommandParameter))
        {
            throw new InvalidOperationException(
                $"The command behind '{button.Name}' refuses to execute for this state.");
        }

        command.Execute(button.CommandParameter);
    }

    /// <summary>
    /// Runs the shipped <c>Ctrl+8</c> keyboard binding of the shown shell: the very
    /// <see cref="KeyBinding"/> the product declares for the Workflows screen, executed with its own command
    /// and parameter. This is not a synthesised operating-system keystroke.
    /// </summary>
    public static void InvokeShortcut(DependencyObject root, Key key, ModifierKeys modifiers)
    {
        ArgumentNullException.ThrowIfNull(root);

        foreach (var element in Descendants<FrameworkElement>(root))
        {
            foreach (var binding in element.InputBindings)
            {
                if (binding is KeyBinding candidate
                    && candidate.Key == key
                    && candidate.Modifiers == modifiers
                    && candidate.Command is not null)
                {
                    candidate.Command.Execute(candidate.CommandParameter);
                    return;
                }
            }
        }

        throw new InvalidOperationException(
            $"The shown window declares no {modifiers}+{key} key binding.");
    }

    public static IReadOnlyList<string> Texts(DependencyObject root) =>
        Descendants<TextBlock>(root)
            .Select(block => block.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

    private static void Collect<T>(DependencyObject parent, List<T> found)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);

            if (child is T typed)
            {
                found.Add(typed);
            }

            Collect(child, found);
        }
    }
}
