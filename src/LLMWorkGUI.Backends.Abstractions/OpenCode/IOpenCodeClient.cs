using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode;

public interface IOpenCodeClient
{
    Uri BaseUrl { get; }

    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    Task<OpenCodeDocResponse> GetDocAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpenCodeProviderInfo>> ListProvidersAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpenCodeModelInfo>> ListModelsAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default);

    Task<OpenCodeConfiguredProvidersResponse> ListConfiguredProvidersAsync(
        CancellationToken cancellationToken = default);

    Task<OpenCodeSessionResponse> CreateSessionAsync(
        OpenCodeCreateSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<OpenCodeSessionResponse?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpenCodeSessionResponse>> ListSessionsAsync(
        CancellationToken cancellationToken = default);

    Task<OpenCodeSessionResponse> ForkSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<bool> AbortSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<bool> SendPromptAsync(
        string sessionId,
        OpenCodePromptRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> ReplyPermissionAsync(
        string permissionId,
        OpenCodePermissionReply reply,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeEventsAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default);
}
