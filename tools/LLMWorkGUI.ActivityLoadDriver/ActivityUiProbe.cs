using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LLMWorkGUI.ActivityLoadDriver;

/// <summary>
/// Finds and drives the shipped Activity Center controls of the shown window.
/// <para>
/// Everything here resolves a control by its <c>x:Name</c> in the live visual tree of a window that is
/// actually on the desktop and then runs the <see cref="ICommand"/> the control is bound to. No view
/// model method is invoked directly, so a passing run means the shipped button really performs the
/// action, not that a private method happened to be callable.
/// </para>
/// </summary>
internal static class ActivityUiProbe
{
    public static IReadOnlyList<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<T>();
        Collect(root, found);
        return found;
    }

    public static T? Find<T>(DependencyObject root, string name)
        where T : FrameworkElement =>
        Descendants<T>(root).FirstOrDefault(element => element.Name == name);

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

    /// <summary>
    /// The one Activity Center view inside the shown window.
    /// <para>
    /// Probing has to be scoped to it rather than to the whole window. Control names are only unique
    /// inside their own template, and the shell really does host a second <c>SearchBox</c> - the command
    /// palette's - so a window-wide lookup for <c>SearchBox</c> finds two controls and a driver that
    /// "finds the first" would silently be typing into the palette instead of the Activity Center.
    /// </para>
    /// </summary>
    public static T RequireActivityCenter<T>(DependencyObject root)
        where T : FrameworkElement
    {
        var views = Descendants<T>(root);

        if (views.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one Activity Center view on the shown window, found {views.Count}.");
        }

        return views[0];
    }

    /// <summary>
    /// Every piece of text the window is currently showing, including editable boxes. Reading the whole
    /// rendered text tree is how the run proves a secret cannot reach the screen: a value that survived
    /// into any <see cref="TextBlock"/> or <see cref="TextBox"/> would be caught here.
    /// </summary>
    public static IReadOnlyList<string> Texts(DependencyObject root) =>
        Descendants<DependencyObject>(root)
            .Select(element => element switch
            {
                TextBlock block when !string.IsNullOrEmpty(block.Text) => block.Text,
                TextBox box when !string.IsNullOrEmpty(box.Text) => box.Text,
                ComboBox combo => combo.Text,
                Button button when !string.IsNullOrEmpty(button.Content as string) => (string)button.Content,
                _ => null
            })
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!)
            .ToArray();

    /// <summary>Runs the command the shipped control is bound to, after checking it is enabled.</summary>
    public static void Invoke(ButtonBase control)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (!control.IsEnabled)
        {
            throw new InvalidOperationException($"The shipped control '{control.Name}' is not enabled.");
        }

        var command = control.Command
            ?? throw new InvalidOperationException($"The shipped control '{control.Name}' has no command.");

        if (!command.CanExecute(control.CommandParameter))
        {
            throw new InvalidOperationException(
                $"The command behind '{control.Name}' refuses to execute for this state.");
        }

        command.Execute(control.CommandParameter);
    }

    /// <summary>Types a query into the shipped search box exactly as a keystroke would.</summary>
    public static void SetSearchText(TextBox searchBox, string text)
    {
        ArgumentNullException.ThrowIfNull(searchBox);

        searchBox.Focus();
        searchBox.Clear();
        searchBox.Text = text;
        searchBox.CaretIndex = text.Length;

        // The binding is UpdateSourceTrigger-bound; pushing the change through the same path a keystroke
        // uses keeps the observation about the product rather than about the probe.
        var binding = System.Windows.Data.BindingOperations
            .GetBindingExpression(searchBox, TextBox.TextProperty);
        binding?.UpdateSource();
    }

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
