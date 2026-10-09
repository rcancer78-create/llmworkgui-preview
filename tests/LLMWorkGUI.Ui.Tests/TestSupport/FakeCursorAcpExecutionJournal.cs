using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal sealed class FakeCursorAcpExecutionJournal : ICursorAcpExecutionJournal
{
    public static readonly CursorAcpStoredRoute Route = new("fixture-route", "cursor", "fixture-account", "local-model", "native-model");
    public List<CursorAcpTurnResult> Completions { get; } = new();
    public Exception? BeginFailure { get; set; }
    public Exception? CompleteFailure { get; set; }
    public int BeginCount { get; private set; }
    public int CompleteCount { get; private set; }
    public bool DispatchAllowed { get; set; }
    public Func<Task>? BeforeAuthorizationReturnsAsync { get; set; }
    public List<long> DispatchGenerations { get; } = new();
    public async Task<bool> AuthorizePromptDispatchAsync(CursorAcpJournalEntry entry, string projectId, string rootPath,
        CursorAcpPromptRequest request, string? writerLockId, long processGeneration, CancellationToken cancellationToken = default)
    {
        DispatchGenerations.Add(processGeneration);
        if (BeforeAuthorizationReturnsAsync is not null) await BeforeAuthorizationReturnsAsync();
        return DispatchAllowed;
    }
    public Task<IReadOnlyList<CursorAcpStoredRoute>> ListRoutesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CursorAcpStoredRoute>>(new[] { Route });
    public Task<CursorAcpJournalEntry> BeginAsync(string projectId, string rootPath, string nativeSessionId,
        CursorAcpStoredRoute expectedRoute, string modeId, string clientRequestId, string promptHash, CancellationToken cancellationToken = default)
    {
        BeginCount++;
        return BeginFailure is not null
            ? Task.FromException<CursorAcpJournalEntry>(BeginFailure)
            : Task.FromResult(new CursorAcpJournalEntry("local-execution-" + Guid.NewGuid(), "local-session", nativeSessionId, clientRequestId, expectedRoute));
    }
    public Task CompleteAsync(CursorAcpJournalEntry entry, CursorAcpTurnResult result, CancellationToken cancellationToken = default)
    {
        CompleteCount++;
        Completions.Add(result);
        return CompleteFailure is not null ? Task.FromException(CompleteFailure) : Task.CompletedTask;
    }
    public static void SelectRoute(CursorWorkspaceViewModel vm) { vm.Routes.Add(Route); vm.SelectedRoute = Route; }
}
