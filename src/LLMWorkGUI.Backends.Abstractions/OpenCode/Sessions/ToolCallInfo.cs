using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

public sealed record ToolCallInfo
{
    public required string CallId { get; init; }

    public required string Tool { get; init; }

    public string? Status { get; init; }

    public JsonElement Input { get; init; }

    public JsonElement Output { get; init; }
}
