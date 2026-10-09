using LLMWorkGUI.App.ViewModels;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

[Collection("OpenCode permission UI isolation")]
public sealed class OpenCodePermissionWorkspaceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitReply_WaitsForDurableProjectionAndUsesSelectedReceipt(bool allow)
    {
        using var native = new ControlledLifecycle(); var journal = new ApprovalJournal();
        var vm = await Start(native, journal);
        var own = Permission("receipt-1");
        native.Pending = new[] { Permission("foreign", "foreign-session"), own };
        await Until(() => vm.HasPendingOpenCodePermissions);
        Assert.Equal(new[] { true }, journal.Waiting);
        Assert.Equal("WaitingApproval", Assert.Single(vm.ConversationTurns).Status);
        Assert.Equal(own, Assert.Single(vm.OpenCodePendingPermissions));
        Assert.Empty(native.Replies);
        Assert.True(vm.AllowOpenCodePermissionOnceCommand.CanExecute(null));
        Assert.True(vm.DenyOpenCodePermissionCommand.CanExecute(null));
        await vm.ReplyOpenCodePermissionAsync(allow);
        Assert.Equal(("native-owned", "receipt-1", allow ? "once" : "reject"), Assert.Single(native.Replies));
        await Until(() => journal.Waiting.Count == 2);
        Assert.Equal(new[] { true, false }, journal.Waiting);
        Assert.False(vm.HasPendingOpenCodePermissions);
        native.Finish(); await Until(() => !vm.IsBusy);
        Assert.Equal("Completed", Assert.Single(vm.ConversationTurns).Status);
        Assert.False(vm.AllowOpenCodePermissionOnceCommand.CanExecute(null));
        Assert.Null(vm.SelectedOpenCodePermission);
    }

    [Fact]
    public async Task CompletedTurn_WithReplyStillInFlight_CannotResetOrForgetSession()
    {
        var replyCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var native = new ControlledLifecycle { ReplyCompletion = replyCompletion.Task };
        var vm = await Start(native, new ApprovalJournal());
        native.Pending = new[] { Permission("receipt-1") };
        await Until(() => vm.HasPendingOpenCodePermissions);
        var reply = vm.ReplyOpenCodePermissionAsync(true);
        Assert.Single(native.Replies);
        native.Finish(); await Until(() => !vm.IsBusy);
        Assert.True(vm.IsSessionConfirmed);
        Assert.False(vm.IsWriterLockRetained);
        Assert.False(vm.NewSessionCommand.CanExecute(null));
        Assert.False(vm.ResetSessionCommand.CanExecute(null));
        vm.NewSessionCommand.Execute(null);
        vm.ResetSessionCommand.Execute(null);
        Assert.True(vm.IsSessionConfirmed);
        Assert.Equal("native-owned", vm.NativeSessionId);
        replyCompletion.SetResult(true); await reply;
        Assert.True(vm.NewSessionCommand.CanExecute(null));
        Assert.True(vm.ResetSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReplyButtons_AreDisabledUntilWaitingTransactionCompletes()
    {
        using var native = new ControlledLifecycle(); var journal = new ApprovalJournal { DelayWaiting = true };
        var vm = await Start(native, journal); native.Pending = new[] { Permission("receipt-1") };
        await Until(() => journal.Waiting.Count == 1);
        Assert.False(vm.HasPendingOpenCodePermissions);
        Assert.False(vm.AllowOpenCodePermissionOnceCommand.CanExecute(null));
        journal.WaitingCommit.SetResult();
        await Until(() => vm.HasPendingOpenCodePermissions);
        native.Finish(); await Until(() => !vm.IsBusy);
    }

    [Fact]
    public async Task UnconfirmedReply_DisablesCommandsAndRedactsError()
    {
        using var native = new ControlledLifecycle { FailReply = true }; var journal = new ApprovalJournal();
        var vm = await Start(native, journal); native.Pending = new[] { Permission("receipt-1") };
        await Until(() => vm.HasPendingOpenCodePermissions);
        await vm.ReplyOpenCodePermissionAsync(true);
        Assert.DoesNotContain("synthetic-ui-permission-value", vm.OpenCodePermissionStatus);
        Assert.Contains("Ответ OpenCode не подтверждён", vm.OpenCodePermissionStatus);
        Assert.False(vm.AllowOpenCodePermissionOnceCommand.CanExecute(null));
        Assert.False(vm.DenyOpenCodePermissionCommand.CanExecute(null));
        await vm.ReplyOpenCodePermissionAsync(false); Assert.Single(native.Replies);
        native.Finish(); await Until(() => !vm.IsBusy);
    }

    [Fact]
    public async Task ReplyThrownAfterSend_NamesRouteAndUnsafeRetry()
    {
        using var native = new ControlledLifecycle { FailReply = true };
        var vm = await Start(native, new ApprovalJournal());
        native.Pending = new[] { Permission("receipt-1") };
        await Until(() => vm.HasPendingOpenCodePermissions);
        await vm.ReplyOpenCodePermissionAsync(true);
        Assert.Contains("route-1", vm.OpenCodePermissionStatus);
        Assert.Contains("provider/native-model", vm.OpenCodePermissionStatus);
        Assert.Contains("OpenCode", vm.OpenCodePermissionStatus);
        Assert.Contains("native-owned", vm.OpenCodePermissionStatus);
        Assert.Contains("Ответ мог быть доставлен", vm.OpenCodePermissionStatus);
        Assert.Contains("Повтор небезопасен", vm.OpenCodePermissionStatus);
        Assert.Contains("Состояние здоровья не изменялось", vm.OpenCodePermissionStatus);
        Assert.Contains("reconciliation", vm.OpenCodePermissionStatus);
        Assert.DoesNotContain("synthetic-ui-permission-value", vm.OpenCodePermissionStatus);
        Assert.DoesNotContain("повторите попытку", vm.OpenCodePermissionStatus);
        Assert.Single(native.Replies);
    }

    [Fact]
    public async Task ReplyThrownBeforeSend_DoesNotClaimDelivery()
    {
        using var native = new ControlledLifecycle();
        var guard = new ArmedGuard();
        var vm = await Start(native, new ApprovalJournal(), guard: guard);
        native.Pending = new[] { Permission("receipt-1") };
        await Until(() => vm.HasPendingOpenCodePermissions);
        guard.Throw = true;
        await vm.ReplyOpenCodePermissionAsync(true);
        Assert.Empty(native.Replies);
        Assert.Contains("route-1", vm.OpenCodePermissionStatus);
        Assert.Contains("Ответ не доставлялся", vm.OpenCodePermissionStatus);
        Assert.DoesNotContain("Ответ мог быть доставлен", vm.OpenCodePermissionStatus);
        Assert.DoesNotContain("могла получить запрос", vm.OpenCodePermissionStatus);
        Assert.DoesNotContain("synthetic-before-send", vm.OpenCodePermissionStatus);
        Assert.Contains("Состояние здоровья не изменялось", vm.OpenCodePermissionStatus);
    }

    [Fact]
    public async Task Polling_PreservesSelectionAndRejectsStaleObjectAfterRequestChanges()
    {
        // Observe collection and selection on the same dispatcher as their UI update.
        await StaTestRunner.Run<Task>(async () =>
        {
            using var native = new ControlledLifecycle(); var vm = await Start(native, new ApprovalJournal());
            var first = Permission("receipt-1"); var second = Permission("receipt-2");
            native.Pending = new[] { first, second }; await Until(() => vm.OpenCodePendingPermissions.Count == 2);
            vm.SelectedOpenCodePermission = second;
            native.Pending = new[] { first with { Explanation = "Updated explanation" }, second };
            await Until(() => vm.OpenCodePendingPermissions[0].Explanation == "Updated explanation");
            Assert.Same(second, vm.SelectedOpenCodePermission);
            native.Pending = new[] { Permission("new-receipt") };
            await Until(() => vm.OpenCodePendingPermissions.Count == 1);
            vm.SelectedOpenCodePermission = second;
            Assert.Equal("new-receipt", vm.SelectedOpenCodePermission!.ReceiptId);
            native.Finish(); await Until(() => !vm.IsBusy);
        });
    }

    [Fact]
    public async Task UnsupportedRequest_IsVisibleWithBothCommandsDisabled()
    {
        using var native = new ControlledLifecycle(); var vm = await Start(native, new ApprovalJournal());
        native.Pending = new[] { Permission("unsupported") with { CanAllowOnce = false, CanDeny = false } };
        await Until(() => vm.HasPendingOpenCodePermissions);
        Assert.False(vm.AllowOpenCodePermissionOnceCommand.CanExecute(null));
        Assert.False(vm.DenyOpenCodePermissionCommand.CanExecute(null));
        await vm.ReplyOpenCodePermissionAsync(true); Assert.Empty(native.Replies);
        native.Finish(); await Until(() => !vm.IsBusy);
    }

    [Fact]
    public async Task JournalFailure_CancelsObservedExecutionAndRetainsAmbiguousOwnership()
    {
        using var native = new ControlledLifecycle(); var locks = new FakeOpenCodeCheckoutLockService();
        var journal = new ApprovalJournal { FailWaiting = true };
        var vm = await Start(native, journal, locks); native.Pending = new[] { Permission("receipt-1") };
        await Until(() => !vm.IsBusy);
        Assert.Equal(1, native.Cancellations);
        Assert.Empty(native.Replies); Assert.False(vm.HasPendingOpenCodePermissions);
        Assert.True(vm.IsWriterLockRetained); Assert.True(locks.Token!.IsHeld);
        Assert.Equal("Ambiguous", Assert.Single(vm.ConversationTurns).Status);
        Assert.True(Assert.Single(journal.Inner.Completions).IsDeliveryUncertain);
        locks.Token.Dispose(); // Synthetic in-memory teardown; no production reconciliation.
    }

    private static OpenCodePendingPermission Permission(string receipt, string session = "native-owned") => new()
    { ReceiptId = receipt, RequestId = "per_fixture_" + receipt, SessionId = session, NativeKind = "edit",
        Kind = NormalizedApprovalKind.WriteFile, OriginalRequestDisplay = "Synthetic write request: probe.txt",
        Explanation = "Синтетический запрос записи файла.", CanAllowOnce = true, CanDeny = true };

    private static async Task<WorkspaceViewModel> Start(ControlledLifecycle native, ApprovalJournal journal,
        FakeOpenCodeCheckoutLockService? locks = null, IApplicationInstanceGuard? guard = null)
    {
        var projects = new InMemoryProjectRepository();
        await projects.UpsertAsync(new Project("project-1", "Fixture", @"D:\synthetic permission workspace",
            null, false, false, null, null, DataClassification.PrivateSource));
        var profiles = new InMemoryProviderProfileRepository();
        profiles.Save(new ProviderProfile("default", "Fixture", BackendType.OpenCode, null, null, DataClassification.PrivateSource, true));
        var vm = new WorkspaceViewModel(new Timeline(), native, projectRepository: projects,
            dataClassificationGate: new DataClassificationGate(profiles), executionJournal: journal,
            checkoutLockService: locks ?? new FakeOpenCodeCheckoutLockService(), instanceGuard: guard,
            serverConnection: new FakeOpenCodeProcessConnection().Connection) { ProjectId = "project-1" };
        await vm.RefreshOpenCodeRoutesAsync(); vm.SelectedOpenCodeRoute = Assert.Single(vm.OpenCodeRoutes);
        vm.CreateSessionCommand.Execute(null); await Until(() => !vm.IsBusy);
        Assert.True(vm.IsSessionConfirmed, vm.SendBlocker);
        vm.PromptInput = "synthetic permission UI"; vm.SendPromptCommand.Execute(null);
        await Until(() => native.Executions == 1); return vm;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Timeline : IActivityTimelineService
    {
        public ActivityTimeline BuildTimeline(IReadOnlyList<ObservableRunProjection> projections) => ActivityTimeline.Empty;
        public ActivityTimeline BuildProjectTimeline(string p, IReadOnlyList<Execution> e, IReadOnlyList<Session> s, EvidenceSourceKind k) => ActivityTimeline.Empty;
        public ActivityTimeline BuildSessionTimeline(string s, IReadOnlyList<Execution> e, IReadOnlyList<Session> sessions, EvidenceSourceKind k) => ActivityTimeline.Empty;
    }
    private sealed class ControlledLifecycle : IOpenCodeSessionLifecycleService, IDisposable
    {
        private readonly TaskCompletionSource<TurnResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<OpenCodePendingPermission> Pending { get; set; } = Array.Empty<OpenCodePendingPermission>();
        public List<(string Session, string Receipt, string Reply)> Replies { get; } = new();
        public bool FailReply { get; set; }
        public int Executions { get; private set; }
        public Task<bool>? ReplyCompletion { get; set; }
        public int Cancellations { get; private set; }
        public void Finish() => _completion.TrySetResult(new() { SessionId = "native-owned", Status = TurnResult.CompletedStatus, OutputText = "synthetic result" });
        public void Dispose() => Finish();
        public IReadOnlyList<OpenCodePendingPermission> GetPendingPermissions(string sessionId) => Pending;
        public Task<bool> ReplyPermissionAsync(string sessionId, string receiptId, string response, CancellationToken cancellationToken = default)
        {
            Replies.Add((sessionId, receiptId, response));
            if (FailReply)
            {
                Pending = Pending.Select(p => p with { CanAllowOnce = false, CanDeny = false, ReplyAttempted = true }).ToArray();
                // Labelled synthetic redaction control, not authentication material.
                throw new InvalidOperationException("api_key=synthetic-ui-permission-value");
            }
            Pending = Array.Empty<OpenCodePendingPermission>(); return ReplyCompletion ?? Task.FromResult(true);
        }
        public Task<OpenCodeSessionResponse> CreateAndConfirmSessionAsync(OpenCodeCreateSessionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new OpenCodeSessionResponse { Id = "native-owned" });
        public Task<OpenCodeSessionResponse> ContinueSessionAsync(string sessionId, SessionBinding binding, CancellationToken cancellationToken = default) => Task.FromResult(new OpenCodeSessionResponse { Id = sessionId });
        public Task<TurnResult> ExecuteTurnAsync(string sessionId, OpenCodePromptRequest request, CancellationToken cancellationToken = default) { Executions++; return _completion.Task; }
        public Task<bool> CancelTurnAsync(string sessionId, CancellationToken cancellationToken = default) { Cancellations++; Finish(); return Task.FromResult(true); }
        public Task<OpenCodeSessionResponse> ResetSessionAsync(string oldSessionId, OpenCodeCreateSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<string> GetAncestry(string sessionId) => Array.Empty<string>();
    }
    private sealed class ApprovalJournal : IOpenCodeExecutionJournal
    {
        public FakeOpenCodeExecutionJournal Inner { get; } = new();
        public List<bool> Waiting { get; } = new();
        public bool DelayWaiting { get; set; }
        public bool FailWaiting { get; set; }
        public TaskCompletionSource WaitingCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SetWaitingApprovalAsync(OpenCodeJournalEntry entry, bool waiting, CancellationToken cancellationToken = default)
        { Waiting.Add(waiting); if (FailWaiting) throw new InvalidOperationException("Synthetic journal projection failure."); if (waiting && DelayWaiting) await WaitingCommit.Task; }
        public Task<IReadOnlyList<OpenCodeStoredRoute>> ListRoutesAsync(CancellationToken cancellationToken = default) => Inner.ListRoutesAsync(cancellationToken);
        public Task<string> ConfirmSessionAsync(string projectId, string rootPath, string nativeSessionId, OpenCodeStoredRoute route, CancellationToken cancellationToken = default) => Inner.ConfirmSessionAsync(projectId, rootPath, nativeSessionId, route, cancellationToken);
        public Task<OpenCodeJournalEntry> BeginAsync(string projectId, string rootPath, string nativeSessionId, OpenCodeStoredRoute route, string clientRequestId, string promptHash, long processGeneration, CancellationToken cancellationToken = default) => Inner.BeginAsync(projectId, rootPath, nativeSessionId, route, clientRequestId, promptHash, processGeneration, cancellationToken);
        public Task MarkRunningAsync(OpenCodeJournalEntry entry, CancellationToken cancellationToken = default) => Inner.MarkRunningAsync(entry, cancellationToken);
        public Task CompleteAsync(OpenCodeJournalEntry entry, TurnResult result, CancellationToken cancellationToken = default) => Inner.CompleteAsync(entry, result, cancellationToken);
    }

    private sealed class ArmedGuard : IApplicationInstanceGuard
    {
        public bool Throw { get; set; }
        public string InstanceId => "fixture-instance";
        public bool IsPrimarySupervisor => true;
        public bool IsViewOnly => false;
        public void EnsureSupervisorPermitted()
        {
            if (Throw) throw new InvalidOperationException("api_key=synthetic-before-send");
        }
        public void Dispose() { }
    }
}

[CollectionDefinition("OpenCode permission UI isolation", DisableParallelization = true)]
public sealed class OpenCodePermissionUiCollection { }
