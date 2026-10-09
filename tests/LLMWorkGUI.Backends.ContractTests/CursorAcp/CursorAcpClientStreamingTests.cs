using System.Text.Json;
using System.Text.Json.Nodes;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpClientStreamingTests
{
    private const string SessionId = "sess-test-42";
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task SubscribeEventsAsync_NormalizesAllFixtureUpdateTypes()
    {
        var transport = await CreateReadyTransportAsync();
        var client = CreateClient(transport);

        for (var index = 0; index < CursorAcpTestData.ReadStreamingUpdateLines().Count; index++)
        {
            transport.PublishNotification(CursorAcpTestData.ReadStreamingNotification(index));
        }

        var events = await CollectAsync(client, SessionId, expectedCount: 4);

        Assert.Collection(
            events,
            streamEvent =>
            {
                var text = Assert.IsType<CursorAcpStreamEvent.TextChunk>(streamEvent);
                Assert.Equal("I am inspecting the workspace.", text.Text);
                Assert.Equal(SessionId, text.SessionId);
                Assert.Equal("session/update", text.Method);
            },
            streamEvent =>
            {
                var thought = Assert.IsType<CursorAcpStreamEvent.Thought>(streamEvent);
                Assert.Equal("Checking directory contents first.", thought.Text);
            },
            streamEvent =>
            {
                var toolCall = Assert.IsType<CursorAcpStreamEvent.ToolCall>(streamEvent);
                Assert.Equal("call-101", toolCall.CallId);
                Assert.Equal("readFile", toolCall.ToolName);
                Assert.Equal("README.md", toolCall.Arguments!.Value.GetProperty("path").GetString());
            },
            streamEvent =>
            {
                var status = Assert.IsType<CursorAcpStreamEvent.StatusUpdate>(streamEvent);
                Assert.Equal("generating", status.Phase);
            });

        Assert.Equal(0, client.SkippedStreamEventCount);
    }

    [Fact]
    public async Task SubscribeEventsAsync_PermissionRequest_IsFailClosedUnknownHighRisk()
    {
        var transport = await CreateReadyTransportAsync();
        var client = CreateClient(transport);

        var request = CursorAcpTestData.ReadPermissionRequest();
        transport.PublishNotification(new JsonRpcNotification
        {
            Method = "session/request_permission",
            Parameters = request.GetProperty("params").Clone(),
            Id = request.GetProperty("id").Clone()
        });

        var events = await CollectAsync(client, SessionId, expectedCount: 1);

        var permission = Assert.IsType<CursorAcpStreamEvent.PermissionRequest>(Assert.Single(events));
        Assert.StartsWith("acp-v1:", permission.RequestId, StringComparison.Ordinal);
        Assert.NotEqual("perm-req-1", permission.RequestId);
        Assert.Equal("Read file README.md from workspace.", permission.Description);
        Assert.Equal(NormalizedApprovalKind.UnknownHighRisk, permission.ApprovalKind);
        Assert.Equal(SessionId, permission.SessionId);
        Assert.NotNull(permission.RawPayload);
        Assert.Equal("session/request_permission", permission.Method);
    }

    [Fact]
    public async Task SubscribeEventsAsync_UnknownMethodsAndMalformedBlocks_AreCountedAndSkipped()
    {
        var transport = await CreateReadyTransportAsync();
        var client = CreateClient(transport);

        transport.PublishNotification(new JsonRpcNotification { Method = "session/unknown" });
        transport.PublishNotification(new JsonRpcNotification
        {
            Method = "session/update",
            Parameters = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["sessionId"] = SessionId,
                ["update"] = new JsonObject { ["type"] = "text" }
            })
        });
        transport.PublishNotification(new JsonRpcNotification
        {
            Method = "session/update",
            Parameters = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["sessionId"] = SessionId,
                ["update"] = new JsonObject { ["type"] = "not-a-real-update" }
            })
        });
        transport.PublishNotification(new JsonRpcNotification
        {
            Method = "session/request_permission",
            Parameters = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["sessionId"] = SessionId,
                ["permissionId"] = "perm-1"
            })
        });
        transport.PublishNotification(CursorAcpTestData.CreateSessionUpdateNotification(
            SessionId,
            "text",
            "still alive"));

        var events = await CollectAsync(client, SessionId, expectedCount: 1);

        var text = Assert.IsType<CursorAcpStreamEvent.TextChunk>(Assert.Single(events));
        Assert.Equal("still alive", text.Text);
        Assert.Equal(4, client.SkippedStreamEventCount);
    }

    [Fact]
    public async Task SubscribeEventsAsync_FiltersForeignSessions()
    {
        var transport = await CreateReadyTransportAsync();
        var client = CreateClient(transport);

        transport.PublishNotification(CursorAcpTestData.CreateSessionUpdateNotification(
            "sess-other",
            "text",
            "foreign"));
        transport.PublishNotification(CursorAcpTestData.CreateSessionUpdateNotification(
            SessionId,
            "text",
            "mine"));

        var events = await CollectAsync(client, SessionId, expectedCount: 1);

        var text = Assert.IsType<CursorAcpStreamEvent.TextChunk>(Assert.Single(events));
        Assert.Equal("mine", text.Text);
        Assert.Equal(0, client.SkippedStreamEventCount);
    }

    [Fact]
    public async Task SubscribeEventsAsync_WithoutFilter_DeliversEverySession()
    {
        var transport = await CreateReadyTransportAsync();
        var client = CreateClient(transport);

        transport.PublishNotification(CursorAcpTestData.CreateSessionUpdateNotification("sess-a", "text", "a"));
        transport.PublishNotification(CursorAcpTestData.CreateSessionUpdateNotification("sess-b", "text", "b"));

        var events = await CollectAsync(client, sessionId: null, expectedCount: 2);

        Assert.Equal(2, events.Count);
        Assert.Equal(0, client.SkippedStreamEventCount);
    }

    [Fact]
    public async Task SubscribeEventsAsync_TransportCompletion_EndsTheStreamGracefully()
    {
        var transport = await CreateReadyTransportAsync();
        var client = CreateClient(transport);

        transport.PublishNotification(CursorAcpTestData.CreateSessionUpdateNotification(SessionId, "text", "only"));
        transport.CompleteNotifications();

        var events = new List<CursorAcpStreamEvent>();

        using var cancellation = new CancellationTokenSource(BoundedWait);

        await foreach (var streamEvent in client.SubscribeEventsAsync(SessionId, cancellation.Token))
        {
            events.Add(streamEvent);
        }

        var text = Assert.IsType<CursorAcpStreamEvent.TextChunk>(Assert.Single(events));
        Assert.Equal("only", text.Text);
    }

    private static CursorAcpClient CreateClient(FakeJsonRpcTransport transport) =>
        new(transport, new CursorAcpOptions { RequestTimeout = TimeSpan.FromSeconds(17) });

    private static async Task<FakeJsonRpcTransport> CreateReadyTransportAsync()
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : throw new InvalidOperationException($"Unexpected ACP method '{method}'."));

        var client = CreateClient(transport);
        var handshake = await client.InitializeAsync();
        Assert.True(handshake.IsReady);

        return transport;
    }

    private static async Task<List<CursorAcpStreamEvent>> CollectAsync(
        CursorAcpClient client,
        string? sessionId,
        int expectedCount)
    {
        var events = new List<CursorAcpStreamEvent>();

        using var cancellation = new CancellationTokenSource(BoundedWait);

        await foreach (var streamEvent in client.SubscribeEventsAsync(sessionId, cancellation.Token))
        {
            events.Add(streamEvent);

            if (events.Count >= expectedCount)
            {
                break;
            }
        }

        return events;
    }
}
