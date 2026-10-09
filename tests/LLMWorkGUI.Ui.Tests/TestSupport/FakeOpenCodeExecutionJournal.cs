using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal sealed class FakeOpenCodeExecutionJournal : IOpenCodeExecutionJournal
{
    public static readonly OpenCodeStoredRoute Route = new("route-1", "default", "local-account", "local-model", "provider/native-model");
    public IReadOnlyList<OpenCodeStoredRoute> Routes { get; set; } = new[] { Route };
    public List<OpenCodeJournalEntry> Entries { get; } = new();
    public List<TurnResult> Completions { get; } = new();
    public Exception? CompletionError { get; set; }
    public Exception? ConfirmError { get; set; }
    public Func<Task<string>>? ConfirmSessionHandler { get; set; }
    public Exception? DispatchError { get; set; }
    public List<OpenCodeJournalEntry> DispatchedEntries { get; } = new();
    public Func<Task<bool>>? AuthorizeDispatch { get; set; }
    public Task<bool> AuthorizePromptDispatchAsync(OpenCodeJournalEntry entry, string nativeSessionId,
        OpenCodePromptRequest request, Uri requestUri, CancellationToken cancellationToken = default) =>
        AuthorizeDispatch?.Invoke() ?? Task.FromResult(true);
    public Task MarkRunningAsync(OpenCodeJournalEntry entry, CancellationToken cancellationToken = default)
    {
        if (DispatchError is not null) throw DispatchError;
        DispatchedEntries.Add(entry); return Task.CompletedTask;
    }
    public Task<IReadOnlyList<OpenCodeStoredRoute>> ListRoutesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Routes);
    public Task<string> ConfirmSessionAsync(string projectId, string rootPath, string nativeSessionId,
        OpenCodeStoredRoute route, CancellationToken cancellationToken = default)
    {
        if (ConfirmError is not null) throw ConfirmError;
        return ConfirmSessionHandler?.Invoke() ?? Task.FromResult("local-" + nativeSessionId);
    }
    public Task<OpenCodeJournalEntry> BeginAsync(string projectId, string rootPath, string nativeSessionId,
        OpenCodeStoredRoute expectedRoute, string clientRequestId, string promptHash, long processGeneration,
        CancellationToken cancellationToken = default)
    {
        if (!Routes.Contains(expectedRoute)) throw new InvalidOperationException("Маршрут изменился.");
        var entry = new OpenCodeJournalEntry(Guid.NewGuid().ToString(), "local-" + nativeSessionId, nativeSessionId, clientRequestId, expectedRoute)
            { ProcessGeneration = processGeneration };
        Entries.Add(entry);
        return Task.FromResult(entry);
    }
    public Task CompleteAsync(OpenCodeJournalEntry entry, TurnResult result, CancellationToken cancellationToken = default)
    {
        if (CompletionError is not null) throw CompletionError;
        Completions.Add(result);
        return Task.CompletedTask;
    }
}

internal sealed class FakeOpenCodeCheckoutLockService : ICheckoutLockService
{
    public Exception? AcquisitionError { get; set; }
    public FakeToken? Token { get; private set; }
    public long? AcquiredGeneration { get; private set; }
    public bool RequiresWriterLock(string? executionMode) => true;
    public bool RequiresWriterLock(LLMWorkGUI.Domain.Enums.WorkflowRole role, string? executionMode) => true;
    public Task<ICheckoutLockToken> AcquireWriterLockAsync(string projectId, string canonicalRootPath, string executionId,
        long processGeneration, CancellationToken cancellationToken = default)
    {
        if (AcquisitionError is not null) throw AcquisitionError;
        AcquiredGeneration = processGeneration;
        Token = new FakeToken(projectId, canonicalRootPath, executionId);
        return Task.FromResult<ICheckoutLockToken>(Token);
    }
    public async Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(string projectId, string canonicalRootPath, string executionId,
        long processGeneration, string? executionMode, LLMWorkGUI.Domain.Enums.WorkflowRole role = LLMWorkGUI.Domain.Enums.WorkflowRole.Unknown,
        CancellationToken cancellationToken = default) => await AcquireWriterLockAsync(projectId, canonicalRootPath, executionId, processGeneration, cancellationToken);
    internal sealed class FakeToken(string projectId, string root, string executionId) : ICheckoutLockToken
    {
        public string LockId => "lock-1";
        public string ProjectId => projectId;
        public string CanonicalRootPath => root;
        public string ExecutionId => executionId;
        public string ApplicationInstanceId => "fixture";
        public bool IsHeld { get; private set; } = true;
        public Task ReleaseAsync(string reason, CancellationToken cancellationToken = default) { IsHeld = false; return Task.CompletedTask; }
        public void Dispose() => IsHeld = false;
        public ValueTask DisposeAsync() { IsHeld = false; return ValueTask.CompletedTask; }
    }
}
