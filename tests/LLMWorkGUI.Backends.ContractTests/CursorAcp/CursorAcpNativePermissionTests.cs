using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpNativePermissionTests
{
    [Fact]
    public async Task MalformedDuplicateCannotCompeteWithConsumedReceiptDuringReplyWrite()
    {
        var (client, transport) = await Ready();
        var permission = await Publish(client, transport, "19");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        transport.ResponseHandler = async _ =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        var pending = client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
            { PermissionId = permission.RequestId, SessionId = "session", Decision = CursorAcpPermissionDecision.Deny });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            transport.PublishNotification(new JsonRpcNotification
            {
                Method = "session/request_permission", Id = JsonSerializer.SerializeToElement(19),
                Parameters = JsonSerializer.SerializeToElement(new { sessionId = "session", toolCall = new { }, options = Array.Empty<object>() })
            });
            transport.CompleteNotifications();
            await foreach (var unexpected in client.SubscribeEventsAsync()) Assert.Fail("Malformed duplicate reached approval UI.");
            Assert.Single(transport.Responses);
            Assert.Equal(1, writes);
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task MalformedDuplicateDoesNotCompeteWithCapturedPermissionReceipt()
    {
        var (client, transport) = await Ready();
        var permission = await Publish(client, transport, "17");
        transport.PublishNotification(new JsonRpcNotification
        {
            Method = "session/request_permission", Id = JsonSerializer.SerializeToElement(17),
            Parameters = JsonSerializer.SerializeToElement(new { sessionId = "session", toolCall = new { }, options = Array.Empty<object>() })
        });
        transport.CompleteNotifications();
        await foreach (var unexpected in client.SubscribeEventsAsync()) Assert.Fail("Malformed duplicate reached approval UI.");
        Assert.Empty(transport.Responses);
        Assert.True((await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
        {
            PermissionId = permission.RequestId, SessionId = permission.SessionId,
            Decision = CursorAcpPermissionDecision.Deny
        })).IsSent);
        var response = Assert.Single(transport.Responses);
        Assert.Null(response.Error);
        Assert.Equal("17", response.Id.GetRawText());
        Assert.Equal("no-once", response.Result!.Value.GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task Supervisor_KeepsWaitingOnFailedReplyAndRemainingQueuedApproval()
    {
        var finish = new TaskCompletionSource<CursorAcpPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeCursorAcpClient { PromptHandler = (_, _) => finish.Task };
        var policy = new LLMWorkGUI.Infrastructure.CursorAcp.CursorAcpModePolicy();
        var supervisor = new CursorAcpTurnSupervisor(client, policy);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        supervisor.TurnStateChanged += (_, e) => { if (e.State == CursorAcpTurnState.WaitingApproval && Interlocked.Increment(ref count) == 2) both.TrySetResult(); };
        var turn = supervisor.ExecuteTurnAsync(policy.Evaluate(CursorAcpMode.Ask, CursorAcpModeAccess.ReadOnly, LLMWorkGUI.Domain.Enums.CapabilityState.Supported), null, CursorAcpPromptRequest.Create("session", "Hello"));
        foreach (var id in new[] { "one", "two" }) client.PushEvent(new CursorAcpStreamEvent.PermissionRequest { Method = "session/request_permission", SessionId = "session", RequestId = id, Description = id });
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reply = new CursorAcpPermissionReplyRequest { PermissionId = "one", SessionId = "session", Decision = CursorAcpPermissionDecision.Deny };
        client.PermissionReplyHandler = (_, _) => Task.FromResult(CursorAcpPermissionReplyResult.Degraded(CursorAcpPermissionReplyFailureKind.TransportFailure, "failed"));
        Assert.False((await supervisor.ReplyPermissionAsync(reply)).IsSent);
        Assert.True(supervisor.TryGetTurnState("session", out var state));
        Assert.Equal(CursorAcpTurnState.WaitingApproval, state);
        client.PermissionReplyHandler = (_, _) => Task.FromResult(CursorAcpPermissionReplyResult.Sent());
        await supervisor.ReplyPermissionAsync(reply);
        Assert.True(supervisor.TryGetTurnState("session", out state));
        Assert.Equal(CursorAcpTurnState.WaitingApproval, state);
        await supervisor.ReplyPermissionAsync(reply with { PermissionId = "two" });
        Assert.True(supervisor.TryGetTurnState("session", out state));
        Assert.Equal(CursorAcpTurnState.Running, state);
        finish.SetResult(CursorAcpPromptResult.Completed("end_turn"));
        Assert.Equal(CursorAcpTurnOutcome.Succeeded, (await turn.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
    }

    [Theory]
    [InlineData("7", true, "yes-once")]
    [InlineData("\"7\"", false, "no-once")]
    public async Task Reply_EchoesExactTypedIdAndObservedOneShotOption(string id, bool allow, string expected)
    {
        var (client, transport) = await Ready();
        var permission = await Publish(client, transport, id);
        Assert.True(permission.CanAllowOnce);
        Assert.Equal("Read fixture", permission.Description);
        Assert.Equal(LLMWorkGUI.Domain.Enums.NormalizedApprovalKind.UnknownHighRisk, permission.ApprovalKind);
        Assert.Empty(transport.Responses);
        var request = new CursorAcpPermissionReplyRequest { PermissionId = permission.RequestId, SessionId = "session", Decision = allow ? CursorAcpPermissionDecision.AllowOnce : CursorAcpPermissionDecision.Deny };
        Assert.True((await client.ReplyPermissionAsync(request)).IsSent);
        var reply = Assert.Single(transport.Responses);
        Assert.Equal(id, reply.Id.GetRawText());
        Assert.Equal("selected", reply.Result!.Value.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal(expected, reply.Result.Value.GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.False((await client.ReplyPermissionAsync(request)).IsSent);
        Assert.Single(transport.Responses);
    }

    [Fact]
    public async Task PersistentOnly_CannotGrantOrRememberDenial()
    {
        var (client, transport) = await Ready();
        var permission = await Publish(client, transport, "9", "allow_always", "reject_always");
        Assert.False(permission.CanAllowOnce);
        var request = new CursorAcpPermissionReplyRequest { PermissionId = permission.RequestId, SessionId = "session", Decision = CursorAcpPermissionDecision.AllowOnce };
        Assert.False((await client.ReplyPermissionAsync(request)).IsSent);
        Assert.Empty(transport.Responses);
        Assert.True((await client.ReplyPermissionAsync(request with { Decision = CursorAcpPermissionDecision.Deny })).IsSent);
        Assert.Equal("cancelled", Assert.Single(transport.Responses).Result!.Value.GetProperty("outcome").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task WrongSession_DoesNotConsumeOrWritePermission()
    {
        var (client, transport) = await Ready();
        var permission = await Publish(client, transport, "11");
        var request = new CursorAcpPermissionReplyRequest { PermissionId = permission.RequestId, SessionId = "other", Decision = CursorAcpPermissionDecision.AllowOnce };
        Assert.False((await client.ReplyPermissionAsync(request)).IsSent);
        Assert.Empty(transport.Responses);
        Assert.True((await client.ReplyPermissionAsync(request with { SessionId = "session" })).IsSent);
    }

    [Fact]
    public async Task Cancel_ResolvesPendingAndLateRequestsWithoutGrant()
    {
        var (client, transport) = await Ready();
        var permission = await Publish(client, transport, "12");
        Assert.True((await client.CancelSessionAsync(new CursorAcpCancelRequest { SessionId = "session" })).IsSent);
        Assert.False((await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest { PermissionId = permission.RequestId, SessionId = "session", Decision = CursorAcpPermissionDecision.AllowOnce })).IsSent);
        transport.PublishNotification(Notification("13"));
        transport.CompleteNotifications();
        await foreach (var unexpected in client.SubscribeEventsAsync()) Assert.Fail("Late request must not reach approval UI");
        Assert.Equal(2, transport.Responses.Count);
        Assert.All(transport.Responses, r => Assert.Equal("cancelled", r.Result!.Value.GetProperty("outcome").GetProperty("outcome").GetString()));
    }

    [Theory]
    [InlineData("{\"sessionId\":\"session\",\"toolCall\":{},\"options\":[]}")]
    [InlineData("{\"sessionId\":\"session\",\"toolCall\":{\"toolCallId\":\"t\"},\"options\":null}")]
    [InlineData("{\"sessionId\":\"session\",\"toolCall\":{\"toolCallId\":\"t\"},\"options\":[{\"optionId\":\"x\",\"kind\":\"allow_once\",\"kind\":\"allow_always\",\"name\":\"X\"}]}")]
    public async Task MalformedNativeRequest_NeverFallsBackToLegacy(string payload)
    {
        var (client, transport) = await Ready();
        transport.PublishNotification(new JsonRpcNotification { Method = "session/request_permission", Id = JsonSerializer.SerializeToElement(1), Parameters = JsonDocument.Parse(payload).RootElement.Clone() });
        transport.CompleteNotifications();
        await foreach (var unexpected in client.SubscribeEventsAsync()) Assert.Fail("Malformed permission was emitted");
        // Malformed requests may receive an exact-envelope protocol error, never a grant or a
        // legacy decision. Typed error liveness is checked in CursorAcpMalformedPermissionReplyTests.
        Assert.All(transport.Responses, response => Assert.Null(response.Result));
        Assert.Equal(1, client.SkippedStreamEventCount);
    }

    private static async Task<(CursorAcpClient, FakeJsonRpcTransport)> Ready()
    {
        var transport = FakeJsonRpcTransport.RespondingWith(CursorAcpTestData.ReadHandshakeResult());
        var client = new CursorAcpClient(transport);
        Assert.True((await client.InitializeAsync()).IsReady);
        return (client, transport);
    }

    private static JsonRpcNotification Notification(string id, string allowKind = "allow_once", string rejectKind = "reject_once") => new()
    {
        Method = "session/request_permission", Id = JsonDocument.Parse(id).RootElement.Clone(),
        Parameters = JsonSerializer.SerializeToElement(new { sessionId = "session", toolCall = new { toolCallId = "tool", title = "Read fixture" }, options = new[] { new { optionId = "yes-once", name = "Allow", kind = allowKind }, new { optionId = "no-once", name = "Reject", kind = rejectKind } } })
    };

    private static async Task<CursorAcpStreamEvent.PermissionRequest> Publish(CursorAcpClient client, FakeJsonRpcTransport transport, string id, string allow = "allow_once", string deny = "reject_once")
    {
        transport.PublishNotification(Notification(id, allow, deny));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = client.SubscribeEventsAsync(cancellationToken: timeout.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        return Assert.IsType<CursorAcpStreamEvent.PermissionRequest>(events.Current);
    }
}
