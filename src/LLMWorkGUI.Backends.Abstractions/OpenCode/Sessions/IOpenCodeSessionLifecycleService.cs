using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

public interface IOpenCodeSessionLifecycleService
{
    Task<OpenCodeSessionResponse> CreateAndConfirmSessionAsync(
        OpenCodeCreateSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<OpenCodeSessionResponse> ContinueSessionAsync(
        string sessionId,
        SessionBinding binding,
        CancellationToken cancellationToken = default);

    Task<TurnResult> ExecuteTurnAsync(
        string sessionId,
        OpenCodePromptRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels an owned active turn and returns true only after native terminal confirmation.
    /// An unknown or idle session returns false without sending an abort.
    /// </summary>
    Task<bool> CancelTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<OpenCodeSessionResponse> ResetSessionAsync(
        string oldSessionId,
        OpenCodeCreateSessionRequest request,
        CancellationToken cancellationToken = default);

    IReadOnlyList<string> GetAncestry(string sessionId);

    IReadOnlyList<OpenCodePendingPermission> GetPendingPermissions(string sessionId) =>
        Array.Empty<OpenCodePendingPermission>();

    Task<bool> ReplyPermissionAsync(string sessionId, string receiptId, string response,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(new NotSupportedException("Native permission replies are unavailable."));
}
