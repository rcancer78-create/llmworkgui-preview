using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LLMWorkGUI.App.Shell;

namespace LLMWorkGUI.App.Views;

public partial class UnifiedWorkspaceShellView : UserControl
{
    private bool _isLayoutRestored;
    private INotifyPropertyChanged? _shell;
    private IInputElement? _focusBeforeActivityCenter;
    private long _activityTransition;

    public UnifiedWorkspaceShellView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, args) =>
        {
            if (DataContext is UnifiedWorkspaceShellViewModel shell)
                shell.SetViewportWidth(args.NewSize.Width);
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        HookShell(e.NewValue as INotifyPropertyChanged);

    /// <summary>Restores the persisted geometry once the view is in the tree.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookShell(DataContext as INotifyPropertyChanged);

        if (_isLayoutRestored || DataContext is not UnifiedWorkspaceShellViewModel viewModel)
        {
            return;
        }

        _isLayoutRestored = true;
        viewModel.ApplyPersistedLayout();
        viewModel.SetViewportWidth(ActualWidth);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HookShell(null);

        if (DataContext is UnifiedWorkspaceShellViewModel viewModel)
        {
            viewModel.PersistLayout();
        }
    }

    /// <summary>
    /// Single attach/detach point for the shell's property subscription, so an unload/reload of this retained
    /// view cannot leave the subscription missing (focus flow dead) or doubled (focus moved twice).
    /// </summary>
    private void HookShell(INotifyPropertyChanged? shell)
    {
        if (ReferenceEquals(_shell, shell))
        {
            return;
        }

        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
        }

        _shell = shell;

        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
        }

        _focusBeforeActivityCenter = null;
        _activityTransition++;
    }

    /// <summary>
    /// The Activity Center covers the shell, so keyboard focus has to follow it in and come back out. Leaving
    /// focus on the invoker underneath the overlay is what a keyboard operator cannot recover from.
    /// <para>
    /// Each open and each close starts a new transition. Focus moves in a deferred pass, so an open/close/open
    /// cycle inside one dispatcher turn leaves older passes behind: they abort instead of capturing the
    /// overlay itself as the invoker or handing focus back after the overlay has reopened.
    /// </para>
    /// </summary>
    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UnifiedWorkspaceShellViewModel.IsActivityCenterOpen)
            || sender is not UnifiedWorkspaceShellViewModel shell)
        {
            return;
        }

        var transition = ++_activityTransition;

        if (shell.IsActivityCenterOpen)
        {
            EnterActivityCenter(transition);
        }
        else
        {
            LeaveActivityCenter(transition);
        }
    }

    /// <summary>
    /// Moves focus onto the overlay's close control once the overlay is visible. The invoker is captured in
    /// the same deferred pass, after any palette hand-back queued earlier has run.
    /// </summary>
    private void EnterActivityCenter(long transition) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (transition != _activityTransition || IsActivityCenterOpen != true)
            {
                return;
            }

            var invoker = Keyboard.FocusedElement;

            // The overlay's own controls cannot be handed back to, so only an outside invoker is kept.
            _focusBeforeActivityCenter = invoker is UIElement candidate
                                         && candidate.IsVisible
                                         && !IsInsideActivityCenter(candidate)
                ? invoker
                : null;

            if (CloseActivityCenterButton.IsVisible
                && CloseActivityCenterButton.IsEnabled
                && CloseActivityCenterButton.Focusable)
            {
                CloseActivityCenterButton.Focus();
            }
        }));

    /// <summary>
    /// Returns focus to the control that opened the overlay. An invoker that is no longer reachable - the
    /// operator navigated elsewhere, or another overlay took over - is left alone rather than guessed at, and
    /// an overlay that has reopened since is left alone too.
    /// </summary>
    private void LeaveActivityCenter(long transition)
    {
        var target = _focusBeforeActivityCenter;
        _focusBeforeActivityCenter = null;

        if (target is not UIElement element)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (transition != _activityTransition || IsActivityCenterOpen == true)
            {
                return;
            }

            if (element.IsVisible && element.IsEnabled && element.Focusable)
            {
                element.Focus();
            }
        }));
    }

    private bool IsActivityCenterOpen =>
        (DataContext as UnifiedWorkspaceShellViewModel)?.IsActivityCenterOpen == true;

    private bool IsInsideActivityCenter(DependencyObject element) =>
        ReferenceEquals(element, CloseActivityCenterButton) || IsDescendantOf(ActivityCenterOverlay, element);

    private static bool IsDescendantOf(FrameworkElement scope, DependencyObject element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, scope))
            {
                return true;
            }

            element = element is Visual
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private void OnScreenViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep forms usable at the minimum window size; scroll the constrained viewport
        // instead of allowing fixed rows and columns to clip their controls.
        MainScreenContent.Width = Math.Max(680, e.NewSize.Width - SystemParameters.VerticalScrollBarWidth);
        MainScreenContent.Height = Math.Max(500, e.NewSize.Height - SystemParameters.HorizontalScrollBarHeight);
    }

    /// <summary>
    /// Records the widths the operator actually dragged, so the persisted geometry follows the splitter
    /// instead of the last programmatic value.
    /// </summary>
    private void OnPaneSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is UnifiedWorkspaceShellViewModel viewModel)
        {
            viewModel.SetPaneWidths(LeftPaneColumn.ActualWidth, RightPaneColumn.ActualWidth);
        }
    }
}
