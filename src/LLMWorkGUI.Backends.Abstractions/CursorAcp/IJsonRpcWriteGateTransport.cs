using System.Text.Json;

namespace LLMWorkGUI.Backends.Abstractions.CursorAcp;

/// <summary>A request-specific gate runs after outbound queue waiting, before the exact serialized frame is written.</summary>
internal interface IJsonRpcWriteGateTransport : IJsonRpcTransport
{
    Task<JsonRpcResponse> SendRequestWithGateAsync(string method, JsonElement parameters,
        Func<JsonElement, CancellationToken, Task<bool>> authorizeWrite, Action dispatchStarted,
        TimeSpan? timeout = null, CancellationToken beforeWriteCancellationToken = default,
        CancellationToken cancellationToken = default);
}
