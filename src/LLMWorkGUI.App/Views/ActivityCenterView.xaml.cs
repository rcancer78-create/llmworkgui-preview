using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LLMWorkGUI.App.ViewModels;

namespace LLMWorkGUI.App.Views;

/// <summary>
/// Activity Center view. Beside rendering the shipped view model it honors the auto-scroll toggle:
/// when enabled, newly appended events scroll into view unless the operator is inspecting a selection.
/// </summary>
public partial class ActivityCenterView : UserControl
{
    private ActivityCenterViewModel? _viewModel;
    private bool _scrollScheduled;

    public ActivityCenterView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => HookViewModel(DataContext as ActivityCenterViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e) => HookViewModel(null);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        HookViewModel(e.NewValue as ActivityCenterViewModel);

    private void HookViewModel(ActivityCenterViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.Events.CollectionChanged -= OnEventsChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.Events.CollectionChanged += OnEventsChanged;
        }
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel is null
            || !_viewModel.AutoScroll
            || _viewModel.SelectedEvent is not null
            || _viewModel.Events.Count == 0)
        {
            return;
        }

        if (_scrollScheduled) return;
        _scrollScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _scrollScheduled = false;
            if (_viewModel is { AutoScroll: true, SelectedEvent: null } current && current.Events.Count > 0)
            {
                // The page is newest-first. Scroll once after reconciliation, not to the oldest
                // row after every individual insertion and removal in the same batch.
                EventsList.ScrollIntoView(current.Events[0]);
            }
        }));
    }
}
