namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>Classifies why the ACP <c>session/prompt</c> call could not produce a terminal result.</summary>
public enum CursorAcpPromptFailureKind
{
    /// <summary>The initialize handshake has not completed successfully.</summary>
    NotReady,

    /// <summary>The native session identifier was empty.</summary>
    InvalidSessionId,

    /// <summary>The prompt text was empty.</summary>
    InvalidPrompt,

    /// <summary>The turn identity (<c>clientRequestId</c>/<c>promptHash</c>) was empty.</summary>
    InvalidRequest,

    /// <summary>The JSON-RPC stdio transport failed before a response arrived.</summary>
    TransportFailure,

    /// <summary>The prompt request did not complete inside the bounded window.</summary>
    PromptTimeout,

    /// <summary>The agent returned a JSON-RPC error object for session/prompt.</summary>
    AgentError,

    /// <summary>The prompt result was missing or contained no usable stop reason.</summary>
    MalformedResponse,

    /// <summary>Mode selection was not acknowledged; no prompt was dispatched.</summary>
    ModeSelectionFailed,

    /// <summary>Caller cancelled while preparing; no prompt was dispatched.</summary>
    CancelledBeforeDispatch,

    /// <summary>Local client already has a prompt; no request was dispatched.</summary>
    PromptBusy
}

/// <summary>
/// Outcome of an ACP <c>session/prompt</c> call. A successful result carries the terminal
/// <c>stopReason</c> from <c>acp-prompt-exchange-sample.json</c>; expected failures yield a
/// degraded result and never fall back to CLI print-mode.
/// </summary>
public sealed record CursorAcpPromptResult
{
    private CursorAcpPromptResult(
        bool isSuccess,
        string? stopReason,
        string? turnId,
        string? messageId,
        CursorAcpPromptFailureKind? failureKind,
        string? blocker,
        string? guidance)
    {
        IsSuccess = isSuccess;
        StopReason = stopReason;
        TurnId = turnId;
        MessageId = messageId;
        FailureKind = failureKind;
        Blocker = blocker;
        Guidance = guidance;
    }

    public bool IsSuccess { get; }

    public bool IsDegraded => !IsSuccess;

    /// <summary>Terminal stop reason reported by the agent, e.g. <c>endTurn</c> or <c>cancelled</c>.</summary>
    public string? StopReason { get; }

    /// <summary>Native turn identifier when the agent returned one.</summary>
    public string? TurnId { get; }

    /// <summary>Native message identifier when the agent returned one.</summary>
    public string? MessageId { get; }

    public CursorAcpPromptFailureKind? FailureKind { get; }

    /// <summary>Exact degraded-mode reason; null when the turn completed.</summary>
    public string? Blocker { get; }

    /// <summary>User-facing recovery instruction; null when the turn completed.</summary>
    public string? Guidance { get; }

    /// <summary>True when the terminal stop reason confirms an in-band cancellation.</summary>
    public bool IsCancelled =>
        string.Equals(StopReason, CursorAcpStopReasons.Cancelled, StringComparison.OrdinalIgnoreCase);

    public static CursorAcpPromptResult Completed(string stopReason, string? turnId = null, string? messageId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stopReason);

        return new CursorAcpPromptResult(
            isSuccess: true,
            stopReason,
            turnId,
            messageId,
            failureKind: null,
            blocker: null,
            guidance: null);
    }

    public static CursorAcpPromptResult Degraded(
        CursorAcpPromptFailureKind failureKind,
        string blocker,
        string? guidance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blocker);

        return new CursorAcpPromptResult(
            isSuccess: false,
            stopReason: null,
            turnId: null,
            messageId: null,
            failureKind,
            blocker,
            guidance);
    }
}

/// <summary>Stop reasons confirmed by the captured ACP prompt fixture.</summary>
public static class CursorAcpStopReasons
{
    /// <summary>The model completed the turn normally.</summary>
    public const string EndTurn = "endTurn";

    /// <summary>The backend confirmed an in-band cancellation of the turn.</summary>
    public const string Cancelled = "cancelled";
}
