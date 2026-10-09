using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace LLMWorkGUI.VisibleWorkflowHarness;

/// <summary>
/// Read-only snapshots of visible Win32 console windows. No window title or user content is read.
/// A before/after sample cannot prove the absence of transient windows between samples.
/// </summary>
internal static class ConsoleWindowProbe
{
    public static bool HasAttachedConsole => GetConsoleWindow() != IntPtr.Zero;

    public static HashSet<IntPtr> CaptureVisibleConsoleWindows()
    {
        var windows = new HashSet<IntPtr>();
        EnumWindowsCallback callback = (window, _) =>
        {
            if (IsWindowVisible(window))
            {
                var name = new StringBuilder(256);
                if (GetClassName(window, name, name.Capacity) > 0
                    && string.Equals(name.ToString(), "ConsoleWindowClass", StringComparison.Ordinal))
                {
                    windows.Add(window);
                }
            }
            return true;
        };

        if (!EnumWindows(callback, IntPtr.Zero))
        {
            throw new InvalidOperationException("Could not enumerate visible desktop console windows.");
        }
        GC.KeepAlive(callback);
        return windows;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int maximumCount);
}
