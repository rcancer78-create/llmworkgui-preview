namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Outcome of starting a supervised Cursor ACP backend: the managed <c>cursor-agent acp</c> process,
/// the bound JSON-RPC transport and the completed <c>initialize</c> handshake, or a typed degraded
/// state (ADR-0003 §1, §2, §7.2). A degraded result never hides a CLI print-mode fallback.
/// </summary>
public sealed record CursorAcpBackendStartResult
{
    private CursorAcpBackendStartResult()
    {
    }

    /// <summary>True when the process, transport and handshake are all ready.</summary>
    public bool IsReady { get; private init; }
    public bool RequiresReconciliation => FailureKind == CursorAcpBackendFailureKind.ProcessCleanupPending;

    /// <summary>Managed process session; null for a degraded start.</summary>
    public ICursorAcpProcessSession? Session { get; private init; }

    /// <summary>Protocol client bound to the live stdio channel; null for a degraded start.</summary>
    public ICursorAcpClient? Client { get; private init; }

    /// <summary>Validated handshake evidence; null for a degraded start.</summary>
    public CursorAcpHandshakeEvidence? Handshake { get; private init; }

    /// <summary>Exact reason the backend is unavailable; null when ready.</summary>
    public CursorAcpBackendFailureKind? FailureKind { get; private init; }

    /// <summary>User-facing blocker; null when ready.</summary>
    public string? Blocker { get; private init; }

    /// <summary>Actionable remediation guidance; null when ready.</summary>
    public string? Guidance { get; private init; }

    public static CursorAcpBackendStartResult Ready(
        ICursorAcpProcessSession session,
        ICursorAcpClient client,
        CursorAcpHandshakeEvidence handshake)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(handshake);

        return new CursorAcpBackendStartResult
        {
            IsReady = true,
            Session = session,
            Client = client,
            Handshake = handshake
        };
    }

    public static CursorAcpBackendStartResult Degraded(
        CursorAcpBackendFailureKind failureKind,
        string blocker,
        string? guidance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blocker);

        return new CursorAcpBackendStartResult
        {
            IsReady = false,
            FailureKind = failureKind,
            Blocker = blocker,
            Guidance = guidance ?? CursorAcpPolicy.ProcessStartupGuidance
        };
    }
}

/// <summary>Exact reason a Cursor ACP backend could not be started.</summary>
public enum CursorAcpBackendFailureKind
{
    /// <summary><c>cursor-agent</c> is not installed or could not be resolved.</summary>
    ExecutableUnavailable,

    /// <summary>The managed process could not be launched.</summary>
    ProcessLaunchFailed,

    /// <summary>The supervisor could not hand over a duplex stdio channel for the transport.</summary>
    TransportUnavailable,

    /// <summary>The ACP <c>initialize</c> handshake failed or reported an unsupported version.</summary>
    HandshakeFailed,

    /// <summary>A backend is already running for this workspace.</summary>
    AlreadyStarted,

    /// <summary>Native process ownership remains unresolved; no automatic retry is permitted.</summary>
    ProcessCleanupPending
}
