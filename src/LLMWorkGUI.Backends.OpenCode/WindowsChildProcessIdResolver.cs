using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LLMWorkGUI.Backends.OpenCode;

public sealed class WindowsChildProcessIdResolver : IProcessIdResolver
{
    private const uint Th32CsSnapProcess = 0x00000002;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static WindowsChildProcessIdResolver Instance { get; } = new();

    public int? ResolveChildProcessId(string executablePath, DateTimeOffset startedAfterUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var expectedNames = ResolveExpectedProcessNames(executablePath);
        var parentProcessId = Environment.ProcessId;
        var earliestStartUtc = startedAfterUtc.UtcDateTime.AddSeconds(-2);

        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);

        if (snapshot == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };

            if (!Process32First(snapshot, ref entry))
            {
                return null;
            }

            int? bestProcessId = null;
            var bestStartUtc = DateTime.MinValue;

            do
            {
                if (entry.th32ParentProcessID != parentProcessId)
                {
                    continue;
                }

                if (entry.szExeFile is null || !expectedNames.Contains(entry.szExeFile))
                {
                    continue;
                }

                DateTime startUtc;

                try
                {
                    using var process = Process.GetProcessById((int)entry.th32ProcessID);
                    startUtc = process.StartTime.ToUniversalTime();
                }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    continue;
                }

                if (startUtc < earliestStartUtc || startUtc < bestStartUtc)
                {
                    continue;
                }

                bestStartUtc = startUtc;
                bestProcessId = (int)entry.th32ProcessID;
            }
            while (Process32Next(snapshot, ref entry));

            return bestProcessId;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static HashSet<string> ResolveExpectedProcessNames(string executablePath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OpenCodeCommandLine.IsBatchFile(executablePath))
        {
            names.Add("cmd.exe");
            return names;
        }

        names.Add(Path.GetFileName(executablePath));
        return names;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? szExeFile;
    }
}
