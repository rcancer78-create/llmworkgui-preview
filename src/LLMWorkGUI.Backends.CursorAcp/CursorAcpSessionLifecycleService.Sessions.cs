using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>
/// Session, turn, approval and cancellation part of <see cref="CursorAcpSessionLifecycleService"/>.
/// </summary>
public sealed partial class CursorAcpSessionLifecycleService
{
    private sealed record ActiveTurnBinding(string SessionId);
    private ActiveTurnBinding? _activeTurnBinding;
    // Session RPCs preserve invocation order, independently of physical backend stop. The backend
    // gate protects only snapshots/publication, never a potentially stalled protocol await.
    private readonly SemaphoreSlim _sessionOperationGate = new(1, 1);
    private long _sessionGeneration;

    public Task<CursorAcpSessionResult> CreateSessionAsync(
        CursorAcpNewSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunSessionOperationAsync((client, token) => client.CreateSessionAsync(request, token), cancellationToken);
    }

    public Task<CursorAcpSessionResult> LoadSessionAsync(
        CursorAcpLoadSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunSessionOperationAsync((client, token) => client.LoadSessionAsync(request, token), cancellationToken);
    }

    public Task<CursorAcpSessionResult> ResetSessionAsync(
        CursorAcpNewSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A reset creates a replacement session and appends it to the lineage; the previous native
        // session id is never removed from the ancestry (ТЗ §6.4).
        return RunSessionOperationAsync((client, token) => client.CreateSessionAsync(request, token), cancellationToken);
    }

    private async Task<CursorAcpSessionResult> RunSessionOperationAsync(
        Func<ICursorAcpClient, CancellationToken, Task<CursorAcpSessionResult>> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _sessionOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CursorAcpBackendStartResult backend;
            ICursorAcpClient client;
            long generation;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (_pendingCleanupSession is not null || _current is not { IsReady: true, Client: { } readyClient } ready)
                    return NotStartedSessionResult();
                backend = ready;
                client = readyClient;
                generation = _sessionGeneration;
            }
            finally { _gate.Release(); }

