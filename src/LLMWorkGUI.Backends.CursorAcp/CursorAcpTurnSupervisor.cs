using System.Collections.Concurrent;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>
/// End-to-end coordinator of a single Cursor ACP turn (ТЗ §6.7/§6.8, ADR-0003 §4, §6, §10).
/// It gates the turn on the fail-closed mode decision and the checkout writer lock, forbids a
/// second concurrent turn on the same native session, normalizes the protocol event stream,
/// handles approvals through an explicit one-shot reply, sends in-band cancellation and awaits
/// terminal evidence instead of trusting the cancel ack. The writer lock is released strictly
/// after <c>Succeeded</c>, <c>Failed</c> and <c>Cancelled</c>; it is retained on
/// <c>TimedOut</c>, <c>Ambiguous</c>, <c>Orphaned</c> and pre-dispatch refusals until native
/// execution is reconciled (ADR-0003 §10.4).
/// </summary>
public sealed class CursorAcpTurnSupervisor
{
    private static readonly Task<CursorAcpPromptResult> NeverCompletesPrompt =
        new TaskCompletionSource<CursorAcpPromptResult>().Task;

    private readonly ICursorAcpClient _client;
    private readonly ICursorAcpModePolicy _modePolicy;
    private readonly CursorAcpOptions _options;
    private readonly ILogger<CursorAcpTurnSupervisor> _logger;
    private readonly ConcurrentDictionary<string, TurnContext> _activeTurns = new(StringComparer.Ordinal);
    private readonly bool _usesExternalStream;

    public CursorAcpTurnSupervisor(
        ICursorAcpClient client,
        ICursorAcpModePolicy modePolicy,
        CursorAcpOptions? options = null,
        ILogger<CursorAcpTurnSupervisor>? logger = null,
        bool usesExternalStream = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(modePolicy);

        _client = client;
        _modePolicy = modePolicy;
        _options = options ?? new CursorAcpOptions();
        _options.Validate();
        _logger = logger ?? NullLogger<CursorAcpTurnSupervisor>.Instance;
        _usesExternalStream = usesExternalStream;
    }

    /// <summary>Raised for every normative turn state transition of an active turn.</summary>
    public event EventHandler<CursorAcpTurnStateChangedEventArgs>? TurnStateChanged;

    /// <summary>Current normative state of the active turn on a native session, when one exists.</summary>
    public bool TryGetTurnState(string sessionId, out CursorAcpTurnState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (_activeTurns.TryGetValue(sessionId, out var context))
        {
            state = context.CurrentState;
            return true;
        }

        state = default;
        return false;
    }

    /// <summary>
    /// Executes one turn on an existing native session. The turn is refused before any prompt is
    /// sent when the mode decision cannot be sent, when a writer-lock-requiring mode has no active
    /// lock token, or when another non-terminal turn is already running on the same session.
    /// </summary>
    public async Task<CursorAcpTurnResult> ExecuteTurnAsync(
        CursorAcpModeDecision modeDecision,
        ICheckoutLockToken? lockToken,
        CursorAcpPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modeDecision);
        ArgumentNullException.ThrowIfNull(request);

        var evaluated = _modePolicy.Evaluate(modeDecision.Mode, modeDecision.Access, modeDecision.State);

        if (!modeDecision.CanSend || !evaluated.CanSend)
        {
            return Reject(
                request,
                CursorAcpTurnFailureKind.ModeNotSendable,
                modeDecision.Blocker ?? evaluated.Blocker ??
                "The selected Cursor ACP mode cannot be sent; the turn was refused before dispatch.", lockToken);
        }

        var requiresWriterLock = modeDecision.RequiresWriterLock || evaluated.RequiresWriterLock;

        if (requiresWriterLock && lockToken?.IsHeld != true)
        {
            return Reject(
                request,
                CursorAcpTurnFailureKind.WriterLockMissing,
                "The selected Cursor ACP mode requires an active checkout writer lock token, but none was " +
                "provided; the turn was refused before dispatch.", lockToken);
        }

        var context = new TurnContext(request.SessionId);

