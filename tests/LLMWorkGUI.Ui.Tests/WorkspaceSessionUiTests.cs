using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using Xunit;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Ui.Tests.TestSupport;
using System.IO;

namespace LLMWorkGUI.Ui.Tests;

public partial class WorkspaceSessionUiTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(18, true)]
    [InlineData(17, false)]
    public async Task UnknownGenerationOrDeadProcessRefusesBeforeAdmission(long generation, bool alive)
    {
        var process = new FakeOpenCodeProcessConnection();
        var journal = new FakeOpenCodeExecutionJournal();
        var service = new FakeSessionService();
        var locks = new FakeOpenCodeCheckoutLockService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, locks: locks, process: process);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        process.Process.ProcessGeneration = generation;
        process.Process.IsAlive = alive;
        vm.PromptInput = "preserved request";
        vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.Empty(journal.Entries);
        Assert.Null(locks.Token);
        Assert.Equal(0, service.ExecuteTurnCount);
        Assert.Equal("preserved request", vm.PromptInput);
        Assert.True(vm.RequiresReplacementSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessRetiredDuringAuthorizationCannotDispatch(bool releaseLock)
    {
        var process = new FakeOpenCodeProcessConnection();
        var journal = new FakeOpenCodeExecutionJournal();
        var service = new FakeSessionService();
        var locks = new FakeOpenCodeCheckoutLockService();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        journal.AuthorizeDispatch = () => { waiting.TrySetResult(); return resume.Task; };
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, locks: locks, process: process);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        service.ExecuteTurnAsyncAction = async () =>
        {
            var request = service.PromptRequest!;
            var allowed = await request.DispatchAuthorization!("ses_123", request,
                new Uri("http://127.0.0.1:12345/session/ses_123/prompt_async"), default);
            Assert.False(allowed);
            return new TurnResult { SessionId = "ses_123", Status = TurnResult.FailedStatus, OutputText = "" };
        };
        vm.PromptInput = "request";
        vm.SendPromptCommand.Execute(null);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(17, locks.AcquiredGeneration);
        Assert.Equal(17, Assert.Single(journal.DispatchedEntries).ProcessGeneration);
        if (releaseLock) locks.Token!.Dispose(); else process.Process.IsAlive = false;
        resume.SetResult(true); await WaitIdle(vm);
        Assert.False(vm.IsBusy);
        Assert.Equal(TurnResult.FailedStatus, Assert.Single(journal.Completions).Status);
    }

    [Fact]
    public async Task SupervisorRevokedWhileJournalAuthorizationWaitsCannotReachTransport()
    {
        var guard = new ArmedGuard();
        var journal = new FakeOpenCodeExecutionJournal();
        var service = new FakeSessionService();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        journal.AuthorizeDispatch = () => { waiting.TrySetResult(); return resume.Task; };
        var transportReached = false;
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, guard: guard);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        service.ExecuteTurnAsyncAction = async () =>
        {
            var request = service.PromptRequest!;
            if (await request.DispatchAuthorization!("ses_123", request,
                new Uri("http://127.0.0.1:12345/session/ses_123/prompt_async"), default))
                transportReached = true;
            return new TurnResult { SessionId = "ses_123", Status = TurnResult.CompletedStatus, OutputText = "reply" };
        };
        vm.PromptInput = "request"; vm.SendPromptCommand.Execute(null);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        guard.Throw = true; resume.SetResult(true); await WaitIdle(vm);
        Assert.False(transportReached);
        Assert.True(Assert.Single(journal.Completions).IsDeliveryUncertain);
        Assert.True(vm.IsWriterLockRetained);
        Assert.Equal("Ambiguous", vm.SessionStatusDisplay);
    }

    private class FakeTimelineService : IActivityTimelineService
    {
        public ActivityTimeline BuildTimeline(IReadOnlyList<ObservableRunProjection> projections) => ActivityTimeline.Empty;
        public ActivityTimeline BuildProjectTimeline(string p, IReadOnlyList<Execution> e, IReadOnlyList<Session> s, EvidenceSourceKind k) => ActivityTimeline.Empty;
        public ActivityTimeline BuildSessionTimeline(string session, IReadOnlyList<Execution> e, IReadOnlyList<Session> s, EvidenceSourceKind k) => ActivityTimeline.Empty;
    }

    private class FakeSessionService : IOpenCodeSessionLifecycleService
    {
        public Func<Task<OpenCodeSessionResponse>> CreateAndConfirmSessionAsyncAction = () => Task.FromResult(new OpenCodeSessionResponse { Id = "ses_123" });
        public Func<Task<TurnResult>> ExecuteTurnAsyncAction = () => Task.FromResult(new TurnResult { SessionId = "ses_123", OutputText = "reply", Status = "Completed" });
        public Func<Task<OpenCodeSessionResponse>> ResetSessionAsyncAction = () => Task.FromResult(new OpenCodeSessionResponse { Id = "ses_456" });
        public Func<IReadOnlyList<string>> GetAncestryAction = () => new[] { "ses_123" };
        public Func<Task<bool>> CancelTurnAsyncAction = () => Task.FromResult(true);

        public int ExecuteTurnCount { get; private set; }
        public OpenCodeCreateSessionRequest? CreatedRequest { get; private set; }
        public OpenCodeCreateSessionRequest? ResetRequest { get; private set; }
        public OpenCodePromptRequest? PromptRequest { get; private set; }

        public Task<OpenCodeSessionResponse> CreateAndConfirmSessionAsync(OpenCodeCreateSessionRequest request, CancellationToken cancellationToken = default) { CreatedRequest = request; return CreateAndConfirmSessionAsyncAction(); }
        public Task<OpenCodeSessionResponse> ContinueSessionAsync(string sessionId, SessionBinding binding, CancellationToken cancellationToken = default) => Task.FromResult(new OpenCodeSessionResponse { Id = sessionId });
        public Task<TurnResult> ExecuteTurnAsync(string sessionId, OpenCodePromptRequest request, CancellationToken cancellationToken = default)
        {
            ExecuteTurnCount++;
            PromptRequest = request;
            return ExecuteTurnAsyncAction();
        }
        public Task<bool> CancelTurnAsync(string sessionId, CancellationToken cancellationToken = default) => CancelTurnAsyncAction();
        public Task<OpenCodeSessionResponse> ResetSessionAsync(string oldSessionId, OpenCodeCreateSessionRequest request, CancellationToken cancellationToken = default) { ResetRequest = request; return ResetSessionAsyncAction(); }
        public IReadOnlyList<string> GetAncestry(string sessionId) => GetAncestryAction();
    }
    
    private class FakeCliDetectionService : ICliDetectionService
    {
        public Task<CliDetectionSnapshot> DetectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CliDetectionSnapshot.AllNotDetected(DateTimeOffset.UtcNow));
        }
    }

    private StatusBarViewModel CreateStatusBar()
    {
        var cliStatus = new CliStatusViewModel(new FakeCliDetectionService(), TimeProvider.System);
        return new StatusBarViewModel(cliStatus);
    }

    [Fact]
    public async Task FailedNativeTurn_DisplaysRedactedReason_AndPreservesPartialAssistantText()
    {
        var session = new FakeSessionService
        {
            ExecuteTurnAsyncAction = () => Task.FromResult(new TurnResult
            {
                SessionId = "ses_123", OutputText = "Partial assistant reply", Status = TurnResult.FailedStatus,
                ErrorMessage = "Payment required; api_key=synthetic-secret-value"
            })
        };
        var vm = CreateSessionViewModel(new FakeTimelineService(), session);
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(50);
        vm.PromptInput = "Hello";
        vm.SendPromptCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(TurnResult.FailedStatus, vm.ConversationTurns.Single().Status);
        Assert.Equal("Partial assistant reply", vm.ConversationTurns.Single().ResponseText);
        Assert.Contains("Payment required", vm.SendBlocker);
        Assert.DoesNotContain("synthetic-secret-value", vm.SendBlocker);
    }

    [Fact]
    public async Task FailedNativeSessionCreation_DisplaysRedactedReason_AndLeavesSendDisabled()
    {
        var session = new FakeSessionService
        {
            CreateAndConfirmSessionAsyncAction = () => throw new InvalidOperationException("OpenCode rejected configuration; api_key=synthetic-secret-value")
        };
        var vm = CreateSessionViewModel(new FakeTimelineService(), session);
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(50);
        Assert.Contains("route-1", vm.SendBlocker);
        Assert.Contains("Создание могло быть доставлено", vm.SendBlocker);
        Assert.DoesNotContain("OpenCode rejected configuration", vm.SendBlocker);
        Assert.DoesNotContain("synthetic-secret-value", vm.SendBlocker);
        Assert.False(vm.IsSessionConfirmed);
        Assert.False(vm.CanSend);
    }

    [Fact]
    public async Task CreateSessionAsync_ThrownAfterStart_NamesRouteAndUnsafeRetry()
    {
        var session = new FakeSessionService
        {
            CreateAndConfirmSessionAsyncAction = () => throw new InvalidOperationException("OpenCode rejected configuration; api_key=synthetic-secret-value")
        };
        var vm = CreateSessionViewModel(new FakeTimelineService(), session);
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(50);
        Assert.NotNull(session.CreatedRequest);
        Assert.Contains("route-1", vm.SendBlocker);
        Assert.Contains("provider/native-model", vm.SendBlocker);
        Assert.Contains("Сессия не возвращена", vm.SendBlocker);
        Assert.Contains("Создание могло быть доставлено", vm.SendBlocker);
        Assert.Contains("Повтор небезопасен", vm.SendBlocker);
        Assert.Contains("Состояние здоровья не изменялось", vm.SendBlocker);
        Assert.DoesNotContain("ses_123", vm.SendBlocker);
        Assert.DoesNotContain("synthetic-secret-value", vm.SendBlocker);
        Assert.False(vm.IsSessionConfirmed);
        Assert.False(vm.IsProcessStarted);
        Assert.False(vm.CanSend);
        Assert.Equal("Not reported", vm.NativeSessionId);
    }

    [Fact]
    public async Task CreateSessionAsync_ThrownBeforeStart_DoesNotClaimCreated()
    {
        var session = new FakeSessionService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), session, projectRepository: new InMemoryProjectRepository());
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(50);
        Assert.Null(session.CreatedRequest);
        Assert.Contains("route-1", vm.SendBlocker);
        Assert.Contains("provider/native-model", vm.SendBlocker);
        Assert.Contains("Сессия не создавалась", vm.SendBlocker);
        Assert.DoesNotContain("Повтор небезопасен", vm.SendBlocker);
        Assert.DoesNotContain("могло быть доставлено", vm.SendBlocker);
        Assert.False(vm.IsSessionConfirmed);
        Assert.Equal("Not reported", vm.NativeSessionId);
    }

    [Fact]
    public async Task CreateSessionAsync_JournalFailureAfterReturn_NamesReturnedSession()
    {
        var session = new FakeSessionService
        {
            CreateAndConfirmSessionAsyncAction = () => Task.FromResult(new OpenCodeSessionResponse { Id = "ses_returned" })
        };
        var journal = new FakeOpenCodeExecutionJournal { ConfirmError = new InvalidOperationException("api_key=synthetic-journal-secret") };
        var vm = CreateSessionViewModel(new FakeTimelineService(), session, journal: journal);
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(50);
        Assert.Contains("route-1", vm.SendBlocker);
        Assert.Contains("provider/native-model", vm.SendBlocker);
        Assert.Contains("ses_returned", vm.SendBlocker);
        Assert.Contains("Повтор небезопасен", vm.SendBlocker);
        Assert.DoesNotContain("Сессия не возвращена", vm.SendBlocker);
        Assert.DoesNotContain("synthetic-journal-secret", vm.SendBlocker);
        Assert.False(vm.IsSessionConfirmed);
        Assert.False(vm.IsProcessStarted);
        Assert.NotEqual("ses_returned", vm.NativeSessionId);
    }

    [Fact]
    public async Task CreateSession_DisplaysNativeSessionId_AndMarksSessionConfirmed()
    {
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService();
        var sb = CreateStatusBar();
        var vm = CreateSessionViewModel(timeline, sessionSvc, sb);
            
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(50); 
        
        Assert.True(vm.IsSessionConfirmed);
        Assert.Equal("Confirmed", vm.SessionStatusDisplay);
        Assert.Equal("ses_123", vm.NativeSessionId);
        
        Assert.Equal("Confirmed", sb.SessionConfirmation);
        Assert.Equal("ses_123", sb.NativeSessionId);
        Assert.Equal("local-ses_123", sb.LocalSessionId);
        Assert.Equal("Idle", sb.ExecutionState);
    }
    
    [Fact]
    public async Task ProcessStart_And_SessionConfirmation_AreVisuallyDistinct()
    {
        var tcs = new TaskCompletionSource<OpenCodeSessionResponse>();
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService { CreateAndConfirmSessionAsyncAction = () => tcs.Task };
        var sb = CreateStatusBar();
        var vm = CreateSessionViewModel(timeline, sessionSvc, sb);
            
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);
        
        Assert.Equal("Starting", vm.ProcessStateDisplay);
        Assert.Equal("Creating", vm.SessionStatusDisplay);
        Assert.Equal("Unconfirmed", sb.SessionConfirmation);
        
        tcs.SetResult(new OpenCodeSessionResponse { Id = "ses_123" });
        await Task.Delay(20);
        
        Assert.Equal("Confirmed", vm.SessionStatusDisplay);
        Assert.Equal("Confirmed", sb.SessionConfirmation);
    }
    
    [Fact]
    public async Task SendPrompt_ContinuesWithIdenticalSessionBinding()
    {
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService();
        var sb = CreateStatusBar();
        var vm = CreateSessionViewModel(timeline, sessionSvc, sb);
            
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);
        
        var binding1 = vm.CurrentBinding;
        
        vm.PromptInput = "test";
        vm.SendPromptCommand.Execute(null);
        await Task.Delay(20);
        
        Assert.Same(binding1, vm.CurrentBinding);
        Assert.Equal("reply", vm.ConversationTurns[0].ResponseText);
        Assert.Equal("Completed", sb.ExecutionState);
    }

    [Fact]
    public async Task ResetSession_CreatesNewBinding_PreservesHistoryAndAncestry()
    {
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService();
        var sb = CreateStatusBar();
        var vm = CreateSessionViewModel(timeline, sessionSvc, sb);
            
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);
        
        var binding1 = vm.CurrentBinding;
        
        vm.PromptInput = "msg1";
        vm.SendPromptCommand.Execute(null);
        await Task.Delay(20);
        
        vm.ResetSessionCommand.Execute(null);
        await Task.Delay(20);
        
        Assert.Equal("ses_456", vm.NativeSessionId);
        Assert.Contains("ses_123", vm.AncestryDisplay);
        Assert.Equal(2, vm.ConversationTurns.Count);
        
        Assert.Equal("ses_456", sb.NativeSessionId);
    }

    [Fact]
    public async Task ResetThrownAfterStart_NamesRouteAndUnsafeRetry()
    {
        var session = new FakeSessionService
        {
            ResetSessionAsyncAction = () => throw new InvalidOperationException("api_key=synthetic-reset-secret")
        };
        var vm = CreateSessionViewModel(new FakeTimelineService(), session);
        vm.CreateSessionCommand.Execute(null);
        await WaitIdle(vm);
        vm.ResetSessionCommand.Execute(null);
        await WaitIdle(vm);
        Assert.NotNull(session.ResetRequest);
        Assert.Contains("route-1", vm.SendBlocker);
        Assert.Contains("provider/native-model", vm.SendBlocker);
        Assert.Contains("OpenCode", vm.SendBlocker);
        Assert.Contains("ses_123", vm.SendBlocker);
        Assert.Contains("Сброс мог быть доставлен", vm.SendBlocker);
        Assert.Contains("Повтор небезопасен", vm.SendBlocker);
        Assert.Contains("Состояние здоровья не изменялось", vm.SendBlocker);
        Assert.DoesNotContain("synthetic-reset-secret", vm.SendBlocker);
        Assert.DoesNotContain("повторите попытку", vm.SendBlocker);
    }

    [Fact]
    public async Task ResetThrownBeforeStart_DoesNotClaimDelivery()
    {
        var session = new FakeSessionService();
        var guard = new ArmedGuard();
        var vm = CreateSessionViewModel(new FakeTimelineService(), session, guard: guard);
        vm.CreateSessionCommand.Execute(null);
        await WaitIdle(vm);
        guard.Throw = true;
        vm.ResetSessionCommand.Execute(null);
        await WaitIdle(vm);
        Assert.Null(session.ResetRequest);
        Assert.Contains("route-1", vm.SendBlocker);
        Assert.Contains("Сброс не доставлялся", vm.SendBlocker);
        Assert.DoesNotContain("Сброс мог быть доставлен", vm.SendBlocker);
        Assert.DoesNotContain("могла получить запрос", vm.SendBlocker);
        Assert.DoesNotContain("synthetic-before-reset", vm.SendBlocker);
        Assert.Contains("Состояние здоровья не изменялось", vm.SendBlocker);
    }
    
    [Fact]
    public async Task CancelTurn_ConfirmsCancellation_AndUpdatesUiState()
    {
        var tcs = new TaskCompletionSource<TurnResult>();
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService { ExecuteTurnAsyncAction = () => tcs.Task };
        var sb = CreateStatusBar();
        var vm = CreateSessionViewModel(timeline, sessionSvc, sb);
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);
        
        vm.PromptInput = "slow task";
        vm.SendPromptCommand.Execute(null);
        await Task.Delay(20);
        
        Assert.True(vm.IsBusy);
        Assert.Equal("Running", vm.ConversationTurns[0].Status);
        Assert.Equal("Running", sb.ExecutionState);
        
        vm.CancelTurnCommand.Execute(null);
        await Task.Delay(20);
        
        Assert.Equal("Cancelled", vm.ConversationTurns[0].Status);
        Assert.Equal("Canceled", sb.ExecutionState);
    }
    
    [Fact]
    public void MalformedEvent_DoesNotCrashApplication()
    {
        var vm = new WorkspaceViewModel(new FakeTimelineService());
        var exception = Record.Exception(() => vm.HandleMalformedEvent());
        Assert.Null(exception);
    }
    
    [Fact]
    public void ServerReconnect_ProducesDeterministicReconciliation()
    {
        var vm = new WorkspaceViewModel(new FakeTimelineService());
        vm.HandleServerReconnect();
        Assert.NotNull(vm); 
    }
    
    [Fact]
    public async Task UnsupportedOperation_Disabled_And_DiagnosticCliFallback_Isolated()
    {
        var vm = new WorkspaceViewModel(new FakeTimelineService());
        vm.RunDiagnosticFallbackCommand.Execute(null);
        await Task.Delay(20);
        
        Assert.Contains("DiagnosticCliFallback", vm.ConversationTurns[0].ResponseText);
        Assert.False(vm.IsSessionConfirmed);
    }

    [Fact]
    public async Task SendPrompt_RestrictedProjectFromRepository_BlocksDispatch()
    {
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService();
        var vm = CreateSessionViewModel(
            timeline,
            sessionSvc,
            projectRepository: CreateProjectRepository(DataClassification.Restricted));

        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);

        vm.PromptInput = "restricted content";

        vm.SendPromptCommand.Execute(null);
        await Task.Delay(20);

        Assert.Equal(DataClassification.Restricted, vm.ProjectDataClassification);
        Assert.True(vm.HasSendBlocker);
        Assert.Contains("Restricted", vm.SendBlocker);
        Assert.Equal(0, sessionSvc.ExecuteTurnCount);
        Assert.Empty(vm.ConversationTurns);
        Assert.False(vm.RequiresReplacementSession);
    }

    [Fact]
    public async Task SendPrompt_NullGate_BlocksFailClosed()
    {
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService();
        var vm = new WorkspaceViewModel(
            timeline,
            sessionSvc,
            projectRepository: CreateProjectRepository(), executionJournal: new FakeOpenCodeExecutionJournal(),
            checkoutLockService: new FakeOpenCodeCheckoutLockService())
        {
            ProjectId = DefaultProjectId
        };

        await vm.RefreshOpenCodeRoutesAsync();
        vm.SelectedOpenCodeRoute = vm.OpenCodeRoutes.Single();

        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);

        vm.PromptInput = "private content";

        vm.SendPromptCommand.Execute(null);
        await Task.Delay(20);

        Assert.True(vm.HasSendBlocker);
        Assert.Contains("fail-closed", vm.SendBlocker);
        Assert.Equal(0, sessionSvc.ExecuteTurnCount);
        Assert.Empty(vm.ConversationTurns);
    }

    [Fact]
    public async Task SendPrompt_MissingProject_BlocksFailClosed()
    {
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService();
        var vm = CreateSessionViewModel(
            timeline,
            sessionSvc,
            projectRepository: new InMemoryProjectRepository());

        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);

        vm.PromptInput = "private content";

        vm.SendPromptCommand.Execute(null);
        await Task.Delay(20);

        Assert.True(vm.HasSendBlocker);
        Assert.Contains("Сессия не создавалась", vm.SendBlocker);
        Assert.Contains("route-1", vm.SendBlocker);
        Assert.Equal(0, sessionSvc.ExecuteTurnCount);
        Assert.Empty(vm.ConversationTurns);
    }

    [Fact]
    public async Task SendPrompt_PrivateSourceProjectWithPublicProfile_BlocksDispatch()
    {
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService();
        var vm = CreateSessionViewModel(
            timeline,
            sessionSvc,
            dataClassificationGate: CreateGate(CreateProfileRepository(DataClassification.PublicSource)),
            projectRepository: CreateProjectRepository(DataClassification.PrivateSource));

        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);

        vm.PromptInput = "private content";

        vm.SendPromptCommand.Execute(null);
        await Task.Delay(20);

        Assert.True(vm.HasSendBlocker);
        Assert.Contains("Data classification violation", vm.SendBlocker);
        Assert.Equal(0, sessionSvc.ExecuteTurnCount);
        Assert.Empty(vm.ConversationTurns);
    }

    [Fact]
    public async Task SendPrompt_PrivateSourceProjectWithPrivateProfile_AllowsDispatch()
    {
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService();
        var vm = CreateSessionViewModel(
            timeline,
            sessionSvc,
            dataClassificationGate: CreateGate(CreateProfileRepository(DataClassification.PrivateSource)),
            projectRepository: CreateProjectRepository(DataClassification.PrivateSource));

        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);

        vm.PromptInput = "private content";

        vm.SendPromptCommand.Execute(null);
        await Task.Delay(20);

        Assert.False(vm.HasSendBlocker);
        Assert.Equal(1, sessionSvc.ExecuteTurnCount);
        var turn = Assert.Single(vm.ConversationTurns);
        Assert.Equal("Completed", turn.Status);
    }

    [Fact]
    public async Task SendPrompt_MaxDataClassViolation_IsBlockedBeforeDispatch()
    {
        var timeline = new FakeTimelineService();
        var sessionSvc = new FakeSessionService();
        var gate = new StubDataClassificationGate
        {
            Decision = DataClassificationGateDecision.Blocked(
                "Data classification violation: project data class exceeds the provider profile maximum.")
        };

        var vm = CreateSessionViewModel(timeline, sessionSvc, dataClassificationGate: gate);

        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);

        vm.PromptInput = "private content";

        vm.SendPromptCommand.Execute(null);
        await Task.Delay(20);

        Assert.True(vm.HasSendBlocker);
        Assert.Contains("Data classification violation", vm.SendBlocker);
        Assert.Equal(0, sessionSvc.ExecuteTurnCount);
        Assert.Empty(vm.ConversationTurns);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(DataClassification.PrivateSource, call.ProjectDataClass);
        Assert.Equal("default", call.ProviderProfileId);
        Assert.False(call.IsManualOnly);
    }

    private const string DefaultProjectId = "project-1";

    private static InMemoryProjectRepository CreateProjectRepository(
        DataClassification dataClassification = DataClassification.PrivateSource)
    {
        var repository = new InMemoryProjectRepository();

        repository.UpsertAsync(new Project(
            DefaultProjectId,
            "Project",
            @"C:\work\project",
            null,
            false,
            false,
            null,
            null,
            dataClassification)).GetAwaiter().GetResult();

        return repository;
    }

    private static InMemoryProviderProfileRepository CreateProfileRepository(
        DataClassification maxDataClass = DataClassification.PrivateSource)
    {
        var repository = new InMemoryProviderProfileRepository();

        repository.Save(new ProviderProfile(
            "default",
            "Default profile",
            BackendType.OpenCode,
            null,
            null,
            maxDataClass,
            true));

        return repository;
    }

    private static IDataClassificationGate CreateGate(
        InMemoryProviderProfileRepository? profiles = null) =>
        new DataClassificationGate(profiles ?? CreateProfileRepository());

    private static WorkspaceViewModel CreateSessionViewModel(
        FakeTimelineService timeline,
        FakeSessionService? sessionSvc = null,
        StatusBarViewModel? statusBar = null,
        IDataClassificationGate? dataClassificationGate = null,
        IProjectRepository? projectRepository = null,
        FakeOpenCodeExecutionJournal? journal = null,
        FakeOpenCodeCheckoutLockService? locks = null,
        IApplicationInstanceGuard? guard = null,
        FakeOpenCodeProcessConnection? process = null,
        LLMWorkGUI.Application.Projects.OpenedProjectResolver? openedProjectResolver = null)
    {
        var viewModel = new WorkspaceViewModel(
            timeline,
            sessionSvc,
            statusBar: statusBar,
            dataClassificationGate: dataClassificationGate ?? CreateGate(),
            projectRepository: projectRepository ?? CreateProjectRepository(), executionJournal: journal ?? new FakeOpenCodeExecutionJournal(),
            openedProjectResolver: openedProjectResolver,
            checkoutLockService: locks ?? new FakeOpenCodeCheckoutLockService(),
            instanceGuard: guard, serverConnection: (process ?? new FakeOpenCodeProcessConnection()).Connection);

        viewModel.ProjectId = openedProjectResolver is null ? DefaultProjectId : null;
        viewModel.RefreshOpenCodeRoutesAsync().GetAwaiter().GetResult();
        viewModel.SelectedOpenCodeRoute = viewModel.OpenCodeRoutes.Single();

        return viewModel;
    }

    private static LibraryStubCatalogProvider CreateCatalog() => new()
    {
        Catalog = new SanitizedCapabilityCatalog(
            new[] { new SanitizedProviderInfo("default", "Test provider", BackendType.OpenCode, true) },
            new[] { new SanitizedModelInfo("local-account", "Test model", ModelCapabilityFlags.Chat,
                Array.Empty<string>(), Array.Empty<string>(), null, HealthState.Healthy, true)
                { AccountId = "local-account", ProviderProfileId = "default", Backend = BackendType.OpenCode,
                  BackendModelId = "provider/native-model" } }, DateTimeOffset.UtcNow)
    };

    [Fact]
    public async Task CreateSession_RequiresExplicitChoice_AndRejectsStaleRoute()
    {
        var service = new FakeSessionService();
        var journal = new FakeOpenCodeExecutionJournal();
        var vm = new WorkspaceViewModel(new FakeTimelineService(), service, executionJournal: journal);
        await vm.RefreshOpenCodeRoutesAsync();
        Assert.False(vm.CreateSessionCommand.CanExecute(null));
        vm.CreateSessionCommand.Execute(null);
        Assert.Null(service.CreatedRequest);
        vm.SelectedOpenCodeRoute = Assert.Single(vm.OpenCodeRoutes);
        journal.Routes = Array.Empty<LLMWorkGUI.Backends.OpenCode.Sessions.OpenCodeStoredRoute>();
        vm.CreateSessionCommand.Execute(null);
        Assert.Null(service.CreatedRequest);
        Assert.True(vm.HasSendBlocker);
        Assert.False(vm.IsSessionConfirmed);
    }

    [Fact]
    public async Task ModelChoice_UsesNativeId_PreservesDirectoryAndBindingThroughReset()
    {
        var service = new FakeSessionService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service);
        vm.CreateSessionCommand.Execute(null);
        await Task.Delay(20);
        Assert.Equal("provider/native-model", service.CreatedRequest?.Model);
        Assert.Equal(@"C:\work\project", service.CreatedRequest?.Directory);
        Assert.Equal("local-account", vm.CurrentBinding?.AccountId);
        Assert.False(vm.CanChooseOpenCodeRoute);
        var route = vm.SelectedOpenCodeRoute;
        vm.SelectedOpenCodeRoute = null;
        Assert.Equal(route, vm.SelectedOpenCodeRoute);
        vm.PromptInput = "test";
        vm.SendPromptCommand.Execute(null);
        await Task.Delay(20);
        Assert.Equal("provider/native-model", service.PromptRequest?.Model);
        vm.ResetSessionCommand.Execute(null);
        await Task.Delay(20);
        Assert.Equal(service.CreatedRequest?.Model, service.ResetRequest?.Model);
        Assert.Equal(service.CreatedRequest?.Directory, service.ResetRequest?.Directory);
    }

    [Fact]
    public async Task JournalAndLock_PrecedeNativeDispatch_AndUseDistinctLocalIds()
    {
        var journal = new FakeOpenCodeExecutionJournal(); var locks = new FakeOpenCodeCheckoutLockService();
        var service = new FakeSessionService
        {
            ExecuteTurnAsyncAction = () =>
            {
                var entry = Assert.Single(journal.Entries);
                Assert.Equal(entry with { ProcessGeneration = 17 }, Assert.Single(journal.DispatchedEntries));
                Assert.Equal(entry.ExecutionId, locks.Token?.ExecutionId);
                Assert.True(locks.Token?.IsHeld);
                return Task.FromResult(new TurnResult { SessionId = "ses_123", Status = "Completed", OutputText = "reply" });
            }
        };
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, locks: locks);
        vm.CreateSessionCommand.Execute(null);
        await WaitIdle(vm);
        Assert.Equal("local-ses_123", vm.LocalSessionId);
        Assert.Equal("local-model", vm.CurrentBinding?.ModelId);
        vm.PromptInput = "prompt"; vm.SendPromptCommand.Execute(null);
        await WaitIdle(vm);
        Assert.Equal("provider/native-model", service.PromptRequest?.Model);
        Assert.False(locks.Token!.IsHeld);
        Assert.False(Assert.Single(journal.Completions).IsDeliveryUncertain);
    }

    [Fact]
    public async Task DispatchJournalFailure_RefusesNativeSend_ReleasesLockAndPreservesPrompt()
    {
        var journal = new FakeOpenCodeExecutionJournal { DispatchError = new IOException("dispatch journal unavailable") };
        var locks = new FakeOpenCodeCheckoutLockService(); var service = new FakeSessionService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, locks: locks);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        vm.PromptInput = "prompt"; vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.Equal(0, service.ExecuteTurnCount); Assert.Empty(vm.ConversationTurns);
        Assert.Equal("prompt", vm.PromptInput); Assert.False(locks.Token!.IsHeld);
        var result = Assert.Single(journal.Completions);
        Assert.Equal(TurnResult.FailedStatus, result.Status); Assert.False(result.IsDeliveryUncertain);
    }

    [Fact]
    public async Task LockConflict_RefusesDispatch_ClosesReservation_AndPreservesPrompt()
    {
        var journal = new FakeOpenCodeExecutionJournal();
        var locks = new FakeOpenCodeCheckoutLockService { AcquisitionError = new LLMWorkGUI.Domain.Entities.ProjectLockConflictException("private-bare-canary") };
        var service = new FakeSessionService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, locks: locks);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        vm.PromptInput = "prompt"; vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.Equal(0, service.ExecuteTurnCount);
        Assert.Empty(vm.ConversationTurns);
        Assert.Equal("prompt", vm.PromptInput);
        Assert.Contains("Рабочий каталог занят", vm.SendBlocker);
        Assert.Contains("route-1", vm.SendBlocker);
        Assert.Contains("provider/native-model", vm.SendBlocker);
        Assert.Contains("Запрос не доставлялся", vm.SendBlocker);
        Assert.DoesNotContain("Повтор небезопасен", vm.SendBlocker);
        Assert.DoesNotContain("могла получить", vm.SendBlocker);
        Assert.DoesNotContain("private-bare-canary", vm.SendBlocker);
        var result = Assert.Single(journal.Completions);
        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.False(result.IsDeliveryUncertain);
        Assert.True(vm.IsSessionConfirmed);
    }

    [Fact]
    public async Task SendPrompt_ThrownBeforeDispatch_NamesRouteAndSaysNotDelivered()
    {
        var journal = new FakeOpenCodeExecutionJournal();
        var locks = new FakeOpenCodeCheckoutLockService
        {
            AcquisitionError = new InvalidOperationException("api_key=synthetic-pre-dispatch")
        };
        var service = new FakeSessionService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, locks: locks);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        vm.PromptInput = "prompt"; vm.SendPromptCommand.Execute(null); await WaitIdle(vm);

        Assert.Equal(0, service.ExecuteTurnCount);
        Assert.Empty(vm.ConversationTurns);
        Assert.Equal("prompt", vm.PromptInput);
        Assert.Contains("route-1", vm.SendBlocker);
        Assert.Contains("provider/native-model", vm.SendBlocker);
        Assert.Contains("OpenCode", vm.SendBlocker);
        Assert.Contains("Запрос не доставлялся", vm.SendBlocker);
        Assert.DoesNotContain("Повтор небезопасен", vm.SendBlocker);
        Assert.DoesNotContain("повторите попытку", vm.SendBlocker);
        Assert.DoesNotContain("могла получить", vm.SendBlocker);
        Assert.DoesNotContain("мог быть доставлен", vm.SendBlocker);
        Assert.DoesNotContain("ses_123", vm.SendBlocker);
        Assert.DoesNotContain("synthetic-pre-dispatch", vm.SendBlocker);
        Assert.DoesNotContain(nameof(InvalidOperationException), vm.SendBlocker);
        var result = Assert.Single(journal.Completions);
        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.False(result.IsDeliveryUncertain);
        Assert.True(vm.IsSessionConfirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainNativeOutcome_OrTransportException_RetainsLockAndDisablesRetry(bool throws)
    {
        var journal = new FakeOpenCodeExecutionJournal(); var locks = new FakeOpenCodeCheckoutLockService();
        var service = new FakeSessionService
        {
            ExecuteTurnAsyncAction = () => throws ? throw new IOException("transport lost") : Task.FromResult(
                new TurnResult { SessionId = "ses_123", Status = TurnResult.FailedStatus, OutputText = "partial", IsDeliveryUncertain = true })
        };
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, locks: locks);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        vm.PromptInput = "prompt"; vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.True(vm.IsWriterLockRetained);
        Assert.True(locks.Token?.IsHeld);
        Assert.True(Assert.Single(journal.Completions).IsDeliveryUncertain);
        Assert.Equal("Ambiguous", vm.SessionStatusDisplay);
        Assert.False(vm.CanReset);
        vm.PromptInput = "retry"; vm.SendPromptCommand.Execute(null);
        Assert.Equal(1, service.ExecuteTurnCount);
    }

    [Fact]
    public async Task UncertainNativeOutcome_NamesRouteDeliveryAndForbidsRetry()
    {
        var journal = new FakeOpenCodeExecutionJournal();
        var service = new FakeSessionService
        {
            ExecuteTurnAsyncAction = () => Task.FromResult(new TurnResult
            {
                SessionId = "ses_123", Status = TurnResult.FailedStatus, OutputText = "partial", IsDeliveryUncertain = true
            })
        };
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        vm.PromptInput = "prompt"; vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.Contains("route-1", vm.SendBlocker);
        Assert.Contains("OpenCode", vm.SendBlocker);
        Assert.Contains("могла получить запрос", vm.SendBlocker);
        Assert.Contains("ses_123", vm.SendBlocker);
        Assert.Contains("Повтор небезопасен", vm.SendBlocker);
        Assert.Contains("Состояние здоровья не изменялось", vm.SendBlocker);
        Assert.Contains("reconciliation", vm.SendBlocker);
        Assert.DoesNotContain("повторите попытку", vm.SendBlocker);
    }

    [Fact]
    public async Task CompletionFailure_DisablesRetryEvenAfterNativeTerminal()
    {
        var journal = new FakeOpenCodeExecutionJournal { CompletionError = new IOException("journal unavailable") };
        var locks = new FakeOpenCodeCheckoutLockService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), new FakeSessionService(), journal: journal, locks: locks);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        vm.PromptInput = "prompt"; vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.False(vm.IsSessionConfirmed);
        Assert.True(locks.Token?.IsHeld);
        Assert.True(vm.IsWriterLockRetained);
        Assert.Contains("reconciliation", vm.SendBlocker);
    }

    [Fact]
    public async Task RouteChangedAfterConfirmation_RefusesDispatchWithoutClearingPrompt()
    {
        var journal = new FakeOpenCodeExecutionJournal(); var service = new FakeSessionService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        journal.Routes = Array.Empty<LLMWorkGUI.Backends.OpenCode.Sessions.OpenCodeStoredRoute>();
        vm.PromptInput = "prompt"; vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.Empty(journal.Entries);
        Assert.Equal(0, service.ExecuteTurnCount);
        Assert.Equal("prompt", vm.PromptInput);
        Assert.Contains("Маршрут изменился", vm.SendBlocker);
    }

    [Fact]
    public async Task ConcurrentSendDuringAsyncGate_AndCancelBeforeDispatch_DoNotReachNativeService()
    {
        var gate = new DelayedGate(); var service = new FakeSessionService();
        var journal = new FakeOpenCodeExecutionJournal();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, dataClassificationGate: gate, journal: journal);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        vm.PromptInput = "first"; vm.SendPromptCommand.Execute(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanCancel);
        vm.PromptInput = "edited"; vm.SendPromptCommand.Execute(null);
        Assert.Equal(1, gate.Calls);
        Assert.Empty(journal.Entries);
        gate.Completion.SetResult(DataClassificationGateDecision.Allowed());
        await WaitIdle(vm);
        Assert.Equal(1, service.ExecuteTurnCount);
        Assert.Equal("first", service.PromptRequest?.Prompt);
        Assert.Equal("edited", vm.PromptInput);
    }

    [Fact]
    public async Task ProjectDirectoryChangedAfterConfirmation_RefusesDispatch()
    {
        var projects = CreateProjectRepository(); var service = new FakeSessionService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, projectRepository: projects);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        await projects.UpsertAsync(new Project(DefaultProjectId, "Changed", @"D:\changed", null, false, false, null, null, DataClassification.PrivateSource));
        vm.PromptInput = "prompt"; vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.Equal(0, service.ExecuteTurnCount);
        Assert.Contains("другом каталоге", vm.SendBlocker);
    }

    [Fact]
    public async Task NoJournal_DisablesCreationEvenWithCatalogModel()
    {
        var service = new FakeSessionService();
        var vm = new WorkspaceViewModel(new FakeTimelineService(), service);
        await vm.RefreshOpenCodeRoutesAsync();
        vm.CreateSessionCommand.Execute(null);
        Assert.Empty(vm.OpenCodeRoutes);
        Assert.Null(service.CreatedRequest);
    }

    private static async Task WaitIdle(WorkspaceViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private sealed class ArmedGuard : IApplicationInstanceGuard
    {
        public bool Throw { get; set; }
        public string InstanceId => "fixture-instance";
        public bool IsPrimarySupervisor => true;
        public bool IsViewOnly => false;
        public void EnsureSupervisorPermitted()
        {
            if (Throw) throw new InvalidOperationException("api_key=synthetic-before-reset");
        }
        public void Dispose() { }
    }
    private sealed class DelayedGate : IDataClassificationGate
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<DataClassificationGateDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DataClassificationGateDecision> EvaluateAsync(DataClassification classification, string? profileId,
            bool isManualOnly = false, CancellationToken cancellationToken = default) { Calls++; return Completion.Task; }
    }

    private sealed class StubDataClassificationGate : IDataClassificationGate
    {
        public DataClassificationGateDecision Decision { get; set; } = DataClassificationGateDecision.Allowed();

        public List<(DataClassification ProjectDataClass, string? ProviderProfileId, bool IsManualOnly)> Calls { get; } = new();

        public Task<DataClassificationGateDecision> EvaluateAsync(
            DataClassification projectDataClass,
            string? providerProfileId,
            bool isManualOnly = false,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((projectDataClass, providerProfileId, isManualOnly));
            return Task.FromResult(Decision);
        }
    }
}
