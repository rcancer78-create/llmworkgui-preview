using System.IO;
using System.Threading.Tasks;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[CollectionDefinition("Cursor dispatcher isolation", DisableParallelization = true)]
public sealed class CursorDispatcherIsolationCollection;

[Collection("Cursor dispatcher isolation")]
public sealed class CursorNativeDiscoveryTests
{
    [Fact]
    public async Task BackgroundStream_UpdatesCollectionOnOwningUiThread()
    {
        StaTestRunner.EnsureApplication();
        var (vm, lifecycle) = await StaTestRunner.Run(() => Ready());
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StaTestRunner.Run(() => vm.StreamEvents.CollectionChanged += (_, _) =>
            observed.TrySetResult(System.Windows.Application.Current.Dispatcher.CheckAccess()));
        await Task.Run(() => lifecycle.RaiseStreamEvent(new CursorAcpStreamEvent.TextChunk
        {
            Method = "session/update", SessionId = "s", Text = "native response"
        }));
        Assert.True(await observed.Task.WaitAsync(System.TimeSpan.FromSeconds(5)));
        StaTestRunner.Run(() => Assert.Equal("native response", Assert.Single(vm.StreamEvents).Text));
    }

    [Fact]
    public async Task Discovery_IsSessionScoped_AndDoesNotInferReadOnlyAccess()
    {
        var (vm, lifecycle) = await Ready();
        Assert.Equal(CapabilityState.Supported, vm.SelectedModeState);
        Assert.Equal(CursorAcpModeAccess.Unknown, vm.SelectedModeAccess);
        Assert.True(vm.ModeRequiresWriterLock);
        vm.SelectedModeId = "plan";
        Assert.Equal(CapabilityState.Unknown, vm.SelectedModeState);
        vm.SelectedModeId = "ask";
        Assert.Equal(CapabilityState.Supported, vm.SelectedModeState);
        lifecycle.SessionHandler = () => CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "new" });
        await vm.ResetSessionAsync(Path.GetTempPath());
        Assert.Equal(CapabilityState.Unknown, vm.SelectedModeState);
        Assert.False(vm.CanSendPrompt);
        await vm.StopBackendAsync();
        Assert.Equal(CapabilityState.Unknown, vm.SelectedModeState);
    }

    [Fact]
    public async Task Permissions_QueueInOrderAndPersistentOnlyRequestCannotBeAllowed()
    {
        StaTestRunner.EnsureApplication();
        await StaTestRunner.Run(async () =>
        {
            var (vm, lifecycle) = await Ready();
            lifecycle.RaisePermissionRequest(new CursorAcpStreamEvent.PermissionRequest { Method = "session/request_permission", SessionId = "s", RequestId = "one", Description = "First", CanAllowOnce = false });
            lifecycle.RaisePermissionRequest(new CursorAcpStreamEvent.PermissionRequest { Method = "session/request_permission", SessionId = "s", RequestId = "two", Description = "Second", CanAllowOnce = true });
            Assert.Equal("First", vm.PendingPermissionDescription);
            Assert.False(vm.AllowOnceCommand.CanExecute(null));
            await vm.ReplyPermissionAsync(true);
            Assert.Empty(lifecycle.PermissionReplies);
            await vm.ReplyPermissionAsync(false);
            Assert.Equal("one", Assert.Single(lifecycle.PermissionReplies).PermissionId);
            Assert.Equal("Second", vm.PendingPermissionDescription);
            Assert.True(vm.AllowOnceCommand.CanExecute(null));
            await vm.ReplyPermissionAsync(true);
            Assert.Equal("two", lifecycle.PermissionReplies[1].PermissionId);
            Assert.False(vm.IsAwaitingPermission);
        });
    }

    private static async Task<(CursorWorkspaceViewModel, FakeCursorAcpSessionLifecycleService)> Ready()
    {
        var lifecycle = new FakeCursorAcpSessionLifecycleService
        {
            StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart,
            SessionHandler = () => CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "s", AvailableModeIds = new[] { "ask" } })
        };
        var vm = new CursorWorkspaceViewModel(lifecycle, new CursorAcpModePolicy()) { PromptInput = "Hello" };
        await vm.StartBackendAsync();
        await vm.CreateSessionAsync(Path.GetTempPath());
        return (vm, lifecycle);
    }
}
