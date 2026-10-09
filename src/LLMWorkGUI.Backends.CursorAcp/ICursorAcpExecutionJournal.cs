using LLMWorkGUI.Backends.Abstractions.CursorAcp;

namespace LLMWorkGUI.Backends.CursorAcp;

/// <summary>Local requested-route accounting. It never establishes native account/model identity.</summary>
public interface ICursorAcpExecutionJournal
{
    Task<IReadOnlyList<CursorAcpStoredRoute>> ListRoutesAsync(CancellationToken cancellationToken = default);
    Task<CursorAcpJournalEntry> BeginAsync(string projectId, string rootPath, string nativeSessionId,
        CursorAcpStoredRoute expectedRoute, string modeId, string clientRequestId, string promptHash,
        CancellationToken cancellationToken = default);
    Task CompleteAsync(CursorAcpJournalEntry entry, CursorAcpTurnResult result,
        CancellationToken cancellationToken = default);
    Task<bool> AuthorizePromptDispatchAsync(CursorAcpJournalEntry entry, string projectId, string rootPath,
        CursorAcpPromptRequest request, string? writerLockId, long processGeneration,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}

public sealed record CursorAcpStoredRoute(string Id, string ProviderProfileId, string AccountId,
    string ModelId, string NativeModelId)
{
    public string Display => $"{NativeModelId} · {AccountId} · {Id}";
}

public sealed record CursorAcpJournalEntry(string ExecutionId, string SessionId,
    string NativeSessionId, string ClientRequestId, CursorAcpStoredRoute Route);
