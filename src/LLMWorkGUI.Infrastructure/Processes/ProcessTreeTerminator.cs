using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace LLMWorkGUI.Infrastructure.Processes;

internal sealed class ProcessTreeTerminator : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private static readonly TimeSpan ExternalToolTimeout = TimeSpan.FromSeconds(15);

    private readonly SafeFileHandle _jobHandle;
    private readonly ILogger _logger;
    private readonly List<Process> _descendants = new();
    private bool _assigned;
    private readonly object _lifetimeGate = new();
    private int _activeOperations;
    private bool _disposed;

    private ProcessTreeTerminator(IntPtr jobHandle, ILogger logger)
    {
        _jobHandle = new SafeFileHandle(jobHandle, ownsHandle: true);
        _logger = logger;
    }

    public static ProcessTreeTerminator Create(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!OperatingSystem.IsWindows())
        {
            return new ProcessTreeTerminator(IntPtr.Zero, logger);
        }

        var jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (jobHandle == IntPtr.Zero)
        {
            logger.LogWarning(
                "Could not create a Windows job object (error {Error}); falling back to in-process tree termination.",
                Marshal.GetLastWin32Error());

            return new ProcessTreeTerminator(IntPtr.Zero, logger);
        }

        var limitInformation = new JobObjectExtendedLimitInformation();
        limitInformation.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(limitInformation, buffer, fDeleteOld: false);

            if (!NativeMethods.SetInformationJobObject(
                    jobHandle,
                    JobObjectExtendedLimitInformationClass,
                    buffer,
                    (uint)length))
            {
                logger.LogWarning(
                    "Could not configure the Windows job object (error {Error}); falling back to in-process tree termination.",
                    Marshal.GetLastWin32Error());

                NativeMethods.CloseHandle(jobHandle);
                return new ProcessTreeTerminator(IntPtr.Zero, logger);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new ProcessTreeTerminator(jobHandle, logger);
    }

    public bool TryAssign(Process process)
    {
        EnterOperation();
        try { return TryAssignCore(process); }
        finally { ExitOperation(); }
    }

    private bool TryAssignCore(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        CaptureExistingDescendants(process);
        if (_jobHandle.IsInvalid)
        {
            return false;
        }

        try
        {
            if (!NativeMethods.AssignProcessToJobObject(_jobHandle, process.SafeHandle))
            {
                _logger.LogWarning(
                    "Could not assign process {ProcessId} to the supervisor job object (error {Error}); falling back to in-process tree termination.",
                    process.Id,
                    Marshal.GetLastWin32Error());

                return false;
            }

            _assigned = true;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public async Task TerminateAsync(
        Process process,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken)
    {
        EnterOperation();
        try { await TerminateCoreAsync(process, gracefulTimeout, cancellationToken).ConfigureAwait(false); }
        finally { ExitOperation(); }
    }

    private async Task TerminateCoreAsync(Process process, TimeSpan gracefulTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);

        // A child can start before its parent is assigned to the job. Capture it while
        // ancestry still exists, before graceful termination can remove its parent.
        CaptureExistingDescendants(process);
        if (!process.HasExited)
        {
            await TryGracefulTerminationAsync(process, gracefulTimeout, cancellationToken)
                .ConfigureAwait(false);
        }

        Process[] descendants;
        lock (_lifetimeGate) { descendants = _descendants.ToArray(); }
        foreach (var descendant in descendants.Reverse())
        {
            try
            {
                if (!descendant.HasExited) { descendant.Kill(entireProcessTree: true); }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                _logger.LogWarning("Captured descendant cleanup failed with {ExceptionType}.", exception.GetType().Name);
            }
        }
        if (!_jobHandle.IsInvalid && _assigned)
        {
            NativeMethods.TerminateJobObject(_jobHandle, 1);
        }

        if (!await WaitForExitAsync(process, ExternalToolTimeout, cancellationToken).ConfigureAwait(false))
        {
            ForceKillTree(process);
            if (!await WaitForExitAsync(process, ExternalToolTimeout, cancellationToken).ConfigureAwait(false))
                throw new TimeoutException("The root process did not terminate within the cleanup budget.");
        }
        foreach (var descendant in descendants)
        {
            if (!await WaitForExitAsync(descendant, ExternalToolTimeout, cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException("A captured descendant did not terminate within the cleanup budget.");
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            _disposed = true;
            if (_activeOperations == 0) { ReleaseResourcesUnderGate(); }
        }
    }

    private void EnterOperation()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeOperations++;
        }
    }

    private void ExitOperation()
    {
        lock (_lifetimeGate)
        {
            _activeOperations--;
            if (_disposed && _activeOperations == 0) { ReleaseResourcesUnderGate(); }
        }
    }

    private void ReleaseResourcesUnderGate()
    {
        _jobHandle.Dispose();
        foreach (var descendant in _descendants) { descendant.Dispose(); }
        _descendants.Clear();
    }

    private void CaptureExistingDescendants(Process root)
    {
        try
        {
            lock (_lifetimeGate)
            {
                var known = _descendants.Select(process => process.Id).ToHashSet();
                foreach (var process in NativeProcessTreeSnapshot.Capture(new[] { root }.Concat(_descendants)))
                {
                    if (known.Add(process.Id)) { _descendants.Add(process); }
                    else { process.Dispose(); }
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            _logger.LogWarning("Could not capture existing descendants ({ExceptionType}); job and tree-kill fallback remain active.", exception.GetType().Name);
        }
    }

    private async Task TryGracefulTerminationAsync(
        Process process,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken)
    {
        CloseMainWindow(process);
        Process[] descendants;
        lock (_lifetimeGate) { descendants = _descendants.ToArray(); }
        foreach (var descendant in descendants)
        {
            CloseMainWindow(descendant);
        }

        if (gracefulTimeout > TimeSpan.Zero)
        {
            await WaitForExitAsync(process, gracefulTimeout, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void CloseMainWindow(Process process)
    {
        try
        {
            if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero)
            {
                process.CloseMainWindow();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private void ForceKillTree(Process process)
    {
        KillTree(process);
        Process[] descendants;
        lock (_lifetimeGate) { descendants = _descendants.ToArray(); }
        foreach (var descendant in descendants.Reverse())
        {
            KillTree(descendant);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return SafeHasExited(process) is true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool? SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(
            IntPtr hJob,
            int jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(SafeFileHandle hJob, SafeProcessHandle hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
