using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpTurnSupervisorTests
{
    private const string SessionId = "sess-test-42";
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("unknown")]
    [InlineData("refusal")]
    public async Task UnknownStopReason_DoesNotSucceedOrReleaseWriter(string stopReason)
    {
        var client = new FakeCursorAcpClient { PromptHandler = (_, _) => Task.FromResult(CursorAcpPromptResult.Completed(stopReason)) };
        var token = new FakeCheckoutLockToken();
        var result = await CreateSupervisor(client).ExecuteTurnAsync(CreateAgentModeDecision(), token, CreatePromptRequest());
        Assert.Equal(CursorAcpTurnOutcome.Ambiguous, result.Outcome);
        Assert.True(token.IsHeld);
        Assert.Equal(0, token.ReleaseCount);
    }

    [Fact]
    public async Task ExecuteTurnAsync_ReleasedWriterTokenIsRefusedBeforeDispatch()
    {
        var client = new FakeCursorAcpClient();
        var token = new FakeCheckoutLockToken();
        await token.ReleaseAsync("previous turn completed");

        var result = await CreateSupervisor(client).ExecuteTurnAsync(
            CreateAgentModeDecision(), token, CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Rejected, result.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.WriterLockMissing, result.FailureKind);
        Assert.Empty(client.PromptRequests);
        Assert.Equal(1, token.ReleaseCount);
        Assert.False(result.LockRetained);
    }

    [Fact]
    public async Task ExecuteTurnAsync_ModeRejectionReportsRetainedSuppliedLock()
    {
        var client = new FakeCursorAcpClient();
        var token = new FakeCheckoutLockToken();
        var rejectedMode = CreateAgentModeDecision() with { CanSend = false, Blocker = "refused" };

        var result = await CreateSupervisor(client).ExecuteTurnAsync(rejectedMode, token, CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Rejected, result.Outcome);
        Assert.True(token.IsHeld);
        Assert.True(result.LockRetained);
        Assert.False(result.LockReleased);
        Assert.Equal(0, token.ReleaseCount);
        Assert.Empty(client.PromptRequests);
    }

    [Fact]
    public async Task ExecuteTurnAsync_SuccessfulTurn_ReleasesWriterLockAndReportsStates()
    {
        var client = new FakeCursorAcpClient();
        var lockToken = new FakeCheckoutLockToken();
        var supervisor = CreateSupervisor(client);

        var result = await supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            lockToken,
            CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Succeeded, result.Outcome);
        Assert.Equal("endTurn", result.StopReason);
        Assert.Null(result.FailureKind);
        Assert.Null(result.Blocker);
        Assert.True(result.IsTerminal);
        Assert.False(result.ConsumesModelRetryBudget);
        Assert.True(result.LockReleased);
        Assert.False(result.LockRetained);
        Assert.Equal(1, lockToken.ReleaseCount);
        Assert.False(lockToken.IsHeld);
        Assert.Equal(
            new[]
            {
                CursorAcpTurnState.Starting,
                CursorAcpTurnState.SessionConfirmed,
                CursorAcpTurnState.Running,
                CursorAcpTurnState.Succeeded
            },
            result.StatesVisited);

        var prompt = Assert.Single(client.PromptRequests);
        Assert.Equal(SessionId, prompt.SessionId);
        Assert.Equal(result.ClientRequestId, prompt.ClientRequestId);
    }

    [Fact]
    public async Task ExecuteTurnAsync_ReadOnlySupportedModeWithoutLock_Succeeds()
    {
        var client = new FakeCursorAcpClient();
        var supervisor = CreateSupervisor(client);

        var result = await supervisor.ExecuteTurnAsync(
            new CursorAcpModeDecision
            {
                Mode = CursorAcpMode.Plan,
                ModeId = "plan",
                Access = CursorAcpModeAccess.ReadOnly,
                State = CapabilityState.Supported,
                CanSend = true,
                RequiresWriterLock = false
            },
            lockToken: null,
            CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Succeeded, result.Outcome);
        Assert.False(result.LockReleased);
        Assert.False(result.LockRetained);
    }

    [Fact]
    public async Task ExecuteTurnAsync_UnknownMode_IsRejectedBeforeDispatch()
    {
        var client = new FakeCursorAcpClient();
        var supervisor = CreateSupervisor(client);

        var result = await supervisor.ExecuteTurnAsync(
            new CursorAcpModeDecision
            {
                Mode = CursorAcpMode.Unknown,
                ModeId = "Unknown",
                Access = CursorAcpModeAccess.Unknown,
                State = CapabilityState.Unknown,
                CanSend = false,
                RequiresWriterLock = true,
                Blocker = "unknown mode"
            },
            lockToken: new FakeCheckoutLockToken(),
            CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Rejected, result.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.ModeNotSendable, result.FailureKind);
        Assert.False(result.IsTerminal);
        Assert.Empty(result.StatesVisited);
        Assert.Empty(client.PromptRequests);
    }

    [Fact]
    public async Task ExecuteTurnAsync_TamperedCanSendDecision_FailsClosed()
    {
        var client = new FakeCursorAcpClient();
        var supervisor = CreateSupervisor(client);

        var result = await supervisor.ExecuteTurnAsync(
            new CursorAcpModeDecision
            {
                Mode = CursorAcpMode.Unknown,
                ModeId = "Unknown",
                Access = CursorAcpModeAccess.ReadOnly,
                State = CapabilityState.Unknown,
                CanSend = true,
                RequiresWriterLock = false
            },
            lockToken: null,
            CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Rejected, result.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.ModeNotSendable, result.FailureKind);
        Assert.Empty(client.PromptRequests);
    }

    [Fact]
    public async Task ExecuteTurnAsync_WriterLockRequiredButMissing_IsRejectedBeforeDispatch()
    {
        var client = new FakeCursorAcpClient();
        var supervisor = CreateSupervisor(client);

        var result = await supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            lockToken: null,
            CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Rejected, result.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.WriterLockMissing, result.FailureKind);
        Assert.Empty(client.PromptRequests);
    }

    [Fact]
    public async Task ExecuteTurnAsync_SecondConcurrentTurnOnSameSession_IsRejected()
    {
        var client = new FakeCursorAcpClient();
        var completion = new TaskCompletionSource<CursorAcpPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.PromptHandler = (_, _) => completion.Task;

        var supervisor = CreateSupervisor(client);
        var firstLock = new FakeCheckoutLockToken();

        var firstTurn = supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            firstLock,
            CreatePromptRequest());

        await client.PromptDispatched.Task.WaitAsync(BoundedWait);

        var secondResult = await supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            new FakeCheckoutLockToken(),
            CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Rejected, secondResult.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.ConcurrentTurn, secondResult.FailureKind);
        Assert.Single(client.PromptRequests);

        completion.SetResult(CursorAcpPromptResult.Completed(CursorAcpStopReasons.EndTurn));

        var firstResult = await firstTurn.WaitAsync(BoundedWait);
        Assert.Equal(CursorAcpTurnOutcome.Succeeded, firstResult.Outcome);
        Assert.Equal(1, firstLock.ReleaseCount);
    }

    [Fact]
    public async Task ExecuteTurnAsync_PermissionRequest_TransitionsRunningWaitingApprovalRunning()
    {
        var client = new FakeCursorAcpClient();
        var approvalReplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.PromptHandler = async (_, _) =>
        {
            await approvalReplied.Task;

            return CursorAcpPromptResult.Completed(CursorAcpStopReasons.EndTurn);
        };
        client.PermissionReplyHandler = (_, _) =>
        {
            return Task.FromResult(CursorAcpPermissionReplyResult.Sent());
        };
        client.PushEvent(new CursorAcpStreamEvent.PermissionRequest
        {
            SessionId = SessionId,
            Method = CursorAcpClient.RequestPermissionMethod,
            RequestId = "perm-req-1",
            Description = "Read file README.md from workspace."
        });

        var supervisor = CreateSupervisor(client);
        var approvalReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        supervisor.TurnStateChanged += (_, args) =>
        {
            if (args.State == CursorAcpTurnState.WaitingApproval)
            {
                approvalReached.TrySetResult();
            }
        };

        var lockToken = new FakeCheckoutLockToken();
        var turnTask = supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            lockToken,
            CreatePromptRequest());

        await approvalReached.Task.WaitAsync(BoundedWait);

        Assert.True(supervisor.TryGetTurnState(SessionId, out var state));
        Assert.Equal(CursorAcpTurnState.WaitingApproval, state);

        var reply = await supervisor.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
        {
            PermissionId = "perm-req-1",
            Decision = CursorAcpPermissionDecision.AllowOnce,
            SessionId = SessionId
        });

        Assert.True(reply.IsSent);

        approvalReplied.TrySetResult();

        var result = await turnTask.WaitAsync(BoundedWait);

        Assert.Equal(CursorAcpTurnOutcome.Succeeded, result.Outcome);
        Assert.Equal(
            new[]
            {
                CursorAcpTurnState.Starting,
                CursorAcpTurnState.SessionConfirmed,
                CursorAcpTurnState.Running,
                CursorAcpTurnState.WaitingApproval,
                CursorAcpTurnState.Running,
                CursorAcpTurnState.Succeeded
            },
            result.StatesVisited);

        var recordedReply = Assert.Single(client.PermissionReplies);
        Assert.Equal(CursorAcpPermissionDecision.AllowOnce, recordedReply.Decision);
        Assert.Equal(1, lockToken.ReleaseCount);
    }

    [Fact]
    public async Task ExecuteTurnAsync_CancellationConfirmed_CancelsAndReleasesLock()
    {
        var client = new FakeCursorAcpClient();
        var prompt = new TaskCompletionSource<CursorAcpPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.PromptHandler = (_, _) => prompt.Task;
        client.CancelHandler = (_, _) =>
        {
            prompt.TrySetResult(CursorAcpPromptResult.Completed(CursorAcpStopReasons.Cancelled));
            return Task.FromResult(CursorAcpCancelResult.Accepted());
        };

        var supervisor = CreateSupervisor(client);
        var lockToken = new FakeCheckoutLockToken();

        using var cancellation = new CancellationTokenSource();
        var turnTask = supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            lockToken,
            CreatePromptRequest(),
            cancellation.Token);

        await client.PromptDispatched.Task.WaitAsync(BoundedWait);

        cancellation.Cancel();

        var result = await turnTask.WaitAsync(BoundedWait);

        Assert.Equal(CursorAcpTurnOutcome.Cancelled, result.Outcome);
        Assert.True(result.LockReleased);
        Assert.Equal(1, lockToken.ReleaseCount);
        Assert.False(result.ConsumesModelRetryBudget);

        var cancelRequest = Assert.Single(client.CancelRequests);
        Assert.Equal(SessionId, cancelRequest.SessionId);

        Assert.Equal(
            new[]
            {
                CursorAcpTurnState.Starting,
                CursorAcpTurnState.SessionConfirmed,
                CursorAcpTurnState.Running,
                CursorAcpTurnState.Cancelling,
                CursorAcpTurnState.Cancelled
            },
            result.StatesVisited);
    }

    [Theory]
    [InlineData(true, "cancelled")]
    [InlineData(true, "canceled")]
    [InlineData(false, "cancelled")]
    [InlineData(false, "canceled")]
    public async Task ExecuteTurnAsync_UncorrelatedCancellationStatus_RetainsWriterUntilPromptTerminates(
        bool beforeCancellation, string phase)
    {
        var client = new FakeCursorAcpClient();
        var prompt = new TaskCompletionSource<CursorAcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PromptHandler = (_, _) => prompt.Task;
        void PublishPriorTurnStatus()
        {
            client.PushEvent(new CursorAcpStreamEvent.StatusUpdate
            {
                SessionId = SessionId, Method = CursorAcpClient.SessionUpdateMethod, Phase = phase
            });
            client.CompleteEvents();
        }
        // Both an already-buffered status and one arriving after the new cancel send lack
        // evidence that the current prompt (and its writer) has stopped.
        if (beforeCancellation) PublishPriorTurnStatus();
        client.CancelHandler = async (_, _) =>
        {
            if (!beforeCancellation)
            {
                PublishPriorTurnStatus();
                await client.EventsCompleted.Task.WaitAsync(BoundedWait);
            }
            return CursorAcpCancelResult.Accepted();
        };
        var supervisor = CreateSupervisor(client, options => options.CancellationTimeout = TimeSpan.FromMilliseconds(500));
        var writer = new FakeCheckoutLockToken();
        using var cancellation = new CancellationTokenSource();
        var turn = supervisor.ExecuteTurnAsync(CreateAgentModeDecision(), writer, CreatePromptRequest(), cancellation.Token);
        try
        {
            await client.PromptDispatched.Task.WaitAsync(BoundedWait);
            if (beforeCancellation) await client.EventsCompleted.Task.WaitAsync(BoundedWait);
            cancellation.Cancel();
            var result = await turn.WaitAsync(BoundedWait);

            Assert.False(prompt.Task.IsCompleted);
            Assert.Equal(CursorAcpTurnOutcome.Ambiguous, result.Outcome);
            Assert.Equal(CursorAcpTurnFailureKind.CancellationUnconfirmed, result.FailureKind);
            Assert.True(result.LockRetained);
            Assert.False(result.LockReleased);
            Assert.True(writer.IsHeld);
            Assert.Equal(0, writer.ReleaseCount);
            Assert.Single(client.CancelRequests);
        }
        finally
        {
            prompt.TrySetResult(CursorAcpPromptResult.Completed(CursorAcpStopReasons.Cancelled));
            cancellation.Cancel();
            await turn.WaitAsync(BoundedWait);
        }
    }

    [Fact]
    public async Task ExecuteTurnAsync_CancellationWithoutTerminalEvidence_IsAmbiguousAndRetainsLock()
    {
        var client = new FakeCursorAcpClient();
        var neverCompletes = new TaskCompletionSource<CursorAcpPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.PromptHandler = (_, _) => neverCompletes.Task;
        client.CancelHandler = (_, _) => Task.FromResult(CursorAcpCancelResult.Accepted());

        var supervisor = CreateSupervisor(
            client,
            options => options.CancellationTimeout = TimeSpan.FromMilliseconds(300));

        var lockToken = new FakeCheckoutLockToken();

        using var cancellation = new CancellationTokenSource();
        var turnTask = supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            lockToken,
            CreatePromptRequest(),
            cancellation.Token);

        await client.PromptDispatched.Task.WaitAsync(BoundedWait);

        cancellation.Cancel();

        var result = await turnTask.WaitAsync(BoundedWait);

        Assert.Equal(CursorAcpTurnOutcome.Ambiguous, result.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.CancellationUnconfirmed, result.FailureKind);
        Assert.False(result.LockReleased);
        Assert.True(result.LockRetained);
        Assert.Equal(0, lockToken.ReleaseCount);
        Assert.True(lockToken.IsHeld);
        Assert.Equal(CursorAcpTurnState.Ambiguous, result.StatesVisited[^1]);
    }

    [Fact]
    public async Task ExecuteTurnAsync_TransportLostAfterDelivery_IsAmbiguousAndRetainsLock()
    {
        var client = new FakeCursorAcpClient();

        client.PushEvent(new CursorAcpStreamEvent.TextChunk
        {
            SessionId = SessionId,
            Method = CursorAcpClient.SessionUpdateMethod,
            Text = "I am inspecting the workspace."
        });
        client.CompleteEvents();
        client.PromptHandler = async (_, _) =>
        {
            await client.EventDelivered.Task;
            await Task.Delay(100);

            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.TransportFailure,
                "The agent standard output reached EOF.");
        };

        var supervisor = CreateSupervisor(client);
        var lockToken = new FakeCheckoutLockToken();

        var result = await supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            lockToken,
            CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Ambiguous, result.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.TransportLost, result.FailureKind);
        Assert.False(result.LockReleased);
        Assert.True(result.LockRetained);
        Assert.Equal(0, lockToken.ReleaseCount);
        Assert.True(lockToken.IsHeld);
    }

    [Fact]
    public async Task ExecuteTurnAsync_ProcessLostBeforeDelivery_IsOrphanedAndRetainsLock()
    {
        var client = new FakeCursorAcpClient();
        client.CompleteEvents();
        client.PromptHandler = async (_, _) =>
        {
            await client.EventsCompleted.Task;
            await Task.Delay(200);

            return CursorAcpPromptResult.Degraded(
                CursorAcpPromptFailureKind.TransportFailure,
                "The agent standard output reached EOF.");
        };

        var supervisor = CreateSupervisor(client);
        var lockToken = new FakeCheckoutLockToken();

        var result = await supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            lockToken,
            CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Orphaned, result.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.ProcessLost, result.FailureKind);
        Assert.False(result.LockReleased);
        Assert.True(result.LockRetained);
        Assert.Equal(0, lockToken.ReleaseCount);
    }

    [Fact]
    public async Task ExecuteTurnAsync_PromptRejectedBeforeDelivery_IsFailedAndReleasesLock()
    {
        var client = new FakeCursorAcpClient();
        client.PromptHandler = (_, _) => Task.FromResult(CursorAcpPromptResult.Degraded(
            CursorAcpPromptFailureKind.NotReady,
            "The handshake is not ready."));

        var supervisor = CreateSupervisor(client);
        var lockToken = new FakeCheckoutLockToken();

        var result = await supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            lockToken,
            CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.Failed, result.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.PromptRejected, result.FailureKind);
        Assert.True(result.LockReleased);
        Assert.Equal(1, lockToken.ReleaseCount);
        Assert.True(result.ConsumesModelRetryBudget);
    }

    [Fact]
    public async Task ExecuteTurnAsync_HardTimeoutWithDelivery_RetainsLockUntilNativeStopIsConfirmed()
    {
        var client = new FakeCursorAcpClient();
        var neverCompletes = new TaskCompletionSource<CursorAcpPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.PromptHandler = async (_, _) =>
        {
            client.PushEvent(new CursorAcpStreamEvent.StatusUpdate
            {
                SessionId = SessionId,
                Method = CursorAcpClient.SessionUpdateMethod,
                Phase = "generating"
            });

            return await neverCompletes.Task;
        };

        var supervisor = CreateSupervisor(
            client,
            options =>
            {
                options.TurnHardTimeout = TimeSpan.FromMilliseconds(300);
                options.CancellationTimeout = TimeSpan.FromSeconds(5);
            });

        var lockToken = new FakeCheckoutLockToken();

        var result = await supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            lockToken,
            CreatePromptRequest());

        Assert.Equal(CursorAcpTurnOutcome.TimedOut, result.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.TurnTimeout, result.FailureKind);
        Assert.False(result.LockReleased);
        Assert.Equal(0, lockToken.ReleaseCount);
        Assert.True(result.ConsumesModelRetryBudget);
    }

    [Fact]
    public async Task ExecuteTurnAsync_CallerCancelledBeforeDispatch_IsCancelledWithoutPrompt()
    {
        var client = new FakeCursorAcpClient();
        var supervisor = CreateSupervisor(client);
        var lockToken = new FakeCheckoutLockToken();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await supervisor.ExecuteTurnAsync(
            CreateAgentModeDecision(),
            lockToken,
            CreatePromptRequest(),
            cancellation.Token);

        Assert.Equal(CursorAcpTurnOutcome.Cancelled, result.Outcome);
        Assert.Equal(CursorAcpTurnFailureKind.TurnCancelledBeforeStart, result.FailureKind);
        Assert.True(result.LockReleased);
        Assert.Equal(1, lockToken.ReleaseCount);
        Assert.Empty(client.PromptRequests);
        Assert.Empty(client.CancelRequests);
    }

    private static CursorAcpTurnSupervisor CreateSupervisor(
        FakeCursorAcpClient client,
        Action<CursorAcpOptions>? configure = null)
    {
        var options = new CursorAcpOptions
        {
            TurnHardTimeout = TimeSpan.FromSeconds(30),
            CancellationTimeout = TimeSpan.FromSeconds(30)
        };

        configure?.Invoke(options);

        return new CursorAcpTurnSupervisor(client, new CursorAcpModePolicy(), options);
    }

    private static CursorAcpModeDecision CreateAgentModeDecision() => new()
    {
        Mode = CursorAcpMode.Agent,
        ModeId = "agent",
        Access = CursorAcpModeAccess.Write,
        State = CapabilityState.Supported,
        CanSend = true,
        RequiresWriterLock = true
    };

    private static CursorAcpPromptRequest CreatePromptRequest() =>
        CursorAcpPromptRequest.Create(SessionId, "Inspect the repository structure.");
}
