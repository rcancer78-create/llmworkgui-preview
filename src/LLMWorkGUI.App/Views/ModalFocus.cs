using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LLMWorkGUI.App.Views;

/// <summary>Moves keyboard focus into an overlay and restores its reachable invoker when it closes.</summary>
public static class ModalFocus
{
    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.RegisterAttached(
        "CloseCommand", typeof(ICommand), typeof(ModalFocus), new PropertyMetadata(null, OnCloseCommandChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(FocusState), typeof(ModalFocus));

    public static ICommand? GetCloseCommand(DependencyObject element) => (ICommand?)element.GetValue(CloseCommandProperty);
    public static void SetCloseCommand(DependencyObject element, ICommand? value) => element.SetValue(CloseCommandProperty, value);

    private static void OnCloseCommandChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement scope) return;
        if (scope.GetValue(StateProperty) is FocusState previous) previous.Detach();
        scope.ClearValue(StateProperty);
        if (args.NewValue is ICommand) scope.SetValue(StateProperty, new FocusState(scope));
    }

    private sealed class FocusState
    {
        private readonly FrameworkElement _scope;
        private UIElement? _invoker;
        private long _transition;

        public FocusState(FrameworkElement scope)
        {
            _scope = scope;
            KeyboardNavigation.SetTabNavigation(scope, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(scope, KeyboardNavigationMode.Cycle);
            scope.IsVisibleChanged += OnVisibilityChanged;
            scope.Loaded += OnLoaded;
            scope.PreviewKeyDown += OnKeyDown;
            if (scope.IsLoaded && scope.IsVisible) Transition();
        }

        public void Detach()
        {
            ++_transition;
            _scope.IsVisibleChanged -= OnVisibilityChanged;
            _scope.Loaded -= OnLoaded;
            _scope.PreviewKeyDown -= OnKeyDown;
            _invoker = null;
        }

        private void OnLoaded(object sender, RoutedEventArgs args) { if (_scope.IsVisible) Transition(); }
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Transition();

        private void Transition()
        {
            var transition = ++_transition;
            _scope.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (transition != _transition) return;
                if (_scope.IsVisible && _scope.IsLoaded)
                {
                    if (Keyboard.FocusedElement is DependencyObject focused && IsInside(focused)) return;
                    _invoker = Keyboard.FocusedElement as UIElement;
                    FocusFirst(_scope);
                }
                else
                {
                    var target = _invoker;
                    _invoker = null;
                    if (target is { IsVisible: true, IsEnabled: true, Focusable: true }) target.Focus();
                }
            }));
        }

        private bool IsInside(DependencyObject element)
        {
            for (DependencyObject? current = element; current is not null;
                 current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                if (ReferenceEquals(current, _scope)) return true;
            return false;
        }

        private static bool FocusFirst(DependencyObject element)
        {
            if (element is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } control && control.Focus()) return true;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                if (FocusFirst(VisualTreeHelper.GetChild(element, index))) return true;
            return false;
        }

        private void OnKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            var command = GetCloseCommand(_scope);
            if (command?.CanExecute(null) == true) command.Execute(null);
        }
    }
}
