using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Infrastructure.Processes;

public sealed partial class ProcessSupervisor : IProcessSupervisor
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LogDrainTimeout = TimeSpan.FromSeconds(30);

    private readonly ProcessSupervisorOptions _options;
    private readonly StorageOptions _storageOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProcessSupervisor> _logger;
    private readonly Func<Process, bool> _startProcess;
    private readonly Func<Process, Task>? _terminateProcess;
    private readonly Func<string, Stream>? _protocolStandardErrorSinkFactory;
    private readonly Func<string, Stream>? _regularOutputSinkFactory;

    public ProcessSupervisor(
        IOptions<ProcessSupervisorOptions> options,
        StorageOptions storageOptions,
        TimeProvider? timeProvider = null,
        ILogger<ProcessSupervisor>? logger = null)
        : this(options, storageOptions, timeProvider, logger, static process => process.Start())
    {
    }

    internal ProcessSupervisor(IOptions<ProcessSupervisorOptions> options, StorageOptions storageOptions,
        TimeProvider? timeProvider, ILogger<ProcessSupervisor>? logger, Func<Process, bool> startProcess,
        Func<Process, Task>? terminateProcess = null,
        Func<string, Stream>? protocolStandardErrorSinkFactory = null,
        Func<string, Stream>? regularOutputSinkFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storageOptions);

        _options = options.Value;
        _storageOptions = storageOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ProcessSupervisor>.Instance;
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
        _terminateProcess = terminateProcess;
        _protocolStandardErrorSinkFactory = protocolStandardErrorSinkFactory;
        _regularOutputSinkFactory = regularOutputSinkFactory;
    }

    public async Task<ProcessExecutionResult> ExecuteAsync(
        ProcessStartSpecification specification,
        IProgress<ProcessOutputEvent>? outputProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentException.ThrowIfNullOrWhiteSpace(specification.ExecutionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(specification.FileName);
        cancellationToken.ThrowIfCancellationRequested();
        if (specification.StdinPolicy == ProcessStdinPolicy.DirectProtocolTransport)
        {
            throw new ArgumentException(
                "DirectProtocolTransport requires StartProtocolProcessAsync to hand over duplex stream ownership.",
                nameof(specification));
        }

        var timingStarted = Stopwatch.GetTimestamp();
        var startedAt = _timeProvider.GetUtcNow();
        var runDirectory = ResolveRunDirectory(specification);
        Directory.CreateDirectory(runDirectory);

        var standardOutputLogPath = Path.Combine(runDirectory, ProcessSupervisorOptions.StandardOutputFileName);
        var standardErrorLogPath = Path.Combine(runDirectory, ProcessSupervisorOptions.StandardErrorFileName);

        using var startupOwner = new StartupOwnership(this, CreateProcess(specification), specification.ExecutionId);
        var pending = _ownedProcesses.GetOrAdd(specification.ExecutionId, startupOwner);
        if (!ReferenceEquals(pending, startupOwner))
            return CreateStartupFailureResult(specification, startedAt, runDirectory, standardOutputLogPath,
                standardErrorLogPath, "A previous process attempt still owns this execution; retry is forbidden.", null,
                pending.PendingReason);

        var process = startupOwner.Process;

        StartOutcome startOutcome;
        try
        {
            startOutcome = await StartProcessAsync(startupOwner, specification, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (startupOwner.HasAssociatedProcess)
            {
                startupOwner.Abandon();
                return CreateStartupFailureResult(specification, startedAt, runDirectory, standardOutputLogPath,
                    standardErrorLogPath, "Startup was interrupted after process creation; cleanup is pending.", null,
                    ProcessTerminationReason.StartupPending);
            }
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (startupOwner.HasAssociatedProcess)
            {
                startupOwner.Abandon();
                return CreateStartupFailureResult(specification, startedAt, runDirectory, standardOutputLogPath,
                    standardErrorLogPath, "Startup failed after process creation; cleanup is pending.", null,
                    ProcessTerminationReason.StartupPending);
            }
            return CreateStartupFailureResult(
                specification,
                startedAt,
                runDirectory,
                standardOutputLogPath,
                standardErrorLogPath,
                exception.Message,
                exception);
        }

        if (startOutcome == StartOutcome.Pending)
        {
            return CreateStartupFailureResult(
                specification,
                startedAt,
                runDirectory,
                standardOutputLogPath,
                standardErrorLogPath,
                "Process startup did not settle; termination is unconfirmed and cleanup retains ownership.",
                exception: null, ProcessTerminationReason.StartupPending);
        }

        var treeTerminator = startupOwner.Tree;
        startupOwner.MarkStarted();

        try
        {
            LogTiming("started", timingStarted);
            LogTiming("tree-assigned", timingStarted);
            specification.ProcessStarted?.Invoke(ManagedProcessGeneration.Next());
            return await RunSupervisedProcessAsync(
                    process,
                    specification,
                    treeTerminator,
                    outputProgress,
                    startedAt,
                    runDirectory,
                    standardOutputLogPath,
                    standardErrorLogPath,
                    timingStarted,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            startupOwner.Abandon(ProcessTerminationReason.CleanupPending);
            return CreateStartupFailureResult(specification, startedAt, runDirectory, standardOutputLogPath,
                standardErrorLogPath, "Process supervision did not complete; cleanup retains ownership.", null,
                ProcessTerminationReason.CleanupPending);
        }
    }

    public async Task<IProtocolProcessSession> StartProtocolProcessAsync(
        ProcessStartSpecification specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentException.ThrowIfNullOrWhiteSpace(specification.ExecutionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(specification.FileName);
        cancellationToken.ThrowIfCancellationRequested();

        if (specification.StdinPolicy != ProcessStdinPolicy.DirectProtocolTransport)
        {
            throw new ArgumentException(
                "A protocol process must declare ProcessStdinPolicy.DirectProtocolTransport so the " +
                "owning transport keeps the duplex stdio channel.",
                nameof(specification));
        }

        var startedAt = _timeProvider.GetUtcNow();
        var runDirectory = ResolveRunDirectory(specification);
        Directory.CreateDirectory(runDirectory);

        var standardErrorLogPath = Path.Combine(runDirectory, ProcessSupervisorOptions.StandardErrorFileName);

        using var startupOwner = new StartupOwnership(this, CreateProcess(specification), specification.ExecutionId);
        var pending = _ownedProcesses.GetOrAdd(specification.ExecutionId, startupOwner);
        if (!ReferenceEquals(pending, startupOwner)) throw new ProcessStartupPendingException(pending.CleanupCompletion, pending.PendingReason);
        var process = startupOwner.Process;
        ProcessTreeTerminator? treeTerminator = null;
        CancellationTokenSource? stderrAbort = null;

        try
        {
            var startOutcome = await StartProcessAsync(startupOwner, specification, cancellationToken)
                .ConfigureAwait(false);

            if (startOutcome == StartOutcome.Pending)
            {
                throw new ProcessStartupPendingException(startupOwner.CleanupCompletion);
            }

            treeTerminator = startupOwner.Tree;
            startupOwner.MarkStarted();

            stderrAbort = new CancellationTokenSource();
            var standardErrorBytes = 0L;
            var standardErrorCaptureIncomplete = 0;

            var standardErrorSpoolTask = SpoolStandardErrorAsync(
                process.StandardError,
                standardErrorLogPath,
                bytes => Interlocked.Add(ref standardErrorBytes, bytes),
                () => Interlocked.Exchange(ref standardErrorCaptureIncomplete, 1),
                stderrAbort.Token);

            var session = new ProtocolProcessSession(
                specification.ExecutionId,
                process,
                treeTerminator,
                runDirectory,
                standardErrorLogPath,
                stderrAbort,
                standardErrorSpoolTask,
                () => Interlocked.Read(ref standardErrorBytes),
                startedAt,
                _options.GracefulShutdownTimeout,
                _timeProvider,
                _logger,
                startupOwner.CompleteDetachedOwnership,
                standardErrorSpoolLimitBytes: _options.ProtocolStandardErrorSpoolLimitBytes,
                standardErrorCaptureIncomplete: () => Volatile.Read(ref standardErrorCaptureIncomplete) != 0);

            _logger.LogInformation(
                "Started protocol process {ProcessId} for execution {ExecutionId}; the stdio duplex " +
                "channel is owned by the caller transport.",
                session.ProcessId,
                specification.ExecutionId);

            startupOwner.Detach();
            return session;
        }
        catch
        {
            stderrAbort?.Cancel();
            stderrAbort?.Dispose();
            var associated = startupOwner.HasAssociatedProcess;
            startupOwner.Abandon(startupOwner.StartupCompleted ? ProcessTerminationReason.CleanupPending : ProcessTerminationReason.StartupPending);
            if (!startupOwner.StartupCompleted || associated)
                throw new ProcessStartupPendingException(startupOwner.CleanupCompletion, startupOwner.PendingReason);
            throw;
        }
    }

    /// <summary>
    /// Spools the standard-error stream of a protocol process with a bounded writer. Standard output
    /// is never spooled here: it belongs to the protocol transport.
    /// </summary>
    private async Task SpoolStandardErrorAsync(
        StreamReader reader,
        string standardErrorLogPath,
        Action<int> onBytes,
        Action onCaptureIncomplete,
        CancellationToken abortToken)
    {
        Stream? spool = null;
        try
        {
            spool = _protocolStandardErrorSinkFactory?.Invoke(standardErrorLogPath) ?? new FileStream(standardErrorLogPath,
                FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            onCaptureIncomplete();
            _logger.LogWarning("The protocol standard-error sink could not be opened; capture is incomplete and the pipe continues draining.");
        }


        var bufferSize = Math.Max(16, _options.StreamReadBufferSize);
        var buffer = new char[bufferSize];
        var encoded = new byte[Utf8NoBom.GetMaxByteCount(bufferSize)];
        var encoder = Utf8NoBom.GetEncoder();
        var redactor = new DiagnosticSpoolRedactor();
        var written = 0L;
        var prefixComplete = spool is null;

        async Task CaptureAsync(string safeText)
        {
            var safeBytes = Utf8NoBom.GetBytes(safeText);
            var byteCount = safeBytes.Length;
            if (prefixComplete || byteCount == 0) return;
            var available = Math.Max(0L, _options.ProtocolStandardErrorSpoolLimitBytes - written);
            var take = (int)Math.Min(available, byteCount);
            if (take < byteCount)
            {
                // Preserve a valid UTF-8 prefix; never retain part of a multibyte scalar.
                while (take > 0 && (safeBytes[take] & 0xC0) == 0x80) take--;
                prefixComplete = true;
            }
            if (take > 0 && spool is not null)
            {
                try
                {
                    await spool.WriteAsync(safeBytes.AsMemory(0, take), abortToken).ConfigureAwait(false);
                    await spool.FlushAsync(abortToken).ConfigureAwait(false);
                    written += take;
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
                {
                    prefixComplete = true;
                    onCaptureIncomplete();
                    _logger.LogWarning("The protocol standard-error sink failed; capture is incomplete and the pipe continues draining.");
                }
            }
        }

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), abortToken).ConfigureAwait(false);
                if (read == 0)
                {
                    onBytes(encoder.GetBytes(Array.Empty<char>(), 0, 0, encoded, 0, flush: true));
                    await CaptureAsync(redactor.Append([], complete: true)).ConfigureAwait(false);
                    break;
                }
                // Keep decoding/draining after the disk prefix is full. A noisy diagnostic pipe
                // must not block ACP stdout or release ownership of a still-running process.
                onBytes(encoder.GetBytes(buffer, 0, read, encoded, 0, flush: false));
                await CaptureAsync(redactor.Append(buffer.AsSpan(0, read))).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            onCaptureIncomplete();
        }
        catch (IOException)
        {
            onCaptureIncomplete();
        }
        catch (ObjectDisposedException)
        {
            onCaptureIncomplete();
        }
        finally
        {
            if (spool is not null)
            {
                try { await spool.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
                {
                    onCaptureIncomplete();
                    _logger.LogWarning("The protocol standard-error sink could not be closed; capture is incomplete.");
                }
            }
        }
    }

    private async Task<ProcessExecutionResult> RunSupervisedProcessAsync(
        Process process,
        ProcessStartSpecification specification,
        ProcessTreeTerminator treeTerminator,
        IProgress<ProcessOutputEvent>? outputProgress,
        DateTimeOffset startedAt,
        string runDirectory,
        string standardOutputLogPath,
        string standardErrorLogPath,
        long timingStarted,
        CancellationToken cancellationToken)
    {
        var standardOutputCapture = new BoundedOutputCapture(
            _options.OutputMemoryLimitBytes,
            _options.OutputHeadRetentionBytes,
            _options.OutputTailRetentionBytes);
        var standardErrorCapture = new BoundedOutputCapture(
            _options.OutputMemoryLimitBytes,
            _options.OutputHeadRetentionBytes,
            _options.OutputTailRetentionBytes);

        var channel = Channel.CreateBounded<ProcessOutputEvent>(
            new BoundedChannelOptions(_options.OutputChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        using var drainAbortCts = new CancellationTokenSource();
        using var turnTimeoutCts = new CancellationTokenSource();
        using var inactivityTimeoutCts = new CancellationTokenSource();

        if (_options.TurnTimeout is { } turnTimeout)
        {
            turnTimeoutCts.CancelAfter(turnTimeout);
        }

        var inactivityTimeout = _options.InactivityTimeout;
        if (inactivityTimeout is not null)
        {
            inactivityTimeoutCts.CancelAfter(inactivityTimeout.Value);
        }

        using var terminationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            turnTimeoutCts.Token,
            inactivityTimeoutCts.Token);

        Action onActivity = inactivityTimeout is null
            ? static () => { }
            : () => inactivityTimeoutCts.CancelAfter(inactivityTimeout.Value);

        var standardOutputReadTask = ReadStreamAsync(
            process.StandardOutput,
            ProcessStreamKind.StdOut,
            standardOutputLogPath,
            channel.Writer,
            onActivity,
            outputProgress as IProcessOutputCaptureObserver,
            drainAbortCts.Token);

        var standardErrorReadTask = ReadStreamAsync(
            process.StandardError,
            ProcessStreamKind.StdErr,
            standardErrorLogPath,
            channel.Writer,
            onActivity,
            outputProgress as IProcessOutputCaptureObserver,
            drainAbortCts.Token);

        var pumpTask = PumpOutputAsync(
            channel.Reader,
            standardOutputCapture,
            standardErrorCapture,
            outputProgress);

        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var cancellationTask = Task.Delay(Timeout.Infinite, terminationCts.Token);

        var completed = await Task.WhenAny(exitTask, cancellationTask).ConfigureAwait(false);
        LogTiming("exit-or-cancellation-observed", timingStarted);

        ProcessTerminationReason terminationReason;

        if (completed == exitTask)
        {
            await exitTask.ConfigureAwait(false);
            terminationReason = ProcessTerminationReason.None;
        }
        else
        {
            terminationReason = ResolveCancellationReason(
                cancellationToken,
                turnTimeoutCts,
                inactivityTimeoutCts);
        }

        try
        {
            await (_terminateProcess?.Invoke(process)
                ?? treeTerminator.TerminateAsync(process, _options.GracefulShutdownTimeout, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            drainAbortCts.Cancel();
            channel.Writer.TryComplete();
            _ = Task.WhenAll(exitTask, standardOutputReadTask, standardErrorReadTask, pumpTask).ContinueWith(
                static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _logger.LogWarning(
                exception,
                "Failed to terminate the process tree for execution {ExecutionId}.",
                specification.ExecutionId);
            throw; // The outer owner must retain CleanupPending; never enter an unbounded exit drain here.
        }

        LogTiming("tree-cleanup-finished", timingStarted);
        await DrainOutputAsync(
                exitTask,
                standardOutputReadTask,
                standardErrorReadTask,
                channel,
                pumpTask,
                drainAbortCts)
            .ConfigureAwait(false);
        LogTiming("output-drained", timingStarted);

        var captureIncomplete = await standardOutputReadTask.ConfigureAwait(false)
            | await standardErrorReadTask.ConfigureAwait(false);

        if (terminationReason == ProcessTerminationReason.None
            && (standardOutputCapture.Overflowed || standardErrorCapture.Overflowed || captureIncomplete))
        {
            terminationReason = ProcessTerminationReason.BufferOverflow;
        }

        var exitedAt = _timeProvider.GetUtcNow();

        return new ProcessExecutionResult
        {
            ExecutionId = specification.ExecutionId,
            ProcessId = process.Id,
            TerminationReason = terminationReason,
            ExitCode = SafeGetExitCode(process),
            StartedAtUtc = startedAt,
            ExitedAtUtc = exitedAt,
            RunDirectory = runDirectory,
            StandardOutputLogPath = standardOutputLogPath,
            StandardErrorLogPath = standardErrorLogPath,
            StandardOutputBytes = standardOutputCapture.TotalBytes,
            StandardErrorBytes = standardErrorCapture.TotalBytes,
            StandardOutputHead = standardOutputCapture.HeadText,
            StandardOutputTail = standardOutputCapture.TailText,
            StandardErrorHead = standardErrorCapture.HeadText,
            StandardErrorTail = standardErrorCapture.TailText,
            OutputOverflowed = standardOutputCapture.Overflowed || standardErrorCapture.Overflowed || captureIncomplete,
            OutputCaptureIncomplete = captureIncomplete,
            OutputLimitExceeded = standardOutputCapture.Overflowed || standardErrorCapture.Overflowed
        };
    }

    private async Task<StartOutcome> StartProcessAsync(
        StartupOwnership startupOwner,
        ProcessStartSpecification specification,
        CancellationToken cancellationToken)
    {
        var process = startupOwner.Process;
        // Validate and arm deadline infrastructure before any native launch can begin.
        using var startupCts = new CancellationTokenSource(_options.StartupTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupCts.Token);
        cancellationToken.ThrowIfCancellationRequested();
        var timingStarted = Stopwatch.GetTimestamp();
        LogTiming("start-queued", timingStarted);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startTask = completion.Task;
        startupOwner.Track(startTask);
        try
        {
            _ = Task.Run(() =>
            {
                try
                {
                    LogTiming("start-worker-entered", timingStarted);
                    var started = _startProcess(process);
                    startupOwner.Tree.TryAssign(process);
                    ApplyStdinPolicy(process, specification.StdinPolicy);
                    LogTiming("start-worker-returned", timingStarted);
                    completion.TrySetResult(started);
                }
                catch (Exception exception) { completion.TrySetException(exception); }
            }, CancellationToken.None);
        }
        catch (Exception exception) { completion.TrySetException(exception); }

        var completed = await Task.WhenAny(
                startTask,
                Task.Delay(Timeout.Infinite, linkedCts.Token))
            .ConfigureAwait(false);

        // The caller may cancel while process.Start() is still in flight. Losing the race is not
        // evidence that the process failed to start: give the already-running start a bounded grace
        // window so a started process is always reported as Started and then terminated through the
        // normal supervised path, which yields a deterministic result instead of an exception.
        if (completed != startTask && startTask.IsCompleted)
        {
            completed = startTask;
        }
        else if (completed != startTask && cancellationToken.IsCancellationRequested)
        {
            try
            {
                await startTask.WaitAsync(_options.StartupGraceWindow, CancellationToken.None)
                    .ConfigureAwait(false);

                completed = startTask;
            }
            catch (TimeoutException)
            {
                // The native call did not settle. This says nothing about OS process creation.
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A genuine start failure is reported by awaiting the task below.
                completed = startTask;
            }
        }

        if (completed == startTask)
        {
            await startTask.ConfigureAwait(false);
            LogTiming("stdin-policy-applied", timingStarted);
            return StartOutcome.Started;
        }

        startupOwner.Abandon();
        return StartOutcome.Pending;
    }

    // Durations and scheduler counters only: never log command text, paths, stdin or output.
    // These phases distinguish child startup/EOF from scheduling, tree cleanup and stream drain.
    private void LogTiming(string phase, long started)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
        _logger.LogDebug("Process timing {Phase}: {ElapsedMilliseconds} ms; pool threads {PoolThreads}, pending {PendingWorkItems}.",
            phase, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount);
    }

    private async Task<bool> ReadStreamAsync(
        StreamReader reader,
        ProcessStreamKind streamKind,
        string spoolPath,
        ChannelWriter<ProcessOutputEvent> channelWriter,
        Action onActivity,
        IProcessOutputCaptureObserver? captureObserver,
        CancellationToken abortToken)
    {
        var bufferSize = Math.Max(16, _options.StreamReadBufferSize);
        var buffer = new char[bufferSize];
        Stream? spoolStream = null;
        StreamWriter? spoolWriter = null;
        var captureIncomplete = false;
        var redactor = new DiagnosticSpoolRedactor();

        void MarkCaptureIncomplete()
        {
            if (captureIncomplete) return;
            captureIncomplete = true;
            // Required host retirement cannot depend on a diagnostic logger accepting a row.
            try { captureObserver?.OnOutputCaptureFailure(streamKind); }
            catch (Exception exception)
            {
                try
                {
                    _logger.LogWarning("A process capture observer failed ({ExceptionType}); capture remains incomplete and drainage remains owned.",
                        exception.GetType().Name);
                }
                catch (Exception) { } // Diagnostic delivery cannot prevent owned pipe drainage.
            }
            // Capture failure is not necessarily a byte-limit overflow. Keep private paths,
            // output and disk exception details out of this single metadata-only warning.
            try { _logger.LogWarning("The {StreamKind} process output spool is incomplete; output continues draining.", streamKind); }
            catch (Exception) { } // Capture metadata/ownership is retained independently of logging.
        }

        async Task RetireSpoolAsync()
        {
            var writer = spoolWriter;
            var stream = spoolStream;
            spoolWriter = null;
            spoolStream = null;
            try
            {
                if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                MarkCaptureIncomplete();
            }
            finally
            {
                // The writer leaves the underlying stream open so its failed implicit Flush
                // cannot skip release of the actual owned file. This reader owns both until join.
                if (stream is not null)
                {
                    try { await stream.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
                    { MarkCaptureIncomplete(); }
                }
            }
        }

        try
        {
            spoolStream = CreateSpoolStream(spoolPath);
            spoolWriter = new StreamWriter(spoolStream, Utf8NoBom, bufferSize: 1024, leaveOpen: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            MarkCaptureIncomplete();
            await RetireSpoolAsync().ConfigureAwait(false);
        }

        async Task WriteSafeSpoolAsync(string safeText)
        {
            if (spoolWriter is null || safeText.Length == 0) return;
            try
            {
                await spoolWriter.WriteAsync(safeText.AsMemory(), abortToken).ConfigureAwait(false);
                await spoolWriter.FlushAsync(abortToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                MarkCaptureIncomplete();
                await RetireSpoolAsync().ConfigureAwait(false);
            }
        }

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), abortToken).ConfigureAwait(false);
                if (read == 0)
                {
                    await WriteSafeSpoolAsync(redactor.Append([], complete: true)).ConfigureAwait(false);
                    break;
                }

                var text = new string(buffer, 0, read);

                await WriteSafeSpoolAsync(redactor.Append(text)).ConfigureAwait(false);

                var outputEvent = new ProcessOutputEvent(
                    streamKind,
                    text,
                    _timeProvider.GetUtcNow(),
                    Utf8NoBom.GetByteCount(text));

                await channelWriter.WriteAsync(outputEvent, abortToken).ConfigureAwait(false);
                onActivity();
            }
        }
        catch (OperationCanceledException)
        {
            MarkCaptureIncomplete();
        }
        catch (IOException)
        {
            MarkCaptureIncomplete();
        }
        catch (ObjectDisposedException)
        {
            MarkCaptureIncomplete();
        }
        finally { await RetireSpoolAsync().ConfigureAwait(false); }
        return captureIncomplete;
    }

    private async Task PumpOutputAsync(
        ChannelReader<ProcessOutputEvent> channelReader,
        BoundedOutputCapture standardOutputCapture,
        BoundedOutputCapture standardErrorCapture,
        IProgress<ProcessOutputEvent>? outputProgress)
    {
        await foreach (var outputEvent in channelReader.ReadAllAsync().ConfigureAwait(false))
        {
            var capture = outputEvent.StreamKind == ProcessStreamKind.StdOut
                ? standardOutputCapture
                : standardErrorCapture;

            capture.Append(outputEvent.Text, outputEvent.BytesCount);

            if (outputProgress is null)
            {
                continue;
            }

            try
            {
                outputProgress.Report(outputEvent);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "A process output progress callback failed.");
            }
        }
    }

    private async Task DrainOutputAsync(
        Task exitTask,
        Task standardOutputReadTask,
        Task standardErrorReadTask,
        Channel<ProcessOutputEvent> channel,
        Task pumpTask,
        CancellationTokenSource drainAbortCts)
    {
        var drainTask = DrainOutputCoreAsync(
            exitTask,
            standardOutputReadTask,
            standardErrorReadTask,
            channel,
            pumpTask);

        if (await Task.WhenAny(drainTask, Task.Delay(LogDrainTimeout)).ConfigureAwait(false) != drainTask)
        {
            drainAbortCts.Cancel();
        }

        try
        {
            await drainTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to drain process output streams.");
        }
    }

    private static async Task DrainOutputCoreAsync(
        Task exitTask,
        Task standardOutputReadTask,
        Task standardErrorReadTask,
        Channel<ProcessOutputEvent> channel,
        Task pumpTask)
    {
        await exitTask.ConfigureAwait(false);

        try
        {
            await Task.WhenAll(standardOutputReadTask, standardErrorReadTask).ConfigureAwait(false);
        }
        catch (Exception) when (standardOutputReadTask.IsCanceled || standardErrorReadTask.IsCanceled)
        {
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        await pumpTask.ConfigureAwait(false);
    }

    private static ProcessTerminationReason ResolveCancellationReason(
        CancellationToken cancellationToken,
        CancellationTokenSource turnTimeoutCts,
        CancellationTokenSource inactivityTimeoutCts)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ProcessTerminationReason.UserCancelled;
        }

        if (turnTimeoutCts.IsCancellationRequested)
        {
            return ProcessTerminationReason.TurnTimeout;
        }

        if (inactivityTimeoutCts.IsCancellationRequested)
        {
            return ProcessTerminationReason.InactivityTimeout;
        }

        return ProcessTerminationReason.UserCancelled;
    }

    private ProcessExecutionResult CreateStartupFailureResult(
        ProcessStartSpecification specification,
        DateTimeOffset startedAt,
        string runDirectory,
        string standardOutputLogPath,
        string standardErrorLogPath,
        string failureMessage,
        Exception? exception,
        ProcessTerminationReason reason = ProcessTerminationReason.StartupTimeout)
    {
        // A pending result must survive the I/O failure that caused cleanup in the first place.
        // It also must not truncate or fabricate logs for a still-owned process.
        if (reason == ProcessTerminationReason.StartupTimeout)
        {
            EnsureEmptyLogFile(standardOutputLogPath);
            EnsureEmptyLogFile(standardErrorLogPath);
        }

        if (exception is null)
        {
            _logger.LogWarning(
                "Process execution {ExecutionId} has no confirmed terminal result: {FailureMessage}",
                specification.ExecutionId,
                failureMessage);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "Process execution {ExecutionId} failed to start: {FailureMessage}",
                specification.ExecutionId,
                failureMessage);
        }

        return new ProcessExecutionResult
        {
            ExecutionId = specification.ExecutionId,
            ProcessId = null,
            TerminationReason = reason,
            ExitCode = null,
            StartedAtUtc = startedAt,
            ExitedAtUtc = _timeProvider.GetUtcNow(),
            RunDirectory = runDirectory,
            StandardOutputLogPath = standardOutputLogPath,
            StandardErrorLogPath = standardErrorLogPath,
            StandardOutputBytes = 0,
            StandardErrorBytes = 0,
            StandardOutputHead = string.Empty,
            StandardOutputTail = string.Empty,
            StandardErrorHead = string.Empty,
            StandardErrorTail = string.Empty,
            OutputOverflowed = false,
            FailureMessage = failureMessage
        };
    }

    private string ResolveRunDirectory(ProcessStartSpecification specification)
    {
        var dataRoot = string.IsNullOrWhiteSpace(_storageOptions.AppDataDirectory)
            ? AppDataPaths.DefaultRootDirectory
            : Path.GetFullPath(_storageOptions.AppDataDirectory);

        var runDirectory = AppDataPaths.GetRunDirectory(dataRoot, specification.ExecutionId);

        if (!string.IsNullOrWhiteSpace(specification.WorkingDirectory))
        {
            var checkoutRoot = Path.GetFullPath(specification.WorkingDirectory);
            if (IsInsideDirectory(runDirectory, checkoutRoot))
            {
                throw new InvalidOperationException(
                    "Run directories must be created outside the project checkout root.");
            }
        }

        return runDirectory;
    }

    private static bool IsInsideDirectory(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        if (string.Equals(normalizedCandidate, normalizedRoot, comparison))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static Process CreateProcess(ProcessStartSpecification specification)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = specification.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        if (!string.IsNullOrWhiteSpace(specification.WorkingDirectory))
        {
            startInfo.WorkingDirectory = specification.WorkingDirectory;
        }

        foreach (var argument in specification.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!specification.InheritEnvironment) startInfo.Environment.Clear();
        foreach (var variable in specification.EnvironmentVariables)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        return new Process { StartInfo = startInfo };
    }

    private static void ApplyStdinPolicy(Process process, ProcessStdinPolicy stdinPolicy)
    {
        if (stdinPolicy is ProcessStdinPolicy.Closed or ProcessStdinPolicy.Null)
        {
            process.StandardInput.Close();
        }
    }

    private Stream CreateSpoolStream(string path)
    {
        return _regularOutputSinkFactory?.Invoke(path) ?? new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    }

    private static void EnsureEmptyLogFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    private static int? SafeGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void TryKillQuietly(Process process)
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

    private enum StartOutcome
    {
        Started,
        Pending
    }
}
