namespace LLMWorkGUI.Backends.Abstractions.StarCliProxy;

/// <summary>
/// OpenAI-compatible HTTP client for a loopback star-cliproxy instance
/// (<c>/v1/models</c>, <c>/v1/chat/completions</c>) with SSE streaming, bounded timeouts,
/// cancellation support and requested/observed route evidence (ТЗ §6.4, §6.11a, ADR-0007).
/// </summary>
public interface IStarCliProxyClient
{
    Task<IReadOnlyList<StarCliProxyModelInfo>> ListModelsAsync(
        StarCliProxyEndpoint endpoint,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StarCliProxyStreamEvent> StreamChatCompletionAsync(
        StarCliProxyEndpoint endpoint,
        StarCliProxyChatRequest request,
        CancellationToken cancellationToken = default);

    Task<StarCliProxyHealthStatus> CheckHealthAsync(
        StarCliProxyEndpoint endpoint,
        CancellationToken cancellationToken = default);
}