            var result = await operation(client, cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Stop invalidates this generation before its physical cleanup begins, including
                // failed/unconfirmed cleanup. A late response cannot resurrect that generation.
                if (_disposed || generation != _sessionGeneration || !ReferenceEquals(backend, _current) ||
                    _pendingCleanupSession is not null)
                    return NotStartedSessionResult();
                if (result.IsReady && result.Evidence is not null) TrackSession(result.Evidence.SessionId);
                return result;
            }
            finally { _gate.Release(); }
        }
        finally { _sessionOperationGate.Release(); }
    }

    public async Task<CursorAcpTurnResult> ExecuteTurnAsync(
        CursorAcpTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var supervisor = _supervisor;

        if (supervisor is null)
        {
            return new CursorAcpTurnResult
            {
                Outcome = CursorAcpTurnOutcome.Rejected,
                SessionId = request.Prompt.SessionId,
                ClientRequestId = request.Prompt.ClientRequestId,
                FailureKind = CursorAcpTurnFailureKind.ModeNotSendable,
                Blocker = "The Cursor ACP backend is not started, so no turn can be dispatched.",
                LockRetained = request.LockToken?.IsHeld == true,
                StatesVisited = Array.Empty<CursorAcpTurnState>()
            };
        }

        var binding = new ActiveTurnBinding(request.Prompt.SessionId);
        if (Interlocked.CompareExchange(ref _activeTurnBinding, binding, null) is not null)
        {
            return new CursorAcpTurnResult
            {
                Outcome = CursorAcpTurnOutcome.Rejected,
                SessionId = request.Prompt.SessionId,
                ClientRequestId = request.Prompt.ClientRequestId,
                FailureKind = CursorAcpTurnFailureKind.ConcurrentTurn,
                Blocker = "Another turn is active in this workspace; the turn was refused before dispatch.",
                LockRetained = request.LockToken?.IsHeld == true
            };
        }

        try
        {
            var result = await supervisor
                .ExecuteTurnAsync(request.Mode, request.LockToken, request.Prompt, cancellationToken)
                .ConfigureAwait(false);

            await ReportTurnHealthAsync(result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeTurnBinding, null, binding);
        }
    }

    /// <summary>
    /// Reports the terminal turn outcome through the Health Center (ТЗ §6.10). A pre-dispatch refusal,
    /// a confirmed cancellation and an ambiguous turn are deliberately not reported: none of them is
    /// evidence that the backend itself failed, and charging the breaker for them would quarantine a
    /// healthy backend (ADR-0003 §10.4).
    /// </summary>
    private async Task ReportTurnHealthAsync(CursorAcpTurnResult result)
    {
        if (result.FailureKind == CursorAcpTurnFailureKind.ConcurrentTurn)
            return; // Local contention is not a backend/provider failure.
        if (result.Outcome == CursorAcpTurnOutcome.Succeeded)
        {
            await ReportHealthSuccessAsync("A Cursor ACP turn completed successfully.").ConfigureAwait(false);
            return;
        }

        if (result.Outcome == CursorAcpTurnOutcome.TimedOut ||
            result.FailureKind == CursorAcpTurnFailureKind.TurnTimeout)
        {
            await ReportHealthFailureAsync(
                    HealthErrorClass.NetworkOrTimeout,
                    "A Cursor ACP turn did not complete inside the hard timeout.")
                .ConfigureAwait(false);
            return;
        }

        if (result.Outcome == CursorAcpTurnOutcome.Failed)
        {
            await ReportHealthFailureAsync(
                    HealthErrorClass.Provider4xx5xx,
                    $"A Cursor ACP turn failed ({result.FailureKind?.ToString() ?? "unclassified"}).")
                .ConfigureAwait(false);
        }
    }

    public async Task<CursorAcpPermissionReplyResult> ReplyPermissionAsync(
        CursorAcpPermissionReplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var supervisor = _supervisor;

        if (supervisor is null)
        {
            return CursorAcpPermissionReplyResult.Degraded(
                CursorAcpPermissionReplyFailureKind.NotReady,
                "The Cursor ACP backend is not started, so no permission reply can be sent.");
        }

        return await supervisor.ReplyPermissionAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CursorAcpCancelResult> CancelTurnAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (RequireClient() is not { } client)
        {
            return CursorAcpCancelResult.Degraded(
                CursorAcpCancelFailureKind.NotReady,
                "The Cursor ACP backend is not started, so no cancellation can be sent.");
        }

        // Session creation/load can update the selected session while the prior turn still runs.
        // Cancellation belongs to the admitted turn, never to that mutable selection.
        if ((Volatile.Read(ref _activeTurnBinding)?.SessionId ?? _nativeSessionId) is not { Length: > 0 } sessionId)
        {
            return CursorAcpCancelResult.Degraded(
                CursorAcpCancelFailureKind.InvalidSessionId,
                "No confirmed native session exists, so no cancellation can be sent.");
        }

        // The send is never a terminal confirmation: the supervised turn moves to Cancelling and
        // awaits terminal evidence or the cancellation watchdog (ADR-0003 §6.2).
        var supervisor = _supervisor;
        return supervisor is not null
            ? await supervisor.CancelTurnAsync(sessionId, cancellationToken).ConfigureAwait(false)
            : await client.CancelSessionAsync(new CursorAcpCancelRequest { SessionId = sessionId }, cancellationToken)
                .ConfigureAwait(false);
    }

    private void StartStreamPump(ICursorAcpClient client)
    {
        _streamPumpCts = new CancellationTokenSource();
        _streamPumpTask = PumpStreamAsync(client, _streamPumpCts.Token);
    }

    private async Task StopStreamPumpAsync()
    {
        var cts = _streamPumpCts;
        var pump = _streamPumpTask;

        _streamPumpCts = null;
        _streamPumpTask = null;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();

        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "The Cursor ACP stream pump ended with an error.");
            }
        }

        cts.Dispose();
    }

    /// <summary>
    /// Observes the normalized event stream of the whole backend and re-publishes it. Permission
    /// requests are surfaced separately so the UI can ask the user explicitly; no automatic approval
    /// is ever applied (ADR-0003 §4.2).
    /// </summary>
    private async Task PumpStreamAsync(ICursorAcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var streamEvent in client
                               .SubscribeEventsAsync(sessionId: null, cancellationToken)
                               .ConfigureAwait(false))
            {
                _supervisor?.ObserveStreamEvent(streamEvent);
                RaiseStreamEvent(streamEvent);

                if (streamEvent is CursorAcpStreamEvent.PermissionRequest permissionRequest)
                {
                    RaisePermissionRequested(permissionRequest);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The Cursor ACP event stream ended unexpectedly; the backend awaits terminal evidence.");

            await ReportHealthFailureAsync(
                    HealthErrorClass.MalformedProtocolEvent,
                    "The Cursor ACP event stream ended unexpectedly; the backend awaits terminal evidence.")
                .ConfigureAwait(false);
        }
        finally
        {
            _supervisor?.ObserveStreamClosed();
        }
    }

    private void RaiseStreamEvent(CursorAcpStreamEvent streamEvent)
    {
        try
        {
            StreamEventObserved?.Invoke(this, streamEvent);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "A Cursor ACP stream observer failed.");
        }
    }

    private void RaisePermissionRequested(CursorAcpStreamEvent.PermissionRequest permissionRequest)
    {
        try
        {
            PermissionRequested?.Invoke(this, permissionRequest);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "A Cursor ACP permission observer failed.");
        }
    }

    private void TrackSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _nativeSessionId = sessionId;

        lock (_ancestry)
        {
            if (_ancestry.Count == 0 ||
                !string.Equals(_ancestry[^1], sessionId, StringComparison.Ordinal))
            {
                _ancestry.Add(sessionId);
            }
        }
    }

    private ICursorAcpClient? RequireClient() => _current is { IsReady: true } ready ? ready.Client : null;

    private static CursorAcpSessionResult NotStartedSessionResult() =>
        CursorAcpSessionResult.Degraded(
            CursorAcpSessionFailureKind.NotReady,
            "The Cursor ACP backend is not started, so no native session can be created or loaded.",
            CursorAcpPolicy.SessionNotReadyGuidance);
}
