namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>Classifies JSON-RPC stdio transport failures without leaking child-process internals.</summary>
public enum JsonRpcTransportFailureKind
{
    /// <summary>A correlated request received no response inside the bounded timeout window.</summary>
    RequestTimedOut,

    /// <summary>The transport was closed (agent stdout reached EOF or the frame channel completed).</summary>
    TransportClosed,

    /// <summary>An outbound frame could not be written to the agent stdin stream.</summary>
    WriteFailed,

    /// <summary>The inbound reader loop failed unexpectedly.</summary>
    ReadFailed
}
