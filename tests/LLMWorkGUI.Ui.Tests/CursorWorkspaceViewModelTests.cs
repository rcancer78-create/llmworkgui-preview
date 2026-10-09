using System;
using System.IO;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// UI contract of the Cursor ACP workspace panel. Every scenario uses deterministic doubles and
/// spends no model quota (ROADMAP general rule 4).
/// </summary>
[Collection("Cursor dispatcher isolation")]
public sealed class CursorWorkspaceViewModelTests
{
    private const string SessionId = "cursor-session-ui";

    [Fact]
    public void WithoutLifecycleService_ReportsBackendUnavailableAndDisablesEverything()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.IsBackendAvailable);
        Assert.False(viewModel.CanStartBackend);
        Assert.False(viewModel.CanCreateSession);
        Assert.False(viewModel.CanSendPrompt);
        Assert.Equal(CursorWorkspaceViewModel.BackendStoppedState, viewModel.BackendStateDisplay);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.UnavailableNotice));
    }

    [Fact]
    public void FreshViewModel_ReportsUnknownFieldsAsNotReported()
    {
        var viewModel = CreateViewModel(new FakeCursorAcpSessionLifecycleService());

        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.NativeSessionId);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.ProtocolVersionDisplay);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.TurnStateDisplay);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.LastStopReason);
        Assert.Equal(CursorWorkspaceViewModel.NoSessionState, viewModel.SessionStatusDisplay);
        Assert.Empty(viewModel.Ancestry);
        Assert.Empty(viewModel.StreamEvents);
    }

    [Fact]
    public async Task StartBackendAsync_Degraded_ShowsBlockerAndKeepsSessionDisabled()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = CreateViewModel(lifecycle);

        await viewModel.StartBackendAsync();

        Assert.False(viewModel.IsBackendReady);
        Assert.Contains("Degraded", viewModel.BackendStateDisplay, StringComparison.Ordinal);
        Assert.True(viewModel.HasBlocker);
        Assert.False(viewModel.CanCreateSession);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.ProtocolVersionDisplay);
    }

    [Fact]
    public async Task StartBackendAsync_Ready_ShowsProtocolVersionAndEnablesSession()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService
        {
            StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart
        };

        var viewModel = CreateViewModel(lifecycle);

        await viewModel.StartBackendAsync();

        Assert.True(viewModel.IsBackendReady);
        Assert.Equal("Ready", viewModel.BackendStateDisplay);
        Assert.Equal("1", viewModel.ProtocolVersionDisplay);
        Assert.True(viewModel.CanCreateSession);
        Assert.False(viewModel.HasBlocker);
        Assert.Equal(1, lifecycle.StartCount);
    }

    [Fact]
    public async Task StartBackendAsync_ThrownAfterStart_NamesRouteAndUnsafeRetry()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService
        {
            StartFailure = new InvalidOperationException("api_key=synthetic-start-secret")
        };
        var viewModel = CreateViewModel(lifecycle);

        await viewModel.StartBackendAsync();

        Assert.Equal(1, lifecycle.StartCount);
        Assert.Contains("fixture-route", viewModel.Blocker);
        Assert.Contains("native-model", viewModel.Blocker);
        Assert.Contains("Сессия не возвращена", viewModel.Blocker);
        Assert.Contains("Запуск мог быть доставлен", viewModel.Blocker);
        Assert.Contains("Повтор небезопасен", viewModel.Blocker);
        Assert.Contains("Состояние здоровья не изменялось", viewModel.Blocker);
        Assert.DoesNotContain(SessionId, viewModel.Blocker);
        Assert.DoesNotContain("synthetic-start-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
        Assert.DoesNotContain("повторите", viewModel.Blocker);
        Assert.DoesNotContain("Проверьте", viewModel.Blocker);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.NativeSessionId);
        Assert.False(viewModel.IsBackendReady);
    }

    [Fact]
    public async Task StartBackendAsync_BeforeStart_DoesNotClaimStarted()
    {
        var viewModel = CreateViewModel();

        await viewModel.StartBackendAsync();

        Assert.DoesNotContain("мог быть доставлен", viewModel.Blocker);
        Assert.DoesNotContain("могла быть доставлена", viewModel.Blocker);
        Assert.DoesNotContain("Сессия не возвращена", viewModel.Blocker);
        Assert.DoesNotContain(SessionId, viewModel.Blocker);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.NativeSessionId);
    }

    [Fact]
    public async Task CreateSessionAsync_Ready_ShowsNativeSessionIdAndAncestry()
    {
        var viewModel = await CreateStartedViewModelAsync();

        Assert.Equal(SessionId, viewModel.NativeSessionId);
        Assert.Equal("Confirmed", viewModel.SessionStatusDisplay);
        Assert.Equal(new[] { SessionId }, viewModel.Ancestry);
    }

    [Fact]
    public async Task CreateSessionAsync_ThrownAfterStart_NamesRouteAndUnsafeRetry()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(lifecycle);
        var requestsBefore = lifecycle.SessionRequests.Count;
        lifecycle.SessionHandler = () =>
            throw new InvalidOperationException("api_key=synthetic-create-session-secret");

        await viewModel.CreateSessionAsync(@"C:\workspace\project");

        Assert.Equal(requestsBefore + 1, lifecycle.SessionRequests.Count);
        Assert.Contains("fixture-route", viewModel.Blocker);
        Assert.Contains("native-model", viewModel.Blocker);
        Assert.Contains("Сессия не возвращена", viewModel.Blocker);
        Assert.Contains("Создание могло быть доставлено", viewModel.Blocker);
        Assert.Contains("Повтор небезопасен", viewModel.Blocker);
        Assert.Contains("Состояние здоровья не изменялось", viewModel.Blocker);
        Assert.DoesNotContain(SessionId, viewModel.Blocker);
        Assert.DoesNotContain("synthetic-create-session-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
        Assert.DoesNotContain("Проверьте", viewModel.Blocker);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.NativeSessionId);
    }

    [Fact]
    public async Task ResetSessionAsync_ThrownAfterStart_NamesRouteAndUnsafeRetry()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(lifecycle);
        var requestsBefore = lifecycle.SessionRequests.Count;
        lifecycle.SessionHandler = () =>
            throw new InvalidOperationException("api_key=synthetic-reset-session-secret");

        await viewModel.ResetSessionAsync(@"C:\workspace\project");

        Assert.Equal(requestsBefore + 1, lifecycle.SessionRequests.Count);
        Assert.Contains("fixture-route", viewModel.Blocker);
        Assert.Contains("native-model", viewModel.Blocker);
        Assert.Contains("Сессия не возвращена", viewModel.Blocker);
        Assert.Contains("Сброс мог быть доставлен", viewModel.Blocker);
        Assert.Contains("Повтор небезопасен", viewModel.Blocker);
        Assert.Contains("Состояние здоровья не изменялось", viewModel.Blocker);
        Assert.DoesNotContain(SessionId, viewModel.Blocker);
        Assert.DoesNotContain("synthetic-reset-session-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
        Assert.DoesNotContain("Проверьте", viewModel.Blocker);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.NativeSessionId);
    }

    [Fact]
    public async Task CreateSessionAsync_ThrownBeforeStart_DoesNotClaimCreated()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(
            lifecycle,
            projectRepository: new ThrowingProjectRepository());
        var requestsBefore = lifecycle.SessionRequests.Count;

        await viewModel.CreateSessionAsync();

        Assert.Equal(requestsBefore, lifecycle.SessionRequests.Count);
        Assert.Contains("fixture-route", viewModel.Blocker);
        Assert.Contains("native-model", viewModel.Blocker);
        Assert.Contains("Сессия не создавалась", viewModel.Blocker);
        Assert.DoesNotContain("Повтор небезопасен", viewModel.Blocker);
        Assert.DoesNotContain("могло быть доставлено", viewModel.Blocker);
        Assert.DoesNotContain("мог быть доставлен", viewModel.Blocker);
        Assert.DoesNotContain(SessionId, viewModel.Blocker);
        Assert.DoesNotContain("synthetic-before-session", viewModel.Blocker);
        Assert.DoesNotContain("Проверьте", viewModel.Blocker);
        Assert.Equal(SessionId, viewModel.NativeSessionId);
    }

    [Fact]
    public async Task ResetSessionAsync_ThrownBeforeStart_DoesNotClaimReset()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(
            lifecycle,
            projectRepository: new ThrowingProjectRepository());
        var requestsBefore = lifecycle.SessionRequests.Count;

        await viewModel.ResetSessionAsync();

        Assert.Equal(requestsBefore, lifecycle.SessionRequests.Count);
        Assert.Contains("fixture-route", viewModel.Blocker);
        Assert.Contains("native-model", viewModel.Blocker);
        Assert.Contains("Сессия не сбрасывалась", viewModel.Blocker);
        Assert.DoesNotContain("Повтор небезопасен", viewModel.Blocker);
        Assert.DoesNotContain("могло быть доставлено", viewModel.Blocker);
        Assert.DoesNotContain("мог быть доставлен", viewModel.Blocker);
        Assert.DoesNotContain(SessionId, viewModel.Blocker);
        Assert.DoesNotContain("synthetic-before-session", viewModel.Blocker);
        Assert.DoesNotContain("Проверьте", viewModel.Blocker);
        Assert.Equal(SessionId, viewModel.NativeSessionId);
    }

    [Fact]
    public async Task SendPromptAsync_UnknownMode_IsRefusedBeforeDispatch()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(lifecycle);

        // 'agent' is a write mode whose capability state is Unknown, so the fail-closed policy must
        // refuse it and the UI must not offer to send.
        viewModel.SelectedModeId = "agent";
        viewModel.PromptInput = "Refactor the parser.";

        Assert.False(viewModel.CanSendPrompt);
        Assert.True(viewModel.ModeRequiresWriterLock);

        await viewModel.SendPromptAsync();

        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_WithoutConfirmedSession_IsDisabled()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService
        {
            StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart
        };

        var viewModel = CreateViewModel(lifecycle);
        await viewModel.StartBackendAsync();

        viewModel.PromptInput = "Explain the failure.";

        Assert.False(viewModel.CanSendPrompt);

        await viewModel.SendPromptAsync();

        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task PermissionRequest_AwaitsExplicitDecisionAndIsAlwaysUnknownHighRisk()
    {
        StaTestRunner.EnsureApplication();
        await StaTestRunner.Run(async () =>
        {
            var lifecycle = new FakeCursorAcpSessionLifecycleService();
            var viewModel = await CreateStartedViewModelAsync(lifecycle);
    
            lifecycle.RaisePermissionRequest(new CursorAcpStreamEvent.PermissionRequest
            {
                Method = "session/request_permission",
                SessionId = SessionId,
                RequestId = "permission-1",
                Description = "Write to a workspace file"
            });
    
            Assert.True(viewModel.IsAwaitingPermission);
            Assert.Equal("Write to a workspace file", viewModel.PendingPermissionDescription);
            Assert.Equal(
                NormalizedApprovalKind.UnknownHighRisk.ToString(),
                viewModel.PendingPermissionKindDisplay);
    
            // Nothing is answered until the user acts.
            Assert.Empty(lifecycle.PermissionReplies);
    
            await viewModel.ReplyPermissionAsync(allow: false);
    
            var reply = Assert.Single(lifecycle.PermissionReplies);
            Assert.Equal(CursorAcpPermissionDecision.Deny, reply.Decision);
            Assert.Equal("permission-1", reply.PermissionId);
            Assert.False(viewModel.IsAwaitingPermission);
        });
    }

    [Fact]
    public async Task StreamEvents_AreProjectedWithKindAndSession()
    {
        StaTestRunner.EnsureApplication();
        await StaTestRunner.Run(async () =>
        {
            var lifecycle = new FakeCursorAcpSessionLifecycleService();
            var viewModel = await CreateStartedViewModelAsync(lifecycle);
    
            lifecycle.RaiseStreamEvent(new CursorAcpStreamEvent.TextChunk
            {
                Method = "session/update",
                SessionId = SessionId,
                Text = "partial answer"
            });
    
            lifecycle.RaiseStreamEvent(new CursorAcpStreamEvent.StatusUpdate
            {
                Method = "session/update",
                SessionId = null,
                Phase = "generating"
            });
    
            Assert.Equal(2, viewModel.StreamEvents.Count);
            Assert.Equal("Text", viewModel.StreamEvents[0].Kind);
            Assert.Equal(SessionId, viewModel.StreamEvents[0].SessionId);
    
            // A missing session id is surfaced honestly instead of being invented.
            Assert.Equal("Status", viewModel.StreamEvents[1].Kind);
            Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.StreamEvents[1].SessionId);
        });
    }

    [Fact]
    public async Task CancelTurnAsync_ShowsCancellingAndNotATerminalState()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(lifecycle);

        await viewModel.CancelTurnAsync();

        Assert.Equal(1, lifecycle.CancelCount);

        // The cancel ack is not terminal evidence (ADR-0003 6.2).
        Assert.Equal(CursorAcpTurnState.Cancelling.ToString(), viewModel.TurnStateDisplay);
    }

    [Fact]
    public async Task StopBackendAsync_ClearsObservedEvidence()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(lifecycle);

        await viewModel.StopBackendAsync();

        Assert.False(viewModel.IsBackendReady);
        Assert.Equal(CursorWorkspaceViewModel.BackendStoppedState, viewModel.BackendStateDisplay);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.NativeSessionId);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.ProtocolVersionDisplay);
        Assert.Equal(CursorWorkspaceViewModel.NoSessionState, viewModel.SessionStatusDisplay);
        Assert.Equal(1, lifecycle.StopCount);
    }

    [Fact]
    public async Task StopBackendAsync_ThrownAfterStart_NamesRouteAndUnsafeRetry()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService
        {
            StopFailure = new InvalidOperationException("api_key=synthetic-stop-secret")
        };
        var viewModel = await CreateStartedViewModelAsync(lifecycle);

        await viewModel.StopBackendAsync();

        Assert.Equal(1, lifecycle.StopCount);
        Assert.Contains("fixture-route", viewModel.Blocker);
        Assert.Contains("native-model", viewModel.Blocker);
        Assert.Contains(SessionId, viewModel.Blocker);
        Assert.Contains("Остановка могла быть доставлена", viewModel.Blocker);
        Assert.Contains("Повтор небезопасен", viewModel.Blocker);
        Assert.Contains("Состояние здоровья не изменялось", viewModel.Blocker);
        Assert.DoesNotContain("synthetic-stop-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
        Assert.DoesNotContain("повторите", viewModel.Blocker);
        Assert.Equal(SessionId, viewModel.NativeSessionId);
    }

    [Fact]
    public async Task StopBackendAsync_BeforeStart_DoesNotClaimDelivery()
    {
        var viewModel = CreateViewModel();

        await viewModel.StopBackendAsync();

        Assert.DoesNotContain("могла быть доставлена", viewModel.Blocker);
        Assert.DoesNotContain("мог быть доставлен", viewModel.Blocker);
        Assert.DoesNotContain("повторите", viewModel.Blocker);
    }

    [Fact]
    public async Task SendPromptAsync_LockRequiringModeWithoutProjectContext_IsRefusedAndTakesNoLock()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var lockService = new FakeCheckoutLockService();
        var viewModel = await CreateStartedViewModelAsync(
            lifecycle,
            lockService,
            projectRepository: new InMemoryProjectRepository(),
            projectId: null);

        // A Supported write mode still needs the project context to resolve; without it the send path
        // is refused fail-closed before any lock is taken.
        lifecycle.TurnHandler = _ => throw new InvalidOperationException("The turn must not be dispatched.");
        viewModel.SelectedModeId = "agent";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.PromptInput = "Rename the symbol.";

        Assert.True(viewModel.ModeRequiresWriterLock);
        Assert.False(viewModel.CanAcquireWriterLock);
        Assert.True(viewModel.CanSendPrompt);

        await viewModel.SendPromptAsync();

        Assert.True(viewModel.HasBlocker);
        Assert.Contains("fail-closed", viewModel.Blocker);
        Assert.Empty(lockService.AcquiredTokens);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_LockAcquisitionFails_ShowsBlockerAndDoesNotDispatch()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var lockService = new FakeCheckoutLockService
        {
            AcquireFailure = new InvalidOperationException("Another writer holds the checkout.")
        };

        var viewModel = await CreateStartedViewModelAsync(lifecycle, lockService);

        viewModel.ProjectId = "proj-cursor";
        viewModel.CanonicalRootPath = @"C:\workspace\project";
        viewModel.SelectedModeId = "agent";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.PromptInput = "Rename the symbol.";

        Assert.True(viewModel.CanAcquireWriterLock);

        await viewModel.SendPromptAsync();

        Assert.True(viewModel.HasBlocker);
        Assert.Contains("Запрос не доставлялся", viewModel.Blocker);
        Assert.DoesNotContain(nameof(InvalidOperationException), viewModel.Blocker);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_LockAcquisitionThrownBeforeSend_NamesRouteAndSaysNotDelivered()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var lockService = new FakeCheckoutLockService
        {
            AcquireFailure = new ProjectLockConflictException("api_key=synthetic-lock-secret")
        };
        var otherExecution = new FakeUiCheckoutLockToken(
            "proj-cursor",
            @"C:\workspace\project",
            "other-execution");
        lockService.AcquiredTokens.Add(otherExecution);
        var viewModel = await CreateStartedViewModelAsync(lifecycle, lockService);
        viewModel.ProjectId = "proj-cursor";
        viewModel.CanonicalRootPath = @"C:\workspace\project";
        viewModel.SelectedModeId = "agent";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.PromptInput = "Rename the symbol.";

        await viewModel.SendPromptAsync();

        Assert.Empty(lifecycle.TurnRequests);
        Assert.Same(otherExecution, Assert.Single(lockService.AcquiredTokens));
        Assert.True(otherExecution.IsHeld);
        Assert.Null(otherExecution.ReleaseReason);
        Assert.Contains("fixture-route", viewModel.Blocker);
        Assert.Contains("native-model", viewModel.Blocker);
        Assert.Contains("Запрос не доставлялся", viewModel.Blocker);
        Assert.DoesNotContain("Повтор небезопасен", viewModel.Blocker);
        Assert.DoesNotContain("могла получить", viewModel.Blocker);
        Assert.DoesNotContain("мог быть доставлен", viewModel.Blocker);
        Assert.DoesNotContain(SessionId, viewModel.Blocker);
        Assert.DoesNotContain("synthetic-lock-secret", viewModel.Blocker);
        Assert.DoesNotContain("ProjectLockConflictException", viewModel.Blocker);
        Assert.DoesNotContain(nameof(InvalidOperationException), viewModel.Blocker);
    }

    [Fact]
    public async Task SendPromptAsync_JournalCompletionBeforeDispatch_NamesRouteAndSaysNotDelivered()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var journal = new FakeCursorAcpExecutionJournal
        {
            CompleteFailure = new InvalidOperationException("api_key=synthetic-complete-secret")
        };
        var lockService = new FakeCheckoutLockService
        {
            AcquireFailure = new ProjectLockConflictException("api_key=synthetic-lock-held")
        };
        var viewModel = await CreateStartedViewModelAsync(lifecycle, lockService, journal: journal);
        viewModel.ProjectId = "proj-cursor";
        viewModel.CanonicalRootPath = @"C:\workspace\project";
        viewModel.SelectedModeId = "agent";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.PromptInput = "Rename the symbol.";

        await viewModel.SendPromptAsync();

        Assert.Empty(lifecycle.TurnRequests);
        Assert.Equal(1, journal.CompleteCount);
        Assert.Contains("fixture-route", viewModel.Blocker);
        Assert.Contains("native-model", viewModel.Blocker);
        Assert.Contains("Запрос не доставлялся", viewModel.Blocker);
        Assert.DoesNotContain("Повтор небезопасен", viewModel.Blocker);
        Assert.DoesNotContain("перед повторной отправкой", viewModel.Blocker);
        Assert.DoesNotContain("могла получить", viewModel.Blocker);
        Assert.DoesNotContain("мог быть доставлен", viewModel.Blocker);
        Assert.DoesNotContain(SessionId, viewModel.Blocker);
        Assert.DoesNotContain("synthetic-complete-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
    }

    [Fact]
    public async Task SendPromptAsync_LockFailureDoesNotExposeRawExceptionText()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var locks = new FakeCheckoutLockService
        { AcquireFailure = new InvalidOperationException(@"token=synthetic-private-value; path=D:\synthetic-private-customer\db") };
        var viewModel = await CreateStartedViewModelAsync(lifecycle, locks);
        viewModel.ProjectId = "proj-cursor";
        viewModel.CanonicalRootPath = @"C:\workspace\project";
        viewModel.SelectedModeId = "agent";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.PromptInput = "synthetic prompt";

        await viewModel.SendPromptAsync();

        Assert.Empty(lifecycle.TurnRequests);
        Assert.True(viewModel.HasBlocker);
        Assert.DoesNotContain("synthetic-private-value", viewModel.Blocker, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-customer", viewModel.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendPromptAsync_SupportedReadOnlyMode_SendsWithoutTakingTheWriterLock()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var lockService = new FakeCheckoutLockService();
        var viewModel = await CreateStartedViewModelAsync(lifecycle, lockService);

        // Read-only nature must be proven by both a Supported state and a declared read-only access.
        viewModel.SelectedModeId = "ask";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.SelectedModeAccess = CursorAcpModeAccess.ReadOnly;
        viewModel.PromptInput = "Explain the failure.";

        Assert.False(viewModel.ModeRequiresWriterLock);
        Assert.True(viewModel.CanSendPrompt);

        await viewModel.SendPromptAsync();

        var request = Assert.Single(lifecycle.TurnRequests);
        Assert.Null(request.LockToken);
        Assert.Empty(lockService.AcquiredTokens);
    }

    [Fact]
    public async Task SelectedModeState_Unknown_IsNeverSendableAndAlwaysRequiresTheLock()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var lockService = new FakeCheckoutLockService();
        var viewModel = await CreateStartedViewModelAsync(lifecycle, lockService);

        viewModel.ProjectId = "proj-cursor";
        viewModel.CanonicalRootPath = @"C:\workspace\project";
        viewModel.SelectedModeAccess = CursorAcpModeAccess.ReadOnly;
        viewModel.PromptInput = "Explain the failure.";

        foreach (var modeId in viewModel.AvailableModeIds)
        {
            viewModel.SelectedModeId = modeId;
            viewModel.SelectedModeState = CapabilityState.Unknown;

            Assert.Equal(nameof(CapabilityState.Unknown), viewModel.SelectedModeStateDisplay);
            Assert.True(viewModel.ModeRequiresWriterLock, modeId);
            Assert.False(viewModel.CanSendPrompt, modeId);
        }

        await viewModel.SendPromptAsync();

        Assert.Empty(lifecycle.TurnRequests);
        Assert.Empty(lockService.AcquiredTokens);
    }

    [Fact]
    public async Task SendPromptAsync_LockRequiringMode_AcquiresLockAndPassesTokenToTheTurn()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var lockService = new FakeCheckoutLockService();
        var viewModel = await CreateStartedViewModelAsync(lifecycle, lockService);

        viewModel.ProjectId = "proj-cursor";
        viewModel.CanonicalRootPath = @"C:\workspace\project";
        viewModel.SelectedModeId = "agent";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.PromptInput = "Rename the symbol.";

        await viewModel.SendPromptAsync();

        var token = Assert.Single(lockService.AcquiredTokens);
        Assert.Equal("proj-cursor", token.ProjectId);
        Assert.Equal(@"C:\workspace\project", token.CanonicalRootPath);
        Assert.NotEqual(SessionId, token.ExecutionId);
        Assert.StartsWith("local-execution-", token.ExecutionId);
        Assert.Equal(lifecycle.Current!.Session!.ProcessGeneration, Assert.Single(lockService.AcquiredGenerations));

        var request = Assert.Single(lifecycle.TurnRequests);
        Assert.Same(token, request.LockToken);
        Assert.Equal("native-model", request.Prompt.Model);
        Assert.True(request.Prompt.RequireModelAcknowledgement);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SendPromptAsync_UnknownProcessGeneration_IsRefusedBeforeAdmission(long generation)
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var locks = new FakeCheckoutLockService();
        var journal = new FakeCursorAcpExecutionJournal();
        var viewModel = await CreateStartedViewModelAsync(lifecycle, locks, journal: journal);
        ((StubCursorAcpProcessSession)lifecycle.Current!.Session!).ProcessGeneration = generation;
        viewModel.SelectedModeId = "agent";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.PromptInput = "Keep this unsubmitted prompt.";

        await viewModel.SendPromptAsync();

        Assert.Empty(lifecycle.TurnRequests);
        Assert.Empty(locks.AcquiredTokens);
        Assert.Equal(0, journal.BeginCount);
        Assert.Equal("Keep this unsubmitted prompt.", viewModel.PromptInput);
        Assert.Contains("Запрос не доставлялся", viewModel.Blocker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptAuthorization_UsesCapturedGenerationAndRefusesReplacementProcess(bool duringAuthorization)
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var journal = new FakeCursorAcpExecutionJournal { DispatchAllowed = true };
        var viewModel = await CreateStartedViewModelAsync(lifecycle, journal: journal);
        viewModel.SelectedModeId = "ask";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.SelectedModeAccess = CursorAcpModeAccess.ReadOnly;
        viewModel.PromptInput = "Explain the failure.";
        await viewModel.SendPromptAsync();
        var prompt = Assert.Single(lifecycle.TurnRequests).Prompt;
        Assert.NotNull(prompt.AuthorizeDispatchAsync);
        Assert.True(await prompt.AuthorizeDispatchAsync(prompt, CancellationToken.None));
        Assert.Equal(lifecycle.Current!.Session!.ProcessGeneration, Assert.Single(journal.DispatchGenerations));

        // Even a replacement double with the same numeric generation is a different process handle.
        async Task ReplaceProcess()
        {
            await lifecycle.StopBackendAsync();
            await lifecycle.StartBackendAsync("replacement-backend");
        }
        if (duringAuthorization) journal.BeforeAuthorizationReturnsAsync = ReplaceProcess;
        else await ReplaceProcess();
        Assert.False(await prompt.AuthorizeDispatchAsync(prompt, CancellationToken.None));
        Assert.Equal(duringAuthorization ? 2 : 1, journal.DispatchGenerations.Count);
    }

    [Fact]
    public async Task SendPromptAsync_NoSelectedRoute_BlocksBeforeLockAndKeepsPrompt()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var locks = new FakeCheckoutLockService();
        var vm = await CreateStartedViewModelAsync(lifecycle, locks);
        vm.SelectedRoute = null;
        vm.SelectedModeState = CapabilityState.Supported;
        vm.PromptInput = "Keep this request";
        await vm.SendPromptAsync();
        Assert.True(vm.HasBlocker);
        Assert.Equal("Keep this request", vm.PromptInput);
        Assert.Empty(locks.AcquiredTokens);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_AmbiguousOutcome_ReportsRetainedLockWithGuidance()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var lockService = new FakeCheckoutLockService();
        var viewModel = await CreateStartedViewModelAsync(lifecycle, lockService);

        lifecycle.TurnHandler = request => new CursorAcpTurnResult
        {
            Outcome = CursorAcpTurnOutcome.Ambiguous,
            SessionId = request.Prompt.SessionId,
            ClientRequestId = request.Prompt.ClientRequestId,
            FailureKind = CursorAcpTurnFailureKind.TransportLost,
            Blocker = "The transport was lost after the prompt may have been delivered.",
            LockRetained = true
        };

        viewModel.ProjectId = "proj-cursor";
        viewModel.CanonicalRootPath = @"C:\workspace\project";
        viewModel.SelectedModeId = "agent";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.PromptInput = "Rename the symbol.";

        await viewModel.SendPromptAsync();

        // ADR-0003 §10.4: an unconfirmed outcome keeps the lock, and the user is told why.
        Assert.True(viewModel.IsWriterLockRetained);
        Assert.Contains("retained", viewModel.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CursorAcpTurnOutcome.Ambiguous.ToString(), viewModel.TurnStateDisplay);
    }

    [Fact]
    public async Task SendPromptAsync_ThrownAfterDispatch_NamesTheRouteAndUnsafeRetry()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService
        {
            TurnHandler = _ => throw new InvalidOperationException("synthetic-secret-value")
        };
        var viewModel = await CreateStartedViewModelAsync(lifecycle, new FakeCheckoutLockService());
        viewModel.ProjectId = "proj-cursor";
        viewModel.CanonicalRootPath = @"C:\workspace\project";
        viewModel.SelectedModeId = "agent";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.PromptInput = "Rename the symbol.";

        await viewModel.SendPromptAsync();

        Assert.Contains("fixture-route", viewModel.Blocker);
        Assert.Contains("native-model", viewModel.Blocker);
        Assert.Contains("Cursor", viewModel.Blocker);
        Assert.Contains("могла получить запрос", viewModel.Blocker);
        Assert.Contains(SessionId, viewModel.Blocker);
        Assert.Contains("Повтор небезопасен", viewModel.Blocker);
        Assert.Contains("Состояние здоровья не изменялось", viewModel.Blocker);
        Assert.Contains("reconciliation", viewModel.Blocker);
        Assert.DoesNotContain("synthetic-secret-value", viewModel.Blocker);
        Assert.DoesNotContain("повторите попытку", viewModel.Blocker);
    }

    [Fact]
    public async Task SendPromptAsync_RegistrationThrownBeforeSend_NamesRouteAndSaysNotDelivered()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var journal = new FakeCursorAcpExecutionJournal
        {
            BeginFailure = new InvalidOperationException("api_key=synthetic-register-secret")
        };
        var viewModel = await CreateStartedViewModelAsync(lifecycle, journal: journal);
        viewModel.ProjectId = "proj-cursor";
        viewModel.CanonicalRootPath = @"C:\workspace\project";
        viewModel.SelectedModeId = "agent";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.PromptInput = "Rename the symbol.";

        await viewModel.SendPromptAsync();

        Assert.Equal(1, journal.BeginCount);
        Assert.Empty(lifecycle.TurnRequests);
        Assert.Contains("fixture-route", viewModel.Blocker);
        Assert.Contains("native-model", viewModel.Blocker);
        Assert.Contains("Запрос не доставлялся", viewModel.Blocker);
        Assert.DoesNotContain("Повтор небезопасен", viewModel.Blocker);
        Assert.DoesNotContain("могла получить", viewModel.Blocker);
        Assert.DoesNotContain("мог быть доставлен", viewModel.Blocker);
        Assert.DoesNotContain(SessionId, viewModel.Blocker);
        Assert.DoesNotContain("synthetic-register-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
    }

    [Fact]
    public async Task SendPromptAsync_RestrictedProjectFromRepository_BlocksDispatch()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(
            lifecycle,
            projectRepository: CreateProjectRepository(DataClassification.Restricted));

        viewModel.SelectedModeId = "ask";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.SelectedModeAccess = CursorAcpModeAccess.ReadOnly;
        viewModel.PromptInput = "Restricted content.";

        Assert.True(viewModel.CanSendPrompt);

        await viewModel.SendPromptAsync();

        Assert.Equal(DataClassification.Restricted, viewModel.ProjectDataClassification);
        Assert.True(viewModel.HasBlocker);
        Assert.Contains("Restricted", viewModel.Blocker);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_NullGate_BlocksFailClosed()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService
        {
            StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart,
            SessionHandler = () =>
                CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = SessionId })
        };

        var viewModel = CreateViewModel(
            lifecycle,
            dataClassificationGate: null,
            projectRepository: CreateProjectRepository());

        await viewModel.StartBackendAsync();
        await viewModel.CreateSessionAsync(@"C:\workspace\project");

        viewModel.SelectedModeId = "ask";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.SelectedModeAccess = CursorAcpModeAccess.ReadOnly;
        viewModel.PromptInput = "Private content.";

        await viewModel.SendPromptAsync();

        Assert.True(viewModel.HasBlocker);
        Assert.Contains("fail-closed", viewModel.Blocker);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_MissingProject_BlocksFailClosed()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(
            lifecycle,
            projectId: "missing-project");

        viewModel.SelectedModeId = "ask";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.SelectedModeAccess = CursorAcpModeAccess.ReadOnly;
        viewModel.PromptInput = "Private content.";

        await viewModel.SendPromptAsync();

        Assert.True(viewModel.HasBlocker);
        Assert.Contains("not found", viewModel.Blocker);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_ProjectDataClassification_IsAssignedFromRepository()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(
            lifecycle,
            projectRepository: CreateProjectRepository(DataClassification.Restricted));

        Assert.Equal(DataClassification.PrivateSource, viewModel.ProjectDataClassification);

        viewModel.SelectedModeId = "ask";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.SelectedModeAccess = CursorAcpModeAccess.ReadOnly;
        viewModel.PromptInput = "Restricted content.";

        await viewModel.SendPromptAsync();

        Assert.Equal(DataClassification.Restricted, viewModel.ProjectDataClassification);
    }

    [Fact]
    public async Task SendPromptAsync_MaxDataClassViolation_IsBlockedBeforeDispatch()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(
            lifecycle,
            dataClassificationGate: CreateGate(CreateProfileRepository(DataClassification.PublicSource)),
            projectRepository: CreateProjectRepository(DataClassification.PrivateSource));

        viewModel.SelectedModeId = "ask";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.SelectedModeAccess = CursorAcpModeAccess.ReadOnly;
        viewModel.PromptInput = "Private content.";

        Assert.True(viewModel.CanSendPrompt);

        await viewModel.SendPromptAsync();

        // The real gate compares the stored project class against the "cursor" profile maximum.
        Assert.True(viewModel.HasBlocker);
        Assert.Contains("Data classification violation", viewModel.Blocker);
        Assert.Contains("cursor", viewModel.Blocker);
        Assert.Contains(nameof(DataClassification.PublicSource), viewModel.Blocker);
        Assert.Equal(DataClassification.PrivateSource, viewModel.ProjectDataClassification);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_MatchingMaxDataClass_DispatchesPrompt()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService();
        var viewModel = await CreateStartedViewModelAsync(
            lifecycle,
            dataClassificationGate: CreateGate(CreateProfileRepository(DataClassification.PrivateSource)),
            projectRepository: CreateProjectRepository(DataClassification.PrivateSource));

        viewModel.SelectedModeId = "ask";
        viewModel.SelectedModeState = CapabilityState.Supported;
        viewModel.SelectedModeAccess = CursorAcpModeAccess.ReadOnly;
        viewModel.PromptInput = "Private content.";

        await viewModel.SendPromptAsync();

        Assert.False(viewModel.HasBlocker);
        Assert.Equal(DataClassification.PrivateSource, viewModel.ProjectDataClassification);
        Assert.Single(lifecycle.TurnRequests);
    }

    private const string DefaultProjectId = "proj-cursor";

    private static async Task<CursorWorkspaceViewModel> CreateStartedViewModelAsync(
        FakeCursorAcpSessionLifecycleService? lifecycle = null,
        FakeCheckoutLockService? lockService = null,
        IDataClassificationGate? dataClassificationGate = null,
        IProjectRepository? projectRepository = null,
        string? projectId = DefaultProjectId,
        FakeCursorAcpExecutionJournal? journal = null)
    {
        lifecycle ??= new FakeCursorAcpSessionLifecycleService();
        lifecycle.StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart;
        lifecycle.SessionHandler = () =>
            CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = SessionId });

        var viewModel = CreateViewModel(
            lifecycle,
            lockService,
            dataClassificationGate ?? CreateGate(),
            projectRepository ?? CreateProjectRepository(),
            projectId,
            journal);

        await viewModel.StartBackendAsync();
        await viewModel.CreateSessionAsync(@"C:\workspace\project");

        return viewModel;
    }

    private static CursorWorkspaceViewModel CreateViewModel(
        FakeCursorAcpSessionLifecycleService? lifecycle = null,
        FakeCheckoutLockService? lockService = null,
        IDataClassificationGate? dataClassificationGate = null,
        IProjectRepository? projectRepository = null,
        string? projectId = DefaultProjectId,
        FakeCursorAcpExecutionJournal? journal = null)
    {
        var viewModel = new CursorWorkspaceViewModel(
            lifecycle,
            new CursorAcpModePolicy(),
            lockService,
            dataClassificationGate,
            projectRepository,
            executionJournal: journal ?? new FakeCursorAcpExecutionJournal());
        FakeCursorAcpExecutionJournal.SelectRoute(viewModel);

        if (projectId is not null)
        {
            viewModel.ProjectId = projectId;
        }

        return viewModel;
    }

    private static InMemoryProjectRepository CreateProjectRepository(
        DataClassification dataClassification = DataClassification.PrivateSource)
    {
        var repository = new InMemoryProjectRepository();

        repository.UpsertAsync(new Project(
            DefaultProjectId,
            "Cursor project",
            @"C:\workspace\project",
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
            "cursor",
            "Cursor profile",
            BackendType.CursorAcp,
            null,
            null,
            maxDataClass,
            true));

        return repository;
    }

    private static IDataClassificationGate CreateGate(
        InMemoryProviderProfileRepository? profiles = null) =>
        new DataClassificationGate(profiles ?? CreateProfileRepository());

    private sealed class ThrowingProjectRepository : IProjectRepository
    {
        public Task UpsertAsync(Project project, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Project?> GetByIdAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromException<Project?>(new InvalidOperationException("api_key=synthetic-before-session"));

        public Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<Project?>(null);

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Project>>(Array.Empty<Project>());

        public Task<bool> DeleteAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
