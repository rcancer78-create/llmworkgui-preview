using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>
/// Default <see cref="ICursorAcpSessionLifecycleService"/>: starts the managed ACP process, binds the
/// transport, performs the handshake, owns the native session lineage and supervises turns through
/// <see cref="CursorAcpTurnSupervisor"/> (ADR-0003, ТЗ §6.4, §6.7, §6.8).
///
/// Every start and turn outcome is reported through <see cref="IHealthCenterService"/> under the
/// <c>cursor-agent-acp</c> backend scope; the lifecycle service never writes breaker state directly
/// (ТЗ §6.10).
/// </summary>
public sealed partial class CursorAcpSessionLifecycleService : ICursorAcpSessionLifecycleService
{
    /// <summary>Backend scope every Cursor ACP health observation is recorded under.</summary>
    public const string BackendHealthScopeId = "cursor-agent-acp";

    private static readonly HealthScope BackendHealthScope = HealthScope.ForBackend(BackendHealthScopeId);

    private readonly ICursorAcpProcessManager _processManager;
    private readonly ICursorAcpClientFactory _clientFactory;
    private readonly ICursorAcpModePolicy _modePolicy;
    private readonly CursorAcpOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CursorAcpSessionLifecycleService> _logger;
    private readonly IHealthCenterService? _healthCenter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _ancestry = new();

    private CursorAcpBackendStartResult? _current;
    private ICursorAcpProcessSession? _pendingCleanupSession;
    private CursorAcpTurnSupervisor? _supervisor;
    private CancellationTokenSource? _streamPumpCts;
    private Task? _streamPumpTask;
    private string? _nativeSessionId;
    private bool _disposed;

    public CursorAcpSessionLifecycleService(
        ICursorAcpProcessManager processManager,
        ICursorAcpClientFactory clientFactory,
        ICursorAcpModePolicy modePolicy,
        IOptions<CursorAcpOptions>? options = null,
        ILoggerFactory? loggerFactory = null,
        IHealthCenterService? healthCenter = null)
    {
        ArgumentNullException.ThrowIfNull(processManager);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(modePolicy);

        _processManager = processManager;
        _clientFactory = clientFactory;
        _modePolicy = modePolicy;
        _options = options?.Value ?? new CursorAcpOptions();
        _options.Validate();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<CursorAcpSessionLifecycleService>();
        _healthCenter = healthCenter;
    }

    public CursorAcpBackendStartResult? Current => _current;

    public string? NativeSessionId => _nativeSessionId;

    public IReadOnlyList<string> Ancestry
    {
        get
        {
            lock (_ancestry)
            {
                return _ancestry.ToArray();
            }
        }
    }

    public event EventHandler<CursorAcpStreamEvent>? StreamEventObserved;

    public event EventHandler<CursorAcpStreamEvent.PermissionRequest>? PermissionRequested;

    public event EventHandler<CursorAcpTurnStateChangedEventArgs>? TurnStateChanged;