        if (!_activeTurns.TryAdd(request.SessionId, context))
        {
            context.DisposeCancellation();
            return Reject(
                request,
                CursorAcpTurnFailureKind.ConcurrentTurn,
                $"Another non-terminal turn is already active on native session '{request.SessionId}'; a " +
                "second concurrent turn is forbidden (ADR-0003 §10.3).", lockToken);
        }

        try
        {
            using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken);
            return await RunTurnAsync(context, request with { ModeId = evaluated.ModeId, BeforeDispatchCancellationToken = turnCancellation.Token }, lockToken, turnCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _activeTurns.TryRemove(new KeyValuePair<string, TurnContext>(request.SessionId, context));
            context.CancelSent.TrySetResult(CursorAcpCancelResult.Degraded(CursorAcpCancelFailureKind.NotReady,
                "The turn ended before an in-band cancel was sent."));
            context.DisposeCancellation();
        }
    }

    internal async Task<CursorAcpCancelResult> CancelTurnAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_activeTurns.TryGetValue(sessionId, out var context))
        {
            context.RequestCancellation();
            return await context.CancelSent.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return await _client.CancelSessionAsync(new CursorAcpCancelRequest { SessionId = sessionId }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an explicit one-shot permission decision for the active turn and returns the turn to
    /// <c>Running</c> as soon as the approval is resolved. No automatic approval is ever applied.
    /// </summary>
    public async Task<CursorAcpPermissionReplyResult> ReplyPermissionAsync(
        CursorAcpPermissionReplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _client.ReplyPermissionAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsSent) ResolveApproval(request);
        return result;
    }

    private async Task<CursorAcpTurnResult> RunTurnAsync(
        TurnContext context,
        CursorAcpPromptRequest request,
        ICheckoutLockToken? lockToken,
        CancellationToken cancellationToken)
    {
        MarkState(context, CursorAcpTurnState.Starting);
        MarkState(context, CursorAcpTurnState.SessionConfirmed);

        if (cancellationToken.IsCancellationRequested)
        {
            MarkState(context, CursorAcpTurnState.Cancelling);

            return await CompleteAsync(
                    context,
                    request,
                    lockToken,
                    CursorAcpTurnOutcome.Cancelled,
                    stopReason: null,
                    CursorAcpTurnFailureKind.TurnCancelledBeforeStart,
                    "The turn was cancelled by the caller before the prompt was dispatched.")
                .ConfigureAwait(false);
        }

        using var promptCancellation = new CancellationTokenSource();
        using var pumpCancellation = new CancellationTokenSource();
        using var hardTimeoutCancellation = new CancellationTokenSource();

        var hardTimeoutTask = Task.Delay(_options.TurnHardTimeout, hardTimeoutCancellation.Token);
        var userCancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        MarkState(context, CursorAcpTurnState.Running);

        var dispatchObservation = _client.ReportsPromptDispatch
            ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        var observedRequest = dispatchObservation is null ? request : request with { DispatchObserver = value => dispatchObservation.TrySetResult(value) };
        var promptTask = _client.PromptAsync(observedRequest, promptCancellation.Token);
        var pumpTask = _usesExternalStream ? Task.CompletedTask : PumpEventsAsync(context, pumpCancellation.Token);

        // Phase 1: the turn runs until the prompt completes, the caller cancels, or the hard
        // timeout elapses. Model silence is an observation, never a failure signal.
        while (true)
        {
            var completed = await Task.WhenAny(promptTask, userCancellationTask, hardTimeoutTask)
                .ConfigureAwait(false);

            if (completed == promptTask)
            {
                var completion = await ClassifyPromptAsync(context, promptTask, isCancelling: false)
                    .ConfigureAwait(false);

                pumpCancellation.Cancel();
                ObserveDetached(promptTask);
                await ObservePumpAsync(pumpTask).ConfigureAwait(false);

                return await CompleteAsync(
                        context,
                        request,
                        lockToken,
                        completion.Outcome!.Value,
                        completion.StopReason,
                        completion.FailureKind,
                        completion.Blocker)
                    .ConfigureAwait(false);
            }

            if (completed == hardTimeoutTask)
            {
                return await FinishHardTimeoutAsync(context, request, lockToken, promptTask, promptCancellation, pumpCancellation, pumpTask)
                    .ConfigureAwait(false);
            }

            break;
        }

        // Phase 2: cancellation was requested. The cancel send is not a terminal confirmation;
        // the turn waits for the current prompt's terminal response or the cancellation watchdog.
        MarkState(context, CursorAcpTurnState.Cancelling);

        var cancelWatchdogTask = Task.Delay(_options.CancellationTimeout);

        if (dispatchObservation is not null)
        {
            // Cancelling local mode preparation must not send session/cancel to an idle session.
            // This observation says only whether a prompt call was attempted, never delivered.
            await Task.WhenAny(dispatchObservation.Task, promptTask, hardTimeoutTask, cancelWatchdogTask).ConfigureAwait(false);
        }
        if (!promptTask.IsCompleted && (dispatchObservation is null ||
                (dispatchObservation.Task.IsCompletedSuccessfully && dispatchObservation.Task.Result)))
            await SendCancelAsync(context).ConfigureAwait(false);

        while (true)
        {
            var completed = await Task.WhenAny(
                    promptTask,
                    hardTimeoutTask,
                    cancelWatchdogTask)
                .ConfigureAwait(false);

            if (completed == hardTimeoutTask)
            {
                return await FinishHardTimeoutAsync(context, request, lockToken, promptTask, promptCancellation, pumpCancellation, pumpTask)
                    .ConfigureAwait(false);
            }

            if (completed == cancelWatchdogTask)
            {
                promptCancellation.Cancel();
                pumpCancellation.Cancel();
                ObserveDetached(promptTask);
                await ObservePumpAsync(pumpTask).ConfigureAwait(false);

                if (promptTask.IsCompleted)
                {
                    var lateCompletion = await ClassifyPromptAsync(context, promptTask, isCancelling: true)
                        .ConfigureAwait(false);

                    if (!lateCompletion.IsTransient)
                    {
                        return await CompleteAsync(
                                context,
                                request,
                                lockToken,
                                lateCompletion.Outcome!.Value,
                                lateCompletion.StopReason,
                                lateCompletion.FailureKind,
                                lateCompletion.Blocker)
                            .ConfigureAwait(false);
                    }
                }

                return await CompleteAsync(
                        context,
                        request,
                        lockToken,
                        CursorAcpTurnOutcome.Ambiguous,
                        stopReason: null,
                        CursorAcpTurnFailureKind.CancellationUnconfirmed,
                        $"Cancellation was requested but no terminal evidence arrived within {_options.CancellationTimeout}; " +
                        "the writer lock is retained until reconciliation (ADR-0003 §6.4, §10.4).")
                    .ConfigureAwait(false);
            }

            var completion = await ClassifyPromptAsync(context, promptTask, isCancelling: true)
                .ConfigureAwait(false);

            if (!completion.IsTransient)
            {
                pumpCancellation.Cancel();
                ObserveDetached(promptTask);
                await ObservePumpAsync(pumpTask).ConfigureAwait(false);

                return await CompleteAsync(
                        context,
                        request,
                        lockToken,
                        completion.Outcome!.Value,
                        completion.StopReason,
                        completion.FailureKind,
                        completion.Blocker)
                    .ConfigureAwait(false);
            }

            // The prompt ended without terminal evidence while cancelling; keep waiting for the
            // watchdog. A session-only status cannot identify which prompt stopped.
            promptTask = NeverCompletesPrompt;
        }
    }

    private async Task<CursorAcpTurnResult> FinishHardTimeoutAsync(
        TurnContext context,
        CursorAcpPromptRequest request,
        ICheckoutLockToken? lockToken,
        Task<CursorAcpPromptResult> promptTask,
        CancellationTokenSource promptCancellation,
        CancellationTokenSource pumpCancellation,
        Task pumpTask)
    {
        context.RequestHardTimeout();
        promptCancellation.Cancel();
        pumpCancellation.Cancel();
        ObserveDetached(promptTask);
        await ObservePumpAsync(pumpTask).ConfigureAwait(false);

        var deliveryKnown = context.HasDeliveryObservation();

        return await CompleteAsync(
                context,
                request,
                lockToken,
                deliveryKnown ? CursorAcpTurnOutcome.TimedOut : CursorAcpTurnOutcome.Ambiguous,
                stopReason: null,
                CursorAcpTurnFailureKind.TurnTimeout,
                deliveryKnown
                    ? $"The turn did not complete within the hard timeout of {_options.TurnHardTimeout}; " +
                      "native termination is unconfirmed, so the writer lock is retained until reconciliation."
                    : $"The turn hard timeout of {_options.TurnHardTimeout} elapsed while the prompt delivery status " +
                      "was unknown; the writer lock is retained until reconciliation (ADR-0003 §10.4).")
            .ConfigureAwait(false);
    }

    private async Task<TurnCompletion> ClassifyPromptAsync(
        TurnContext context,
        Task<CursorAcpPromptResult> promptTask,
        bool isCancelling)
    {
        CursorAcpPromptResult promptResult;

        try
        {
            promptResult = await promptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.HardTimeoutRequested)
        {
            return context.HasDeliveryObservation()
                ? new TurnCompletion(
                    CursorAcpTurnOutcome.TimedOut,
                    CursorAcpTurnFailureKind.TurnTimeout,
                    $"The turn did not complete within the hard timeout of {_options.TurnHardTimeout}.",
                    StopReason: null)
                : new TurnCompletion(
                    CursorAcpTurnOutcome.Ambiguous,
                    CursorAcpTurnFailureKind.TurnTimeout,
                    "The turn hard timeout elapsed while the prompt delivery status was unknown.",
                    StopReason: null);
        }
        catch (OperationCanceledException)
        {
            return new TurnCompletion(
                CursorAcpTurnOutcome.Ambiguous,
                CursorAcpTurnFailureKind.TransportLost,
                "The prompt wait ended without terminal evidence.",
                StopReason: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "The Cursor ACP prompt call for session {SessionId} failed unexpectedly.",
                context.SessionId);

            return new TurnCompletion(
                CursorAcpTurnOutcome.Ambiguous,
                CursorAcpTurnFailureKind.TransportLost,
                $"The prompt call failed without terminal evidence: {exception.Message}",
                StopReason: null);
        }

        if (promptResult.IsSuccess)
        {
            if (!promptResult.IsCancelled
                && promptResult.StopReason is not (CursorAcpStopReasons.EndTurn or "end_turn"))
                return new TurnCompletion(CursorAcpTurnOutcome.Ambiguous,
                    CursorAcpTurnFailureKind.PromptRejected,
                    "The backend returned an unrecognized stop reason; terminal success is unconfirmed.",
                    promptResult.StopReason);
            return promptResult.IsCancelled
                ? new TurnCompletion(
                    CursorAcpTurnOutcome.Cancelled,
                    FailureKind: null,
                    Blocker: null,
                    promptResult.StopReason)
                : new TurnCompletion(
                    CursorAcpTurnOutcome.Succeeded,
                    FailureKind: null,
                    Blocker: null,
                    promptResult.StopReason);
        }

        switch (promptResult.FailureKind)
        {
            case CursorAcpPromptFailureKind.PromptBusy:
                return new TurnCompletion(CursorAcpTurnOutcome.Failed,
                    CursorAcpTurnFailureKind.ConcurrentTurn, promptResult.Blocker, StopReason: null);
            case CursorAcpPromptFailureKind.CancelledBeforeDispatch:
                return new TurnCompletion(CursorAcpTurnOutcome.Cancelled,
                    CursorAcpTurnFailureKind.TurnCancelledBeforeStart, promptResult.Blocker, StopReason: null);
            case CursorAcpPromptFailureKind.NotReady:
            case CursorAcpPromptFailureKind.InvalidSessionId:
            case CursorAcpPromptFailureKind.InvalidPrompt:
            case CursorAcpPromptFailureKind.InvalidRequest:
            case CursorAcpPromptFailureKind.ModeSelectionFailed:
            case CursorAcpPromptFailureKind.AgentError:
                return new TurnCompletion(
                    CursorAcpTurnOutcome.Failed,
                    CursorAcpTurnFailureKind.PromptRejected,
                    promptResult.Blocker,
                    StopReason: null);

            case CursorAcpPromptFailureKind.MalformedResponse:
                // The prompt request was attempted, but malformed response data cannot prove a
                // terminal failure or completion. Retain writer ownership until reconciliation.
                return new TurnCompletion(CursorAcpTurnOutcome.Ambiguous,
                    CursorAcpTurnFailureKind.PromptRejected, promptResult.Blocker, StopReason: null);

            case CursorAcpPromptFailureKind.PromptTimeout:
                if (isCancelling)
                {
                    return TurnCompletion.Transient;
                }

                return context.HasDeliveryObservation()
                    ? new TurnCompletion(
                        CursorAcpTurnOutcome.TimedOut,
                        CursorAcpTurnFailureKind.TurnTimeout,
                        promptResult.Blocker,
                        StopReason: null)
                    : new TurnCompletion(
                        CursorAcpTurnOutcome.Ambiguous,
                        CursorAcpTurnFailureKind.TransportLost,
                        promptResult.Blocker,
                        StopReason: null);

            default:
                if (isCancelling)
                {
                    return TurnCompletion.Transient;
                }

                if (!context.HasDeliveryObservation() && context.TransportClosedObserved)
                {
                    return new TurnCompletion(
                        CursorAcpTurnOutcome.Orphaned,
                        CursorAcpTurnFailureKind.ProcessLost,
                        promptResult.Blocker ??
                        "The managed 'cursor-agent acp' process was lost before any delivery evidence was observed.",
                        StopReason: null);
                }

                return new TurnCompletion(
                    CursorAcpTurnOutcome.Ambiguous,
                    CursorAcpTurnFailureKind.TransportLost,
                    promptResult.Blocker,
                    StopReason: null);
        }
    }

    private async Task SendCancelAsync(TurnContext context)
    {
        try
        {
            var cancelResult = await _client
                .CancelSessionAsync(
                    new CursorAcpCancelRequest { SessionId = context.SessionId },
                    CancellationToken.None)
                .ConfigureAwait(false);

            context.CancelSent.TrySetResult(cancelResult);

            if (!cancelResult.IsSent)
            {
                _logger.LogWarning(
                    "The in-band cancel for session {SessionId} could not be sent: {Blocker}",
                    context.SessionId,
                    cancelResult.Blocker);
            }
        }
        catch (Exception exception)
        {
            context.CancelSent.TrySetResult(CursorAcpCancelResult.Degraded(CursorAcpCancelFailureKind.TransportFailure,
                "The in-band cancel could not be sent; terminal confirmation is still required."));
            _logger.LogWarning(
                exception,
                "The in-band cancel for session {SessionId} failed unexpectedly.",
                context.SessionId);
        }
    }

    private async Task PumpEventsAsync(TurnContext context, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var streamEvent in _client
                               .SubscribeEventsAsync(context.SessionId, cancellationToken)
                               .ConfigureAwait(false))
            {
                ObserveStreamEvent(streamEvent);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "The Cursor ACP event stream for session {SessionId} failed; the turn awaits terminal evidence.",
                context.SessionId);
        }
        finally
        {
            context.MarkTransportClosed();
        }
    }

    // A lifecycle has one reader for its native queue and fans each event to both the supervisor
    // and the UI. Starting a second reader here would split, rather than broadcast, notifications.
    internal void ObserveStreamEvent(CursorAcpStreamEvent streamEvent)
    {
        if (streamEvent.SessionId is not { } sessionId || !_activeTurns.TryGetValue(sessionId, out var context))
            return;
        context.MarkDeliveryObserved();
        // Session statuses can be buffered from an earlier turn, even after this turn's
        // cancel was sent. Only its correlated prompt response can release ownership.
        if (streamEvent is CursorAcpStreamEvent.PermissionRequest permission && context.TryBeginApproval(permission.RequestId))
            RaiseStateChanged(context.SessionId, CursorAcpTurnState.WaitingApproval);
    }

    internal void ObserveStreamClosed()
    {
        foreach (var context in _activeTurns.Values)
            context.MarkTransportClosed();
    }

    private async Task<CursorAcpTurnResult> CompleteAsync(
        TurnContext context,
        CursorAcpPromptRequest request,
        ICheckoutLockToken? lockToken,
        CursorAcpTurnOutcome outcome,
        string? stopReason,
        CursorAcpTurnFailureKind? failureKind,
        string? blocker)
    {
        MarkState(context, ToState(outcome));

        var lockReleased = false;
        var lockRetained = false;

        if (lockToken is not null)
        {
            if (ShouldReleaseLock(outcome) && lockToken.IsHeld)
            {
                try
                {
                    await lockToken
                        .ReleaseAsync(
                            $"Cursor ACP turn {outcome} for session {request.SessionId}",
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    lockReleased = true;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to release the checkout writer lock after a {Outcome} turn on session {SessionId}.",
                        outcome,
                        request.SessionId);
                }
            }

            lockRetained = !lockReleased;
        }

        return new CursorAcpTurnResult
        {
            Outcome = outcome,
            SessionId = request.SessionId,
            ClientRequestId = request.ClientRequestId,
            StopReason = stopReason,
            FailureKind = failureKind,
            Blocker = blocker,
            StatesVisited = context.SnapshotStates(),
            LockReleased = lockReleased,
            LockRetained = lockRetained
        };
    }

    private void ResolveApproval(CursorAcpPermissionReplyRequest request)
    {
        if (request.SessionId is { } sessionId &&
            _activeTurns.TryGetValue(sessionId, out var sessionContext))
        {
            if (sessionContext.TryResolveApproval(request.PermissionId))
            {
                RaiseStateChanged(sessionId, sessionContext.CurrentState);
            }

            return;
        }

        foreach (var context in _activeTurns.Values)
        {
            if (context.TryResolveApproval(request.PermissionId))
            {
                RaiseStateChanged(context.SessionId, context.CurrentState);
                return;
            }
        }
    }

    private void MarkState(TurnContext context, CursorAcpTurnState state)
    {
        if (context.TryMark(state))
        {
            RaiseStateChanged(context.SessionId, state);
        }
    }

    private void RaiseStateChanged(string sessionId, CursorAcpTurnState state)
    {
        var handler = TurnStateChanged;

        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new CursorAcpTurnStateChangedEventArgs(sessionId, state));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "A Cursor ACP turn state observer failed.");
        }
    }

    private static async Task ObservePumpAsync(Task pumpTask)
    {
        try
        {
            await pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void ObserveDetached(Task task)
    {
        _ = task.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool ShouldReleaseLock(CursorAcpTurnOutcome outcome) =>
        outcome is CursorAcpTurnOutcome.Succeeded
            or CursorAcpTurnOutcome.Failed
            or CursorAcpTurnOutcome.Cancelled;

    private static CursorAcpTurnState ToState(CursorAcpTurnOutcome outcome) => outcome switch
    {
        CursorAcpTurnOutcome.Succeeded => CursorAcpTurnState.Succeeded,
        CursorAcpTurnOutcome.Failed => CursorAcpTurnState.Failed,
        CursorAcpTurnOutcome.TimedOut => CursorAcpTurnState.TimedOut,
        CursorAcpTurnOutcome.Cancelled => CursorAcpTurnState.Cancelled,
        CursorAcpTurnOutcome.Ambiguous => CursorAcpTurnState.Ambiguous,
        CursorAcpTurnOutcome.Orphaned => CursorAcpTurnState.Orphaned,
        _ => CursorAcpTurnState.Rejected
    };

    private static CursorAcpTurnResult Reject(
        CursorAcpPromptRequest request,
        CursorAcpTurnFailureKind failureKind,
        string blocker,
        ICheckoutLockToken? lockToken) =>
        new()
        {
            Outcome = CursorAcpTurnOutcome.Rejected,
            SessionId = request.SessionId,
            ClientRequestId = request.ClientRequestId,
            FailureKind = failureKind,
            Blocker = blocker,
            LockRetained = lockToken?.IsHeld == true,
            StatesVisited = Array.Empty<CursorAcpTurnState>()
        };

    private readonly record struct TurnCompletion(
        CursorAcpTurnOutcome? Outcome,
        CursorAcpTurnFailureKind? FailureKind,
        string? Blocker,
        string? StopReason)
    {
        public static TurnCompletion Transient => new(null, null, null, null);

        public bool IsTransient => Outcome is null;
    }

    private sealed class TurnContext
    {
        private readonly object _sync = new();
        private readonly List<CursorAcpTurnState> _states = new();

        private CursorAcpTurnState _state = CursorAcpTurnState.Queued;
        private bool _deliveryObserved;
        private bool _transportClosedObserved;
        private bool _hardTimeoutRequested;
        private readonly CancellationTokenSource _cancellation = new();
        private bool _cancellationDisposed;

        public TurnContext(string sessionId)
        {
            SessionId = sessionId;
        }

        public string SessionId { get; }

        public CancellationToken CancellationToken => _cancellation.Token;

        public TaskCompletionSource<CursorAcpCancelResult> CancelSent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RequestCancellation()
        {
            lock (_sync)
            {
                if (!_cancellationDisposed)
                    _cancellation.Cancel();
            }
        }

        public void DisposeCancellation()
        {
            lock (_sync)
            {
                _cancellationDisposed = true;
                _cancellation.Dispose();
            }
        }

        public CursorAcpTurnState CurrentState
        {
            get
            {
                lock (_sync)
                {
                    return _state;
                }
            }
        }

        public bool HardTimeoutRequested
        {
            get
            {
                lock (_sync)
                {
                    return _hardTimeoutRequested;
                }
            }
        }

        public bool TransportClosedObserved
        {
            get
            {
                lock (_sync)
                {
                    return _transportClosedObserved;
                }
            }
        }

        public bool TryMark(CursorAcpTurnState state)
        {
            lock (_sync)
            {
                if (_state == state)
                {
                    return false;
                }

                _state = state;
                AddState(state);
                return true;
            }
        }

        public bool TryBeginApproval(string permissionId)
        {
            lock (_sync)
            {
                if (_state is not (CursorAcpTurnState.Running or CursorAcpTurnState.WaitingApproval) ||
                    _pendingPermissionIds.Contains(permissionId))
                {
                    return false;
                }

                _state = CursorAcpTurnState.WaitingApproval;
                _pendingPermissionIds.Enqueue(permissionId);
                PendingPermissionId = _pendingPermissionIds.Peek();
                AddState(CursorAcpTurnState.WaitingApproval);
                return true;
            }
        }

        public bool TryResolveApproval(string permissionId)
        {
            lock (_sync)
            {
                if (_state != CursorAcpTurnState.WaitingApproval ||
                    !string.Equals(PendingPermissionId, permissionId, StringComparison.Ordinal))
                {
                    return false;
                }

                _pendingPermissionIds.Dequeue();
                PendingPermissionId = _pendingPermissionIds.TryPeek(out var next) ? next : null;
                _state = PendingPermissionId is null ? CursorAcpTurnState.Running : CursorAcpTurnState.WaitingApproval;
                AddState(_state);
                return true;
            }
        }

        public string? PendingPermissionId { get; private set; }
        private readonly Queue<string> _pendingPermissionIds = new();

        public void MarkDeliveryObserved()
        {
            lock (_sync)
            {
                _deliveryObserved = true;
            }
        }

        public bool HasDeliveryObservation()
        {
            lock (_sync)
            {
                return _deliveryObserved;
            }
        }

        public void MarkTransportClosed()
        {
            lock (_sync)
            {
                _transportClosedObserved = true;
            }
        }

        public void RequestHardTimeout()
        {
            lock (_sync)
            {
                _hardTimeoutRequested = true;
            }
        }

        public IReadOnlyList<CursorAcpTurnState> SnapshotStates()
        {
            lock (_sync)
            {
                return _states.ToArray();
            }
        }

        private void AddState(CursorAcpTurnState state)
        {
            if (_states.Count == 0 || _states[^1] != state)
            {
                _states.Add(state);
            }
        }
    }
}
