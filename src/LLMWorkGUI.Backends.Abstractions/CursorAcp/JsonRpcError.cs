using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>JSON-RPC 2.0 error object returned by the agent.</summary>
public sealed record JsonRpcError
{
    public required int Code { get; init; }

    public required string Message { get; init; }

    public JsonElement? Data { get; init; }
}
