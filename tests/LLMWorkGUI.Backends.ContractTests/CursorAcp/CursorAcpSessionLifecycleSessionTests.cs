using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

/// <summary>
/// Session, ancestry, turn, approval and cancellation contract of
/// <see cref="CursorAcpSessionLifecycleService"/> (ТЗ §6.4, §6.7, §6.9, ADR-0003 §4.2, §6.2).
/// </summary>
public sealed class CursorAcpSessionLifecycleSessionTests
{
    private const string WorkingDirectory = @"C:\workspace\project";

    [Theory]
    [InlineData("create")]
    [InlineData("load")]
    [InlineData("reset")]
    public async Task SessionResponseAfterBackendStopCannotRepublishStoppedGeneration(string operation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();
        async Task<CursorAcpSessionResult> DelayedResponse()
        {
            entered.TrySetResult();
            // Deliberately ignores cancellation: physical stop must not wait on this RPC.
            await release.Task;
            return CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "stale-session" });
        }
        client.CreateSessionHandler = (_, _) => DelayedResponse();
        client.LoadSessionHandler = (_, _) => DelayedResponse();
        await using var service = await StartedServiceAsync(client);
        var request = new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory };
        var pending = operation switch
        {
            "create" => service.CreateSessionAsync(request),
            "load" => service.LoadSessionAsync(new CursorAcpLoadSessionRequest
                { SessionId = "persisted-session", WorkingDirectory = WorkingDirectory }),
            _ => service.ResetSessionAsync(request)
        };
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.StopBackendAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.Null(service.Current);
            release.TrySetResult();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.IsReady);
            Assert.Equal(CursorAcpSessionFailureKind.NotReady, result.FailureKind);
            Assert.Null(service.NativeSessionId);
            Assert.DoesNotContain("stale-session", service.Ancestry);
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ConcurrentSessionOperationsKeepInvocationOrderWithoutBlockingPhysicalStop()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();
        client.CreateSessionHandler = async (_, _) =>
        {
            var sequence = Interlocked.Increment(ref calls);
            if (sequence == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            else secondEntered.TrySetResult();
            return CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "ordered-" + sequence });
        };
        await using var service = await StartedServiceAsync(client);
        var request = new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory };
        var first = service.CreateSessionAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.ResetSessionAsync(request);
        try
        {
            // Handler entry is synchronous before its first await. Calling Reset must queue rather
            // than issue a second session RPC while the first owns session-operation order.
            Assert.False(secondEntered.Task.IsCompleted);
            release.TrySetResult();
            Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsReady);
            Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(5))).IsReady);
            Assert.Equal("ordered-2", service.NativeSessionId);
            Assert.Equal(new[] { "ordered-1", "ordered-2" }, service.Ancestry);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CreateSessionAsync_BeforeStart_IsDegradedWithoutSending()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();

        await using var service = CursorAcpSessionLifecycleStartTests.CreateService(
            FakeCursorAcpProcessManager.Started(new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused())),
            client);

        var result = await service.CreateSessionAsync(new CursorAcpNewSessionRequest
        {
            WorkingDirectory = WorkingDirectory
        });

        Assert.True(result.IsDegraded);
        Assert.Equal(CursorAcpSessionFailureKind.NotReady, result.FailureKind);
        Assert.Empty(client.CreateSessionRequests);
        Assert.Null(service.NativeSessionId);
        Assert.Empty(service.Ancestry);
    }

    [Fact]
    public async Task CreateSessionAsync_ReadyBackend_TracksNativeSessionAndAncestry()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();
        client.CreateSessionHandler = (_, _) => Task.FromResult(
            CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "cursor-session-1" }));

        await using var service = await StartedServiceAsync(client);

        var result = await service.CreateSessionAsync(new CursorAcpNewSessionRequest
        {
            WorkingDirectory = WorkingDirectory
        });

        Assert.True(result.IsReady, result.Blocker);
        Assert.Equal("cursor-session-1", service.NativeSessionId);
        Assert.Equal(new[] { "cursor-session-1" }, service.Ancestry);
    }

    [Fact]
    public async Task ResetSessionAsync_AppendsLineageAndKeepsPreviousSessionId()
    {
        var sessionIds = new Queue<string>(new[] { "cursor-session-1", "cursor-session-2" });

        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();
        client.CreateSessionHandler = (_, _) => Task.FromResult(
            CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = sessionIds.Dequeue() }));

        await using var service = await StartedServiceAsync(client);

        await service.CreateSessionAsync(new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });
        var reset = await service.ResetSessionAsync(new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.True(reset.IsReady, reset.Blocker);
        Assert.Equal("cursor-session-2", service.NativeSessionId);

        // The previous native session id is retained: a reset never deletes history (ТЗ §6.4).
        Assert.Equal(new[] { "cursor-session-1", "cursor-session-2" }, service.Ancestry);
    }

    [Fact]
    public async Task LoadSessionAsync_UnsupportedCapability_IsDegradedAndDoesNotTrackSession()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();
        client.LoadSessionHandler = (_, _) => Task.FromResult(CursorAcpSessionResult.Degraded(
            CursorAcpSessionFailureKind.UnsupportedCapability,
            "The agent did not report the loadSession capability."));

        await using var service = await StartedServiceAsync(client);

        var result = await service.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = "cursor-session-persisted",
            WorkingDirectory = WorkingDirectory
        });

        Assert.True(result.IsDegraded);
        Assert.Equal(CursorAcpSessionFailureKind.UnsupportedCapability, result.FailureKind);
        Assert.Null(service.NativeSessionId);
        Assert.Empty(service.Ancestry);
    }

    [Fact]
    public async Task ExecuteTurnAsync_BeforeStart_IsRejectedWithoutPrompt()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();

        await using var service = CursorAcpSessionLifecycleStartTests.CreateService(
            FakeCursorAcpProcessManager.Started(new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused())),
            client);

        var result = await service.ExecuteTurnAsync(CreateTurnRequest());

        Assert.Equal(CursorAcpTurnOutcome.Rejected, result.Outcome);
        Assert.False(result.IsTerminal);
        Assert.Empty(client.PromptRequests);
    }

    [Fact]
    public async Task ExecuteTurnAsync_ReadyBackend_CompletesAndReportsStates()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();

        await using var service = await StartedServiceAsync(client);

        var states = new List<CursorAcpTurnState>();
        service.TurnStateChanged += (_, args) => states.Add(args.State);

        var result = await service.ExecuteTurnAsync(CreateTurnRequest());

        Assert.Equal(CursorAcpTurnOutcome.Succeeded, result.Outcome);
        Assert.Single(client.PromptRequests);
        Assert.Contains(CursorAcpTurnState.Running, states);
        Assert.Contains(CursorAcpTurnState.Succeeded, states);
    }

    [Fact]
    public async Task CancelTurnAsync_WithoutConfirmedSession_IsDegraded()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();

        await using var service = await StartedServiceAsync(client);

        var result = await service.CancelTurnAsync();

        Assert.False(result.IsSent);
        Assert.Equal(CursorAcpCancelFailureKind.InvalidSessionId, result.FailureKind);
        Assert.Empty(client.CancelRequests);
    }

    [Fact]
    public async Task CancelTurnAsync_ConfirmedSession_SendsCancelForThatSession()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();
        client.CreateSessionHandler = (_, _) => Task.FromResult(
            CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "cursor-session-cancel" }));

        await using var service = await StartedServiceAsync(client);

        await service.CreateSessionAsync(new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        var result = await service.CancelTurnAsync();

        Assert.True(result.IsSent);

        // The cancel send is never a terminal confirmation (ADR-0003 §6.2).
        Assert.False(result.IsTerminal);
        var request = Assert.Single(client.CancelRequests);
        Assert.Equal("cursor-session-cancel", request.SessionId);
    }

    [Fact]
    public async Task CancelTurnAsync_ActiveTurnEntersCancellingAndObservesNativeConfirmation()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();
        client.CreateSessionHandler = (_, _) => Task.FromResult(
            CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "cursor-session-turn" }));
        var prompt = new TaskCompletionSource<CursorAcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PromptHandler = (_, _) => prompt.Task;
        await using var service = await StartedServiceAsync(client);
        await service.CreateSessionAsync(new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });
        var states = new System.Collections.Concurrent.ConcurrentQueue<CursorAcpTurnState>();
        service.TurnStateChanged += (_, args) => states.Enqueue(args.State);
        var turn = service.ExecuteTurnAsync(CreateTurnRequest());
        await client.PromptDispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var sent = await service.CancelTurnAsync();
            Assert.True(sent.IsSent);
            Assert.Contains(CursorAcpTurnState.Cancelling, states);
            Assert.False(turn.IsCompleted);
            prompt.TrySetResult(CursorAcpPromptResult.Completed(CursorAcpStopReasons.Cancelled));
            var result = await turn.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CursorAcpTurnOutcome.Cancelled, result.Outcome);
            Assert.Single(client.CancelRequests);
        }
        finally
        {
            prompt.TrySetResult(CursorAcpPromptResult.Completed(CursorAcpStopReasons.Cancelled));
            await turn.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CancelTurnAsync_SessionResetCannotRedirectCancellationAwayFromActiveTurn()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();
        client.CreateSessionHandler = (_, _) => Task.FromResult(
            CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "cursor-session-turn" }));
        var prompt = new TaskCompletionSource<CursorAcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PromptHandler = (_, _) => prompt.Task;
        await using var service = await StartedServiceAsync(client);
        await service.CreateSessionAsync(new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });
        var turn = service.ExecuteTurnAsync(CreateTurnRequest());
        await client.PromptDispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            client.CreateSessionHandler = (_, _) => Task.FromResult(
                CursorAcpSessionResult.Ready(new CursorAcpSessionEvidence { SessionId = "replacement-session" }));
            await service.ResetSessionAsync(new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });
            Assert.Equal("replacement-session", service.NativeSessionId);

            Assert.True((await service.CancelTurnAsync()).IsSent);
            Assert.Equal("cursor-session-turn", Assert.Single(client.CancelRequests).SessionId);
            Assert.False(turn.IsCompleted);
        }
        finally
        {
            prompt.TrySetResult(CursorAcpPromptResult.Completed(CursorAcpStopReasons.Cancelled));
            await turn.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task PermissionRequested_IsSurfacedAsUnknownHighRiskWithoutAutoApproval()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();

        await using var service = await StartedServiceAsync(client);

        var observed = new TaskCompletionSource<CursorAcpStreamEvent.PermissionRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        service.PermissionRequested += (_, request) => observed.TrySetResult(request);

        client.PushEvent(new CursorAcpStreamEvent.PermissionRequest
        {
            Method = "session/request_permission",
            SessionId = "cursor-session-permission",
            RequestId = "permission-1",
            Description = "Write to a workspace file"
        });

        var permission = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NormalizedApprovalKind.UnknownHighRisk, permission.ApprovalKind);

        // No reply is sent until the user decides explicitly.
        Assert.Empty(client.PermissionReplies);
    }

    [Fact]
    public async Task StreamEventObserved_RepublishesNormalizedEvents()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();

        await using var service = await StartedServiceAsync(client);

        var observed = new TaskCompletionSource<CursorAcpStreamEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        service.StreamEventObserved += (_, streamEvent) => observed.TrySetResult(streamEvent);

        client.PushEvent(new CursorAcpStreamEvent.TextChunk
        {
            Method = "session/update",
            SessionId = "cursor-session-text",
            Text = "partial answer"
        });

        var chunk = Assert.IsType<CursorAcpStreamEvent.TextChunk>(
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("partial answer", chunk.Text);
    }

    private static async Task<CursorAcpSessionLifecycleService> StartedServiceAsync(FakeCursorAcpClient client)
    {
        var service = CursorAcpSessionLifecycleStartTests.CreateService(
            FakeCursorAcpProcessManager.Started(new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused())),
            client);

        var started = await service.StartBackendAsync("exec-session-tests");
        Assert.True(started.IsReady, started.Blocker);

        return service;
    }

    private static CursorAcpTurnRequest CreateTurnRequest() =>
        new()
        {
            Prompt = CursorAcpPromptRequest.Create("cursor-session-turn", "Explain the failing test."),
            Mode = new CursorAcpModeDecision
            {
                Mode = CursorAcpMode.Ask,
                ModeId = "ask",
                Access = CursorAcpModeAccess.ReadOnly,
                State = CapabilityState.Supported,
                CanSend = true,
                RequiresWriterLock = false
            }
        };
}
