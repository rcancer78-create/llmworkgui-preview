using System.Runtime.InteropServices;
using System.Windows;

namespace LLMWorkGUI.App.Services;

/// <summary>WPF clipboard implementation with a few bounded retries for transient clipboard locks.</summary>
public sealed class ClipboardService : IClipboardService
{
    private const int MaxAttempts = 3;

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (COMException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }
}
