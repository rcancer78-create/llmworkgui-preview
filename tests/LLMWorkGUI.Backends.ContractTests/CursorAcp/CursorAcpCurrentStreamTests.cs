using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpCurrentStreamTests
{
    [Theory]
    [InlineData("agent_message_chunk", false)]
    [InlineData("agent_thought_chunk", true)]
    public async Task NativeContentBlock_PreservesTextAndItsKind(string kind, bool thought)
    {
        var transport = FakeJsonRpcTransport.Unused();
        var client = new CursorAcpClient(transport);
        transport.PublishNotification(Notification("s", new { sessionUpdate = kind, content = new { type = "text", text = "fixture text" } }));
        transport.CompleteNotifications();
        var events = new List<CursorAcpStreamEvent>();
        await foreach (var ev in client.SubscribeEventsAsync("s")) events.Add(ev);
        var actual = Assert.Single(events);
        Assert.Equal("fixture text", thought ? Assert.IsType<CursorAcpStreamEvent.Thought>(actual).Text : Assert.IsType<CursorAcpStreamEvent.TextChunk>(actual).Text);
        Assert.Equal("s", actual.SessionId);
        Assert.Equal(0, client.SkippedStreamEventCount);
    }

    [Theory]
    [InlineData("{\"sessionUpdate\":\"agent_message_chunk\",\"content\":null}")]
    [InlineData("{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"image\",\"text\":\"not text\"}}")]
    [InlineData("{\"sessionUpdate\":\"agent_thought_chunk\",\"type\":\"text\",\"text\":\"conflict\"}")]
    [InlineData("{\"sessionUpdate\":42,\"type\":\"text\",\"text\":\"conflict\"}")]
    [InlineData("{\"sessionUpdate\":\"\",\"type\":\"text\",\"text\":\"conflict\"}")]
    public async Task MalformedOrConflictingNativeText_IsNeverPublicOutput(string payload)
    {
        var transport = FakeJsonRpcTransport.Unused();
        var client = new CursorAcpClient(transport);
        transport.PublishNotification(Notification("s", JsonDocument.Parse(payload).RootElement));
        transport.CompleteNotifications();
        await foreach (var _ in client.SubscribeEventsAsync("s")) Assert.Fail("Invalid content was emitted.");
        Assert.Equal(1, client.SkippedStreamEventCount);
    }

    [Fact]
    public async Task ForeignSessionAndModeMetadata_AreNotDeliveryOrCancellation()
    {
        var transport = FakeJsonRpcTransport.Unused();
        var client = new CursorAcpClient(transport);
        transport.PublishNotification(Notification("foreign", new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "foreign" } }));
        transport.PublishNotification(Notification("s", new { sessionUpdate = "current_mode_update", currentModeId = "ask" }));
        transport.CompleteNotifications();
        await foreach (var _ in client.SubscribeEventsAsync("s")) Assert.Fail("Metadata/foreign event was emitted.");
        Assert.Equal(0, client.SkippedStreamEventCount);
    }

    [Fact]
    public async Task ActiveLifecycle_HasOneQueueReader_AndBroadcastsApprovalToUiAndSupervisor()
    {
        var client = CursorAcpSessionLifecycleStartTests.CreateReadyClient();
        var terminal = new TaskCompletionSource<CursorAcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PromptHandler = (_, token) => terminal.Task.WaitAsync(token);
        await using var service = CursorAcpSessionLifecycleStartTests.CreateService(
            FakeCursorAcpProcessManager.Started(new FakeCursorAcpProcessSession(FakeJsonRpcTransport.Unused())), client);
        Assert.True((await service.StartBackendAsync("stream-reader-regression")).IsReady);
        var approval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new System.Collections.Concurrent.ConcurrentQueue<CursorAcpTurnState>();
        service.PermissionRequested += (_, _) => approval.TrySetResult();
        service.TurnStateChanged += (_, e) => { states.Enqueue(e.State); if (e.State == CursorAcpTurnState.WaitingApproval) waiting.TrySetResult(); };
        var turn = service.ExecuteTurnAsync(new CursorAcpTurnRequest { Prompt = CursorAcpPromptRequest.Create("s", "fixture"), Mode = new CursorAcpModePolicy().Evaluate(CursorAcpMode.Ask, CursorAcpModeAccess.ReadOnly, CapabilityState.Supported) });
        await client.PromptDispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(1, client.SubscriptionCount);
            client.PushEvent(new CursorAcpStreamEvent.PermissionRequest { SessionId = "s", Method = "session/request_permission", RequestId = "p", Description = "Fixture permission" });
            await Task.WhenAll(approval.Task, waiting.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(client.PermissionReplies);
            await service.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest { SessionId = "s", PermissionId = "p", Decision = CursorAcpPermissionDecision.Deny });
            Assert.Single(client.PermissionReplies);
            Assert.Equal(CursorAcpTurnState.Running, states.Last());
        }
        finally { terminal.TrySetResult(CursorAcpPromptResult.Completed("end_turn")); }
        Assert.Equal(CursorAcpTurnOutcome.Succeeded, (await turn).Outcome);
    }

    private static JsonRpcNotification Notification(string sessionId, object update) => new()
    {
        Method = "session/update", Parameters = JsonSerializer.SerializeToElement(new { sessionId, update })
    };
}
