namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>MCP server entry of the ACP <c>session/new</c> params (<c>acp-session-new-schema.json</c>).</summary>
public sealed record CursorAcpMcpServer
{
    /// <summary>Required server name; the agent rejects entries without a name.</summary>
    public required string Name { get; init; }

    /// <summary>Executable for a stdio MCP server.</summary>
    public string? Command { get; init; }

    /// <summary>Arguments passed to a stdio MCP server.</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>Transport hint: <c>stdio</c>, <c>http</c> or <c>sse</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Endpoint for http/sse MCP servers.</summary>
    public string? Url { get; init; }
}
