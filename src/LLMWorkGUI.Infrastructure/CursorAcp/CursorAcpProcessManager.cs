using System.Collections.Concurrent;
using LLMWorkGUI.Application.Configuration;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.Abstractions.Processes;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Infrastructure.CursorAcp;

/// <summary>
/// Owns the lifecycle of managed <c>cursor-agent acp</c> processes through the shared process
/// supervisor (ADR-0003 §11, ТЗ §4.2, §6.8). The launch is typed argv only (no shell interpolation),
/// runs hidden (<c>CreateNoWindow</c>), receives a dedicated working/spool directory under the app
/// data root (outside any project checkout), reserves stdin for the JSON-RPC transport
/// (<see cref="ProcessStdinPolicy.DirectProtocolTransport"/>) and terminates the whole process tree
/// on stop. Missing executables and launch failures produce a degraded result, never an unhandled
/// exception and never a CLI print-mode fallback.
/// </summary>
public sealed class CursorAcpProcessManager : ICursorAcpProcessManager
{
    private readonly ICursorExecutableResolver _executableResolver;
    private readonly IProcessSupervisor _processSupervisor;
    private readonly StorageOptions _storageOptions;
    private readonly CursorAcpOptions _options;
    private readonly IJsonRpcTransportFactory? _transportFactory;
    private readonly IProcessIdResolver? _processIdResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CursorAcpProcessManager> _logger;
    private readonly ConcurrentDictionary<string, CursorAcpProcessSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IProtocolProcessSession> _unpublishedSessions = new(StringComparer.Ordinal);

    public CursorAcpProcessManager(
        ICursorExecutableResolver executableResolver,
        IProcessSupervisor processSupervisor,
        StorageOptions storageOptions,
        IOptions<CursorAcpOptions>? options = null,
        IJsonRpcTransportFactory? transportFactory = null,
        IProcessIdResolver? processIdResolver = null,
        TimeProvider? timeProvider = null,
        ILogger<CursorAcpProcessManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(executableResolver);
        ArgumentNullException.ThrowIfNull(processSupervisor);
        ArgumentNullException.ThrowIfNull(storageOptions);

        _executableResolver = executableResolver;
        _processSupervisor = processSupervisor;
        _storageOptions = storageOptions;
        _options = options?.Value ?? new CursorAcpOptions();
        _options.Validate();
        _transportFactory = transportFactory;
        _processIdResolver = processIdResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CursorAcpProcessManager>.Instance;
    }

    public async Task<CursorAcpProcessStartResult> StartAsync(
        CursorAcpProcessStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutionId);

        if (_unpublishedSessions.TryGetValue(request.ExecutionId, out var unpublished))
        {
            var pendingCleanup = await DisposeUnpublishedAsync(unpublished).ConfigureAwait(false);
            if (pendingCleanup is not null) return pendingCleanup;
        }

        if (_sessions.TryGetValue(request.ExecutionId, out var existingSession))
        {
            if (existingSession.IsRunning)
            {
                return CursorAcpProcessStartResult.Degraded(
                    CursorAcpProcessStartFailureKind.ProcessCleanupPending,
                    $"A managed cursor-agent acp process is already running for execution '{request.ExecutionId}'. " +
                    "One native session owns at most one ACP process.",
                    "Stop the existing process and confirm cleanup before retrying this execution.");
            }

            try { await existingSession.DisposeAsync().ConfigureAwait(false); }
            catch (Exception)
            {
                return CursorAcpProcessStartResult.Degraded(CursorAcpProcessStartFailureKind.ProcessCleanupPending,
                    "The previous Cursor ACP session still requires confirmed cleanup.", "Do not retry until cleanup or reconciliation completes.");
            }
            ((ICollection<KeyValuePair<string, CursorAcpProcessSession>>)_sessions).Remove(new(request.ExecutionId, existingSession));
        }

        CursorAcpExecutableResolution resolution;

