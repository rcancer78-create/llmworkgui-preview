namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Native session evidence parsed from the ACP <c>session/new</c> result. This is a baseline
/// deserializer: it accepts a non-empty session identifier from <c>result.sessionId</c> or the
/// alternative <c>result.id</c> until a live response is captured and the exact field is frozen.
/// The JSON-RPC envelope id is never used as a session id.
/// </summary>
public sealed record CursorAcpSessionEvidence
{
    /// <summary>Non-empty native session identifier assigned by the agent.</summary>
    public required string SessionId { get; init; }

    /// <summary>Validated mode IDs reported for this session; null means discovery was absent or malformed.
    /// Availability does not prove read-only access, nor identify the executing account/model.</summary>
    public IReadOnlyList<string>? AvailableModeIds { get; init; }
}
