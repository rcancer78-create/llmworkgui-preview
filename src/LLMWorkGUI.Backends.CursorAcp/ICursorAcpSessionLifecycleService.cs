using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>
/// Orchestrates the full Cursor ACP backend lifecycle for one workspace: it starts the managed
/// <c>cursor-agent acp</c> process, binds the JSON-RPC transport, completes the <c>initialize</c>
/// handshake, creates or loads the native session and supervises prompt turns (ADR-0003, ТЗ §6.7,
/// §6.8). Every expected failure is reported as a typed degraded result; no silent fallback exists.
/// </summary>
public interface ICursorAcpSessionLifecycleService : IAsyncDisposable
{
    /// <summary>Currently started backend, or null when the backend is not running.</summary>
    CursorAcpBackendStartResult? Current { get; }

    /// <summary>Native session id confirmed by the agent, or null when no session is confirmed.</summary>
    string? NativeSessionId { get; }

    /// <summary>
    /// Ordered lineage of native session ids observed for this workspace. A reset appends a new id
    /// and never deletes the previous one (ТЗ §6.4).
    /// </summary>
    IReadOnlyList<string> Ancestry { get; }

    /// <summary>
    /// Starts the managed process, binds the transport and performs the handshake. Calling it twice
    /// without stopping returns <see cref="CursorAcpBackendFailureKind.AlreadyStarted"/>.
    /// </summary>
    Task<CursorAcpBackendStartResult> StartBackendAsync(
        string executionId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a new native session through <c>session/new</c>.</summary>
    Task<CursorAcpSessionResult> CreateSessionAsync(
        CursorAcpNewSessionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a persisted native session through <c>session/load</c>. It requires the
    /// <c>loadSession</c> capability and otherwise degrades without sending a request.
    /// </summary>
    Task<CursorAcpSessionResult> LoadSessionAsync(
        CursorAcpLoadSessionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one supervised prompt turn under the fail-closed mode decision and the checkout writer
    /// lock. Streaming events are surfaced through <see cref="StreamEventObserved"/> and permission
    /// requests through <see cref="PermissionRequested"/>.
    /// </summary>
    Task<CursorAcpTurnResult> ExecuteTurnAsync(
        CursorAcpTurnRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Sends an explicit one-shot permission decision for the pending approval.</summary>
    Task<CursorAcpPermissionReplyResult> ReplyPermissionAsync(
        CursorAcpPermissionReplyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests in-band cancellation of the active turn. The send is not a terminal confirmation:
    /// the turn moves to <c>Cancelling</c> and awaits terminal evidence (ADR-0003 §6.2).
    /// </summary>
    Task<CursorAcpCancelResult> CancelTurnAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a replacement native session without deleting the previous lineage entry
    /// (ТЗ §6.4). It requires an explicit caller decision and never happens implicitly.
    /// </summary>
    Task<CursorAcpSessionResult> ResetSessionAsync(
        CursorAcpNewSessionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Stops the managed process and closes the transport. Idempotent.</summary>
    Task StopBackendAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised for every normalized streaming event of the active turn.</summary>
    event EventHandler<CursorAcpStreamEvent>? StreamEventObserved;

    /// <summary>
    /// Raised when the agent requests a permission. The normalized kind is always
    /// <c>UnknownHighRisk</c>: the decision belongs to the user (ADR-0003 §4.2, ТЗ §6.9).
    /// </summary>
    event EventHandler<CursorAcpStreamEvent.PermissionRequest>? PermissionRequested;

    /// <summary>Raised for every normative turn state transition (ТЗ §6.7).</summary>
    event EventHandler<CursorAcpTurnStateChangedEventArgs>? TurnStateChanged;
}
