namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Terminal classification of a supervised Cursor ACP turn, strictly following the execution state
/// machine of ТЗ §6.7/§6.8. <see cref="Rejected"/> is the pre-dispatch refusal of the supervisor
/// (mode/lock/concurrency gate) and is not a turn outcome of the protocol.
/// </summary>
public enum CursorAcpTurnOutcome
{
    /// <summary>A normalized terminal success event was received.</summary>
    Succeeded,

    /// <summary>A definite terminal failure occurred before or after prompt delivery.</summary>
    Failed,

    /// <summary>The turn hard timeout elapsed while the prompt delivery status was known.</summary>
    TimedOut,

    /// <summary>The backend confirmed cancellation/termination of the turn.</summary>
    Cancelled,

    /// <summary>The prompt could have been delivered but no terminal evidence exists.</summary>
    Ambiguous,

    /// <summary>The process was lost without terminal confirmation and delivery is excluded.</summary>
    Orphaned,

    /// <summary>The supervisor refused to dispatch the turn before any prompt was sent.</summary>
    Rejected
}

/// <summary>Exact reason the supervisor refused or classified a turn.</summary>
public enum CursorAcpTurnFailureKind
{
    /// <summary>The mode decision has <c>CanSend == false</c> (e.g. Unknown state).</summary>
    ModeNotSendable,

    /// <summary>A mode requiring the checkout writer lock was used without an active lock token.</summary>
    WriterLockMissing,

    /// <summary>Another non-terminal turn is already active on the same native session.</summary>
    ConcurrentTurn,

    /// <summary>The turn was cancelled by the caller before the prompt was dispatched.</summary>
    TurnCancelledBeforeStart,

    /// <summary>The prompt was rejected before delivery (not ready, invalid request).</summary>
    PromptRejected,

    /// <summary>The transport was lost after the prompt may have been delivered.</summary>
    TransportLost,

    /// <summary>The turn hard timeout elapsed.</summary>
    TurnTimeout,

    /// <summary>Cancellation was requested but no terminal evidence arrived inside the watchdog window.</summary>
    CancellationUnconfirmed,

    /// <summary>The process was lost before any delivery evidence was observed.</summary>
    ProcessLost
}

/// <summary>
/// Normative execution states observed by the turn supervisor (ТЗ §6.7). The list of visited
/// states is reported on <see cref="CursorAcpTurnResult.StatesVisited"/> for diagnostics and tests.
/// </summary>
public enum CursorAcpTurnState
{
    /// <summary>The turn was accepted and the mode/lock gates passed.</summary>
    Queued,

    /// <summary>The turn started; no session evidence yet.</summary>
    Starting,

    /// <summary>The native session id and binding are confirmed.</summary>
    SessionConfirmed,

    /// <summary>The prompt was dispatched and the turn is executing.</summary>
    Running,

    /// <summary>The backend requested an approval.</summary>
    WaitingApproval,

    /// <summary>A cancel was requested and the supervisor is awaiting terminal evidence.</summary>
    Cancelling,

    /// <summary>Terminal success.</summary>
    Succeeded,

    /// <summary>Terminal failure.</summary>
    Failed,

    /// <summary>Terminal hard timeout.</summary>
    TimedOut,

    /// <summary>Terminal cancellation confirmed by the protocol.</summary>
    Cancelled,

    /// <summary>Terminal outcome unknown.</summary>
    Ambiguous,

    /// <summary>Process lost without terminal confirmation.</summary>
    Orphaned,

    /// <summary>The turn was refused before dispatch.</summary>
    Rejected
}

/// <summary>Result of one supervised Cursor ACP turn.</summary>
public sealed record CursorAcpTurnResult
{
    /// <summary>Terminal classification of the turn.</summary>
    public required CursorAcpTurnOutcome Outcome { get; init; }

    /// <summary>Native session the turn belonged to.</summary>
    public required string SessionId { get; init; }

    /// <summary>Turn identity copied from the prompt request.</summary>
    public string? ClientRequestId { get; init; }

    /// <summary>Terminal stop reason reported by the agent, when any.</summary>
    public string? StopReason { get; init; }

    /// <summary>Exact classification reason; null for a clean success.</summary>
    public CursorAcpTurnFailureKind? FailureKind { get; init; }

    /// <summary>Human-readable explanation of the outcome; null for a clean success.</summary>
    public string? Blocker { get; init; }

    /// <summary>Execution states visited in order (ТЗ §6.7).</summary>
    public IReadOnlyList<CursorAcpTurnState> StatesVisited { get; init; } = Array.Empty<CursorAcpTurnState>();

    /// <summary>True when the supervisor released the writer lock token after this turn.</summary>
    public bool LockReleased { get; init; }

    /// <summary>True when a writer lock token was supplied but retained (including unconfirmed timeout).</summary>
    public bool LockRetained { get; init; }

    /// <summary>False only for a pre-dispatch <see cref="CursorAcpTurnOutcome.Rejected"/> turn.</summary>
    public bool IsTerminal => Outcome != CursorAcpTurnOutcome.Rejected;

    /// <summary>
    /// True only for <see cref="CursorAcpTurnOutcome.Failed"/> and <see cref="CursorAcpTurnOutcome.TimedOut"/>:
    /// user cancel, approval deny and <see cref="CursorAcpTurnOutcome.Ambiguous"/> never consume the
    /// workflow retry budget (ТЗ §6.7).
    /// </summary>
    public bool ConsumesModelRetryBudget =>
        Outcome is CursorAcpTurnOutcome.Failed or CursorAcpTurnOutcome.TimedOut;
}

/// <summary>Observation of a normative turn state transition.</summary>
public sealed class CursorAcpTurnStateChangedEventArgs : EventArgs
{
    public CursorAcpTurnStateChangedEventArgs(string sessionId, CursorAcpTurnState state)
    {
        SessionId = sessionId;
        State = state;
    }

    public string SessionId { get; }

    public CursorAcpTurnState State { get; }
}
