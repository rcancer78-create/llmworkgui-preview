using System.Collections.Concurrent;
using System.Diagnostics;
using LLMWorkGUI.Application.Processes;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Processes;

public sealed partial class ProcessSupervisor
{
    private readonly ConcurrentDictionary<StartupOwnership, byte> _pendingStartups = new();
    private readonly ConcurrentDictionary<string, StartupOwnership> _ownedProcesses = new(StringComparer.Ordinal);
    internal int PendingStartupCount => _pendingStartups.Count;
    public Task WaitForStartupCleanupAsync(string executionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        return (_ownedProcesses.TryGetValue(executionId, out var retained)
            ? retained.CleanupCompletion : Task.CompletedTask).WaitAsync(cancellationToken);
    }

    /// <summary>Do not dispose Process concurrently with an in-flight native Start. On abandonment,
    /// a retained cleanup operation becomes the sole owner; successful protocol handoff transfers both handles.</summary>
    private sealed class StartupOwnership(ProcessSupervisor owner, Process process, string executionId) : IDisposable
    {
        private Task _startup = Task.CompletedTask;
        private int _abandoned;
        private bool _detached;
        private readonly TaskCompletionSource _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Process Process { get; } = process;
        public string ExecutionId { get; } = executionId;
        public ProcessTreeTerminator Tree { get; } = ProcessTreeTerminator.Create(owner._logger);
        public Task CleanupCompletion => _cleanup.Task;
        public ProcessTerminationReason PendingReason { get; private set; } = ProcessTerminationReason.StartupPending;
        public bool HasAssociatedProcess
        {
            get { try { _ = Process.Id; return true; } catch (InvalidOperationException) { return false; } }
        }
        public bool StartupCompleted => _startup.IsCompleted;
        public void Track(Task startup) => _startup = startup;
        public void MarkStarted() => PendingReason = ProcessTerminationReason.CleanupPending;
        public void Detach() => _detached = true;
        public void CompleteDetachedOwnership()
        {
            ((ICollection<KeyValuePair<string, StartupOwnership>>)owner._ownedProcesses)
                .Remove(new(ExecutionId, this));
            _cleanup.TrySetResult();
        }

        public void Abandon(ProcessTerminationReason reason = ProcessTerminationReason.StartupPending)
        {
            if (Interlocked.Exchange(ref _abandoned, 1) != 0) return;
            PendingReason = reason;
            owner._pendingStartups.TryAdd(this, 0);
            // The caller is bounded even if native Start or cleanup is blocked. The dictionary and
            // this operation retain ownership; no continuation dereferences an already disposed Process.
            _ = Task.Run(CleanupAsync);
        }

        private async Task CleanupAsync()
        {
            var warned = false;
            // OS association can become observable while native Start is still blocked. Stop
            // that exact process through a separate handle; the worker keeps its original
            // Process/Tree and retry reservation until its native call actually settles.
            while (!_startup.IsCompleted)
            {
                if (HasAssociatedProcess)
                {
                    try
                    {
                        using var observed = System.Diagnostics.Process.GetProcessById(Process.Id);
                        if (observed.StartTime.ToUniversalTime() != Process.StartTime.ToUniversalTime())
                            throw new InvalidOperationException("Startup process identity changed before cleanup.");
                        Tree.TryAssign(observed); // Capture descendants before forcing the owned tree.
                        TryKillQuietly(observed);
                        await Tree.TerminateAsync(observed, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
                        if (!observed.HasExited) throw new TimeoutException("Late associated process termination is unconfirmed.");
                    }
                    catch (Exception)
                    {
                        if (!warned)
                        {
                            owner._logger.LogWarning("Late startup remains unconfirmed; native handle ownership and retry exclusion are retained.");
                            warned = true;
                        }
                    }
                }
                await Task.WhenAny(_startup, Task.Delay(TimeSpan.FromMilliseconds(100))).ConfigureAwait(false);
            }
            try { await _startup.ConfigureAwait(false); }
            catch (Exception) { /* Observe startup failure; it may have happened after OS creation. */ }

            while (true)
            {
                try
                {
                    // An unassociated Process proves that Start never supplied an OS process.
                    try { _ = Process.Id; }
                    catch (InvalidOperationException) { break; }
                    if (_startup.IsFaulted || _startup.IsCanceled) Tree.TryAssign(Process);
                    // Abandoned startup has no caller-owned protocol to shut down gracefully.
                    // Capture descendants first, then force this owned tree without a grace delay.
                    TryKillQuietly(Process);
                    await Tree.TerminateAsync(Process, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
                    if (!Process.HasExited) throw new TimeoutException("Owned process termination remains unconfirmed.");
                    break;
                }
                catch (Exception)
                {
                    if (!warned)
                    {
                        owner._logger.LogWarning("Late process startup cleanup is unconfirmed; ownership and retry exclusion remain active.");
                        warned = true;
                    }
                    // Retry only cleanup of this owned process, never the native request/start.
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
            }
            Tree.Dispose();
            Process.Dispose();
            owner._pendingStartups.TryRemove(this, out _);
            CompleteDetachedOwnership();
        }

        public void Dispose()
        {
            if (_detached || Volatile.Read(ref _abandoned) != 0) return;
            if (!_startup.IsCompleted) { Abandon(); return; }
            // A normal supervised turn already awaited exit/tree cleanup. On a settled startup failure,
            // closing the owned job also prevents a created process from escaping.
            Tree.Dispose();
            Process.Dispose();
            CompleteDetachedOwnership();
        }
    }
}
