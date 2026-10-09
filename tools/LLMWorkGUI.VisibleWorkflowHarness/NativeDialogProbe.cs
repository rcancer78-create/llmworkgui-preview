using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LLMWorkGUI.VisibleWorkflowHarness;

internal static class NativeDialogProbe
{
    private delegate bool EnumWindow(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out RectNative rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr handle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr handle, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }

    public static async Task CaptureAndCancelAsync(string path)
    {
        IntPtr dialog = IntPtr.Zero;
        for (var i = 0; i < 100 && dialog == IntPtr.Zero; i++)
        {
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var process);
                if (process != Environment.ProcessId) return true;
                var name = new StringBuilder(128); GetClassName(handle, name, name.Capacity);
                if (name.ToString() == "#32770") dialog = handle;
                return true;
            }, IntPtr.Zero);
            await Task.Delay(100);
        }
        if (dialog == IntPtr.Zero) throw new InvalidOperationException("Native folder picker did not open.");
        try
        {
            await Task.Delay(500);
            GetWindowRect(dialog, out var rect);
            var dc = GetWindowDC(dialog); var memory = CreateCompatibleDC(dc);
            var bitmap = CreateCompatibleBitmap(dc, rect.Right - rect.Left, rect.Bottom - rect.Top);
            var previous = SelectObject(memory, bitmap);
            try
            {
                if (!PrintWindow(dialog, memory, 2)) throw new InvalidOperationException("Native dialog capture failed.");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions())));
                using var stream = File.Create(path); encoder.Save(stream);
            }
            finally { SelectObject(memory, previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(dialog, dc); }
        }
        finally { PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); }
    }
}