    public async Task<CursorAcpBackendStartResult> StartBackendAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ThrowIfDisposed();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        ICursorAcpProcessSession? unpublishedSession = null;
        try
        {
            ThrowIfDisposed();
            if (_pendingCleanupSession is not null)
                return PendingCleanupResult();
            if (_current is { IsReady: true })
            {
                return CursorAcpBackendStartResult.Degraded(
                    CursorAcpBackendFailureKind.AlreadyStarted,
                    "A Cursor ACP backend is already running for this workspace; stop it before starting " +
                    "another one. One workspace owns at most one ACP process.");
            }

            var startResult = await _processManager
                .StartAsync(new CursorAcpProcessStartRequest { ExecutionId = executionId }, cancellationToken)
                .ConfigureAwait(false);

            if (!startResult.IsStarted || startResult.Session is null)
            {
                var failureKind = startResult.FailureKind switch
                {
                    CursorAcpProcessStartFailureKind.ExecutableMissing or
                        CursorAcpProcessStartFailureKind.ExecutableUnresolved =>
                        CursorAcpBackendFailureKind.ExecutableUnavailable,
                    CursorAcpProcessStartFailureKind.ProcessCleanupPending => CursorAcpBackendFailureKind.ProcessCleanupPending,
                    _ => CursorAcpBackendFailureKind.ProcessLaunchFailed
                };

                var blocker = startResult.Blocker ?? CursorAcpPolicy.NotInstalledBlocker;

                await ReportHealthFailureAsync(
                        MapProcessStartErrorClass(startResult.FailureKind),
                        blocker)
                    .ConfigureAwait(false);

                return CursorAcpBackendStartResult.Degraded(failureKind, blocker, startResult.Guidance);
            }

            var session = startResult.Session;
            unpublishedSession = session;

            if (session.Transport is null)
            {
                const string transportBlocker =
                    "The managed 'cursor-agent acp' process started but no JSON-RPC stdio transport was " +
                    "bound to it, so the ACP protocol cannot be used. A CLI print-mode fallback is not " +
                    "attempted (ADR-0003 §7.2).";

                var pending = await CleanupUnpublishedAsync(session).ConfigureAwait(false);
                unpublishedSession = null;
                if (pending is not null) return pending;
                await ReportHealthFailureAsync(HealthErrorClass.StartupOrSessionCreation, transportBlocker)
                    .ConfigureAwait(false);
                return CursorAcpBackendStartResult.Degraded(
                    CursorAcpBackendFailureKind.TransportUnavailable,
                    transportBlocker);
            }

            var client = _clientFactory.Create(session.Transport);
            var handshake = await client.InitializeAsync(cancellationToken).ConfigureAwait(false);

            if (!handshake.IsReady || handshake.Evidence is null)
            {
                var handshakeBlocker =
                    handshake.Blocker ?? "The ACP initialize handshake did not reach a ready state.";

                var pending = await CleanupUnpublishedAsync(session).ConfigureAwait(false);
                unpublishedSession = null;
                if (pending is not null) return pending;
                await ReportHealthFailureAsync(HealthErrorClass.StartupOrSessionCreation, handshakeBlocker)
                    .ConfigureAwait(false);
                return CursorAcpBackendStartResult.Degraded(
                    CursorAcpBackendFailureKind.HandshakeFailed,
                    handshakeBlocker,
                    handshake.Guidance);
            }

            var ready = CursorAcpBackendStartResult.Ready(session, client, handshake.Evidence);

            _current = ready;
            unpublishedSession = null;
            _supervisor = new CursorAcpTurnSupervisor(
                client,
                _modePolicy,
                _options,
                _loggerFactory.CreateLogger<CursorAcpTurnSupervisor>(),
                usesExternalStream: true);
            _supervisor.TurnStateChanged += OnTurnStateChanged;

            StartStreamPump(client);

            _logger.LogInformation(
                "The Cursor ACP backend for execution {ExecutionId} is ready (protocolVersion {ProtocolVersion}).",
                executionId,
                handshake.Evidence.ProtocolVersion);

            await ReportHealthSuccessAsync(
                    $"The Cursor ACP backend reached a ready handshake (protocolVersion {handshake.Evidence.ProtocolVersion}).")
                .ConfigureAwait(false);

            return ready;
        }
        catch (Exception) when (unpublishedSession is not null)
        {
            var pending = await CleanupUnpublishedAsync(unpublishedSession).ConfigureAwait(false);
            if (pending is not null) return pending;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static CursorAcpBackendStartResult PendingCleanupResult() => CursorAcpBackendStartResult.Degraded(
        CursorAcpBackendFailureKind.ProcessCleanupPending,
        "Cursor ACP process cleanup is unconfirmed; the existing session is retained.",
        "Do not start another process until cleanup or reconciliation confirms termination.");

    private async Task<CursorAcpBackendStartResult?> CleanupUnpublishedAsync(ICursorAcpProcessSession session)
    {
        _pendingCleanupSession = session;
        try
        {
            await _processManager.StopAsync(session, CancellationToken.None).ConfigureAwait(false);
            _pendingCleanupSession = null;
            return null;
        }
        catch (Exception)
        {
            var pending = PendingCleanupResult();
            await ReportHealthFailureAsync(HealthErrorClass.UnknownOrAmbiguousCompletion, pending.Blocker!).ConfigureAwait(false);
            return pending;
        }
    }

    public async Task StopBackendAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await StopBackendCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed && _current is null && _pendingCleanupSession is null)
                return;
            _disposed = true;
            try
            {
                await StopBackendCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(exception, "Stopping the Cursor ACP backend during disposal failed.");
            }
        }
        finally
        {
            // In-flight callers may still be waiting to enter and observe disposal. Disposing the
            // semaphore would race their WaitAsync/Release; it owns no native wait handle here.
            _gate.Release();
        }
    }

    private async Task StopBackendCoreAsync(CancellationToken cancellationToken)
    {
        // Invalidate session replies before waiting for physical stop. A failed stop still means
        // the previous session RPC cannot establish new interactive readiness.
        _sessionGeneration++;
        var session = _current?.Session ?? _pendingCleanupSession;

        // A failed or cancelled stop is not proof that the owned process ended. Preserve the
        // session and its observers so callers can retry cleanup without losing its only handle.
        if (session is not null)
        {
            _pendingCleanupSession = session;
            await _processManager.StopAsync(session, cancellationToken).ConfigureAwait(false);
            _pendingCleanupSession = null;
        }

        if (_supervisor is not null)
        {
            _supervisor.TurnStateChanged -= OnTurnStateChanged;
            _supervisor = null;
        }

        await StopStreamPumpAsync().ConfigureAwait(false);

        _current = null;
        _nativeSessionId = null;

    }

    private void OnTurnStateChanged(object? sender, CursorAcpTurnStateChangedEventArgs args) =>
        RaiseTurnStateChanged(args);

    private void RaiseTurnStateChanged(CursorAcpTurnStateChangedEventArgs args)
    {
        try
        {
            TurnStateChanged?.Invoke(this, args);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "A Cursor ACP turn state observer failed.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Maps a process-start failure onto the normative error taxonomy. A missing or unresolved
    /// executable is a deployment fault; pending ownership is ambiguous, other failures are startup/session creation.
    /// </summary>
    private static HealthErrorClass MapProcessStartErrorClass(CursorAcpProcessStartFailureKind? failureKind) =>
        failureKind switch
        {
            CursorAcpProcessStartFailureKind.ExecutableMissing or
                CursorAcpProcessStartFailureKind.ExecutableUnresolved =>
                HealthErrorClass.ExecutableMissingOrVersion,
            CursorAcpProcessStartFailureKind.ProcessCleanupPending => HealthErrorClass.UnknownOrAmbiguousCompletion,
            _ => HealthErrorClass.StartupOrSessionCreation
        };

    /// <summary>
    /// Reports an observed backend failure through the Health Center. Reporting is bookkeeping and must
    /// never change the outcome of the backend operation, so a reporting error is only logged.
    /// </summary>
    private async Task ReportHealthFailureAsync(HealthErrorClass errorClass, string reason)
    {
        if (_healthCenter is null)
        {
            return;
        }

        try
        {
            await _healthCenter
                .ReportFailureAsync(BackendHealthScope, errorClass, reason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Reporting the Cursor ACP backend health failure failed.");
        }
    }

    /// <summary>Reports an observed backend success through the Health Center.</summary>
    private async Task ReportHealthSuccessAsync(string reason)
    {
        if (_healthCenter is null)
        {
            return;
        }

        try
        {
            await _healthCenter
                .ReportSuccessAsync(BackendHealthScope, reason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Reporting the Cursor ACP backend health success failed.");
        }
    }
}
