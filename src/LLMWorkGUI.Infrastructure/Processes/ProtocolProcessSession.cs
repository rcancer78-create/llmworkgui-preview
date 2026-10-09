using System.ComponentModel;
using System.Diagnostics;
using LLMWorkGUI.Application.Processes;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Infrastructure.Processes;

/// <summary>
/// Session of a long-lived protocol process whose stdio duplex channel is owned by an explicit
/// transport. The supervisor keeps liveness, the bounded standard-error spool and process-tree
/// termination; the caller owns the standard input/output streams (ТЗ §4.2, §6.8, ADR-0003 §1).
/// </summary>
internal sealed class ProtocolProcessSession : IProtocolProcessSession
{
    private readonly Process _process;
    private readonly ProcessTreeTerminator _treeTerminator;
    private readonly CancellationTokenSource _stderrAbort;
    private readonly Task _standardErrorSpoolTask;
    private readonly TimeSpan _gracefulShutdownTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<ProcessExecutionResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _stopRequested;
    private readonly object _cleanupGate = new();
    private Task? _stopTask;
    private Task? _disposeTask;
    private readonly Action? _releaseOwnership;
    private readonly long? _standardErrorSpoolLimitBytes;
    private readonly Func<bool>? _standardErrorCaptureIncomplete;
    private static readonly TimeSpan CallerCleanupBudget = TimeSpan.FromSeconds(45);

    internal ProtocolProcessSession(
        string executionId,
        Process process,
        ProcessTreeTerminator treeTerminator,
        string runDirectory,
        string standardErrorLogPath,
        CancellationTokenSource stderrAbort,
        Task standardErrorSpoolTask,
        Func<long> standardErrorByteCounter,
        DateTimeOffset startedAtUtc,
        TimeSpan gracefulShutdownTimeout,
        TimeProvider timeProvider,
        ILogger logger,
        Action? releaseOwnership = null,
        long? standardErrorSpoolLimitBytes = null,
        Func<bool>? standardErrorCaptureIncomplete = null)
    {
        ExecutionId = executionId;
        ProcessId = process.Id;
        ProcessGeneration = ManagedProcessGeneration.Next();
        RunDirectory = runDirectory;
        StandardErrorLogPath = standardErrorLogPath;
        StartedAtUtc = startedAtUtc;

        _process = process;
        _treeTerminator = treeTerminator;
        _stderrAbort = stderrAbort;
        _standardErrorSpoolTask = standardErrorSpoolTask;
        _gracefulShutdownTimeout = gracefulShutdownTimeout;
        _timeProvider = timeProvider;
        _logger = logger;
        _releaseOwnership = releaseOwnership;
        _standardErrorSpoolLimitBytes = standardErrorSpoolLimitBytes;
        _standardErrorCaptureIncomplete = standardErrorCaptureIncomplete;

        StandardInput = process.StandardInput.BaseStream;
        StandardOutput = process.StandardOutput.BaseStream;

        _ = WatchExitAsync(standardErrorByteCounter);
    }

    public string ExecutionId { get; }

    public int ProcessId { get; }

    public long ProcessGeneration { get; }

    public string RunDirectory { get; }

    public string StandardErrorLogPath { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public Stream StandardInput { get; }

    public Stream StandardOutput { get; }

    public Task<ProcessExecutionResult> Completion => _completion.Task;

    public bool IsRunning => !_completion.Task.IsCompleted && !SafeHasExited();

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopping;
        lock (_cleanupGate) { stopping = _stopTask ??= StopCoreAsync(); }
        await AwaitCleanupAsync(stopping, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        var warned = false;
        while (true)
        {
            try
            {
                await _treeTerminator.TerminateAsync(_process, _gracefulShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
                if (!_process.HasExited) throw new TimeoutException("Protocol process termination is unconfirmed.");
                break;
            }
            catch (Exception)
            {
                if (!warned)
                {
                    _logger.LogWarning("Protocol process cleanup remains unconfirmed; ownership is retained.");
                    warned = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        await ObserveCompletionAsync().ConfigureAwait(false);
    }

    private async Task AwaitCleanupAsync(Task cleanup, CancellationToken cancellationToken)
    {
        try { await cleanup.WaitAsync(CallerCleanupBudget, _timeProvider, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            throw new ProcessStartupPendingException(cleanup, ProcessTerminationReason.CleanupPending);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task disposing;
        lock (_cleanupGate) { disposing = _disposeTask ??= DisposeCoreAsync(); }
        await AwaitCleanupAsync(disposing, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        Task stopping;
        lock (_cleanupGate) { stopping = _stopTask ??= StopCoreAsync(); }
        await stopping.ConfigureAwait(false);
        _stderrAbort.Cancel();
        await ObserveSpoolAsync().ConfigureAwait(false);

        _stderrAbort.Dispose();
        _treeTerminator.Dispose();
        _process.Dispose();
        _releaseOwnership?.Invoke();
    }

    private async Task WatchExitAsync(Func<long> standardErrorByteCounter)
    {
        try
        {
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(
                exception,
                "Waiting for the protocol process of execution {ExecutionId} failed.",
                ExecutionId);
        }

        _stderrAbort.CancelAfter(TimeSpan.FromSeconds(5));
        await ObserveSpoolAsync().ConfigureAwait(false);

        var terminationReason = Volatile.Read(ref _stopRequested) != 0
            ? ProcessTerminationReason.UserCancelled
            : ProcessTerminationReason.None;
        var standardErrorBytes = standardErrorByteCounter();
        var captureIncomplete = _standardErrorCaptureIncomplete?.Invoke() == true;
        var limitExceeded = _standardErrorSpoolLimitBytes is { } limit && standardErrorBytes > limit;

        _completion.TrySetResult(new ProcessExecutionResult
        {
            ExecutionId = ExecutionId,
            ProcessId = ProcessId,
            TerminationReason = terminationReason,
            ExitCode = SafeGetExitCode(),
            StartedAtUtc = StartedAtUtc,
            ExitedAtUtc = _timeProvider.GetUtcNow(),
            RunDirectory = RunDirectory,
            // Standard output belongs to the protocol transport and is deliberately never spooled.
            StandardOutputLogPath = string.Empty,
            StandardErrorLogPath = StandardErrorLogPath,
            StandardOutputBytes = 0,
            StandardErrorBytes = standardErrorBytes,
            StandardOutputHead = string.Empty,
            StandardOutputTail = string.Empty,
            StandardErrorHead = string.Empty,
            StandardErrorTail = string.Empty,
            // Diagnostic truncation does not prove a native turn failed or a process stopped.
            OutputCaptureIncomplete = captureIncomplete,
            OutputLimitExceeded = limitExceeded,
            OutputOverflowed = captureIncomplete || limitExceeded
        });
    }

    private async Task ObserveSpoolAsync()
    {
        try
        {
            await _standardErrorSpoolTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "The standard-error spool of execution {ExecutionId} ended with an error.",
                ExecutionId);
        }
    }

    private async Task ObserveCompletionAsync()
    {
        try
        {
            await _completion.Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private bool SafeHasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private int? SafeGetExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
