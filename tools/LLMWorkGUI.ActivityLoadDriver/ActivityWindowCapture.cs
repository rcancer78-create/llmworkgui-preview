using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LLMWorkGUI.ActivityLoadDriver;

/// <summary>
/// Renders the shown window into the run's own evidence directory.
/// <para>
/// This is a WPF render of the live visual tree of a window that is actually on the desktop, not an
/// operating-system screen grab and not a synthetic view model: it proves what the shipped layout painted
/// under the load. It never writes into <c>tests/LLMWorkGUI.Ui.Tests/Screenshots</c>, so the reference
/// fixtures of the shipped visual tests are untouched - the driver refuses a screenshot directory outside
/// its own run root for exactly that reason.
/// </para>
/// </summary>
internal static class ActivityWindowCapture
{
    /// <summary>
    /// The dispatcher of the shown window. The walk's continuations move to the thread pool as soon as an
    /// await captures no synchronization context, and on a pool thread <c>Dispatcher.CurrentDispatcher</c>
    /// is a brand new, never-pumped dispatcher. Everything that settles the UI has to name the real one
    /// explicitly, otherwise "wait for the UI to catch up" silently degrades into a bare sleep.
    /// </summary>
    public static Dispatcher UiDispatcher { get; set; } = Dispatcher.CurrentDispatcher;

    public static string Capture(Window window, string directory, string stem, double dpi = 96d)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);

        Directory.CreateDirectory(directory);

        var logicalWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var logicalHeight = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        var scale = dpi / 96d;
        var pixelWidth = (int)Math.Max(1d, Math.Ceiling(logicalWidth * scale));
        var pixelHeight = (int)Math.Max(1d, Math.Ceiling(logicalHeight * scale));

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var path = Path.Combine(directory, stem + ".png");

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);

        return path;
    }

    /// <summary>
    /// Lets WPF finish layout, bindings and rendering before a step is read or captured.
    /// <para>
    /// Bounded on purpose. A settle that awaits <see cref="DispatcherPriority.ContextIdle"/> can starve:
    /// ContextIdle is only reached when the queue holds nothing of higher priority, and a screen that keeps
    /// re-posting its own refresh work at <see cref="DispatcherPriority.Background"/> never gets there. An
    /// unbounded settle would turn that into a hang that looks exactly like the bug being investigated. The
    /// wait is therefore raced against a budget, and the elapsed time is reported so a slow settle is
    /// visible in the trace instead of silently inflating the run.
    /// </para>
    /// </summary>
    public static async Task SettleAsync(TimeSpan? budget = null)
    {
        var dispatcher = UiDispatcher;
        var limit = budget ?? TimeSpan.FromSeconds(2);
        var pending = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await pending.Task.WaitAsync(limit).ConfigureAwait(false);
            // Allow the completed layout/binding pass to become visible before capture.
            await Task.Delay(150).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            pending.Abort();
            throw;
        }
        finally
        {
            elapsed.Stop();
            LastSettleMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>Duration of the most recent settle, for the trace.</summary>
    public static double LastSettleMilliseconds { get; private set; }

    /// <summary>
    /// Runs an action on the UI thread and waits for it. Every mutation of a bound control and every read
    /// of a live view-model property goes through here, so the driver never touches WPF state from a pool
    /// thread.
    /// </summary>
    public static async Task<T> OnUiAsync<T>(Func<T> action)
    {
        var dispatcher = UiDispatcher;

        if (dispatcher.CheckAccess())
        {
            return action();
        }

        return await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
    }

    /// <summary>Run form of <see cref="OnUiAsync{T}"/>.</summary>
    public static async Task OnUiAsync(Action action)
    {
        var dispatcher = UiDispatcher;

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
    }
}
