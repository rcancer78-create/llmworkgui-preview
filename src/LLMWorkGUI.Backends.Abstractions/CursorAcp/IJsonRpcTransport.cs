using System.Text.Json;
using System.Threading.Channels;

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>
/// Duplex newline-delimited JSON-RPC 2.0 transport over the stdio channel of a managed
/// <c>cursor-agent acp</c> process (ADR-0003 §1). The transport correlates responses by request
/// id, routes notifications, applies bounded backpressure, tolerates malformed frames and closes
/// deterministically.
/// </summary>
public interface IJsonRpcTransport : IAsyncDisposable
{
    /// <summary>Inbound method-carrying messages (streaming updates, permission requests).</summary>
    ChannelReader<JsonRpcNotification> Notifications { get; }

    /// <summary>Number of inbound lines rejected as malformed JSON or malformed JSON-RPC frames.</summary>
    int MalformedFrameCount { get; }

    /// <summary>
    /// Sends a request frame and waits for the correlated response. Throws
    /// <see cref="JsonRpcTransportException"/> with <see cref="JsonRpcTransportFailureKind.RequestTimedOut"/>
    /// when the bounded timeout elapses, and propagates <see cref="OperationCanceledException"/>
    /// only for caller cancellation.
    /// </summary>
    Task<JsonRpcResponse> SendRequestAsync(
        string method,
        JsonElement? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a notification frame (no id, no response expected).</summary>
    Task SendNotificationAsync(
        string method,
        JsonElement? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a response frame correlated to a server-to-client request id (for example the reply to
    /// <c>session/request_permission</c>). Exactly one of <paramref name="result"/> and
    /// <paramref name="error"/> must be provided.
    /// </summary>
    Task SendResponseAsync(
        JsonElement id,
        JsonElement? result = null,
        JsonRpcError? error = null,
        CancellationToken cancellationToken = default);

    /// <summary>Flushes pending frames, fails outstanding requests and closes the transport.</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
