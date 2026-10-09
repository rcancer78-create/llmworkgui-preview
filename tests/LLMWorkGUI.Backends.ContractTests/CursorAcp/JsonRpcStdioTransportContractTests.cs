using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class JsonRpcStdioTransportContractTests
{
    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NumericRequestId_CannotBeCompletedByAStringResponseId()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = CreateTransport(harness);
        var pending = transport.SendRequestAsync("initialize", null, BoundedWait);
        var id = ReadId(await harness.ReadFrameAsync(BoundedWait));
        await harness.WriteRawLineAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = id.ToString(System.Globalization.CultureInfo.InvariantCulture), result = new { marker = "wrong-type" } }));
        await harness.WriteRawLineAsync(CreateMarkerResponse(id, "exact-numeric"));

        var response = await pending.WaitAsync(BoundedWait);

        Assert.Equal(JsonValueKind.Number, response.Id.ValueKind);
        Assert.Equal(id, response.Id.GetInt32());
        Assert.Equal("exact-numeric", response.Result!.Value.GetProperty("marker").GetString());
        Assert.Equal(1, transport.UnmatchedResponseCount);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task SendRequestAsync_MatchingId_CompletesWithParsedResult()
    {
        await using var harness = new LoopbackJsonRpcHarness
        {
            FrameHandler = frame =>
            {
                using var document = JsonDocument.Parse(frame);
                var id = document.RootElement.GetProperty("id").GetInt32();

                return CursorAcpTestData.CreateResponseJson(id, CursorAcpTestData.ReadHandshakeResult());
            }
        };

        await using var transport = CreateTransport(harness);

        var response = await transport.SendRequestAsync("initialize", null, BoundedWait);

        Assert.False(response.IsError);
        Assert.Equal(1, response.Id.GetInt32());
        Assert.Equal(1, response.Result!.Value.GetProperty("protocolVersion").GetInt32());

        var requestFrame = await harness.ReadFrameAsync(BoundedWait);
        using var requestDocument = JsonDocument.Parse(requestFrame);
        Assert.Equal("2.0", requestDocument.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("initialize", requestDocument.RootElement.GetProperty("method").GetString());
        Assert.False(requestDocument.RootElement.TryGetProperty("params", out _));
    }

    [Fact]
    public async Task SendRequestAsync_OutOfOrderResponses_CorrelatesById()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = CreateTransport(harness);

        var firstTask = transport.SendRequestAsync("first", null, BoundedWait);
        var firstFrame = await harness.ReadFrameAsync(BoundedWait);
        var firstId = ReadId(firstFrame);

        var secondTask = transport.SendRequestAsync("second", null, BoundedWait);
        var secondFrame = await harness.ReadFrameAsync(BoundedWait);
        var secondId = ReadId(secondFrame);

        Assert.NotEqual(firstId, secondId);

        await harness.WriteRawLineAsync(CreateMarkerResponse(secondId, "second"));
        await harness.WriteRawLineAsync(CreateMarkerResponse(firstId, "first"));

        var first = await firstTask;
        var second = await secondTask;

        Assert.Equal("first", first.Result!.Value.GetProperty("marker").GetString());
        Assert.Equal("second", second.Result!.Value.GetProperty("marker").GetString());
    }

    [Fact]
    public async Task SendRequestAsync_ErrorResponse_PopulatesErrorObject()
    {
        await using var harness = new LoopbackJsonRpcHarness
        {
            FrameHandler = frame =>
            {
                using var document = JsonDocument.Parse(frame);
                var id = document.RootElement.GetProperty("id").GetInt32();

                return CursorAcpTestData.CreateErrorResponseJson(id, -32601, "Method not found");
            }
        };

        await using var transport = CreateTransport(harness);

        var response = await transport.SendRequestAsync("initialize", null, BoundedWait);

        Assert.True(response.IsError);
        Assert.Equal(-32601, response.Error!.Code);
        Assert.Equal("Method not found", response.Error.Message);
        Assert.Null(response.Result);
    }

    [Fact]
    public async Task SendRequestAsync_NoResponse_TimesOutAndReleasesPendingRequest()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = CreateTransport(harness);

        var exception = await Assert.ThrowsAsync<JsonRpcTransportException>(
            () => transport.SendRequestAsync("initialize", null, TimeSpan.FromMilliseconds(200)));

        Assert.Equal(JsonRpcTransportFailureKind.RequestTimedOut, exception.Kind);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task SendRequestAsync_CallerCancellation_ThrowsOperationCanceledAndReleasesPendingRequest()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = CreateTransport(harness);

        using var cancellation = new CancellationTokenSource();
        var requestTask = transport.SendRequestAsync("initialize", null, BoundedWait, cancellation.Token);

        await harness.ReadFrameAsync(BoundedWait);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
        Assert.Equal(0, transport.PendingRequestCount);
    }

    [Fact]
    public async Task InboundMalformedLines_AreCountedAndDoNotBreakCorrelation()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = CreateTransport(harness);

        var requestTask = transport.SendRequestAsync("initialize", null, BoundedWait);
        var frame = await harness.ReadFrameAsync(BoundedWait);
        var id = ReadId(frame);

        await harness.WriteRawLineAsync("this is not json");
        await harness.WriteRawLineAsync("{\"jsonrpc\":\"2.0\"}");
        await harness.WriteRawLineAsync("[1,2,3]");
        await harness.WriteRawLineAsync(CursorAcpTestData.CreateResponseJson(id, CursorAcpTestData.ReadHandshakeResult()));

        var response = await requestTask;

        Assert.False(response.IsError);
        await LoopbackJsonRpcHarness.WaitForAsync(
            () => transport.MalformedFrameCount >= 3,
            BoundedWait);

        Assert.Equal(3, transport.MalformedFrameCount);
    }

    [Fact]
    public async Task InboundNotification_IsRoutedToNotificationsChannel()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = CreateTransport(harness);

        await harness.WriteRawLineAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"value\":42}}");

        var notification = await transport.Notifications.ReadAsync().AsTask().WaitAsync(BoundedWait);

        Assert.Equal("session/update", notification.Method);
        Assert.Equal(42, notification.Parameters!.Value.GetProperty("value").GetInt32());
        Assert.Null(notification.Id);
    }

    [Fact]
    public async Task InboundServerRequestWithId_IsRoutedWithCorrelationId()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = CreateTransport(harness);

        await harness.WriteRawLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":\"perm-1\",\"method\":\"session/request_permission\",\"params\":{}}");

        var notification = await transport.Notifications.ReadAsync().AsTask().WaitAsync(BoundedWait);

        Assert.Equal("session/request_permission", notification.Method);
        Assert.NotNull(notification.Id);
        Assert.Equal("perm-1", notification.Id!.Value.GetString());
    }

    [Fact]
    public async Task InboundUnmatchedResponse_IsCountedAndIgnored()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = CreateTransport(harness);

        await harness.WriteRawLineAsync("{\"jsonrpc\":\"2.0\",\"id\":999,\"result\":{}}");

        await LoopbackJsonRpcHarness.WaitForAsync(
            () => transport.UnmatchedResponseCount == 1,
            BoundedWait);

        Assert.Equal(1, transport.UnmatchedResponseCount);
    }

    [Fact]
    public async Task CloseAsync_FailsPendingRequestsAndCompletesNotifications()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        var transport = CreateTransport(harness);

        var requestTask = transport.SendRequestAsync("initialize", null, BoundedWait);
        await harness.ReadFrameAsync(BoundedWait);

        await transport.CloseAsync();

        var exception = await Assert.ThrowsAsync<JsonRpcTransportException>(() => requestTask);
        Assert.Equal(JsonRpcTransportFailureKind.TransportClosed, exception.Kind);
        Assert.True(transport.Notifications.Completion.IsCompleted);

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task CloseAsync_IsIdempotent()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        var transport = CreateTransport(harness);

        await transport.CloseAsync();
        await transport.CloseAsync();
        await transport.DisposeAsync();
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task SendNotificationAsync_WritesFrameWithoutId()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = CreateTransport(harness);

        await transport.SendNotificationAsync("session/cancel", null);

        var frame = await harness.ReadFrameAsync(BoundedWait);
        using var document = JsonDocument.Parse(frame);

        Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("session/cancel", document.RootElement.GetProperty("method").GetString());
        Assert.False(document.RootElement.TryGetProperty("id", out _));
    }

    private static JsonRpcStdioTransport CreateTransport(
        LoopbackJsonRpcHarness harness,
        CursorAcpOptions? options = null) =>
        new(harness.TransportInput, harness.TransportOutput, options);

    private static int ReadId(string frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("id").GetInt32();
    }

    private static string CreateMarkerResponse(int id, string marker) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = new System.Text.Json.Nodes.JsonObject { ["marker"] = marker }
        }.ToJsonString();
}
