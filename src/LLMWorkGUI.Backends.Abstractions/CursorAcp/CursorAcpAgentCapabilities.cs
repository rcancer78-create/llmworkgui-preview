namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Agent capabilities observed during the ACP <c>initialize</c> handshake (ADR-0003 §2.3).
/// Absent or false required capabilities are treated as an unsupported version, never as a
/// silent fallback.
/// </summary>
public sealed record CursorAcpAgentCapabilities
{
    /// <summary>Required: the agent supports <c>session/load</c> for persisted native sessions.</summary>
    public required bool LoadSession { get; init; }

    /// <summary>Required: image prompt content is accepted.</summary>
    public required bool PromptImage { get; init; }

    /// <summary>Observed audio prompt support (not required by the product baseline).</summary>
    public required bool PromptAudio { get; init; }

    /// <summary>Observed embedded-context prompt support (not required by the product baseline).</summary>
    public required bool PromptEmbeddedContext { get; init; }

    /// <summary>Required: MCP servers can be provided over HTTP.</summary>
    public required bool McpHttp { get; init; }

    /// <summary>Required: MCP servers can be provided over SSE.</summary>
    public required bool McpSse { get; init; }

    /// <summary>Required: <c>sessionCapabilities.list</c> is present for session discovery.</summary>
    public required bool SessionList { get; init; }
}
