using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpPermissionOwnershipDeltaTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(CursorAcpPermissionDecision.AllowOnce)]
    [InlineData(CursorAcpPermissionDecision.Deny)]
    public async Task PreCancelledReplyPreservesCapturedReceiptBeforeActualTransportWrite(CursorAcpPermissionDecision decision)
    {
        await using var harness = new LoopbackJsonRpcHarness
        {
            FrameHandler = frame =>
            {
                using var document = JsonDocument.Parse(frame);
                return document.RootElement.TryGetProperty("method", out var method) && method.GetString() == "initialize"
                    ? CursorAcpTestData.CreateResponseJson(document.RootElement.GetProperty("id").GetInt32(),
                        CursorAcpTestData.ReadHandshakeResult()) : null;
            }
        };
        await using var transport = new JsonRpcStdioTransport(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(transport);
        Assert.True((await client.InitializeAsync()).IsReady);
        await harness.ReadFrameAsync(Wait); // The actual initialize write is already observed.
        var notification = Notification(19);
        await harness.WriteRawLineAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = notification.Id, method = notification.Method, @params = notification.Parameters }));
        using var deadline = new CancellationTokenSource(Wait);
        await using var events = client.SubscribeEventsAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        var permission = Assert.IsType<CursorAcpStreamEvent.PermissionRequest>(events.Current);
        var request = new CursorAcpPermissionReplyRequest
            { PermissionId = permission.RequestId, SessionId = "session", Decision = decision };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReplyPermissionAsync(request, cancelled.Token));
        var retry = await client.ReplyPermissionAsync(request);

        Assert.True(retry.IsSent, "Known pre-send cancellation must leave the captured native receipt usable.");
        using var response = JsonDocument.Parse(await harness.ReadFrameAsync(Wait));
        Assert.Equal(19, response.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(decision == CursorAcpPermissionDecision.AllowOnce ? "yes" : "no",
            response.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.False((await client.ReplyPermissionAsync(request)).IsSent);
    }

    [Fact]
    public async Task UncertainFirstCleanupRetiresAllOldReceiptsBeforeAnotherPromptCanGrant()
    {
        var firstFinished = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFinished = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptCount = 0;
        var transport = new FakeJsonRpcTransport((method, _, _, _) =>
        {
            if (method == CursorAcpClient.InitializeMethod)
                return Task.FromResult(FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult()));
            if (method == CursorAcpClient.PromptMethod)
            {
                if (Interlocked.Increment(ref promptCount) == 1) return firstFinished.Task;
                secondEntered.TrySetResult();
                return secondFinished.Task;
            }
            return Task.FromResult(FakeJsonRpcTransport.CreateResultResponse(JsonSerializer.SerializeToElement(new { })));
        });
        var client = new CursorAcpClient(transport);
        Assert.True((await client.InitializeAsync()).IsReady);
        var first = client.PromptAsync(CursorAcpPromptRequest.Create("session", "Synthetic first turn"));
        var old = await Capture(client, transport, 31);
        var other = await Capture(client, transport, 32);
        transport.ResponseHandler = _ => Task.FromException(new IOException("Synthetic uncertain response write"));
        firstFinished.TrySetResult(FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadPromptResult()));
        await first.WaitAsync(Wait);
        Assert.Single(transport.Responses); // Only the first response attempt reached the failing transport.
        transport.ResponseHandler = null;
        var second = client.PromptAsync(CursorAcpPromptRequest.Create("session", "Synthetic next turn"));
        try
        {
            await secondEntered.Task.WaitAsync(Wait);
            var stale = await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
                { PermissionId = other.RequestId, SessionId = "session", Decision = CursorAcpPermissionDecision.AllowOnce });
            Assert.False(stale.IsSent, "A surviving previous-turn receipt must never become grantable in the next turn.");
            Assert.Single(transport.Responses);
            Assert.False((await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
                { PermissionId = old.RequestId, SessionId = "session", Decision = CursorAcpPermissionDecision.AllowOnce })).IsSent);

            // The failed first response still owns its exact wire ID: a malformed duplicate
            // must not acquire a competing error-response claim during the next turn.
            transport.PublishNotification(new JsonRpcNotification
            {
                Method = "session/request_permission", Id = JsonSerializer.SerializeToElement(31),
                Parameters = JsonSerializer.SerializeToElement(new { sessionId = "session", toolCall = new { }, options = Array.Empty<object>() })
            });
            var fresh = await Capture(client, transport, 33);
            Assert.Single(transport.Responses);
            Assert.True((await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
                { PermissionId = fresh.RequestId, SessionId = "session", Decision = CursorAcpPermissionDecision.AllowOnce })).IsSent);
            Assert.Equal("33", transport.Responses[^1].Id.GetRawText());
            Assert.Equal("yes", transport.Responses[^1].Result!.Value.GetProperty("outcome").GetProperty("optionId").GetString());
        }
        finally
        {
            firstFinished.TrySetResult(FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadPromptResult()));
            secondFinished.TrySetResult(FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadPromptResult()));
            await first.WaitAsync(Wait);
            await second.WaitAsync(Wait);
        }
    }

    private static async Task<CursorAcpStreamEvent.PermissionRequest> Capture(CursorAcpClient client, FakeJsonRpcTransport transport, int id)
    {
        transport.PublishNotification(Notification(id));
        using var deadline = new CancellationTokenSource(Wait);
        await using var events = client.SubscribeEventsAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        return Assert.IsType<CursorAcpStreamEvent.PermissionRequest>(events.Current);
    }

    private static JsonRpcNotification Notification(int id) => new()
    {
        Method = "session/request_permission", Id = JsonSerializer.SerializeToElement(id),
        Parameters = JsonSerializer.SerializeToElement(new
        {
            sessionId = "session", toolCall = new { toolCallId = "owned-tool", title = "Synthetic permission" },
            options = new[] { new { optionId = "yes", name = "Allow once", kind = "allow_once" },
                new { optionId = "no", name = "Reject once", kind = "reject_once" } }
        })
    };
}
