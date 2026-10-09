using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

/// <summary>
/// UI contract of the Mirasim workspace panel. Every scenario uses deterministic doubles and spends no
/// model quota (ROADMAP general rule 4).
/// </summary>
[Collection("Cursor dispatcher isolation")]
public sealed class MirasimWorkspaceViewModelTests
{
    private const string SessionKey = "mirasim-session-1";

    private const string TurnId = "mirasim-turn-1";

    [Theory]
    [InlineData(DataClassification.PrivateSource)]
    [InlineData(DataClassification.Restricted)]
    public async Task OpenedWorkspaceSwitch_AfterCompletedTurn_RevalidatesProjectAndDoesNotCacheRoot(DataClassification otherClassification)
    {
        var projects = CreateProjectRepository();
        const string otherRoot = @"C:\work\other-mirasim-project";
        await projects.UpsertAsync(new Project("other", "Other", otherRoot, null, false, false, null, null, otherClassification));
        var settings = new InMemoryApplicationSettingsRepository();
        await settings.SetValueAsync(OpenedProjectResolver.WorkspaceSettingKey, @"C:\work\ws");
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var vm = new MirasimWorkspaceViewModel(new FakeMirasimClient(), lifecycle,
            dataClassificationGate: CreateGate(), projectRepository: projects,
            openedProjectResolver: new OpenedProjectResolver(projects, settings));
        await vm.ProbeHostAsync();
        await vm.CreateSessionAsync();
        vm.PromptInput = "Request for first project";
        await vm.SendPromptAsync();
        Assert.Single(lifecycle.TurnRequests);
        Assert.Null(vm.ProjectId);
        Assert.Null(vm.CanonicalRootPath);

        await settings.SetValueAsync(OpenedProjectResolver.WorkspaceSettingKey, otherRoot);
        vm.PromptInput = "Request for second project";
        await vm.SendPromptAsync();
        Assert.Single(lifecycle.TurnRequests);
        Assert.Contains("не совпадает", vm.Blocker);
        Assert.Equal("Request for second project", vm.PromptInput);

        await vm.CreateSessionAsync();
        Assert.Equal(otherRoot, lifecycle.SessionRequests[^1].WorkspacePath);
        await vm.SendPromptAsync();
        if (otherClassification == DataClassification.Restricted)
        {
            Assert.Single(lifecycle.TurnRequests);
            Assert.Contains("Restricted", vm.Blocker);
        }
        else
        {
            Assert.Equal(2, lifecycle.TurnRequests.Count);
            Assert.Equal("other", lifecycle.TurnRequests[^1].ProjectId);
            Assert.Equal(otherRoot, lifecycle.TurnRequests[^1].CanonicalRootPath);
        }
    }

