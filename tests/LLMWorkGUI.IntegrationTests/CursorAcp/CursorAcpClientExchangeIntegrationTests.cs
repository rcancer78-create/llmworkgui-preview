using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.CursorAcp;

public sealed class CursorAcpClientExchangeIntegrationTests
{
    private const string SessionId = "sess-test-42";
    private const string WorkingDirectory = "C:\\workspace\\demo-app";
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task CursorAcpClient_LoadPromptAndCancelOverStdioPipes_UseFixtureContracts()
    {
        await using var harness = new CursorAcpPipeHarness
        {
            FrameHandler = frame =>
            {
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;

                if (!root.TryGetProperty("method", out var methodElement))
                {
                    return null;
                }

                if (methodElement.GetString() == "session/cancel")
                {
                    Assert.False(root.TryGetProperty("id", out _));
                    return null;
                }
                var id = root.GetProperty("id").GetInt32();

                return methodElement.GetString() switch
                {
                    "initialize" => CursorAcpIntegrationTestData.CreateResponseJson(
                        id,
                        CursorAcpIntegrationTestData.ReadHandshakeResult()),
                    "session/load" => CursorAcpIntegrationTestData.CreateResponseJson(
                        id,
                        CursorAcpIntegrationTestData.ReadSessionLoadResult()),
                    "session/prompt" => CursorAcpIntegrationTestData.CreateResponseJson(
                        id,
                        CursorAcpIntegrationTestData.ReadPromptResult()),
                    _ => null
                };
            }
        };

        var factory = new JsonRpcStdioTransportFactory();
        await using var transport = factory.Create(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(
            transport,
            new CursorAcpOptions
            {
                HandshakeTimeout = BoundedWait,
                RequestTimeout = BoundedWait
            });

        var handshake = await client.InitializeAsync();
        Assert.True(handshake.IsReady);

        var loadResult = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = SessionId,
            WorkingDirectory = WorkingDirectory
        });
        Assert.True(loadResult.IsReady);
        Assert.Equal(SessionId, loadResult.Evidence!.SessionId);

        var promptResult = await client.PromptAsync(CursorAcpPromptRequest.Create(
            SessionId,
            "Inspect the repository structure.",
            "claude-3-7-sonnet"));
        Assert.True(promptResult.IsSuccess);
        Assert.Equal(CursorAcpStopReasons.EndTurn, promptResult.StopReason);

        var cancelResult = await client.CancelSessionAsync(new CursorAcpCancelRequest { SessionId = SessionId });
        Assert.True(cancelResult.IsSent);
        Assert.False(cancelResult.IsTerminal);

        var initializeFrame = await ReadFrameAsync(harness);
        Assert.Equal("initialize", initializeFrame.GetProperty("method").GetString());

        var loadFrame = await ReadFrameAsync(harness);
        Assert.Equal("session/load", loadFrame.GetProperty("method").GetString());
        Assert.Equal(SessionId, loadFrame.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(WorkingDirectory, loadFrame.GetProperty("params").GetProperty("cwd").GetString());

        var promptFrame = await ReadFrameAsync(harness);
        Assert.Equal("session/prompt", promptFrame.GetProperty("method").GetString());
        Assert.Equal(SessionId, promptFrame.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(
            "Inspect the repository structure.",
            promptFrame.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal(
            "claude-3-7-sonnet",
            promptFrame.GetProperty("params").GetProperty("model").GetString());

        var cancelFrame = await ReadFrameAsync(harness);
        Assert.Equal("session/cancel", cancelFrame.GetProperty("method").GetString());
        Assert.False(cancelFrame.TryGetProperty("id", out _));
        Assert.Equal(SessionId, cancelFrame.GetProperty("params").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task CursorAcpClient_StreamingAndPermissionReplyOverStdioPipes_NormalizeAndReply()
    {
        await using var harness = new CursorAcpPipeHarness
        {
            FrameHandler = frame =>
            {
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;

                if (!root.TryGetProperty("method", out var methodElement) ||
                    methodElement.GetString() != "initialize")
                {
                    return null;
                }

                return CursorAcpIntegrationTestData.CreateResponseJson(
                    root.GetProperty("id").GetInt32(),
                    CursorAcpIntegrationTestData.ReadHandshakeResult());
            }
        };

        var factory = new JsonRpcStdioTransportFactory();
        await using var transport = factory.Create(harness.TransportInput, harness.TransportOutput);
        var client = new CursorAcpClient(
            transport,
            new CursorAcpOptions
            {
                HandshakeTimeout = BoundedWait,
                RequestTimeout = BoundedWait
            });

        var handshake = await client.InitializeAsync();
        Assert.True(handshake.IsReady);

        foreach (var line in CursorAcpIntegrationTestData.ReadStreamingUpdateLines())
        {
            await harness.WriteRawLineAsync(line);
        }

        await harness.WriteRawLineAsync(CursorAcpIntegrationTestData.ReadPermissionRequestLine());

        var events = await CollectAsync(client, SessionId, expectedCount: 5);

        Assert.IsType<CursorAcpStreamEvent.TextChunk>(events[0]);
        Assert.IsType<CursorAcpStreamEvent.Thought>(events[1]);

        var toolCall = Assert.IsType<CursorAcpStreamEvent.ToolCall>(events[2]);
        Assert.Equal("readFile", toolCall.ToolName);

        Assert.IsType<CursorAcpStreamEvent.StatusUpdate>(events[3]);

        var permission = Assert.IsType<CursorAcpStreamEvent.PermissionRequest>(events[4]);
        Assert.StartsWith("acp-v1:", permission.RequestId, StringComparison.Ordinal);
        Assert.Equal(
            LLMWorkGUI.Domain.Enums.NormalizedApprovalKind.UnknownHighRisk,
            permission.ApprovalKind);

        var reply = await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
        {
            PermissionId = permission.RequestId,
            Decision = CursorAcpPermissionDecision.AllowOnce,
            SessionId = SessionId
        });

        Assert.True(reply.IsSent);

        var initializeFrame = await ReadFrameAsync(harness);
        Assert.Equal("initialize", initializeFrame.GetProperty("method").GetString());

        var replyFrame = await ReadFrameAsync(harness);
        Assert.Equal("perm-req-1", replyFrame.GetProperty("id").GetString());
        Assert.Equal("allow_once", replyFrame.GetProperty("result").GetProperty("decision").GetString());
        Assert.False(replyFrame.TryGetProperty("method", out _));
    }

    private static async Task<JsonElement> ReadFrameAsync(CursorAcpPipeHarness harness)
    {
        var frame = await harness.ReadFrameAsync(BoundedWait);

        return JsonDocument.Parse(frame).RootElement.Clone();
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
