using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

/// <summary>
/// Session handle for one managed <c>cursor-agent acp</c> process. The process supervisor owns the
/// process tree and liveness; this session owns the JSON-RPC transport bound to the live stdio
/// duplex channel and closes it before the process tree is terminated, so pending requests fail
/// deterministically instead of hanging (ADR-0003 §1, §6.4).
/// </summary>
internal sealed class CursorAcpProcessSession : ICursorAcpProcessSession
{
    private readonly IProtocolProcessSession _processSession;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    private readonly object _cleanupGate = new();
    private Task? _stopTask;
    private Task? _disposeTask;

    public CursorAcpProcessSession(
        string executionId,
        int? processId,
        string workingDirectory,
        string runDirectory,
        IProtocolProcessSession processSession,
        IJsonRpcTransport? transport,
        DateTimeOffset startedAtUtc,
        TimeSpan shutdownTimeout,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        ExecutionId = executionId;
        ProcessId = processId ?? processSession.ProcessId;
        WorkingDirectory = workingDirectory;
        RunDirectory = runDirectory;
        StartedAtUtc = startedAtUtc;
        Transport = transport;

        _processSession = processSession;
        _shutdownTimeout = shutdownTimeout;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string ExecutionId { get; }

    public int? ProcessId { get; }

    public long ProcessGeneration => _processSession.ProcessGeneration;

    public string WorkingDirectory { get; }

    public string RunDirectory { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public IJsonRpcTransport? Transport { get; }

    public bool IsRunning => _processSession.IsRunning;

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Once cleanup is requested, caller cancellation cannot abandon an owned process.
        await AwaitCleanupAsync(GetStopTask()).ConfigureAwait(false);
    }

    private Task GetStopTask()
    {
        lock (_cleanupGate)
        {
            if (_stopTask is null || _stopTask.IsFaulted || _stopTask.IsCanceled)
                _stopTask = Task.Run(StopCoreAsync);
            return _stopTask;
        }
    }

    private async Task AwaitCleanupAsync(Task cleanup)
    {
        try { await cleanup.WaitAsync(_shutdownTimeout, _timeProvider).ConfigureAwait(false); }
        catch (TimeoutException) { throw new ProcessStartupPendingException(cleanup, ProcessTerminationReason.CleanupPending); }
    }

    private async Task StopCoreAsync()
    {
        using var timeout = new CancellationTokenSource(_shutdownTimeout);
        var cancellationToken = timeout.Token;

        // Close the protocol channel first so outstanding JSON-RPC requests are failed with a
        // typed transport-closed result instead of waiting for their request timeout.
        if (Transport is not null)
        {
            try
            {
                await Transport.CloseAsync(cancellationToken).WaitAsync(_shutdownTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Even a stalled transport must not prevent process-tree termination below.
            }
            catch (Exception exception)
            {
                // Independent cancellation belongs to the failed transport close, never to
                // physical process ownership. Always continue the owned tree stop below.
                _logger.LogDebug(
                    exception,
                    "Closing the Cursor ACP transport of execution {ExecutionId} failed.",
                    ExecutionId);
            }
        }

        try
        {
            await _processSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ProcessStartupPendingException pending)
        {
            await pending.CleanupCompletion.ConfigureAwait(false);
        }
        await _processSession.Completion.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Task disposing;
        lock (_cleanupGate)
        {
            if (_disposeTask is null || _disposeTask.IsFaulted || _disposeTask.IsCanceled)
                _disposeTask = Task.Run(DisposeCoreAsync);
            disposing = _disposeTask;
        }
        await AwaitCleanupAsync(disposing).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        await GetStopTask().ConfigureAwait(false);

        try
        {
            if (Transport is not null)
                await Transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await _processSession.DisposeAsync().ConfigureAwait(false); }
            catch (ProcessStartupPendingException pending) { await pending.CleanupCompletion.ConfigureAwait(false); }
        }
    }
}