        try
        {
            resolution = await _executableResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CursorAcpProcessStartResult.Degraded(
                CursorAcpProcessStartFailureKind.ExecutableUnresolved,
                $"Cursor Agent CLI discovery failed: {exception.Message}",
                CursorAcpPolicy.ProcessStartupGuidance);
        }

        if (!resolution.IsAvailable)
        {
            var failureKind = resolution.Status == CursorAcpExecutableStatus.Unresolved
                ? CursorAcpProcessStartFailureKind.ExecutableUnresolved
                : CursorAcpProcessStartFailureKind.ExecutableMissing;

            return CursorAcpProcessStartResult.Degraded(
                failureKind,
                resolution.Blocker ?? CursorAcpPolicy.NotInstalledBlocker,
                resolution.Guidance ?? CursorAcpPolicy.ProcessStartupGuidance);
        }

        string runDirectory;
        string workingDirectory;
        ProcessStartSpecification specification;

        try
        {
            runDirectory = AppDataPaths.GetRunDirectory(ResolveDataRoot(), request.ExecutionId);
            workingDirectory = Path.Combine(runDirectory, "workspace");
            Directory.CreateDirectory(workingDirectory);
            specification = CreateStartSpecification(
                request.ExecutionId,
                resolution.ExecutablePath!,
                workingDirectory);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CursorAcpProcessStartResult.Degraded(
                CursorAcpProcessStartFailureKind.LaunchFailed,
                $"Failed to prepare the Cursor ACP working directory for execution '{request.ExecutionId}': {exception.Message}",
                CursorAcpPolicy.ProcessStartupGuidance);
        }

        IProtocolProcessSession protocolSession;

        try
        {
            protocolSession = await _processSupervisor
                .StartProtocolProcessAsync(specification, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessStartupPendingException)
        {
            return CursorAcpProcessStartResult.Degraded(
                CursorAcpProcessStartFailureKind.ProcessCleanupPending,
                "Cursor ACP startup/cleanup is unconfirmed; the supervisor retains process ownership.",
                "Do not retry this execution until cleanup or reconciliation confirms termination.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CursorAcpProcessStartResult.Degraded(
                CursorAcpProcessStartFailureKind.LaunchFailed,
                $"The process supervisor could not start 'cursor-agent acp': {exception.Message}",
                CursorAcpPolicy.ProcessStartupGuidance);
        }

        if (!protocolSession.IsRunning)
        {
            var startupFailure = await ObserveStartupFailureAsync(protocolSession).ConfigureAwait(false);

            var pendingCleanup = await DisposeUnpublishedAsync(protocolSession).ConfigureAwait(false);
            if (pendingCleanup is not null) return pendingCleanup;

            return CursorAcpProcessStartResult.Degraded(
                CursorAcpProcessStartFailureKind.LaunchFailed,
                $"The managed 'cursor-agent acp' process exited before transport initialization: {startupFailure ?? "completion details unavailable"}",
                CursorAcpPolicy.ProcessStartupGuidance);
        }

        var startedAtUtc = _timeProvider.GetUtcNow();
        var processId = protocolSession.ProcessId;
        IJsonRpcTransport? transport = null;

        if (_transportFactory is not null)
        {
            try
            {
                transport = _transportFactory.Create(
                    protocolSession.StandardOutput,
                    protocolSession.StandardInput);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var pendingCleanup = await DisposeUnpublishedAsync(protocolSession).ConfigureAwait(false);
                if (pendingCleanup is not null) return pendingCleanup;

                return CursorAcpProcessStartResult.Degraded(
                    CursorAcpProcessStartFailureKind.LaunchFailed,
                    "The JSON-RPC stdio transport could not be bound to the managed " +
                    $"'cursor-agent acp' process: {exception.Message}",
                    CursorAcpPolicy.ProcessStartupGuidance);
            }
        }

        var session = new CursorAcpProcessSession(
            request.ExecutionId,
            processId,
            workingDirectory,
            runDirectory,
            protocolSession,
            transport,
            startedAtUtc,
            _options.ShutdownTimeout,
            _logger,
            _timeProvider);

        _sessions[request.ExecutionId] = session;

        _logger.LogInformation(
            "Started a managed cursor-agent acp process {ProcessId} for execution {ExecutionId} with a " +
            "bound JSON-RPC stdio transport.",
            processId,
            request.ExecutionId);

        return CursorAcpProcessStartResult.Started(session);
    }

