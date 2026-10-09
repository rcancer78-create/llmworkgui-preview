using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class CursorWorkspaceDirectoryTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "cursor-workspace-directory");

    [Fact]
    public async Task CreateWithoutExplicitPath_UsesOpenedProject_InsteadOfProcessDirectory()
    {
        var (vm, lifecycle, _) = await Ready();
        Assert.NotEqual(Path.GetFullPath(Environment.CurrentDirectory), Root);
        await vm.CreateSessionAsync();
        Assert.Equal(Root, Assert.Single(lifecycle.SessionRequests).WorkingDirectory);
        Assert.Equal("native", vm.NativeSessionId);
    }

    [Fact]
    public async Task MissingOpenedProject_DoesNotCreateNativeSession()
    {
        var (vm, lifecycle, _) = await Ready();
        vm.ProjectId = "missing";
        await vm.CreateSessionAsync();
        Assert.Empty(lifecycle.SessionRequests);
        Assert.True(vm.HasBlocker);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("D:relative")]
    [InlineData("\\relative")]
    public async Task NonAbsoluteExplicitDirectory_IsRejectedBeforeNativeCall(string directory)
    {
        var (vm, lifecycle, _) = await Ready();
        await vm.CreateSessionAsync(directory);
        Assert.Empty(lifecycle.SessionRequests);
        Assert.True(vm.HasBlocker);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DifferentSessionDirectory_IsRefusedBeforeLockOrDispatch_AndKeepsPrompt()
    {
        var (vm, lifecycle, locks) = await Ready();
        await vm.CreateSessionAsync(Path.Combine(Root, "other"));
        vm.PromptInput = "Please inspect the project";
        await vm.SendPromptAsync();
        Assert.Contains("не совпадает", vm.Blocker);
        Assert.Empty(locks.AcquiredTokens);
        Assert.Empty(lifecycle.TurnRequests);
        Assert.Equal("Please inspect the project", vm.PromptInput);
    }

    [Fact]
    public async Task CanonicalEquivalentSessionDirectory_CanReachProtectedTurn()
    {
        var (vm, lifecycle, locks) = await Ready();
        await vm.CreateSessionAsync(Root + "\\child\\..\\");
        Assert.Equal(Root, Assert.Single(lifecycle.SessionRequests).WorkingDirectory);
        vm.PromptInput = "Please inspect the project";
        await vm.SendPromptAsync();
        Assert.Single(locks.AcquiredTokens);
        Assert.Single(lifecycle.TurnRequests);
    }

    [Fact]
    public async Task ChangedProject_IsRefusedUntilNewSessionIsCreated()
    {
        var (vm, lifecycle, locks) = await Ready();
        await vm.CreateSessionAsync();
        vm.ProjectId = "other";
        vm.PromptInput = "Please inspect the project";
        await vm.SendPromptAsync();
        Assert.Empty(locks.AcquiredTokens);
        Assert.Empty(lifecycle.TurnRequests);
        await vm.ResetSessionAsync();
        await vm.SendPromptAsync();
        Assert.Equal(Path.Combine(Root, "other"), locks.AcquiredTokens[0].CanonicalRootPath);
        Assert.Single(lifecycle.TurnRequests);
    }

    [Theory]
    [InlineData(DataClassification.PrivateSource)]
    [InlineData(DataClassification.Restricted)]
    public async Task OpenedWorkspaceSwitch_AfterCompletedTurn_RevalidatesProjectAndDoesNotCacheRoot(DataClassification otherClassification)
    {
        var settings = new InMemoryApplicationSettingsRepository();
        await settings.SetValueAsync(OpenedProjectResolver.WorkspaceSettingKey, Root);
        var (vm, lifecycle, locks) = await Ready(settings: settings, otherClassification: otherClassification);
        await vm.CreateSessionAsync();
        vm.PromptInput = "Request for first project";
        await vm.SendPromptAsync();
        Assert.Single(lifecycle.TurnRequests);
        Assert.Null(vm.ProjectId);
        Assert.Null(vm.CanonicalRootPath);

        var otherRoot = Path.Combine(Root, "other");
        await settings.SetValueAsync(OpenedProjectResolver.WorkspaceSettingKey, otherRoot);
        vm.PromptInput = "Request for second project";
        await vm.SendPromptAsync();
        Assert.Single(lifecycle.TurnRequests);
        Assert.Single(locks.AcquiredTokens);
        Assert.Contains("не совпадает", vm.Blocker);
        Assert.Equal("Request for second project", vm.PromptInput);

        await vm.ResetSessionAsync();
        Assert.Equal(otherRoot, lifecycle.SessionRequests[^1].WorkingDirectory);
        await vm.SendPromptAsync();
        if (otherClassification == DataClassification.Restricted)
        {
            Assert.Single(lifecycle.TurnRequests);
            Assert.Single(locks.AcquiredTokens);
            Assert.Contains("Restricted", vm.Blocker);
        }
        else
        {
            Assert.Equal(2, lifecycle.TurnRequests.Count);
            Assert.Equal("other", locks.AcquiredTokens[^1].ProjectId);
            Assert.Equal(otherRoot, locks.AcquiredTokens[^1].CanonicalRootPath);
        }
    }

    [Fact]
    public async Task AwaitingClassification_ReservesPanel_AndUsesCapturedProjectAndPrompt()
    {
        var gate = new DelayedGate();
        var (vm, lifecycle, locks) = await Ready(gate);
        await vm.CreateSessionAsync();
        vm.PromptInput = "Original prompt";
        var first = vm.SendPromptAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanSendPrompt);
        Assert.False(vm.CanCreateSession);
        Assert.False(vm.CanCancelTurn);
        await vm.CancelTurnAsync();
        Assert.Equal(0, lifecycle.CancelCount);
        vm.ProjectId = "other";
        vm.CanonicalRootPath = Path.Combine(Root, "other");
        vm.PromptInput = "Next prompt";
        await vm.SendPromptAsync();
        await vm.ResetSessionAsync();
        await vm.StopBackendAsync();
        Assert.Equal(1, gate.Calls);
        Assert.Single(lifecycle.SessionRequests);
        Assert.Equal(0, lifecycle.StopCount);
        gate.Completion.SetResult(DataClassificationGateDecision.Allowed());
        await first;
        Assert.Equal("project", Assert.Single(locks.AcquiredTokens).ProjectId);
        Assert.Equal(Root, locks.AcquiredTokens[0].CanonicalRootPath);
        Assert.Equal("Original prompt", Assert.Single(lifecycle.TurnRequests).Prompt.Prompt);
        Assert.Equal("Next prompt", vm.PromptInput);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("D:relative")]
    [InlineData("D:\\bad\0path")]
    public async Task InvalidStoredProjectRoot_IsRefusedWithoutThrowingOrDispatch(string storedRoot)
    {
        var projects = new InMemoryProjectRepository();
        await projects.UpsertAsync(Project("project", storedRoot));
        var lifecycle = new FakeCursorAcpSessionLifecycleService
        {
            StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart,
            SessionHandler = () => CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence
            { SessionId = "native", AvailableModeIds = new[] { "ask" } })
        };
        var locks = new FakeCheckoutLockService();
        var vm = new CursorWorkspaceViewModel(lifecycle, new CursorAcpModePolicy(), locks,
            new DataClassificationGate(), projects) { ProjectId = "project" };
        await vm.StartBackendAsync();
        await vm.CreateSessionAsync(Root);
        vm.PromptInput = "Keep this prompt";
        await vm.SendPromptAsync();
        Assert.True(vm.HasBlocker);
        Assert.False(vm.IsBusy);
        Assert.Empty(lifecycle.TurnRequests);
        Assert.Empty(locks.AcquiredTokens);
        Assert.Equal("Keep this prompt", vm.PromptInput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReset_ClearsPriorSessionEvidence(bool throws)
    {
        var (vm, lifecycle, _) = await Ready();
        await vm.CreateSessionAsync();
        lifecycle.SessionHandler = () => throws
            ? throw new InvalidOperationException("native creation failed")
            : CursorAcpSessionResult.Degraded(CursorAcpSessionFailureKind.AgentError, "native creation failed");
        await vm.ResetSessionAsync();
        vm.SelectedModeState = CapabilityState.Supported;
        vm.PromptInput = "Must not reach the old session";
        Assert.Equal(CursorWorkspaceViewModel.NotReported, vm.NativeSessionId);
        Assert.False(vm.CanSendPrompt);
        Assert.False(vm.IsBusy);
        await vm.SendPromptAsync();
        Assert.Empty(lifecycle.TurnRequests);
    }

    private static async Task<(CursorWorkspaceViewModel, FakeCursorAcpSessionLifecycleService, FakeCheckoutLockService)> Ready(
        IDataClassificationGate? gate = null, IApplicationSettingsRepository? settings = null,
        DataClassification otherClassification = DataClassification.PrivateSource)
    {
        var projects = new InMemoryProjectRepository();
        await projects.UpsertAsync(Project("project", Root));
        await projects.UpsertAsync(Project("other", Path.Combine(Root, "other"), otherClassification));
        var profiles = new InMemoryProviderProfileRepository();
        profiles.Save(new ProviderProfile("cursor", "Cursor", BackendType.CursorAcp, null, null, DataClassification.PrivateSource, true));
        var lifecycle = new FakeCursorAcpSessionLifecycleService
        {
            StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart,
            SessionHandler = () => CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence
            { SessionId = "native", AvailableModeIds = new[] { "ask", "agent" } })
        };
        var locks = new FakeCheckoutLockService();
        var vm = new CursorWorkspaceViewModel(lifecycle, new CursorAcpModePolicy(), locks,
            gate ?? new DataClassificationGate(profiles), projects, new OpenedProjectResolver(projects, settings), new FakeCursorAcpExecutionJournal())
        { ProjectId = settings is null ? "project" : null };
        FakeCursorAcpExecutionJournal.SelectRoute(vm);
        await vm.StartBackendAsync();
        return (vm, lifecycle, locks);
    }

    private static Project Project(string id, string root, DataClassification classification = DataClassification.PrivateSource) =>
        new(id, id, root, null, false, false, null, null, classification);

    private sealed class DelayedGate : IDataClassificationGate
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<DataClassificationGateDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DataClassificationGateDecision> EvaluateAsync(DataClassification projectDataClass,
            string? providerProfileId, bool isManualOnly = false, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Completion.Task;
        }
    }
}
