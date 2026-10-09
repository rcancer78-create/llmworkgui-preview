namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>Request for the ACP <c>session/cancel</c> call (ADR-0003 §6).</summary>
public sealed record CursorAcpCancelRequest
{
    /// <summary>Native session identifier whose active turn is cancelled (<c>params.sessionId</c>).</summary>
    public required string SessionId { get; init; }
}

/// <summary>Classifies why the ACP <c>session/cancel</c> call could not be sent.</summary>
public enum CursorAcpCancelFailureKind
{
    /// <summary>The initialize handshake has not completed successfully.</summary>
    NotReady,

    /// <summary>The native session identifier was empty.</summary>
    InvalidSessionId,

    /// <summary>The JSON-RPC stdio transport failed before a response arrived.</summary>
    TransportFailure,

    /// <summary>The cancel request did not complete inside the bounded window.</summary>
    CancelTimeout,

    /// <summary>The agent returned a JSON-RPC error object for session/cancel.</summary>
    AgentError,

    /// <summary>The cancel result was missing or malformed.</summary>
    MalformedResponse
}

/// <summary>
/// Outcome of an ACP <c>session/cancel</c> send. Per ADR-0003 §6.2 the send itself is never a
/// terminal confirmation: an accepted cancel only means the turn moved to <c>Cancelling</c>, and
/// the execution must still await terminal evidence from the protocol event stream or a process
/// tree termination confirmed by the Process Supervisor.
/// </summary>
public sealed record CursorAcpCancelResult
{
    private CursorAcpCancelResult(
        bool isSent,
        bool isTerminal,
        CursorAcpCancelFailureKind? failureKind,
        string? blocker,
        string? guidance)
    {
        IsSent = isSent;
        IsTerminal = isTerminal;
        FailureKind = failureKind;
        Blocker = blocker;
        Guidance = guidance;
    }

    /// <summary>True when the cancel frame was accepted by the transport.</summary>
    public bool IsSent { get; }

    /// <summary>Always false: sending session/cancel is not a terminal confirmation (ADR-0003 §6.2).</summary>
    public bool IsTerminal { get; }

    public CursorAcpCancelFailureKind? FailureKind { get; }

    /// <summary>Exact degraded-mode reason; null when the cancel was accepted.</summary>
    public string? Blocker { get; }

    /// <summary>User-facing recovery instruction; null when the cancel was accepted.</summary>
    public string? Guidance { get; }

    public static CursorAcpCancelResult Accepted() =>
        new(isSent: true, isTerminal: false, failureKind: null, blocker: null, guidance: null);

    public static CursorAcpCancelResult Degraded(
        CursorAcpCancelFailureKind failureKind,
        string blocker,
        string? guidance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blocker);

        return new CursorAcpCancelResult(
            isSent: false,
            isTerminal: false,
            failureKind,
            blocker,
            guidance);
    }
}
