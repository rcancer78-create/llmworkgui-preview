namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Protocol-level Cursor ACP client. It performs the one-time <c>initialize</c> handshake over a
/// JSON-RPC stdio transport and validates the reported protocol version and required capabilities
/// (ADR-0003 §2). Expected failures are mapped to a degraded result; caller cancellation is the
/// only exception that propagates.
/// </summary>
public interface ICursorAcpClient
{
    /// <summary>Whether PromptAsync reports its local dispatch attempt through the request observer.</summary>
    bool ReportsPromptDispatch => false;

    /// <summary>Last handshake outcome; null until the first initialize attempt completes.</summary>
    CursorAcpHandshakeResult? CurrentReadiness { get; }

    /// <summary>
    /// Count of inbound notifications skipped during normalization because their method or payload
    /// was unknown or malformed. Skipped events never break the event stream (ADR-0003 §4.1).
    /// </summary>
    long SkippedStreamEventCount { get; }

    /// <summary>
    /// Sends ACP <c>initialize</c> with a numeric <c>protocolVersion: 1</c> and the LLMWorkGUI client
    /// info, then validates the response into <see cref="CursorAcpHandshakeEvidence"/>.
    /// </summary>
    Task<CursorAcpHandshakeResult> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends ACP <c>session/new</c> with the mandatory <c>cwd</c> and <c>mcpServers</c> params
    /// (ADR-0003 §3.1) and parses the native session id. Requires a ready initialize handshake;
    /// expected failures are mapped to a degraded <see cref="CursorAcpSessionResult"/> without
    /// throwing.
    /// </summary>
    Task<CursorAcpSessionResult> CreateSessionAsync(
        CursorAcpNewSessionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a persisted native session through ACP <c>session/load</c> (ADR-0003 §3.3). The
    /// call requires a ready handshake and the <c>agentCapabilities.loadSession == true</c>
    /// capability; without it the result is degraded with
    /// <see cref="CursorAcpSessionFailureKind.UnsupportedCapability"/> and no request is sent.
    /// </summary>
    Task<CursorAcpSessionResult> LoadSessionAsync(
        CursorAcpLoadSessionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends ACP <c>session/prompt</c> with structured content blocks and the optional validated
    /// model string (ADR-0003 §4), then parses the terminal <c>stopReason</c>. Expected failures
    /// are mapped to a degraded <see cref="CursorAcpPromptResult"/> without throwing.
    /// </summary>
    Task<CursorAcpPromptResult> PromptAsync(
        CursorAcpPromptRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams normalized events from the transport notifications (ADR-0003 §4.1): text deltas,
    /// thought deltas, tool calls, status updates and permission requests. Unknown methods and
    /// malformed blocks are counted and skipped without ending the stream. When <paramref name="sessionId"/>
    /// is provided, events of other sessions are filtered out.
    /// </summary>
    IAsyncEnumerable<CursorAcpStreamEvent> SubscribeEventsAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a one-shot <c>allow_once</c> or <c>deny</c> reply to a <c>session/request_permission</c>
    /// request (ADR-0003 §4.2). Persistent rules and auto-approval are excluded by construction.
    /// </summary>
    Task<CursorAcpPermissionReplyResult> ReplyPermissionAsync(
        CursorAcpPermissionReplyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends in-band ACP <c>session/cancel</c> for the active turn (ADR-0003 §6). The send is never
    /// a terminal confirmation; the caller must await terminal evidence separately.
    /// </summary>
    Task<CursorAcpCancelResult> CancelSessionAsync(
        CursorAcpCancelRequest request,
        CancellationToken cancellationToken = default);
}
