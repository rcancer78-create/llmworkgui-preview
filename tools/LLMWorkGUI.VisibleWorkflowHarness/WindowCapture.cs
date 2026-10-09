using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LLMWorkGUI.VisibleWorkflowHarness;

/// <summary>
/// Renders the shown window into the run's evidence directory.
/// <para>
/// This is a WPF render of the live visual tree of a window that is actually on the desktop. It is not an
/// operating-system screen grab and not a synthetic view model: it proves what the shipped layout painted,
/// and it deliberately never writes into <c>tests/LLMWorkGUI.Ui.Tests/Screenshots</c>, so the reference
/// fixtures of the existing synthetic screenshot tests are untouched.
/// </para>
/// </summary>
internal static class WindowCapture
{
    /// <summary>
    /// Renders <paramref name="window"/> at the requested raster scale and returns the written file name.
    /// A DPI above 96 re-renders the same live visual tree at a higher raster scale; it is not an
    /// operating-system per-monitor scaling change, and the report says so.
    /// </summary>
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

    /// <summary>Lets WPF finish layout, bindings and rendering before a step is read or captured.</summary>
    public static async Task SettleAsync()
    {
        await Dispatcher.CurrentDispatcher
            .InvokeAsync(() => { }, DispatcherPriority.ContextIdle)
            .Task.ConfigureAwait(false);

        await Task.Delay(120).ConfigureAwait(true);
    }
}
