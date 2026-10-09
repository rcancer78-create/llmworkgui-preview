using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpClientCancelTests
{
    [Fact]
    public async Task CancelSessionAsync_ReadyHandshake_SendsFixturePayloadAndIsNeverTerminal()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadCancelResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CancelSessionAsync(new CursorAcpCancelRequest
        {
            SessionId = "sess-test-42"
        });

        Assert.True(result.IsSent);
        Assert.False(result.IsTerminal);
        Assert.Null(result.FailureKind);
        Assert.Null(result.Blocker);

        Assert.Single(transport.Requests); // initialize only; cancellation has no JSON-RPC id.
        var request = Assert.Single(transport.SentNotifications);
        Assert.Equal("session/cancel", request.Method);
        Assert.Equal("sess-test-42", request.Parameters!.Value.GetProperty("sessionId").GetString());
        Assert.Null(request.Timeout);
    }

    [Fact]
    public async Task CancelSessionAsync_BeforeReadyHandshake_IsDegradedWithoutSending()
    {
        var transport = FakeJsonRpcTransport.RespondingWith(CursorAcpTestData.ReadCancelResult());
        var client = CreateClient(transport);

        var result = await client.CancelSessionAsync(new CursorAcpCancelRequest { SessionId = "sess-test-42" });

        Assert.False(result.IsSent);
        Assert.False(result.IsTerminal);
        Assert.Equal(CursorAcpCancelFailureKind.NotReady, result.FailureKind);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CancelSessionAsync_EmptySessionId_IsInvalidSessionIdWithoutSending(string sessionId)
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadCancelResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CancelSessionAsync(new CursorAcpCancelRequest { SessionId = sessionId });

        Assert.Equal(CursorAcpCancelFailureKind.InvalidSessionId, result.FailureKind);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task CancelSessionAsync_DoesNotSendRpcRequestOrAwaitAgentAcknowledgement()
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : new JsonRpcResponse
            {
                Id = JsonSerializer.SerializeToElement(1),
                Error = new JsonRpcError { Code = -32601, Message = "no active turn" }
            });
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CancelSessionAsync(new CursorAcpCancelRequest { SessionId = "sess-test-42" });

        Assert.True(result.IsSent);
        Assert.False(result.IsTerminal);
        Assert.Single(transport.Requests);
        Assert.Single(transport.SentNotifications);
    }

    [Fact]
    public async Task CancelSessionAsync_TransportTimeout_IsDegradedCancelTimeout()
    {
        var transport = CreateReadyTransportFailingWith(
            new JsonRpcTransportException(JsonRpcTransportFailureKind.RequestTimedOut, "timed out"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CancelSessionAsync(new CursorAcpCancelRequest { SessionId = "sess-test-42" });

        Assert.Equal(CursorAcpCancelFailureKind.CancelTimeout, result.FailureKind);
    }

    [Fact]
    public async Task CancelSessionAsync_TransportClosed_IsDegradedTransportFailure()
    {
        var transport = CreateReadyTransportFailingWith(
            new JsonRpcTransportException(JsonRpcTransportFailureKind.TransportClosed, "eof"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CancelSessionAsync(new CursorAcpCancelRequest { SessionId = "sess-test-42" });

        Assert.Equal(CursorAcpCancelFailureKind.TransportFailure, result.FailureKind);
    }

    [Fact]
    public async Task CancelSessionAsync_BlockedNotification_IsBoundedBySendBudget()
    {
        var transport = CreateReadyTransport(JsonSerializer.SerializeToElement("not-an-object"));
        transport.NotificationHandler = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var client = new CursorAcpClient(transport, new CursorAcpOptions { RequestTimeout = TimeSpan.FromMilliseconds(30) });
        await client.InitializeAsync();

        var result = await client.CancelSessionAsync(new CursorAcpCancelRequest { SessionId = "sess-test-42" });

        Assert.Equal(CursorAcpCancelFailureKind.CancelTimeout, result.FailureKind);
        Assert.False(result.IsTerminal);
    }

    [Fact]
    public async Task CancelSessionAsync_CallerCancellation_PropagatesOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var transport = new FakeJsonRpcTransport((method, _, _, token) => method == CursorAcpClient.InitializeMethod
            ? Task.FromResult(FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult()))
            : Task.FromException<JsonRpcResponse>(new OperationCanceledException(token)));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CancelSessionAsync(
            new CursorAcpCancelRequest { SessionId = "sess-test-42" },
            cancellation.Token));
    }

    private static CursorAcpClient CreateClient(FakeJsonRpcTransport transport) =>
        new(transport, new CursorAcpOptions { RequestTimeout = TimeSpan.FromSeconds(17) });

    private static FakeJsonRpcTransport CreateReadyTransport(JsonElement cancelResult) =>
        FakeJsonRpcTransport.RespondingByMethod(method => method switch
        {
            CursorAcpClient.InitializeMethod => FakeJsonRpcTransport.CreateResultResponse(
                CursorAcpTestData.ReadHandshakeResult()),
            CursorAcpClient.CancelMethod => FakeJsonRpcTransport.CreateResultResponse(cancelResult),
            _ => throw new InvalidOperationException($"Unexpected ACP method '{method}'.")
        });

    private static FakeJsonRpcTransport CreateReadyTransportFailingWith(Exception exception)
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : throw exception);
        transport.NotificationHandler = _ => Task.FromException(exception);
        return transport;
    }
}
