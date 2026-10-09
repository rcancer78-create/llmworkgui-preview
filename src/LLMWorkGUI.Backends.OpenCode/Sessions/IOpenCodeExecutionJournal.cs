using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;

namespace LLMWorkGUI.Backends.OpenCode.Sessions;

/// <summary>Local requested-route admission; it does not prove the native account identity.</summary>
public interface IOpenCodeExecutionJournal
{
    Task<IReadOnlyList<OpenCodeStoredRoute>> ListRoutesAsync(CancellationToken cancellationToken = default);
    Task<string> ConfirmSessionAsync(string projectId, string rootPath, string nativeSessionId,
        OpenCodeStoredRoute route, CancellationToken cancellationToken = default);
    Task<OpenCodeJournalEntry> BeginAsync(string projectId, string rootPath, string nativeSessionId,
        OpenCodeStoredRoute expectedRoute, string clientRequestId, string promptHash,
        long processGeneration,
        CancellationToken cancellationToken = default);
    Task CompleteAsync(OpenCodeJournalEntry entry, TurnResult result, CancellationToken cancellationToken = default);
    Task MarkRunningAsync(OpenCodeJournalEntry entry, CancellationToken cancellationToken = default);
    Task<bool> AuthorizePromptDispatchAsync(OpenCodeJournalEntry entry, string nativeSessionId,
        OpenCodePromptRequest request, Uri requestUri, CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(new NotSupportedException("Project dispatch authority is unavailable."));
    Task SetWaitingApprovalAsync(OpenCodeJournalEntry entry, bool waiting, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Permission waiting journal is unavailable."));
}

public sealed record OpenCodeStoredRoute(string Id, string ProviderProfileId, string AccountId,
    string ModelId, string NativeModelId);

public sealed record OpenCodeJournalEntry(string ExecutionId, string SessionId, string NativeSessionId,
    string ClientRequestId, OpenCodeStoredRoute Route)
{
    public long ProcessGeneration { get; init; }
}

/// <summary>Route identity copied at admission. It is not an observed route.</summary>
public sealed record OpenCodeDispatchDecision(string RequestedRouteId, string ProviderProfileId, string AccountId, string NativeModelId);
