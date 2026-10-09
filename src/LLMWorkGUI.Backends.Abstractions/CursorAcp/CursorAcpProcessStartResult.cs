namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>Classifies why the managed <c>cursor-agent acp</c> process could not be started.</summary>
public enum CursorAcpProcessStartFailureKind
{
    /// <summary>The Cursor Agent CLI is not installed or could not be located.</summary>
    ExecutableMissing,

    /// <summary>The Cursor Agent CLI exists but its version could not be probed.</summary>
    ExecutableUnresolved,

    /// <summary>The process supervisor rejected or failed the launch.</summary>
    LaunchFailed,

    /// <summary>No session was handed over; process ownership remains unresolved. Automatic retry is forbidden.</summary>
    ProcessCleanupPending
}

/// <summary>
/// Outcome of starting a managed ACP process. Failures are reported as a degraded result with an
/// exact blocker and instruction instead of an unhandled exception (ADR-0003 §11, ТЗ §6.8).
/// </summary>
public sealed record CursorAcpProcessStartResult
{
    private CursorAcpProcessStartResult(
        bool isStarted,
        ICursorAcpProcessSession? session,
        CursorAcpProcessStartFailureKind? failureKind,
        string? blocker,
        string? guidance)
    {
        IsStarted = isStarted;
        Session = session;
        FailureKind = failureKind;
        Blocker = blocker;
        Guidance = guidance;
    }

    public bool IsStarted { get; }

    public bool IsDegraded => !IsStarted;
    public bool RequiresReconciliation => FailureKind == CursorAcpProcessStartFailureKind.ProcessCleanupPending;

    public ICursorAcpProcessSession? Session { get; }

    public CursorAcpProcessStartFailureKind? FailureKind { get; }

    public string? Blocker { get; }

    public string? Guidance { get; }

    public static CursorAcpProcessStartResult Started(ICursorAcpProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new CursorAcpProcessStartResult(
            isStarted: true,
            session,
            failureKind: null,
            blocker: null,
            guidance: null);
    }

    public static CursorAcpProcessStartResult Degraded(
        CursorAcpProcessStartFailureKind failureKind,
        string blocker,
        string? guidance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blocker);

        return new CursorAcpProcessStartResult(
            isStarted: false,
            session: null,
            failureKind,
            blocker,
            guidance);
    }
}
