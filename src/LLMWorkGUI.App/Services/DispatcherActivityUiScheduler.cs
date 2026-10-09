using System.Windows.Threading;

namespace LLMWorkGUI.App.Services;

/// <summary>
/// Production scheduler: marshals the Activity Center refresh onto the WPF dispatcher.
/// <para>
/// This is the "coalesce UI refreshes on the dispatcher" half of the normative requirement. The
/// view model decides *whether* a refresh is needed; this type decides *on which thread* it runs and
/// is the only place in the Activity Center that knows about WPF at all, which is what keeps the
/// coalescing logic unit-testable without a message loop.
/// </para>
/// </summary>
public sealed class DispatcherActivityUiScheduler : IActivityUiScheduler
{
    private readonly Dispatcher _dispatcher;

    public DispatcherActivityUiScheduler(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _dispatcher = dispatcher;
    }

    /// <summary>Builds a scheduler over the dispatcher of the current thread.</summary>
    public static DispatcherActivityUiScheduler ForCurrentThread() =>
        new(Dispatcher.CurrentDispatcher);

    public bool IsOnUiThread => _dispatcher.CheckAccess();

    public void Post(Action action, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess() && delay is null or { Ticks: <= 0 })
        {
            action();
            return;
        }

        if (delay is { } wait && wait > TimeSpan.Zero)
        {
            // The view model rate-limits refreshes; this timer posts the next bounded update
            // while leaving input and rendering at their normal WPF priorities.
            EventHandler? onTick = null;
            var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);

            onTick = (sender, _) =>
            {
                ((DispatcherTimer)sender!).Stop();
                timer.Tick -= onTick;

                try
                {
                    action();
                }
                catch (TaskCanceledException)
                {
                    // The refresh was superseded while it waited; the newer one owns the screen.
                }
            };

            timer.Tick += onTick;
            timer.Interval = wait;
            timer.Start();
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    public void Send(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }
}
