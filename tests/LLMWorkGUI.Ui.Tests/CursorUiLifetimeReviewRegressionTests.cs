using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

// These owned doubles prove UI subscriptions and displayed evidence only. They never start a native
// process, own a checkout lock, or prove a remote terminal result.
[Collection("Cursor dispatcher isolation")]
public sealed class CursorUiLifetimeReviewRegressionTests
{
    private const string SessionId = "owned-cursor-ui-session";

    [Fact]
    public void DisposeUnsubscribesAllCallbacksWithoutStoppingOrDisposingTheLifecycle()
    {
        var lifecycle = new OwnedLifecycle();
        var viewModel = Create(lifecycle);
        Assert.Equal(3, lifecycle.SubscriberCount);

        Dispose(viewModel);

        Assert.Equal(0, lifecycle.SubscriberCount);
        Assert.Equal(0, lifecycle.StopCount);
        Assert.Equal(0, lifecycle.DisposeCount);
    }

    [Fact]
    public async Task AlreadyPostedCallbacksCannotMutateTheDisposedPanel()
    {
        StaTestRunner.EnsureApplication();
        await StaTestRunner.Run(async () =>
        {
            var lifecycle = new OwnedLifecycle();
            var viewModel = await StartedAsync(lifecycle);
            var turnState = viewModel.TurnStateDisplay;
            // Block this dispatcher until all callbacks are posted; disposal precedes their publication.
            var producer = new Thread(lifecycle.RaiseCallbacks) { IsBackground = true };
            producer.Start();
            Assert.True(producer.Join(TimeSpan.FromSeconds(5)));
            Dispose(viewModel);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Empty(viewModel.StreamEvents);
            Assert.False(viewModel.IsAwaitingPermission);
            Assert.Equal(turnState, viewModel.TurnStateDisplay);
            Assert.Equal(SessionId, lifecycle.NativeSessionId);
            Assert.Equal(0, lifecycle.StopCount);
        });
    }

    [Fact]
    public async Task DisposedPanelRefusesNewLifecycleCalls()
    {
        var lifecycle = new OwnedLifecycle();
        var viewModel = Create(lifecycle);
        Dispose(viewModel);

        await viewModel.StartBackendAsync();
        await viewModel.CreateSessionAsync(Path.GetTempPath());
        await viewModel.StopBackendAsync();
        await viewModel.CancelTurnAsync();
        await viewModel.ReplyPermissionAsync(false);

        Assert.False(viewModel.CanStartBackend);
        Assert.Equal(0, lifecycle.StartCount);
        Assert.Equal(0, lifecycle.StopCount);
        Assert.Empty(lifecycle.Inner.SessionRequests);
        Assert.Empty(lifecycle.Inner.PermissionReplies);
    }

