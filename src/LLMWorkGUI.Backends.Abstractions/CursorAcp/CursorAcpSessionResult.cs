namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>Classifies why the Cursor ACP backend could not create a native session.</summary>
public enum CursorAcpSessionFailureKind
{
    /// <summary>The initialize handshake has not completed successfully.</summary>
    NotReady,

    /// <summary>The requested working directory was empty or not fully qualified.</summary>
    InvalidWorkingDirectory,

    /// <summary>The requested native session identifier was empty.</summary>
    InvalidSessionId,

    /// <summary>The agent did not report the <c>loadSession</c> capability required by session/load.</summary>
    UnsupportedCapability,

    /// <summary>The JSON-RPC stdio transport failed before a response arrived.</summary>
    TransportFailure,

    /// <summary>The session/new request did not complete inside the bounded window.</summary>
    SessionTimeout,

    /// <summary>The agent returned a JSON-RPC error object for session/new.</summary>
    AgentError,

    /// <summary>The session/new result was missing or contained no usable session id.</summary>
    MalformedResponse
}

/// <summary>
/// Outcome of an ACP <c>session/new</c> call. Expected failures yield a degraded result with an
/// exact blocker and a user instruction; it never throws and never falls back to CLI print-mode.
/// </summary>
public sealed record CursorAcpSessionResult
{
    private CursorAcpSessionResult(
        bool isReady,
        CursorAcpSessionEvidence? evidence,
        CursorAcpSessionFailureKind? failureKind,
        string? blocker,
        string? guidance)
    {
        IsReady = isReady;
        Evidence = evidence;
        FailureKind = failureKind;
        Blocker = blocker;
        Guidance = guidance;
    }

    public bool IsReady { get; }

    public bool IsDegraded => !IsReady;

    public CursorAcpSessionEvidence? Evidence { get; }

    public CursorAcpSessionFailureKind? FailureKind { get; }

    /// <summary>Exact degraded-mode reason; null when the session was created.</summary>
    public string? Blocker { get; }

    /// <summary>User-facing recovery instruction; null when the session was created.</summary>
    public string? Guidance { get; }

    public static CursorAcpSessionResult Ready(CursorAcpSessionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return new CursorAcpSessionResult(
            isReady: true,
            evidence,
            failureKind: null,
            blocker: null,
            guidance: null);
    }

    public static CursorAcpSessionResult Degraded(
        CursorAcpSessionFailureKind failureKind,
        string blocker,
        string? guidance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blocker);

        return new CursorAcpSessionResult(
            isReady: false,
            evidence: null,
            failureKind,
            blocker,
            guidance);
    }
}
