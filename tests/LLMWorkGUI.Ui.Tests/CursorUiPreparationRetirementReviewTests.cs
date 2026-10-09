using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

// These are UI preparation/admission regressions with caller traps, not host-stop or native termination proofs.
[Collection("Cursor dispatcher isolation")]
public sealed class CursorUiPreparationRetirementReviewTests
{
    private const string Root = @"C:\owned-cursor-ui-retirement";
    private static readonly Project Project = new("owned-cursor-project", "Owned project", Root,
        null, false, true, null, null, DataClassification.PrivateSource);

    [Fact]
    public async Task ProjectResolutionFinishingAfterPanelDisposalCannotCreateANativeSession()
    {
        var projects = new OwnedProjects { HoldReads = true };
        var lifecycle = NewLifecycle();
        var model = Create(lifecycle, projects, new OwnedGate(), new OwnedJournal());
        await model.StartBackendAsync();
        var operation = model.CreateSessionAsync();
        try
        {
            Assert.Equal(1, projects.GetCalls);
            Assert.True(model.IsBusy);
            model.Dispose();
            projects.Release.SetResult(Project);
            await operation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(lifecycle.SessionRequests);
            Assert.Equal(0, lifecycle.StopCount);
            Assert.False(model.CanCreateSession);
        }
        finally
        {
            projects.Release.TrySetResult(Project);
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ClassificationFinishingAfterPanelDisposalCannotBeginJournalOrDispatch()
    {
        var gate = new OwnedGate { Hold = true };
        var journal = new OwnedJournal();
        var lifecycle = NewLifecycle();
        var model = await Ready(lifecycle, gate, journal);
        var operation = model.SendPromptAsync();
        try
        {
            Assert.Equal(1, gate.Calls);
            model.Dispose();
            gate.Release.SetResult(DataClassificationGateDecision.Allowed());
            await operation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, journal.BeginCalls);
            Assert.Empty(lifecycle.TurnRequests);
            Assert.Equal(0, lifecycle.StopCount);
        }
        finally
        {
            gate.Release.TrySetResult(DataClassificationGateDecision.Allowed());
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task JournalReservationReturningAfterPanelDisposalIsRejectedWithoutDispatch()
    {
        var journal = new OwnedJournal { HoldBegin = true };
        var lifecycle = NewLifecycle();
        var model = await Ready(lifecycle, new OwnedGate(), journal);
        var operation = model.SendPromptAsync();
        try
        {
            Assert.Equal(1, journal.BeginCalls);
            Assert.NotNull(journal.Reservation);
            model.Dispose();
            journal.Release.SetResult(journal.Reservation!);
            await operation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(lifecycle.TurnRequests);
            Assert.Equal(CursorAcpTurnOutcome.Rejected, Assert.Single(journal.Completions).Outcome);
            Assert.Equal(journal.Reservation!.NativeSessionId, journal.Completions[0].SessionId);
            Assert.False(journal.Completions[0].LockRetained);
            Assert.Equal(0, lifecycle.StopCount);
        }
        finally
        {
            if (journal.Reservation is not null) journal.Release.TrySetResult(journal.Reservation);
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<CursorWorkspaceViewModel> Ready(FakeCursorAcpSessionLifecycleService lifecycle,
        OwnedGate gate, OwnedJournal journal)
    {
        var model = Create(lifecycle, new OwnedProjects(), gate, journal);
        await model.StartBackendAsync();
        await model.CreateSessionAsync(Root);
        Assert.Single(lifecycle.SessionRequests);
        model.SelectedModeId = "ask";
        model.SelectedModeState = CapabilityState.Supported;
        model.SelectedModeAccess = CursorAcpModeAccess.ReadOnly;
        model.PromptInput = "Owned synthetic prompt; never sent to a provider.";
        Assert.True(model.CanSendPrompt);
        return model;
    }

    private static FakeCursorAcpSessionLifecycleService NewLifecycle() => new()
    {
        StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart,
        SessionHandler = () => CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "owned-native-session" })
    };

    private static CursorWorkspaceViewModel Create(FakeCursorAcpSessionLifecycleService lifecycle,
        OwnedProjects projects, OwnedGate gate, OwnedJournal journal)
    {
        var model = new CursorWorkspaceViewModel(lifecycle, new CursorAcpModePolicy(),
            dataClassificationGate: gate, projectRepository: projects, executionJournal: journal)
        { ProjectId = Project.Id };
        FakeCursorAcpExecutionJournal.SelectRoute(model);
        return model;
    }

    private sealed class OwnedProjects : IProjectRepository
    {
        public bool HoldReads { get; init; }
        public int GetCalls { get; private set; }
        public TaskCompletionSource<Project?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Project?> GetByIdAsync(string id, CancellationToken token = default)
        {
            GetCalls++;
            return HoldReads ? Release.Task : Task.FromResult<Project?>(Project);
        }
        public Task<Project?> GetByRootPathAsync(string path, CancellationToken token = default) => Task.FromResult<Project?>(Project);
        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<Project>>([Project]);
        public Task UpsertAsync(Project project, CancellationToken token = default) => throw new InvalidOperationException("No project write is allowed by this fixture.");
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => throw new InvalidOperationException("No project delete is allowed by this fixture.");
    }

    private sealed class OwnedGate : IDataClassificationGate
    {
        public bool Hold { get; init; }
        public int Calls { get; private set; }
        public TaskCompletionSource<DataClassificationGateDecision> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DataClassificationGateDecision> EvaluateAsync(DataClassification classification, string? providerId,
            bool isManualOnly = false, CancellationToken token = default)
        {
            Calls++;
            return Hold ? Release.Task : Task.FromResult(DataClassificationGateDecision.Allowed());
        }
    }

    private sealed class OwnedJournal : ICursorAcpExecutionJournal
    {
        public bool HoldBegin { get; init; }
        public int BeginCalls { get; private set; }
        public CursorAcpJournalEntry? Reservation { get; private set; }
        public List<CursorAcpTurnResult> Completions { get; } = new();
        public TaskCompletionSource<CursorAcpJournalEntry> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<CursorAcpStoredRoute>> ListRoutesAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<CursorAcpStoredRoute>>([FakeCursorAcpExecutionJournal.Route]);
        public Task<CursorAcpJournalEntry> BeginAsync(string projectId, string root, string nativeSessionId,
            CursorAcpStoredRoute route, string modeId, string requestId, string promptHash, CancellationToken token = default)
        {
            BeginCalls++;
            Reservation = new CursorAcpJournalEntry("owned-execution", "owned-session", nativeSessionId, requestId, route);
            return HoldBegin ? Release.Task : Task.FromResult(Reservation!);
        }
        public Task CompleteAsync(CursorAcpJournalEntry entry, CursorAcpTurnResult result, CancellationToken token = default)
        {
            Completions.Add(result);
            return Task.CompletedTask;
        }
    }
}