    [Fact]
    public async Task DeferredStartCompletionCannotPublishIntoDisposedPanel()
    {
        var lifecycle = new OwnedLifecycle
        {
            DeferredStart = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var viewModel = Create(lifecycle);
        var operation = viewModel.StartBackendAsync();
        try
        {
            Assert.Equal(1, lifecycle.StartCount);
            Assert.True(viewModel.IsBusy);
            Dispose(viewModel);
            var stateAfterDispose = viewModel.BackendStateDisplay;
            lifecycle.DeferredStart.SetResult(FakeCursorAcpSessionLifecycleService.CreateReadyStart());
            await operation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(viewModel.IsBackendReady);
            Assert.Equal(stateAfterDispose, viewModel.BackendStateDisplay);
            Assert.False(viewModel.CanStartBackend);
            Assert.Equal(0, lifecycle.StopCount);
        }
        finally
        {
            lifecycle.DeferredStart.TrySetResult(FakeCursorAcpSessionLifecycleService.CreateReadyStart());
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StartFailureBecomesARedactedUiDiagnostic()
    {
        var lifecycle = new OwnedLifecycle { StartFailure = new IOException("credential-canary-private-path") };
        var viewModel = Create(lifecycle);

        var failure = await Record.ExceptionAsync(() => viewModel.StartBackendAsync());

        Assert.Null(failure);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsBackendReady);
        Assert.True(viewModel.HasBlocker);
        Assert.DoesNotContain("credential-canary", viewModel.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedStopPreservesObservedSessionAndReportsUnconfirmedShutdown()
    {
        var lifecycle = new OwnedLifecycle { StopFailure = new IOException("credential-canary-private-path") };
        var viewModel = await StartedAsync(lifecycle);
        var sessionState = viewModel.SessionStatusDisplay;

        var failure = await Record.ExceptionAsync(() => viewModel.StopBackendAsync());

        Assert.Null(failure);
        Assert.Equal(SessionId, viewModel.NativeSessionId);
        Assert.Equal(SessionId, lifecycle.NativeSessionId);
        Assert.Equal(sessionState, viewModel.SessionStatusDisplay);
        Assert.NotEqual(CursorWorkspaceViewModel.BackendStoppedState, viewModel.BackendStateDisplay);
        Assert.True(viewModel.HasBlocker);
        Assert.DoesNotContain("credential-canary", viewModel.Blocker, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public void StartCommandFailureIsVisibleAfterTheSynchronousFailedTask()
    {
        var lifecycle = new OwnedLifecycle { StartFailure = new IOException("credential-canary") };
        var viewModel = Create(lifecycle);
        viewModel.StartBackendCommand.Execute(null);

        Assert.True(viewModel.HasBlocker);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsBackendReady);
        Assert.DoesNotContain("credential-canary", viewModel.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedStopStillClearsTheDisplayedSession()
    {
        var lifecycle = new OwnedLifecycle();
        var viewModel = await StartedAsync(lifecycle);
        await viewModel.StopBackendAsync();

        Assert.Equal(1, lifecycle.StopCount);
        Assert.Null(lifecycle.NativeSessionId);
        Assert.Equal(CursorWorkspaceViewModel.NotReported, viewModel.NativeSessionId);
        Assert.Equal(CursorWorkspaceViewModel.BackendStoppedState, viewModel.BackendStateDisplay);
    }

    private static CursorWorkspaceViewModel Create(OwnedLifecycle lifecycle) => new(lifecycle, new CursorAcpModePolicy());
    private static void Dispose(CursorWorkspaceViewModel viewModel) =>
        Assert.IsAssignableFrom<IDisposable>((object)viewModel).Dispose();
    private static async Task<CursorWorkspaceViewModel> StartedAsync(OwnedLifecycle lifecycle)
    {
        var viewModel = Create(lifecycle);
        await viewModel.StartBackendAsync();
        await viewModel.CreateSessionAsync(Path.GetTempPath());
        Assert.Equal(SessionId, viewModel.NativeSessionId);
        return viewModel;
    }

    private sealed class OwnedLifecycle : ICursorAcpSessionLifecycleService
    {
        public FakeCursorAcpSessionLifecycleService Inner { get; } = new()
        {
            StartHandler = FakeCursorAcpSessionLifecycleService.CreateReadyStart,
            SessionHandler = () => CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = SessionId })
        };
        public Exception? StartFailure { get; init; }
        public Exception? StopFailure { get; init; }
        public TaskCompletionSource<CursorAcpBackendStartResult>? DeferredStart { get; init; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public CursorAcpBackendStartResult? Current => Inner.Current;
        public string? NativeSessionId => Inner.NativeSessionId;
        public IReadOnlyList<string> Ancestry => Inner.Ancestry;
        public event EventHandler<CursorAcpStreamEvent>? StreamEventObserved;
        public event EventHandler<CursorAcpStreamEvent.PermissionRequest>? PermissionRequested;
        public event EventHandler<CursorAcpTurnStateChangedEventArgs>? TurnStateChanged;
        public int SubscriberCount => (StreamEventObserved?.GetInvocationList().Length ?? 0)
            + (PermissionRequested?.GetInvocationList().Length ?? 0) + (TurnStateChanged?.GetInvocationList().Length ?? 0);
        public Task<CursorAcpBackendStartResult> StartBackendAsync(string executionId, CancellationToken cancellationToken = default)
        {
            StartCount++;
            return StartFailure is not null ? Task.FromException<CursorAcpBackendStartResult>(StartFailure)
                : DeferredStart?.Task ?? Inner.StartBackendAsync(executionId, cancellationToken);
        }
        public Task StopBackendAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return StopFailure is not null ? Task.FromException(StopFailure) : Inner.StopBackendAsync(cancellationToken);
        }
        public Task<CursorAcpSessionResult> CreateSessionAsync(CursorAcpNewSessionRequest request, CancellationToken cancellationToken = default) => Inner.CreateSessionAsync(request, cancellationToken);
        public Task<CursorAcpSessionResult> LoadSessionAsync(CursorAcpLoadSessionRequest request, CancellationToken cancellationToken = default) => Inner.LoadSessionAsync(request, cancellationToken);
        public Task<CursorAcpSessionResult> ResetSessionAsync(CursorAcpNewSessionRequest request, CancellationToken cancellationToken = default) => Inner.ResetSessionAsync(request, cancellationToken);
        public Task<CursorAcpTurnResult> ExecuteTurnAsync(CursorAcpTurnRequest request, CancellationToken cancellationToken = default) => Inner.ExecuteTurnAsync(request, cancellationToken);
        public Task<CursorAcpCancelResult> CancelTurnAsync(CancellationToken cancellationToken = default) => Inner.CancelTurnAsync(cancellationToken);
        public Task<CursorAcpPermissionReplyResult> ReplyPermissionAsync(CursorAcpPermissionReplyRequest request, CancellationToken cancellationToken = default) => Inner.ReplyPermissionAsync(request, cancellationToken);
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
        public void RaiseCallbacks()
        {
            StreamEventObserved?.Invoke(this, new CursorAcpStreamEvent.TextChunk { SessionId = SessionId, Method = "session/update", Text = "owned metadata" });
            PermissionRequested?.Invoke(this, new CursorAcpStreamEvent.PermissionRequest { SessionId = SessionId, Method = "session/request_permission", RequestId = "owned-permission", Description = "Owned synthetic permission" });
            TurnStateChanged?.Invoke(this, new CursorAcpTurnStateChangedEventArgs(SessionId, CursorAcpTurnState.WaitingApproval));
        }
    }
}
