using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// JSON-RPC 2.0 response correlated to a request by its id. Exactly one of
/// <see cref="Result"/> and <see cref="Error"/> is populated.
/// </summary>
public sealed record JsonRpcResponse
{
    public required JsonElement Id { get; init; }

    public JsonElement? Result { get; init; }

    public JsonRpcError? Error { get; init; }

    public bool IsError => Error is not null;
}
