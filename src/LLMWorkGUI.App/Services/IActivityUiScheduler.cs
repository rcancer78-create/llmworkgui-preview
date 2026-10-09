namespace LLMWorkGUI.App.Services;

/// <summary>
/// The UI scheduling seam used by the Activity Center to coalesce stream updates.
/// <para>
/// The normative profile of ТЗ §9.2 pushes 50 events per second into a live screen. A screen that
/// re-queried per event would do 50 queries and 50 full list rebuilds per second, which is both the
/// latency and the memory failure the criterion is about. The view model therefore never refreshes
/// inside the append: it posts one coalesced refresh, and this abstraction is the only thing that knows
/// which thread that refresh runs on.
/// </para>
/// <para>
/// It is an interface rather than a direct <see cref="System.Windows.Threading.Dispatcher"/> call so
/// the coalescing, staleness and cancellation behaviour is testable headlessly, without a message loop.
/// </para>
/// </summary>
public interface IActivityUiScheduler
{
    /// <summary>True when the caller already runs on the UI thread.</summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread. When the caller is already on it and no delay is
    /// requested the action runs inline, so a test or a synchronous composition observes the same result
    /// the screen would.
    /// </summary>
    void Post(Action action, TimeSpan? delay = null);

    /// <summary>Invokes <paramref name="action"/> on the UI thread and waits for it to finish.</summary>
    void Send(Action action);
}

/// <summary>
/// Immediate scheduler: every post runs inline on the calling thread. This is the correct default for
/// the headless compositions (unit tests, the DI-only integration graphs) where there is no message loop
/// to post to, and it keeps those graphs deterministic.
/// </summary>
public sealed class ImmediateActivityUiScheduler : IActivityUiScheduler
{
    public static ImmediateActivityUiScheduler Instance { get; } = new();

    public bool IsOnUiThread => true;

    public void Post(Action action, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        action();
    }

    public void Send(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        action();
    }
}