    [Fact]
    public async Task CancelAfterCompletedTurn_DoesNotReopenWriterLockOrCallHost()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            CancelHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Ambiguous
            }
        };
        var viewModel = await CreateReadyViewModelAsync(lifecycle);
        viewModel.PromptInput = "Synthetic completed turn";
        await viewModel.SendPromptAsync();
        Assert.Equal("Completed", viewModel.Status);
        Assert.False(viewModel.IsWriterLockHeld);
        await viewModel.CancelTurnAsync();
        Assert.Empty(lifecycle.CancelRequests);
        Assert.False(viewModel.IsWriterLockHeld);
        Assert.False(viewModel.CanCancelTurn);
        Assert.Equal("Completed", viewModel.Status);
    }

    [Fact]
    public void WithoutBackend_ReportsUnavailableAndDisablesEverything()
    {
        var viewModel = new MirasimWorkspaceViewModel();

        Assert.False(viewModel.IsBackendAvailable);
        Assert.False(viewModel.CanProbeHost);
        Assert.False(viewModel.CanCreateSession);
        Assert.False(viewModel.CanSendPrompt);
        Assert.False(viewModel.CanCancelTurn);
        Assert.False(viewModel.CanReconcileTurn);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.UnavailableNoticeText));
    }

    [Fact]
    public void FreshViewModel_ReportsUnknownFieldsAsNotReportedAndKeepsManualOnlyRoute()
    {
        var viewModel = new MirasimWorkspaceViewModel(new FakeMirasimClient());

        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.InstanceId);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.HostVersion);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.HostUptime);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.HostPid);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.SessionKey);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.TurnId);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.Status);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.ErrorClass);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.AssistantResponse);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.ObservedModel);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.ObservedAccount);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.ObservedLeg);
        Assert.Equal(MirasimWorkspaceViewModel.NotProbedState, viewModel.HostStateDisplay);
        Assert.False(viewModel.IsWriterLockHeld);
        Assert.Equal(MirasimRouteModes.ManualOnly, viewModel.RouteMode);
        Assert.False(viewModel.IsRouteModeEditable);
    }

    [Fact]
    public void FreshViewModel_SelectsCodexAndGpt4oFromTheDeclaredHarnesses()
    {
        var viewModel = new MirasimWorkspaceViewModel(new FakeMirasimClient());

        Assert.Equal(new[] { "codex", "antigravity", "grok" }, viewModel.AvailableHarnesses);
        Assert.Equal("codex", viewModel.SelectedHarness);
        Assert.Equal("gpt-4o", viewModel.ModelId);
    }

    [Fact]
    public async Task ProbeHostAsync_Healthy_ShowsObservedConnectionMetadataAndStoresInstanceId()
    {
        var viewModel = new MirasimWorkspaceViewModel(
            new FakeMirasimClient(),
            new FakeMirasimSessionLifecycleService());

        await viewModel.ProbeHostAsync();

        Assert.Equal(MirasimWorkspaceViewModel.AvailableState, viewModel.HostStateDisplay);
        Assert.Equal("http://127.0.0.1:4970/", viewModel.HostUrl);
        Assert.Equal("0.0.354", viewModel.HostVersion);
        Assert.Equal("instance-1", viewModel.InstanceId);
        Assert.Equal("987654", viewModel.HostUptime);
        Assert.Equal("4321", viewModel.HostPid);
        Assert.True(viewModel.CanCreateSession);
        Assert.False(viewModel.HasBlocker);
    }

    [Fact]
    public async Task ProbeHostAsync_Unhealthy_ReportsNetworkOrTimeoutAndResetsInstanceIdWithoutCompletingProbe()
    {
        var health = new RecordingHealthCenterService();
        var client = new FakeMirasimClient
        {
            ProbeHandler = () => Task.FromResult(MirasimHealthStatus.Unavailable("host is down"))
        };

        var viewModel = new MirasimWorkspaceViewModel(client, healthCenter: health);

        await viewModel.ProbeHostAsync();

        Assert.Equal(MirasimWorkspaceViewModel.UnavailableState, viewModel.HostStateDisplay);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.InstanceId);
        Assert.Equal("host is down", viewModel.Blocker);
        Assert.False(viewModel.CanCreateSession);

        var failure = Assert.Single(health.Failures);
        Assert.Equal(HealthScope.ForBackend(MirasimWorkspaceViewModel.BackendScopeId), failure.Scope);
        Assert.Equal(HealthErrorClass.NetworkOrTimeout, failure.ErrorClass);
        Assert.Empty(health.Successes);
        Assert.Equal(0, health.CompleteProbeCalls);
    }

    [Fact]
    public async Task ProbeHostAsync_ThrownTransportFailure_IsTreatedAsUnavailableAndReported()
    {
        var health = new RecordingHealthCenterService();
        var client = new FakeMirasimClient
        {
            ProbeHandler = () => throw new HttpRequestException("connection refused")
        };

        var viewModel = new MirasimWorkspaceViewModel(client, healthCenter: health);

        await viewModel.ProbeHostAsync();

        Assert.Equal(MirasimWorkspaceViewModel.UnavailableState, viewModel.HostStateDisplay);
        Assert.Contains("Результат пробы неизвестен", viewModel.Blocker);
        Assert.DoesNotContain(nameof(HttpRequestException), viewModel.Blocker);
        Assert.Equal(HealthErrorClass.NetworkOrTimeout, Assert.Single(health.Failures).ErrorClass);
        Assert.Equal(0, health.CompleteProbeCalls);
    }

    [Fact]
    public async Task ProbeHostAsync_ThrownAfterStart_NamesHostAndUnknownResult()
    {
        var health = new RecordingHealthCenterService();
        var client = new FakeMirasimClient
        {
            ProbeHandler = () => throw new InvalidOperationException("api_key=synthetic-probe-secret")
        };
        var viewModel = new MirasimWorkspaceViewModel(client, healthCenter: health);

        await viewModel.ProbeHostAsync();

        Assert.Equal(1, client.ProbeCount);
        Assert.Contains("http://127.0.0.1:4970/", viewModel.Blocker);
        Assert.Contains("codex", viewModel.Blocker);
        Assert.Contains("Результат пробы неизвестен", viewModel.Blocker);
        Assert.Contains("Повтор небезопасен", viewModel.Blocker);
        Assert.Contains("Зафиксирован существующий сбой пробы", viewModel.Blocker);
        Assert.DoesNotContain("synthetic-probe-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
        Assert.DoesNotContain("доставлен", viewModel.Blocker);
        Assert.DoesNotContain("запрос", viewModel.Blocker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("могла получить", viewModel.Blocker);
        Assert.DoesNotContain("instance-1", viewModel.Blocker);
        Assert.Equal(HealthErrorClass.NetworkOrTimeout, Assert.Single(health.Failures).ErrorClass);
        Assert.Equal(0, health.CompleteProbeCalls);
    }

    [Fact]
    public async Task ProbeHostAsync_BeforeStart_SaysProbeDidNotRun()
    {
        var health = new RecordingHealthCenterService();
        var viewModel = new MirasimWorkspaceViewModel(healthCenter: health);

        await viewModel.ProbeHostAsync();

        Assert.Contains("не выполнялась", viewModel.Blocker);
        Assert.DoesNotContain("Результат пробы неизвестен", viewModel.Blocker);
        Assert.DoesNotContain("Повтор небезопасен", viewModel.Blocker);
        Assert.DoesNotContain("доставлен", viewModel.Blocker);
        Assert.DoesNotContain("запрос", viewModel.Blocker, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(health.Failures);
    }

    [Fact]
    public async Task ProbeHostAsync_FailedAfterSuccess_ResetsTheStoredInstanceId()
    {
        var client = new FakeMirasimClient();
        var viewModel = new MirasimWorkspaceViewModel(client);

        await viewModel.ProbeHostAsync();
        Assert.Equal("instance-1", viewModel.InstanceId);

        client.ProbeHandler = () => Task.FromResult(MirasimHealthStatus.Unavailable("host restarted"));

        await viewModel.ProbeHostAsync();

        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.InstanceId);
        Assert.False(viewModel.CanCreateSession);
    }

    [Fact]
    public async Task CreateSessionAsync_PassesTheProbedInstanceIdAndOperatorSelection()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = new MirasimWorkspaceViewModel(new FakeMirasimClient(), lifecycle, projectRepository: CreateProjectRepository())
        {
            ProjectId = DefaultProjectId,
            AuthToken = "token-1",
            WorkspacePath = @"C:\work\ws"
        };

        await viewModel.ProbeHostAsync();

        viewModel.SelectedHarness = "grok";
        viewModel.ModelId = "grok-4.6";

        await viewModel.CreateSessionAsync();

        var request = Assert.Single(lifecycle.SessionRequests);
        Assert.Equal("instance-1", request.InstanceId);
        Assert.Equal("grok", request.Harness);
        Assert.Equal("grok-4.6", request.ModelId);
        Assert.Equal(@"C:\work\ws", request.WorkspacePath);
        Assert.Equal("token-1", request.AuthToken);
        Assert.Equal(DefaultProjectId, lifecycle.LastProjectContext!.ProjectId);
        Assert.Equal("mirasim", lifecycle.LastProjectContext.ProviderProfileId);
        Assert.Equal(SessionKey, viewModel.SessionKey);
        Assert.Equal("Confirmed", viewModel.SessionStatus);
        Assert.Equal(MirasimRouteModes.ManualOnly, viewModel.RouteMode);
    }

    [Fact]
    public async Task CreateSessionAsync_WithoutAProbedInstance_IsRefused()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = new MirasimWorkspaceViewModel(new FakeMirasimClient(), lifecycle);

        Assert.False(viewModel.CanCreateSession);

        await viewModel.CreateSessionAsync();

        Assert.Empty(lifecycle.SessionRequests);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.SessionKey);
    }

    [Fact]
    public async Task CreateSessionAsync_ThrownAfterStart_NamesHarnessAndUnsafeRetry()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            CreateHandler = (_, _, _, _) =>
                throw new InvalidOperationException("api_key=synthetic-create-secret")
        };
        var viewModel = new MirasimWorkspaceViewModel(
            new FakeMirasimClient(),
            lifecycle,
            health,
            projectRepository: CreateProjectRepository())
        {
            ProjectId = DefaultProjectId,
            WorkspacePath = @"C:\work\ws"
        };
        await viewModel.ProbeHostAsync();
        viewModel.SelectedHarness = "codex";
        viewModel.ModelId = "gpt-4o";
        var failuresBefore = health.Failures.Count;

        await viewModel.CreateSessionAsync();

        Assert.Single(lifecycle.SessionRequests);
        Assert.Contains("codex", viewModel.Blocker);
        Assert.Contains("gpt-4o", viewModel.Blocker);
        Assert.Contains("Создание могло быть доставлено", viewModel.Blocker);
        Assert.Contains("Повтор небезопасен", viewModel.Blocker);
        Assert.Contains("Состояние здоровья не изменялось", viewModel.Blocker);
        Assert.Contains("Сессия не возвращена", viewModel.Blocker);
        Assert.DoesNotContain(SessionKey, viewModel.Blocker);
        Assert.DoesNotContain("synthetic-create-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.SessionKey);
        Assert.Equal(failuresBefore, health.Failures.Count);
    }

    [Fact]
    public async Task CreateSessionAsync_BeforeStart_DoesNotClaimCreated()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = new MirasimWorkspaceViewModel(new FakeMirasimClient(), lifecycle)
        {
            WorkspacePath = @"C:\work\ws"
        };
        await viewModel.ProbeHostAsync();

        await viewModel.CreateSessionAsync();

        Assert.Empty(lifecycle.SessionRequests);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.SessionKey);
        Assert.DoesNotContain("могло быть доставлено", viewModel.Blocker);
        Assert.DoesNotContain("могла быть создана", viewModel.Blocker);
        Assert.DoesNotContain(SessionKey, viewModel.Blocker);
    }

    [Fact]
    public async Task SendPromptAsync_Completed_UpdatesObservedEvidenceAndReportsSuccess()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Completed,
                ErrorClass = MirasimTurnErrorClass.None,
                AssistantResponse = "Final answer.",
                ObservedModel = "gpt-4o",
                ObservedAccount = "account-7",
                ObservedLeg = "own",
                RouteMode = MirasimRouteModes.ManualOnly
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);

        viewModel.PromptInput = "Explain the failure.";

        await viewModel.SendPromptAsync();

        Assert.Equal(TurnId, viewModel.TurnId);
        Assert.Equal("Completed", viewModel.Status);
        Assert.Equal("None", viewModel.ErrorClass);
        Assert.Equal("Final answer.", viewModel.AssistantResponse);
        Assert.Equal("gpt-4o", viewModel.ObservedModel);
        Assert.Equal("account-7", viewModel.ObservedAccount);
        Assert.Equal("own", viewModel.ObservedLeg);
        Assert.False(viewModel.IsWriterLockHeld);
        Assert.Empty(viewModel.PromptInput);

        var success = Assert.Single(health.Successes);
        Assert.Equal(HealthScope.ForBackend(MirasimWorkspaceViewModel.BackendScopeId), success.Scope);
        Assert.Empty(health.Failures);
    }

    [Fact]
    public async Task SendPromptAsync_ThrownAfterStart_NamesHarnessAndModel()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => throw new InvalidOperationException("api_key=synthetic-mirasim-secret")
        };
        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);
        viewModel.PromptInput = "Explain the failure.";
        await viewModel.SendPromptAsync();
        Assert.Single(lifecycle.TurnRequests);
        Assert.Contains("codex", viewModel.Blocker);
        Assert.Contains("gpt-4o", viewModel.Blocker);
        Assert.Contains(SessionKey, viewModel.Blocker);
        Assert.Contains("мог быть доставлен", viewModel.Blocker);
        Assert.Contains("Повтор небезопасен", viewModel.Blocker);
        Assert.Contains("Состояние здоровья не изменялось", viewModel.Blocker);
        Assert.DoesNotContain("synthetic-mirasim-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
        Assert.Empty(health.Failures);
    }

    [Fact]
    public async Task SendPromptAsync_PreparationThrownBeforeTurn_NamesHarnessAndSaysNotDelivered()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = await CreateReadyViewModelAsync(
            lifecycle,
            health,
            dataClassificationGate: new ThrowingClassificationGate(
                new InvalidOperationException("api_key=synthetic-prepare-secret")));
        viewModel.PromptInput = "Explain the failure.";
        var failuresBefore = health.Failures.Count;

        await viewModel.SendPromptAsync();

        Assert.Empty(lifecycle.TurnRequests);
        Assert.Contains("codex", viewModel.Blocker);
        Assert.Contains("gpt-4o", viewModel.Blocker);
        Assert.Contains("Запрос не доставлялся", viewModel.Blocker);
        Assert.DoesNotContain("Повтор небезопасен", viewModel.Blocker);
        Assert.DoesNotContain("мог быть доставлен", viewModel.Blocker);
        Assert.DoesNotContain("могла получить", viewModel.Blocker);
        Assert.DoesNotContain(SessionKey, viewModel.Blocker);
        Assert.DoesNotContain("synthetic-prepare-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
        Assert.Equal(SessionKey, viewModel.SessionKey);
        Assert.Equal(failuresBefore, health.Failures.Count);
    }

    [Fact]
    public async Task SendPromptAsync_UnreportedEvidence_StaysNotReported()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Completed
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle);
        viewModel.PromptInput = "Say nothing observable.";

        await viewModel.SendPromptAsync();

        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.AssistantResponse);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.ObservedModel);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.ObservedAccount);
        Assert.Equal(MirasimWorkspaceViewModel.NotReported, viewModel.ObservedLeg);
    }

    [Fact]
    public async Task SendPromptAsync_UsesWriteExecutionModeSoTheLifecycleOwnsTheWriterLock()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = await CreateReadyViewModelAsync(lifecycle);

        viewModel.PromptInput = "Refactor the parser.";
        viewModel.ProjectId = "project-1";
        viewModel.CanonicalRootPath = @"C:\work\ws";

        await viewModel.SendPromptAsync();

        var request = Assert.Single(lifecycle.TurnRequests);
        Assert.Equal(MirasimWorkspaceViewModel.WriteExecutionMode, request.ExecutionMode);
        Assert.Equal(SessionKey, request.SessionKey);
        Assert.Equal("Refactor the parser.", request.Prompt);
        Assert.Equal("codex", request.RequestedHarness);
        Assert.Equal("gpt-4o", request.RequestedModelId);
        Assert.Equal("project-1", request.ProjectId);
        Assert.Equal(@"C:\work\ws", request.CanonicalRootPath);
        Assert.False(string.IsNullOrWhiteSpace(request.ExecutionId));
    }

    [Theory]
    [InlineData(MirasimTurnErrorClass.UpstreamUnavailable503, HealthErrorClass.Provider4xx5xx)]
    [InlineData(MirasimTurnErrorClass.PlatformBusy422, HealthErrorClass.Provider4xx5xx)]
    [InlineData(MirasimTurnErrorClass.ConnectionDrop, HealthErrorClass.NetworkOrTimeout)]
    [InlineData(MirasimTurnErrorClass.Timeout, HealthErrorClass.NetworkOrTimeout)]
    [InlineData(MirasimTurnErrorClass.RouteMismatch, HealthErrorClass.ModelUnavailableOrMismatch)]
    [InlineData(MirasimTurnErrorClass.IncompletePayload, HealthErrorClass.MalformedProtocolEvent)]
    public async Task SendPromptAsync_FailedTurn_MapsTheNormalizedErrorClassIntoTheHealthScope(
        MirasimTurnErrorClass turnErrorClass,
        HealthErrorClass expectedHealthErrorClass)
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Failed,
                ErrorClass = turnErrorClass,
                ErrorMessage = "The Mirasim host rejected the turn."
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);
        viewModel.PromptInput = "Trigger the failure.";

        await viewModel.SendPromptAsync();

        var failure = Assert.Single(health.Failures);
        var expectedScope = turnErrorClass == MirasimTurnErrorClass.RouteMismatch
            ? HealthScope.ForModelRoute(MirasimWorkspaceViewModel.BackendScopeId, viewModel.ModelId)
            : HealthScope.ForBackend(MirasimWorkspaceViewModel.BackendScopeId);
        Assert.Equal(expectedScope, failure.Scope);
        Assert.Equal(expectedHealthErrorClass, failure.ErrorClass);
        Assert.Empty(health.Successes);
    }

    [Fact]
    public async Task SendPromptAsync_Ambiguous_RetainsWriterLockAndReportsUnknownOrAmbiguousCompletion()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Ambiguous,
                ErrorClass = MirasimTurnErrorClass.ConnectionDrop
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);
        viewModel.PromptInput = "Ambiguous turn.";

        await viewModel.SendPromptAsync();

        Assert.Equal("Ambiguous", viewModel.Status);
        Assert.True(viewModel.IsWriterLockHeld);
        Assert.True(viewModel.CanReconcileTurn);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.Guidance));

        var failure = Assert.Single(health.Failures);
        Assert.Equal(HealthErrorClass.UnknownOrAmbiguousCompletion, failure.ErrorClass);
        Assert.False(failure.ErrorClass.IsAccountedByBreaker());
        Assert.Empty(health.Successes);
    }

    [Fact]
    public async Task SendPromptAsync_Cancelled_ReportsUserCancellationWithoutMovingTheBreaker()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Cancelled
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);
        viewModel.PromptInput = "Cancelled turn.";

        await viewModel.SendPromptAsync();

        Assert.Equal("Cancelled", viewModel.Status);
        Assert.False(viewModel.IsWriterLockHeld);

        var failure = Assert.Single(health.Failures);
        Assert.Equal(HealthErrorClass.UserCancellation, failure.ErrorClass);
        Assert.False(failure.ErrorClass.IsAccountedByBreaker());
    }

    [Fact]
    public async Task SendPromptAsync_RefusedByWriterLock_IsNotReportedAsBackendUnhealthy()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult
            {
                TurnId = string.Empty,
                Status = MirasimTurnStatus.RefusedByLock,
                ErrorClass = MirasimTurnErrorClass.RefusedByWriterLock
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);
        viewModel.PromptInput = "Conflicting turn.";

        await viewModel.SendPromptAsync();

        Assert.Equal("RefusedByLock", viewModel.Status);
        Assert.False(viewModel.IsWriterLockHeld);
        Assert.Empty(health.Failures);
        Assert.Empty(health.Successes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyRefusalNeverMovesHealthAndLocalCleanupUsesExistingReconcile(bool cleanup)
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new() { TurnId = "", Status = MirasimTurnStatus.RefusedByPolicy, RequiresLocalCleanup = cleanup },
            ReconcileHandler = _ => new() { TurnId = "", Status = MirasimTurnStatus.RefusedByPolicy }
        };
        var vm = await CreateReadyViewModelAsync(lifecycle, health);
        vm.PromptInput = "review text"; await vm.SendPromptAsync();
        Assert.Equal("mirasim", Assert.Single(lifecycle.TurnRequests).ProviderProfileId);
        Assert.Equal(cleanup, vm.IsWriterLockHeld); Assert.Equal(cleanup, vm.CanReconcileTurn);
        Assert.Empty(health.Failures); Assert.Empty(health.Successes);
        if (cleanup)
        {
            vm.PromptInput = "no automatic retry";
            Assert.False(vm.CanSendPrompt); Assert.False(vm.CanCreateSession);
            await vm.SendPromptAsync(); Assert.Single(lifecycle.TurnRequests);
            await vm.ReconcileTurnAsync(); Assert.Equal("", Assert.Single(lifecycle.ReconcileRequests).TurnId);
            Assert.False(vm.IsWriterLockHeld); Assert.False(vm.CanReconcileTurn); Assert.True(vm.CanSendPrompt);
            Assert.Single(lifecycle.TurnRequests); Assert.Empty(health.Failures); Assert.Empty(health.Successes);
        }
    }

    [Fact]
    public async Task ReconcileTurnAsync_TerminalSuccess_ClearsTheRetainedWriterLock()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Ambiguous
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);
        viewModel.PromptInput = "Ambiguous turn.";
        await viewModel.SendPromptAsync();

        Assert.True(viewModel.IsWriterLockHeld);

        lifecycle.ReconcileHandler = _ => new MirasimTurnResult
        {
            TurnId = TurnId,
            Status = MirasimTurnStatus.Completed,
            AssistantResponse = "Reconciled answer.",
            ObservedModel = "gpt-4o"
        };

        await viewModel.ReconcileTurnAsync();

        Assert.False(viewModel.IsWriterLockHeld);
        Assert.Equal("Completed", viewModel.Status);
        Assert.Equal("Reconciled answer.", viewModel.AssistantResponse);
        Assert.Equal("gpt-4o", viewModel.ObservedModel);

        var reconcile = Assert.Single(lifecycle.ReconcileRequests);
        Assert.Equal(SessionKey, reconcile.SessionKey);
        Assert.Equal(TurnId, reconcile.TurnId);
        Assert.Single(health.Successes);
    }

    [Fact]
    public async Task ReconcileTurnAsync_Inconclusive_KeepsTheWriterLockRetained()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Ambiguous
            },
            ReconcileHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Ambiguous
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle);
        viewModel.PromptInput = "Ambiguous turn.";
        await viewModel.SendPromptAsync();

        await viewModel.ReconcileTurnAsync();

        Assert.True(viewModel.IsWriterLockHeld);
        Assert.Equal("Ambiguous", viewModel.Status);
    }

    [Fact]
    public async Task ReconcileTurnAsync_WithoutARetainedLock_IsRefused()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = await CreateReadyViewModelAsync(lifecycle);

        Assert.False(viewModel.CanReconcileTurn);

        await viewModel.ReconcileTurnAsync();

        Assert.Empty(lifecycle.ReconcileRequests);
    }

    [Fact]
    public async Task CancelTurnAsync_TerminalConfirmation_SetsCancelled()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult { TurnId = TurnId, Status = MirasimTurnStatus.Running },
            CancelHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Cancelled
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);
        viewModel.PromptInput = "Cancel me.";
        await viewModel.SendPromptAsync();

        await viewModel.CancelTurnAsync();

        Assert.Equal("Cancelled", viewModel.Status);
        Assert.False(viewModel.IsWriterLockHeld);

        var cancel = Assert.Single(lifecycle.CancelRequests);
        Assert.Equal(SessionKey, cancel.SessionKey);
        Assert.Equal(TurnId, cancel.TurnId);
    }

    [Fact]
    public async Task CancelTurnAsync_WithoutTerminalConfirmation_NeverClaimsCancelled()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult { TurnId = TurnId, Status = MirasimTurnStatus.Running },
            CancelHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Ambiguous,
                ErrorMessage = "The Mirasim host did not confirm cancellation."
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle);
        viewModel.PromptInput = "Cancel me.";
        await viewModel.SendPromptAsync();

        Assert.Equal("Running", viewModel.Status);

        await viewModel.CancelTurnAsync();

        Assert.NotEqual("Cancelled", viewModel.Status);
        Assert.True(viewModel.IsWriterLockHeld);
        Assert.True(viewModel.HasBlocker);
    }

    [Fact]
    public async Task CancelTurnAsync_ThrownAfterStart_NamesHarnessAndUnsafeRetry()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult { TurnId = TurnId, Status = MirasimTurnStatus.Running },
            CancelHandler = _ => throw new InvalidOperationException("api_key=synthetic-cancel-secret")
        };
        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);
        viewModel.PromptInput = "Cancel me.";
        await viewModel.SendPromptAsync();
        var failuresBefore = health.Failures.Count;
        await viewModel.CancelTurnAsync();
        Assert.Single(lifecycle.CancelRequests);
        Assert.Contains("codex", viewModel.Blocker);
        Assert.Contains("gpt-4o", viewModel.Blocker);
        Assert.Contains(SessionKey, viewModel.Blocker);
        Assert.Contains("Отмена могла быть доставлена", viewModel.Blocker);
        Assert.Contains("Повтор небезопасен", viewModel.Blocker);
        Assert.Contains("Состояние здоровья не изменялось", viewModel.Blocker);
        Assert.DoesNotContain("synthetic-cancel-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
        Assert.Equal(failuresBefore, health.Failures.Count);
    }

    [Fact]
    public async Task CancelTurnAsync_BeforeStart_DoesNotClaimDelivery()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = await CreateReadyViewModelAsync(lifecycle);
        await viewModel.CancelTurnAsync();
        Assert.Empty(lifecycle.CancelRequests);
        Assert.DoesNotContain("могла быть доставлена", viewModel.Blocker);
        Assert.DoesNotContain("мог быть доставлен", viewModel.Blocker);
    }

    [Fact]
    public async Task ReconcileTurnAsync_ThrownAfterStart_NamesHarnessAndUnsafeRetry()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Ambiguous,
                ErrorClass = MirasimTurnErrorClass.ConnectionDrop
            },
            ReconcileHandler = _ => throw new InvalidOperationException("api_key=synthetic-reconcile-secret")
        };
        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);
        viewModel.PromptInput = "Ambiguous turn.";
        await viewModel.SendPromptAsync();
        var failuresBefore = health.Failures.Count;
        await viewModel.ReconcileTurnAsync();
        Assert.Single(lifecycle.ReconcileRequests);
        Assert.Contains("codex", viewModel.Blocker);
        Assert.Contains("gpt-4o", viewModel.Blocker);
        Assert.Contains(SessionKey, viewModel.Blocker);
        Assert.Contains("Сверка могла быть доставлена", viewModel.Blocker);
        Assert.Contains("Повтор небезопасен", viewModel.Blocker);
        Assert.Contains("Состояние здоровья не изменялось", viewModel.Blocker);
        Assert.DoesNotContain("synthetic-reconcile-secret", viewModel.Blocker);
        Assert.DoesNotContain("InvalidOperationException", viewModel.Blocker);
        Assert.Equal(failuresBefore, health.Failures.Count);
    }

    [Fact]
    public async Task HealthReasons_NeverContainPromptTokenOrRawPayload()
    {
        var health = new RecordingHealthCenterService();
        var lifecycle = new FakeMirasimSessionLifecycleService
        {
            TurnHandler = _ => new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Failed,
                ErrorClass = MirasimTurnErrorClass.UpstreamUnavailable503,
                ErrorMessage = "RAW-PAYLOAD-MARKER"
            }
        };

        var viewModel = await CreateReadyViewModelAsync(lifecycle, health);
        viewModel.AuthToken = "TOKEN-MARKER";
        viewModel.PromptInput = "PROMPT-MARKER";

        await viewModel.SendPromptAsync();

        var failure = Assert.Single(health.Failures);
        Assert.NotNull(failure.Reason);
        Assert.DoesNotContain("PROMPT-MARKER", failure.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN-MARKER", failure.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("RAW-PAYLOAD-MARKER", failure.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeFailureReason_NeverContainsTheAccessToken()
    {
        var health = new RecordingHealthCenterService();
        var client = new FakeMirasimClient
        {
            ProbeHandler = () => Task.FromResult(MirasimHealthStatus.Unavailable("unreachable"))
        };

        var viewModel = new MirasimWorkspaceViewModel(client, healthCenter: health)
        {
            AuthToken = "TOKEN-MARKER"
        };

        await viewModel.ProbeHostAsync();

        var failure = Assert.Single(health.Failures);
        Assert.NotNull(failure.Reason);
        Assert.DoesNotContain("TOKEN-MARKER", failure.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Notices_DeclareTheFailClosedApprovalsAndRawRecordingBoundary()
    {
        var viewModel = new MirasimWorkspaceViewModel();

        Assert.Contains("UnknownHighRisk", viewModel.ApprovalsNoticeText, StringComparison.Ordinal);
        Assert.Contains("bypassPermissions", viewModel.ApprovalsNoticeText, StringComparison.Ordinal);
        Assert.Contains("ADR-0008 §9", viewModel.ApprovalsNoticeText, StringComparison.Ordinal);
        Assert.Contains("вести запись", viewModel.RecordingNoticeText, StringComparison.Ordinal);
        Assert.DoesNotContain("(record)", viewModel.RecordingNoticeText, StringComparison.Ordinal);
        Assert.Contains("ADR-0008 §10", viewModel.RecordingNoticeText, StringComparison.Ordinal);
        Assert.Contains("ManualOnly", viewModel.RoutePolicyNoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendPromptAsync_RestrictedProjectFromRepository_BlocksDispatch()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = await CreateReadyViewModelAsync(
            lifecycle,
            projectRepository: CreateProjectRepository(DataClassification.Restricted));

        viewModel.PromptInput = "restricted content";

        await viewModel.SendPromptAsync();

        Assert.Equal(DataClassification.Restricted, viewModel.ProjectDataClassification);
        Assert.True(viewModel.HasBlocker);
        Assert.Contains("Restricted", viewModel.Blocker);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_NullGate_BlocksFailClosed()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = new MirasimWorkspaceViewModel(
            new FakeMirasimClient(),
            lifecycle,
            projectRepository: CreateProjectRepository())
        {
            ProjectId = DefaultProjectId
        };

        await viewModel.ProbeHostAsync();
        await viewModel.CreateSessionAsync();

        viewModel.PromptInput = "private content";

        await viewModel.SendPromptAsync();

        Assert.True(viewModel.HasBlocker);
        Assert.Contains("fail-closed", viewModel.Blocker);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_MissingProject_BlocksFailClosed()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = await CreateReadyViewModelAsync(lifecycle);
        // Establish a real session first, then lose the project binding before send.
        viewModel.ProjectId = "missing-project";

        viewModel.PromptInput = "private content";

        await viewModel.SendPromptAsync();

        Assert.True(viewModel.HasBlocker);
        Assert.Contains("not found", viewModel.Blocker);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_ProjectDataClassification_IsAssignedFromRepository()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = await CreateReadyViewModelAsync(
            lifecycle,
            projectRepository: CreateProjectRepository(DataClassification.Restricted));

        Assert.Equal(DataClassification.PrivateSource, viewModel.ProjectDataClassification);

        viewModel.PromptInput = "restricted content";

        await viewModel.SendPromptAsync();

        Assert.Equal(DataClassification.Restricted, viewModel.ProjectDataClassification);
    }

    [Fact]
    public async Task SendPromptAsync_MaxDataClassViolation_IsBlockedBeforeDispatch()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = await CreateReadyViewModelAsync(
            lifecycle,
            dataClassificationGate: CreateGate(CreateProfileRepository(DataClassification.PublicSource)),
            projectRepository: CreateProjectRepository(DataClassification.PrivateSource));

        viewModel.PromptInput = "private content";

        await viewModel.SendPromptAsync();

        // The real gate compares the stored project class against the "mirasim" profile maximum.
        Assert.True(viewModel.HasBlocker);
        Assert.Contains("Data classification violation", viewModel.Blocker);
        Assert.Contains(MirasimWorkspaceViewModel.BackendScopeId, viewModel.Blocker);
        Assert.Contains(nameof(DataClassification.PublicSource), viewModel.Blocker);
        Assert.Equal(DataClassification.PrivateSource, viewModel.ProjectDataClassification);
        Assert.Empty(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task SendPromptAsync_MatchingMaxDataClass_DispatchesPrompt()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = await CreateReadyViewModelAsync(
            lifecycle,
            dataClassificationGate: CreateGate(CreateProfileRepository(DataClassification.PrivateSource)),
            projectRepository: CreateProjectRepository(DataClassification.PrivateSource));

        viewModel.PromptInput = "private content";

        await viewModel.SendPromptAsync();

        Assert.False(viewModel.HasBlocker);
        Assert.Equal(DataClassification.PrivateSource, viewModel.ProjectDataClassification);
        Assert.Single(lifecycle.TurnRequests);
    }

    private const string DefaultProjectId = "project-1";

    [Fact]
    public async Task CreateSessionAsync_DefaultWorkspaceUsesResolvedProjectRoot()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        await CreateReadyViewModelAsync(lifecycle);

        Assert.Equal(@"C:\work\ws", Assert.Single(lifecycle.SessionRequests).WorkspacePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative-workspace")]
    [InlineData(@"C:\work\ws")]
    public async Task CreateSessionAsync_WithoutProjectNeverSendsWorkspaceEvenWhenAbsolute(string? workspace)
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var viewModel = new MirasimWorkspaceViewModel(new FakeMirasimClient(), lifecycle)
        { WorkspacePath = workspace };
        await viewModel.ProbeHostAsync();

        await viewModel.CreateSessionAsync();

        Assert.Empty(lifecycle.SessionRequests);
        Assert.True(viewModel.HasBlocker);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task SendPromptAsync_ProjectRootChangedSinceSessionCreation_RefusesWithoutLosingPrompt()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var projects = CreateProjectRepository();
        var viewModel = await CreateReadyViewModelAsync(lifecycle, projectRepository: projects);
        await projects.UpsertAsync(new Project(DefaultProjectId, "Changed checkout", @"D:\synthetic-other-root",
            null, false, false, null, null, DataClassification.PrivateSource));
        viewModel.WorkspacePath = @"D:\synthetic-other-root";
        viewModel.CanonicalRootPath = null;
        viewModel.PromptInput = "synthetic prompt";

        await viewModel.SendPromptAsync();

        Assert.Empty(lifecycle.TurnRequests);
        Assert.Equal("synthetic prompt", viewModel.PromptInput);
        Assert.True(viewModel.HasBlocker);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task SendPromptAsync_WhileClassificationIsPending_DoesNotAdmitASecondSend()
    {
        var lifecycle = new FakeMirasimSessionLifecycleService();
        var gate = new HeldClassificationGate();
        var viewModel = await CreateReadyViewModelAsync(lifecycle, dataClassificationGate: gate);
        viewModel.PromptInput = "synthetic single prompt";
        var first = viewModel.SendPromptAsync();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = viewModel.SendPromptAsync();
        gate.Release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("synthetic single prompt", Assert.Single(lifecycle.TurnRequests).Prompt);
        Assert.Equal(1, gate.CallCount);
        Assert.False(viewModel.IsBusy);
    }

    private sealed class HeldClassificationGate : IDataClassificationGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task<DataClassificationGateDecision> EvaluateAsync(DataClassification projectDataClass,
            string? providerProfileId, bool isManualOnly = false, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return DataClassificationGateDecision.Allowed();
        }
    }

    private sealed class ThrowingClassificationGate : IDataClassificationGate
    {
        private readonly Exception _failure;

        public ThrowingClassificationGate(Exception failure) => _failure = failure;

        public Task<DataClassificationGateDecision> EvaluateAsync(
            DataClassification projectDataClass,
            string? providerProfileId,
            bool isManualOnly = false,
            CancellationToken cancellationToken = default) =>
            Task.FromException<DataClassificationGateDecision>(_failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeOperationFailure_DoesNotExposeRawExceptionText(bool duringTurn)
    {
        const string privateText = @"token=synthetic-private-value; path=D:\synthetic-private-customer\file";
        MirasimWorkspaceViewModel viewModel;
        if (duringTurn)
        {
            var lifecycle = new FakeMirasimSessionLifecycleService
            { TurnHandler = _ => throw new InvalidOperationException(privateText) };
            viewModel = await CreateReadyViewModelAsync(lifecycle);
            viewModel.PromptInput = "synthetic prompt";
            await viewModel.SendPromptAsync();
        }
        else
        {
            viewModel = new MirasimWorkspaceViewModel(new FakeMirasimClient
            { ProbeHandler = () => throw new InvalidOperationException(privateText) });
            await viewModel.ProbeHostAsync();
        }

        Assert.True(viewModel.HasBlocker);
        Assert.DoesNotContain("synthetic-private-value", viewModel.Blocker, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-customer", viewModel.Blocker, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    private static async Task<MirasimWorkspaceViewModel> CreateReadyViewModelAsync(
        FakeMirasimSessionLifecycleService lifecycle,
        RecordingHealthCenterService? health = null,
        IDataClassificationGate? dataClassificationGate = null,
        IProjectRepository? projectRepository = null,
        string? projectId = DefaultProjectId)
    {
        var viewModel = new MirasimWorkspaceViewModel(
            new FakeMirasimClient(),
            lifecycle,
            health,
            dataClassificationGate ?? CreateGate(),
            projectRepository ?? CreateProjectRepository());

        if (projectId is not null)
        {
            viewModel.ProjectId = projectId;
        }

        await viewModel.ProbeHostAsync();
        await viewModel.CreateSessionAsync();

        Assert.Equal(SessionKey, viewModel.SessionKey);

        return viewModel;
    }

    private static InMemoryProjectRepository CreateProjectRepository(
        DataClassification dataClassification = DataClassification.PrivateSource)
    {
        var repository = new InMemoryProjectRepository();

        repository.UpsertAsync(new Project(
            DefaultProjectId,
            "Mirasim project",
            @"C:\work\ws",
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
            MirasimWorkspaceViewModel.BackendScopeId,
            "Mirasim profile",
            BackendType.Mirasim,
            null,
            null,
            maxDataClass,
            true));

        return repository;
    }

    private static IDataClassificationGate CreateGate(
        InMemoryProviderProfileRepository? profiles = null) =>
        new DataClassificationGate(profiles ?? CreateProfileRepository());

    private sealed class FakeMirasimClient : IMirasimClient
    {
        public Uri BaseUrl { get; set; } = new("http://127.0.0.1:4970/");

        public Func<Task<MirasimHealthStatus>> ProbeHandler { get; set; } = () =>
            Task.FromResult(new MirasimHealthStatus
            {
                Ok = true,
                Name = "mirasim",
                Version = "0.0.354",
                InstanceId = "instance-1",
                Pid = 4321,
                Uptime = 987654
            });

        public int ProbeCount { get; private set; }

        public Task<MirasimHealthStatus> ProbeHealthAsync(CancellationToken cancellationToken = default)
        {
            ProbeCount++;

            return ProbeHandler();
        }

        public Task<bool> ProbeAuthenticatedEndpointAsync(
            string? testToken = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeMirasimSessionLifecycleService : IMirasimSessionLifecycleService
    {
        public List<(string InstanceId, string Harness, string ModelId, string WorkspacePath, string? AuthToken)>
            SessionRequests { get; } = new();

        public List<MirasimTurnRequest> TurnRequests { get; } = new();
        public LLMWorkGUI.Domain.ValueObjects.ProjectProviderContext? LastProjectContext;

        public List<(string SessionKey, string TurnId, string? AuthToken)> CancelRequests { get; } = new();

        public List<(string SessionKey, string TurnId, string? AuthToken)> ReconcileRequests { get; } = new();

        public Func<MirasimTurnRequest, MirasimTurnResult> TurnHandler { get; set; } = _ =>
            new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Completed
            };

        public Func<string, MirasimTurnResult> CancelHandler { get; set; } = _ =>
            new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Cancelled
            };

        public Func<string, MirasimTurnResult> ReconcileHandler { get; set; } = _ =>
            new MirasimTurnResult
            {
                TurnId = TurnId,
                Status = MirasimTurnStatus.Completed
            };

        public Func<string, string, string, string, MirasimSessionBinding>? CreateHandler { get; set; }

        public Task<MirasimSessionBinding> CreateSessionAsync(
            string instanceId,
            string harness,
            string modelId,
            string workspacePath,
            string? authToken = null,
            CancellationToken cancellationToken = default,
            LLMWorkGUI.Domain.ValueObjects.ProjectProviderContext? projectContext = null)
        {
            SessionRequests.Add((instanceId, harness, modelId, workspacePath, authToken));
            LastProjectContext = projectContext;
            if (CreateHandler is not null)
            {
                return Task.FromResult(CreateHandler(instanceId, harness, modelId, workspacePath));
            }

            return Task.FromResult(new MirasimSessionBinding
            {
                InstanceId = instanceId,
                Harness = harness,
                ModelId = modelId,
                RouteMode = MirasimRouteModes.ManualOnly,
                SessionKey = SessionKey,
                WorkspacePath = workspacePath
            });
        }

        public Task<MirasimSessionBinding> ContinueSessionAsync(
            MirasimSessionBinding existingBinding,
            string? authToken = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(existingBinding);

        public Task<MirasimTurnResult> ExecuteTurnAsync(
            MirasimTurnRequest request,
            CancellationToken cancellationToken = default)
        {
            TurnRequests.Add(request);

            return Task.FromResult(TurnHandler(request));
        }

        public IAsyncEnumerable<MirasimStreamEvent> WatchTurnAsync(
            string sessionKey,
            string turnId,
            string? authToken = null,
            CancellationToken cancellationToken = default) =>
            EmptyStream();

        public Task<MirasimTurnResult> CancelTurnAsync(
            string sessionKey,
            string turnId,
            string? authToken = null,
            CancellationToken cancellationToken = default)
        {
            CancelRequests.Add((sessionKey, turnId, authToken));

            return Task.FromResult(CancelHandler(turnId));
        }

        public Task<MirasimTurnResult> ReconcileTurnAsync(
            string sessionKey,
            string turnId,
            string? authToken = null,
            CancellationToken cancellationToken = default)
        {
            ReconcileRequests.Add((sessionKey, turnId, authToken));

            return Task.FromResult(ReconcileHandler(turnId));
        }

        private static async IAsyncEnumerable<MirasimStreamEvent> EmptyStream()
        {
            await Task.CompletedTask;

            yield break;
        }
    }

    private sealed class RecordingHealthCenterService : IHealthCenterService
    {
        public List<(HealthScope Scope, HealthErrorClass ErrorClass, string? Reason)> Failures { get; } = new();

        public List<(HealthScope Scope, string? Reason)> Successes { get; } = new();

        public int CompleteProbeCalls { get; private set; }

        public event EventHandler<HealthTransitionEventArgs>? TransitionRecorded
        {
            add { }
            remove { }
        }

        public Task<HealthSnapshot> GetSnapshotAsync(
            HealthScope scope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot(scope));

        public Task<IReadOnlyList<HealthSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HealthSnapshot>>(Array.Empty<HealthSnapshot>());

        public Task<HealthFailureOutcome> ReportFailureAsync(
            HealthScope scope,
            HealthErrorClass errorClass,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            Failures.Add((scope, errorClass, reason));

            return Task.FromResult(new HealthFailureOutcome
            {
                Snapshot = Snapshot(scope),
                CountedByBreaker = errorClass.IsAccountedByBreaker(),
                StateChanged = false,
                AutomaticRetryAllowed = false
            });
        }

        public Task<HealthSnapshot> ReportSuccessAsync(
            HealthScope scope,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            Successes.Add((scope, reason));

            return Task.FromResult(Snapshot(scope));
        }

        public Task<HealthSnapshot> ExpireCooldownAsync(
            HealthScope scope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot(scope));

        public Task<HealthSnapshot> StartProbeAsync(
            HealthScope scope,
            string? reason = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot(scope));

        public Task<HealthSnapshot> CompleteProbeAsync(
            HealthScope scope,
            bool succeeded,
            string? evidenceRedactedJson = null,
            CancellationToken cancellationToken = default)
        {
            CompleteProbeCalls++;

            return Task.FromResult(Snapshot(scope));
        }

        public Task<HealthSnapshot> RecordProbeObservationAsync(
            HealthScope scope,
            bool observedSuccess,
            string reason,
            string? evidenceRedactedJson = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot(scope));

        public Task<HealthSnapshot> DisableManuallyAsync(
            HealthScope scope,
            string reason,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot(scope));

        public Task<HealthSnapshot> EnableAsync(
            HealthScope scope,
            string reason,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot(scope));

        public Task<HealthSnapshot> ForceEnableAsync(
            HealthScope scope,
            string reason,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot(scope));

        public Task<HealthSnapshot> RequireProbeAsync(
            HealthScope scope,
            string? reason = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot(scope));

        public Task<IReadOnlyList<HealthEventView>> GetAuditAsync(
            HealthScope scope,
            int? limit = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HealthEventView>>(Array.Empty<HealthEventView>());

        private static HealthSnapshot Snapshot(HealthScope scope) => new()
        {
            Scope = scope,
            State = HealthState.Healthy,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
    }
}
