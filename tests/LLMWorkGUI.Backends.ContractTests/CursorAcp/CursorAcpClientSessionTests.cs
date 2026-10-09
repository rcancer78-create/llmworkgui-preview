using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpClientSessionTests
{
    private const string WorkingDirectory = "C:\\workspace\\demo-app";

    [Fact]
    public async Task CreateSessionAsync_BeforeReadyHandshake_ReturnsDegradedWithoutSending()
    {
        var transport = FakeJsonRpcTransport.RespondingWith(CursorAcpTestData.CreateSessionResult("sess-1"));
        var client = CreateClient(transport);

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.True(result.IsDegraded);
        Assert.Equal(CursorAcpSessionFailureKind.NotReady, result.FailureKind);
        Assert.Null(result.Evidence);
        Assert.False(string.IsNullOrWhiteSpace(result.Blocker));
        Assert.False(string.IsNullOrWhiteSpace(result.Guidance));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task CreateSessionAsync_DegradedHandshake_ReturnsDegradedWithoutSending()
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(_ =>
            FakeJsonRpcTransport.CreateResultResponse(
                CursorAcpTestData.CreateHandshakeResult(node => node["protocolVersion"] = "1")));
        var client = CreateClient(transport);

        var handshake = await client.InitializeAsync();
        Assert.True(handshake.IsDegraded);

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.Equal(CursorAcpSessionFailureKind.NotReady, result.FailureKind);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task CreateSessionAsync_ReadyHandshake_SendsSchemaPayloadAndParsesSessionId()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.CreateSessionResult("sess-42"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(new CursorAcpNewSessionRequest
        {
            WorkingDirectory = WorkingDirectory,
            McpServers = new[]
            {
                new CursorAcpMcpServer
                {
                    Name = "demo-filesystem",
                    Command = "node",
                    Args = new[] { WorkingDirectory + "\\tools\\demo-mcp-server.js" },
                    Type = "stdio"
                }
            }
        });

        Assert.True(result.IsReady);
        Assert.False(result.IsDegraded);
        Assert.Null(result.FailureKind);
        Assert.Null(result.Blocker);
        Assert.Equal("sess-42", result.Evidence!.SessionId);

        Assert.Equal(2, transport.Requests.Count);
        var request = transport.Requests[1];
        Assert.Equal("session/new", request.Method);
        Assert.Equal(TimeSpan.FromSeconds(17), request.Timeout);

        var parameters = request.Parameters!.Value;
        Assert.Equal(WorkingDirectory, parameters.GetProperty("cwd").GetString());

        var server = Assert.Single(parameters.GetProperty("mcpServers").EnumerateArray());
        Assert.Equal("demo-filesystem", server.GetProperty("name").GetString());
        Assert.Equal("node", server.GetProperty("command").GetString());
        Assert.Equal("stdio", server.GetProperty("type").GetString());
        Assert.Equal(
            WorkingDirectory + "\\tools\\demo-mcp-server.js",
            Assert.Single(server.GetProperty("args").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task CreateSessionAsync_EmptyMcpServers_SendsEmptyJsonArray()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.CreateSessionResult("sess-1"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.True(result.IsReady);

        var parameters = transport.Requests[1].Parameters!.Value;
        var mcpServers = parameters.GetProperty("mcpServers");
        Assert.Equal(JsonValueKind.Array, mcpServers.ValueKind);
        Assert.Equal(0, mcpServers.GetArrayLength());
    }

    [Fact]
    public async Task CreateSessionAsync_SessionIdFromAlternativeIdProperty_IsAccepted()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.CreateSessionResultWithIdProperty("sess-fallback"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.True(result.IsReady);
        Assert.Equal("sess-fallback", result.Evidence!.SessionId);
    }

    [Theory]
    [InlineData("{\"sessionId\":\"session-a\",\"id\":\"session-b\"}")]
    [InlineData("{\"sessionId\":\"session-a\",\"sessionId\":\"session-b\"}")]
    public async Task CreateSessionAsync_ContradictoryIdentity_IsMalformedResponse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var transport = CreateReadyTransport(document.RootElement.Clone());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.Equal(CursorAcpSessionFailureKind.MalformedResponse, result.FailureKind);
        Assert.Null(result.Evidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("workspace\\demo-app")]
    [InlineData("C:workspace\\demo-app")]
    public async Task CreateSessionAsync_InvalidWorkingDirectory_IsDegradedWithoutSending(string workingDirectory)
    {
        var transport = CreateReadyTransport(CursorAcpTestData.CreateSessionResult("sess-1"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = workingDirectory });

        Assert.Equal(CursorAcpSessionFailureKind.InvalidWorkingDirectory, result.FailureKind);
        Assert.False(string.IsNullOrWhiteSpace(result.Blocker));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task CreateSessionAsync_EmptySessionId_IsMalformedResponse()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.CreateSessionResult(string.Empty));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.Equal(CursorAcpSessionFailureKind.MalformedResponse, result.FailureKind);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task CreateSessionAsync_NonObjectResult_IsMalformedResponse()
    {
        var transport = CreateReadyTransport(JsonSerializer.SerializeToElement("not-an-object"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.Equal(CursorAcpSessionFailureKind.MalformedResponse, result.FailureKind);
    }

    [Fact]
    public async Task CreateSessionAsync_ResponseWithoutResultOrError_IsMalformedResponse()
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : new JsonRpcResponse { Id = JsonSerializer.SerializeToElement(1) });
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.Equal(CursorAcpSessionFailureKind.MalformedResponse, result.FailureKind);
    }

    [Fact]
    public async Task CreateSessionAsync_AgentErrorResponse_IsDegradedAgentError()
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : new JsonRpcResponse
            {
                Id = JsonSerializer.SerializeToElement(1),
                Error = new JsonRpcError
                {
                    Code = -32602,
                    Message = "cwd is required"
                }
            });
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.Equal(CursorAcpSessionFailureKind.AgentError, result.FailureKind);
        Assert.Contains("-32602", result.Blocker, StringComparison.Ordinal);
        Assert.Contains("cwd is required", result.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateSessionAsync_TransportTimeout_IsDegradedSessionTimeout()
    {
        var transport = CreateReadyTransportFailingWith(
            new JsonRpcTransportException(JsonRpcTransportFailureKind.RequestTimedOut, "timed out"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.Equal(CursorAcpSessionFailureKind.SessionTimeout, result.FailureKind);
        Assert.False(string.IsNullOrWhiteSpace(result.Guidance));
    }

    [Fact]
    public async Task CreateSessionAsync_TransportClosed_IsDegradedTransportFailure()
    {
        var transport = CreateReadyTransportFailingWith(
            new JsonRpcTransportException(JsonRpcTransportFailureKind.TransportClosed, "eof"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.Equal(CursorAcpSessionFailureKind.TransportFailure, result.FailureKind);
    }

    [Fact]
    public async Task CreateSessionAsync_UnexpectedTransportException_IsMappedWithoutThrowing()
    {
        var transport = CreateReadyTransportFailingWith(new IOException("broken pipe"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory });

        Assert.True(result.IsDegraded);
        Assert.Equal(CursorAcpSessionFailureKind.TransportFailure, result.FailureKind);
        Assert.Contains("broken pipe", result.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateSessionAsync_CallerCancellation_PropagatesOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var transport = new FakeJsonRpcTransport((method, _, _, token) => method == CursorAcpClient.InitializeMethod
            ? Task.FromResult(FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult()))
            : Task.FromException<JsonRpcResponse>(new OperationCanceledException(token)));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateSessionAsync(
            new CursorAcpNewSessionRequest { WorkingDirectory = WorkingDirectory },
            cancellation.Token));
    }

    private static CursorAcpClient CreateClient(FakeJsonRpcTransport transport) =>
        new(transport, new CursorAcpOptions { RequestTimeout = TimeSpan.FromSeconds(17) });

    private static FakeJsonRpcTransport CreateReadyTransport(JsonElement sessionResult) =>
        FakeJsonRpcTransport.RespondingByMethod(method => method switch
        {
            CursorAcpClient.InitializeMethod => FakeJsonRpcTransport.CreateResultResponse(
                CursorAcpTestData.ReadHandshakeResult()),
            CursorAcpClient.NewSessionMethod => FakeJsonRpcTransport.CreateResultResponse(sessionResult),
            _ => throw new InvalidOperationException($"Unexpected ACP method '{method}'.")
        });

    private static FakeJsonRpcTransport CreateReadyTransportFailingWith(Exception exception) =>
        FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : throw exception);
}
