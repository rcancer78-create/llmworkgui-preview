namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Validated capability evidence captured from the ACP <c>initialize</c> response
/// (<c>acp-handshake-response.json</c>, ADR-0003 §2). This is a runtime probe, not a permanent
/// contract: the agent version may drift and the handshake is re-validated on every connect.
/// </summary>
public sealed record CursorAcpHandshakeEvidence
{
    /// <summary>ACP protocol version; must be the JSON number 1 (ADR-0003 §2.2).</summary>
    public required int ProtocolVersion { get; init; }

    public required CursorAcpAgentCapabilities AgentCapabilities { get; init; }

    /// <summary>Descriptive auth methods for the UI only; no credential is read or stored.</summary>
    public required IReadOnlyList<CursorAcpAuthMethod> AuthMethods { get; init; }
}
