namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Typed failure of the JSON-RPC stdio transport. Callers convert it into a degraded readiness
/// result instead of propagating an unhandled exception to the UI.
/// </summary>
public sealed class JsonRpcTransportException : Exception
{
    public JsonRpcTransportException(JsonRpcTransportFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public JsonRpcTransportFailureKind Kind { get; }
}
