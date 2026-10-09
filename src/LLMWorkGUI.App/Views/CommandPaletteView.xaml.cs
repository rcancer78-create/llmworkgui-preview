using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LLMWorkGUI.App.Shell;

namespace LLMWorkGUI.App.Views;

public partial class CommandPaletteView : UserControl
{
    private CommandPaletteViewModel? _viewModel;
    private IInputElement? _focusBeforeOpen;
    private long _focusTransition;

    public CommandPaletteView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Owns the palette keyboard focus flow: focus enters the search box when the palette opens and returns
    /// to whatever held focus before, so dismissing the modal never strands the keyboard on the window.
    /// <para>
    /// Each open and each close starts a new transition. Focus moves in a deferred pass, so an open/close/open
    /// cycle inside one dispatcher turn leaves older passes behind. The invoker is captured synchronously
    /// before another handler can move focus; queued entry and restoration passes belong to one transition.
    /// </para>
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        HookViewModel(e.NewValue as CommandPaletteViewModel);

    /// <summary>Re-attaches after an unload/reload cycle of the retained view; the data context is unchanged.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) =>
        HookViewModel(DataContext as CommandPaletteViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e) => HookViewModel(null);

    private void HookViewModel(CommandPaletteViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        _focusBeforeOpen = null;
        _focusTransition++;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommandPaletteViewModel.IsOpen) || _viewModel is null)
        {
            return;
        }

        var transition = ++_focusTransition;

        if (_viewModel.IsOpen)
        {
            EnterPalette(transition);
        }
        else
        {
            LeavePalette(transition);
        }
    }

    /// <summary>
    /// Moves focus into the search box so the whole flow can be driven from the keyboard: type to filter,
    /// Up/Down to select, Enter to run, Escape to close. Preserve an outstanding restoration target when
    /// reopening before the close callback runs while focus is still inside the palette.
    /// </summary>
    private void EnterPalette(long transition)
    {
        var invoker = Keyboard.FocusedElement;
        if (invoker is UIElement candidate && candidate.IsVisible && !IsInsidePalette(candidate))
            _focusBeforeOpen = invoker;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (transition != _focusTransition || _viewModel?.IsOpen != true)
            {
                return;
            }

            SearchBox.Focus();
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        }));
    }

    /// <summary>
    /// Returns focus to the control that opened the palette. A captured element that is gone or collapsed by
    /// the time the palette closes (a screen change, another overlay) is simply not restored, and a palette
    /// that has reopened since is left alone.
    /// </summary>
    private void LeavePalette(long transition)
    {
        var target = _focusBeforeOpen;

        if (target is not UIElement element)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (transition != _focusTransition || _viewModel?.IsOpen == true)
            {
                return;
            }

            _focusBeforeOpen = null;

            if (element.IsVisible && element.IsEnabled && element.Focusable)
            {
                element.Focus();
            }
        }));
    }

    private bool IsInsidePalette(DependencyObject element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, this))
            {
                return true;
            }

            element = element is Visual
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }
}
