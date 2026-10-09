namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>Classifies why the Cursor ACP backend could not reach a ready handshake state.</summary>
public enum CursorAcpHandshakeFailureKind
{
    /// <summary>The Cursor Agent CLI is not installed or could not be located.</summary>
    ExecutableMissing,

    /// <summary>The Cursor Agent CLI exists but its version could not be probed.</summary>
    ExecutableUnresolved,

    /// <summary>The managed <c>cursor-agent acp</c> process could not be started.</summary>
    ProcessStartupFailed,

    /// <summary>The JSON-RPC stdio transport failed before a validated response arrived.</summary>
    TransportFailure,

    /// <summary>The initialize request did not complete inside the bounded handshake window.</summary>
    HandshakeTimeout,

    /// <summary>The agent reported a protocol version other than the JSON number 1.</summary>
    UnsupportedVersion,

    /// <summary>A capability required by ADR-0003 §2.3 is absent or false.</summary>
    MissingRequiredCapability,

    /// <summary>The agent returned a JSON-RPC error object for initialize.</summary>
    AgentError,

    /// <summary>The initialize result did not match the expected ACP shape.</summary>
    MalformedResponse
}

/// <summary>
/// Outcome of the ACP initialize handshake. A failed handshake yields a degraded result with an
/// exact blocker and a user instruction; it never throws and never falls back to CLI print-mode.
/// </summary>
public sealed record CursorAcpHandshakeResult
{
    private CursorAcpHandshakeResult(
        bool isReady,
        CursorAcpHandshakeEvidence? evidence,
        CursorAcpHandshakeFailureKind? failureKind,
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

    public CursorAcpHandshakeEvidence? Evidence { get; }

    public CursorAcpHandshakeFailureKind? FailureKind { get; }

    /// <summary>Exact degraded-mode reason; null when the handshake succeeded.</summary>
    public string? Blocker { get; }

    /// <summary>User-facing recovery instruction; null when the handshake succeeded.</summary>
    public string? Guidance { get; }

    public static CursorAcpHandshakeResult Ready(CursorAcpHandshakeEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return new CursorAcpHandshakeResult(
            isReady: true,
            evidence,
            failureKind: null,
            blocker: null,
            guidance: null);
    }

    public static CursorAcpHandshakeResult Degraded(
        CursorAcpHandshakeFailureKind failureKind,
        string blocker,
        string? guidance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blocker);

        return new CursorAcpHandshakeResult(
            isReady: false,
            evidence: null,
            failureKind,
            blocker,
            guidance);
    }
}
