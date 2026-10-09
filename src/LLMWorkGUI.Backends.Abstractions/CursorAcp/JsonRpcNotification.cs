using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Inbound JSON-RPC message that carries a method (a notification, or a server-to-client request
/// when <see cref="Id"/> is present). ACP streaming updates and permission requests arrive here.
/// </summary>
public sealed record JsonRpcNotification
{
    public required string Method { get; init; }

    public JsonElement? Parameters { get; init; }

    /// <summary>Present when the agent sent a request that expects a client response.</summary>
    public JsonElement? Id { get; init; }
}