    private async Task<CursorAcpProcessStartResult?> DisposeUnpublishedAsync(IProtocolProcessSession session)
    {
        _unpublishedSessions[session.ExecutionId] = session;
        Task? disposal = null;
        try
        {
            disposal = session.DisposeAsync().AsTask();
            await disposal.WaitAsync(_options.ShutdownTimeout, _timeProvider).ConfigureAwait(false);
            ((ICollection<KeyValuePair<string, IProtocolProcessSession>>)_unpublishedSessions).Remove(new(session.ExecutionId, session));
            return null;
        }
        catch (Exception)
        {
            if (disposal is not null) _ = disposal.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return CursorAcpProcessStartResult.Degraded(CursorAcpProcessStartFailureKind.ProcessCleanupPending,
                "Cursor ACP initialization failed and process cleanup is unconfirmed; the session is retained.",
                "Confirm cleanup or reconciliation before retrying this execution.");
        }
    }

    public async Task StopAsync(
        ICursorAcpProcessSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session is not CursorAcpProcessSession tracked)
        {
            throw new ArgumentException(
                "The session was not created by this Cursor ACP process manager.",
                nameof(session));
        }

        if (!_sessions.TryGetValue(tracked.ExecutionId, out var removed) || !ReferenceEquals(removed, tracked))
        {
            return;
        }

        // Disposal closes the bound transport and releases the supervisor-owned standard-error
        // spool handle, so the run directory is no longer locked after the session stopped.
        await tracked.StopAsync(cancellationToken).ConfigureAwait(false);
        await tracked.DisposeAsync().ConfigureAwait(false);
        ((ICollection<KeyValuePair<string, CursorAcpProcessSession>>)_sessions)
            .Remove(new KeyValuePair<string, CursorAcpProcessSession>(tracked.ExecutionId, tracked));
    }

    private async Task<string?> ObserveStartupFailureAsync(IProtocolProcessSession protocolSession)
    {
        try
        {
            var result = await protocolSession.Completion
                .WaitAsync(TimeSpan.FromSeconds(1), _timeProvider)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(result.FailureMessage))
            {
                return result.FailureMessage;
            }

            return result.ExitCode is { } exitCode and not 0
                ? $"exit code {exitCode}"
                : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private static ProcessStartSpecification CreateStartSpecification(
        string executionId,
        string executablePath,
        string workingDirectory)
    {
        var (fileName, arguments) = CursorAcpCommandLine.Create(
            executablePath,
            [CursorAcpPolicy.AcpArgument]);

        return new ProcessStartSpecification
        {
            ExecutionId = executionId,
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            InheritEnvironment = false,
            EnvironmentVariables = ProcessRuntimeEnvironment.CreateBaseline(executablePath),
            StdinPolicy = ProcessStdinPolicy.DirectProtocolTransport
        };
    }

    private int? TryResolveProcessId(string executablePath, DateTimeOffset startedAtUtc)
    {
        if (_processIdResolver is null)
        {
            return null;
        }

        try
        {
            return _processIdResolver.ResolveChildProcessId(executablePath, startedAtUtc);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not resolve the managed cursor-agent process id.");
            return null;
        }
    }

    private string ResolveDataRoot() =>
        string.IsNullOrWhiteSpace(_storageOptions.AppDataDirectory)
            ? AppDataPaths.DefaultRootDirectory
            : Path.GetFullPath(_storageOptions.AppDataDirectory);
}
