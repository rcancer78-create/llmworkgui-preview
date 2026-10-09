namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Request for the ACP <c>session/load</c> call (ADR-0003 §3.3, <c>acp-session-load-sample.json</c>).
/// It restores a persisted native session and requires the agent to report
/// <c>agentCapabilities.loadSession == true</c>; the working directory must be a non-empty
/// fully-qualified path. An empty MCP server array is a valid minimal value.
/// </summary>
public sealed record CursorAcpLoadSessionRequest
{
    /// <summary>Persisted native session identifier to restore (<c>params.sessionId</c>).</summary>
    public required string SessionId { get; init; }

    /// <summary>Absolute workspace directory the session is bound to (<c>params.cwd</c>).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>MCP servers the agent may use (<c>params.mcpServers</c>); empty is valid.</summary>
    public IReadOnlyList<CursorAcpMcpServer> McpServers { get; init; } = Array.Empty<CursorAcpMcpServer>();
}
