using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LLMWorkGUI.Infrastructure.Processes;

/// <summary>
/// Captures existing Windows descendants with retained process handles. Parent PID alone is
/// insufficient: creation time must fall inside the retained parent's lifetime. This is a
/// point-in-time snapshot, not containment of hostile breakaway or already lost ancestry.
/// </summary>
internal static class NativeProcessTreeSnapshot
{
    public static List<Process> Capture(IEnumerable<Process> roots)
    {
        var captured = new List<Process>();
        if (!OperatingSystem.IsWindows()) { return captured; }
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var children = new Dictionary<int, List<int>>();
        if (!NativeMethods.Process32First(snapshot, ref entry))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 18) { throw new Win32Exception(error); } // ERROR_NO_MORE_FILES
            return captured;
        }
        do
        {
            // System.Diagnostics.Process accepts Int32 process IDs. Ignore unrepresentable
            // native entries rather than let an unrelated machine process abort supervision.
            if (entry.ParentProcessId > int.MaxValue || entry.ProcessId > int.MaxValue) { continue; }
            var parent = (int)entry.ParentProcessId;
            if (!children.TryGetValue(parent, out var ids)) { children[parent] = ids = new List<int>(); }
            ids.Add((int)entry.ProcessId);
        } while (NativeMethods.Process32Next(snapshot, ref entry));
        var finalError = Marshal.GetLastWin32Error();
        if (finalError != 18) { throw new Win32Exception(finalError); }

        var pending = new Queue<Process>(roots);
        var seen = new HashSet<int>();
        foreach (var root in pending) { seen.Add(root.Id); }
        try
        {
            while (pending.TryDequeue(out var parent))
            {
                DateTime start;
                DateTime? end;
                try
                {
                    _ = parent.SafeHandle;
                    start = parent.StartTime.ToUniversalTime();
                    end = parent.HasExited ? parent.ExitTime.ToUniversalTime() : null;
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { continue; }
                if (!children.TryGetValue(parent.Id, out var ids)) { continue; }
                foreach (var id in ids)
                {
                    if (!seen.Add(id)) { continue; }
                    Process? child = null;
                    try
                    {
                        child = Process.GetProcessById(id);
                        _ = child.SafeHandle;
                        var born = child.StartTime.ToUniversalTime();
                        if (born < start || (end.HasValue && born > end.Value)) { continue; }
                        captured.Add(child);
                        pending.Enqueue(child);
                        child = null; // Ownership transferred to the returned list.
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { }
                    finally { child?.Dispose(); }
                }
            }
            return captured;
        }
        catch
        {
            foreach (var process in captured) { process.Dispose(); }
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableName;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    }
}
