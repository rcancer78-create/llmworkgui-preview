using System.IO;
using LLMWorkGUI.App.ViewModels.Onboarding;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public partial class WorkspaceSessionUiTests
{
    [Fact]
    public async Task OnboardingProjectSwitch_RefusesOldSession_AndNewSessionUsesCurrentPolicy()
    {
        var directory = Directory.CreateTempSubdirectory("opencode-project-switch-");
        try
        {
            var rootA = Directory.CreateDirectory(Path.Combine(directory.FullName, "A")).FullName;
            var rootB = Directory.CreateDirectory(Path.Combine(directory.FullName, "B")).FullName;
            var projects = new InMemoryProjectRepository();
            await projects.UpsertAsync(new Project("A", "A", rootA, null, false, false, null, null, DataClassification.PrivateSource));
            await projects.UpsertAsync(new Project("B", "B", rootB, null, false, false, null, null, DataClassification.Restricted));
            var settings = new InMemoryApplicationSettingsRepository();
            var onboarding = new OnboardingViewModel(settings: settings, projectRepository: projects);
            await onboarding.ConfirmWorkspaceAsync(rootA);
            await onboarding.OpenProjectAsync();
            var service = new FakeSessionService();
            var journal = new FakeOpenCodeExecutionJournal();
            var locks = new FakeOpenCodeCheckoutLockService();
            var vm = CreateSessionViewModel(new FakeTimelineService(), service, projectRepository: projects,
                journal: journal, locks: locks, openedProjectResolver: new OpenedProjectResolver(projects, settings));
            vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
            vm.PromptInput = "Request for A";
            vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
            Assert.Equal(1, service.ExecuteTurnCount);
            Assert.Null(vm.ProjectId);

            await onboarding.ConfirmWorkspaceAsync(rootB);
            await onboarding.OpenProjectAsync();
            vm.PromptInput = "Restricted request for B";
            vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
            Assert.Equal(1, service.ExecuteTurnCount);
            Assert.Single(journal.Entries);
            Assert.Equal("Restricted request for B", vm.PromptInput);
            Assert.Contains("другом каталоге", vm.SendBlocker);

            vm.ResetSessionCommand.Execute(null); await WaitIdle(vm);
            Assert.Null(service.ResetRequest);
            Assert.True(vm.RequiresReplacementSession);
            Assert.True(vm.NewSessionCommand.CanExecute(null));
            vm.NewSessionCommand.Execute(null);
            Assert.False(vm.IsSessionConfirmed);
            Assert.True(vm.CanChooseOpenCodeRoute);
            Assert.Equal("Restricted request for B", vm.PromptInput);
            Assert.Contains(vm.ConversationTurns, turn => turn.PromptText == "Request for A");
            vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
            Assert.Equal(rootB, service.CreatedRequest!.Directory);
            vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
            Assert.Equal(DataClassification.Restricted, vm.ProjectDataClassification);
            Assert.Contains("Restricted", vm.SendBlocker);
            Assert.Equal(1, service.ExecuteTurnCount);
            Assert.Single(journal.Entries);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ExplicitProjectOverride_RemainsExplicitWhenOnboardingPointsElsewhere()
    {
        var projects = CreateProjectRepository();
        await projects.UpsertAsync(new Project("other", "Other", @"C:\work\other", null, false, false, null, null, DataClassification.Restricted));
        var settings = new InMemoryApplicationSettingsRepository();
        await settings.SetValueAsync(OpenedProjectResolver.WorkspaceSettingKey, @"C:\work\other");
        var service = new FakeSessionService();
        var locks = new FakeOpenCodeCheckoutLockService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, projectRepository: projects, locks: locks,
            openedProjectResolver: new OpenedProjectResolver(projects, settings));
        vm.ProjectId = DefaultProjectId;
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        vm.PromptInput = "Explicit project request";
        vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.Equal(1, service.ExecuteTurnCount);
        Assert.Equal(DefaultProjectId, locks.Token!.ProjectId);
        Assert.Equal(DefaultProjectId, vm.ProjectId);
    }

    [Fact]
    public async Task ConfirmedResetAfterGenerationChange_RebindsGenerationAndAllowsProtectedSend()
    {
        var process = new FakeOpenCodeProcessConnection();
        var journal = new FakeOpenCodeExecutionJournal();
        var locks = new FakeOpenCodeCheckoutLockService();
        var service = new FakeSessionService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, locks: locks, process: process);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        process.Process.ProcessGeneration = 18;
        vm.PromptInput = "Request after confirmed replacement";
        vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.Equal(0, service.ExecuteTurnCount);
        Assert.True(vm.RequiresReplacementSession);
        vm.ResetSessionCommand.Execute(null); await WaitIdle(vm);
        Assert.Equal("ses_456", vm.NativeSessionId);
        Assert.False(vm.RequiresReplacementSession);
        Assert.Empty(vm.SendBlocker);
        service.ExecuteTurnAsyncAction = () => Task.FromResult(new TurnResult
            { SessionId = "ses_456", OutputText = "new process response", Status = TurnResult.CompletedStatus });
        vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.Equal(1, service.ExecuteTurnCount);
        Assert.Equal(18, Assert.Single(journal.Entries).ProcessGeneration);
        Assert.Equal(18, locks.AcquiredGeneration);
        Assert.False(locks.Token!.IsHeld);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationChangesDuringResetOrJournalConfirmation_LeavesSessionUnconfirmed(bool duringJournal)
    {
        var process = new FakeOpenCodeProcessConnection();
        var journal = new FakeOpenCodeExecutionJournal();
        var service = new FakeSessionService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, process: process);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (duringJournal)
            journal.ConfirmSessionHandler = async () => { entered.SetResult(); await resume.Task; return "local-reset"; };
        else
            service.ResetSessionAsyncAction = async () => { entered.SetResult(); await resume.Task; return new OpenCodeSessionResponse { Id = "reset" }; };
        vm.ResetSessionCommand.Execute(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.NewSessionCommand.CanExecute(null));
        process.Process.ProcessGeneration++;
        resume.SetResult(); await WaitIdle(vm);
        Assert.False(vm.IsSessionConfirmed);
        Assert.False(vm.NewSessionCommand.CanExecute(null));
        Assert.Equal("ses_123", vm.NativeSessionId);
        vm.PromptInput = "Must not send on an unconfirmed reset";
        vm.SendPromptCommand.Execute(null); await WaitIdle(vm);
        Assert.Equal(0, service.ExecuteTurnCount);
        Assert.Empty(journal.Entries);
        Assert.Contains("Повтор небезопасен", vm.SendBlocker);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(17, false)]
    public async Task ResetWithoutConfirmedLiveGeneration_NeverCallsNativeReset(long generation, bool alive)
    {
        var process = new FakeOpenCodeProcessConnection();
        var service = new FakeSessionService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, process: process);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        process.Process.ProcessGeneration = generation;
        process.Process.IsAlive = alive;
        vm.ResetSessionCommand.Execute(null); await WaitIdle(vm);
        Assert.Null(service.ResetRequest);
        Assert.Contains("Сброс не доставлялся", vm.SendBlocker);
        Assert.True(vm.NewSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewSessionAfterRetiredRoute_AllowsExplicitRouteReplacementWithoutLosingPrompt()
    {
        var service = new FakeSessionService();
        var journal = new FakeOpenCodeExecutionJournal();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        journal.Routes = [new OpenCodeStoredRoute("replacement", "default", "other-account", "other-model", "provider/other-model")];
        vm.PromptInput = "Preserve pending task";
        vm.ResetSessionCommand.Execute(null); await WaitIdle(vm);
        Assert.Null(service.ResetRequest);
        Assert.True(vm.RequiresReplacementSession);
        vm.NewSessionCommand.Execute(null);
        await vm.RefreshOpenCodeRoutesAsync();
        vm.SelectedOpenCodeRoute = vm.OpenCodeRoutes.Single();
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        Assert.Equal("provider/other-model", service.CreatedRequest!.Model);
        Assert.Equal("other-model", vm.CurrentBinding!.ModelId);
        Assert.Equal("Preserve pending task", vm.PromptInput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewSessionCannotForgetActiveOrUncertainOwnership(bool journalFailure)
    {
        var completion = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeSessionService { ExecuteTurnAsyncAction = () => completion.Task };
        var journal = new FakeOpenCodeExecutionJournal();
        var locks = new FakeOpenCodeCheckoutLockService();
        var vm = CreateSessionViewModel(new FakeTimelineService(), service, journal: journal, locks: locks);
        vm.CreateSessionCommand.Execute(null); await WaitIdle(vm);
        vm.PromptInput = "Active turn";
        vm.SendPromptCommand.Execute(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.NewSessionCommand.CanExecute(null));
        Assert.False(vm.ResetSessionCommand.CanExecute(null));
        vm.NewSessionCommand.Execute(null);
        Assert.Equal("ses_123", vm.NativeSessionId);
        if (journalFailure) journal.CompletionError = new IOException("synthetic commit failure");
        completion.SetResult(new TurnResult { SessionId = "ses_123", Status = TurnResult.CompletedStatus,
            OutputText = "result", IsDeliveryUncertain = !journalFailure });
        await WaitIdle(vm);
        Assert.True(vm.IsWriterLockRetained);
        Assert.False(vm.NewSessionCommand.CanExecute(null));
        Assert.False(vm.ResetSessionCommand.CanExecute(null));
        vm.NewSessionCommand.Execute(null);
        Assert.Equal("ses_123", vm.NativeSessionId);
        Assert.False(vm.CanChooseOpenCodeRoute);
        Assert.True(locks.Token!.IsHeld);
    }
}
