namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Request for the ACP <c>session/new</c> call (ADR-0003 §3.1, <c>acp-session-new-schema.json</c>).
/// The working directory must be a non-empty fully-qualified path; an empty MCP server array is a
/// valid minimal value.
/// </summary>
public sealed record CursorAcpNewSessionRequest
{
    /// <summary>Absolute workspace directory the session is bound to (<c>params.cwd</c>).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>MCP servers the agent may use (<c>params.mcpServers</c>); empty is valid.</summary>
    public IReadOnlyList<CursorAcpMcpServer> McpServers { get; init; } = Array.Empty<CursorAcpMcpServer>();
}
