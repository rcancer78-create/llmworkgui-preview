using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpClientLoadSessionTests
{
    private const string WorkingDirectory = "C:\\workspace\\demo-app";

    [Fact]
    public async Task LoadSessionAsync_ReadyHandshake_SendsFixturePayloadAndParsesSessionId()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadSessionLoadResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = "sess-test-42",
            WorkingDirectory = WorkingDirectory
        });

        Assert.True(result.IsReady);
        Assert.Null(result.FailureKind);
        Assert.Equal("sess-test-42", result.Evidence!.SessionId);

        var request = transport.Requests[1];
        Assert.Equal("session/load", request.Method);
        Assert.Equal(TimeSpan.FromSeconds(17), request.Timeout);

        var parameters = request.Parameters!.Value;
        Assert.Equal("sess-test-42", parameters.GetProperty("sessionId").GetString());
        Assert.Equal(WorkingDirectory, parameters.GetProperty("cwd").GetString());
        Assert.Equal(0, parameters.GetProperty("mcpServers").GetArrayLength());
    }

    [Fact]
    public async Task LoadSessionAsync_SessionIdFromAlternativeIdProperty_IsAccepted()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.CreateSessionResultWithIdProperty("sess-fallback"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = "sess-fallback",
            WorkingDirectory = WorkingDirectory
        });

        Assert.True(result.IsReady);
        Assert.Equal("sess-fallback", result.Evidence!.SessionId);
    }

    [Fact]
    public async Task LoadSessionAsync_BeforeReadyHandshake_ReturnsNotReadyWithoutSending()
    {
        var transport = FakeJsonRpcTransport.RespondingWith(CursorAcpTestData.ReadSessionLoadResult());
        var client = CreateClient(transport);

        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = "sess-test-42",
            WorkingDirectory = WorkingDirectory
        });

        Assert.Equal(CursorAcpSessionFailureKind.NotReady, result.FailureKind);
        Assert.False(string.IsNullOrWhiteSpace(result.Guidance));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task LoadSessionAsync_LoadSessionCapabilityFalse_ReturnsUnsupportedCapabilityWithoutSending()
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : throw new InvalidOperationException($"Unexpected ACP method '{method}'."));
        var client = new CursorAcpClient(
            transport,
            new CursorAcpOptions { RequestTimeout = TimeSpan.FromSeconds(17) },
            new LoadSessionUnsupportedValidator());

        var handshake = await client.InitializeAsync();
        Assert.True(handshake.IsReady);
        Assert.False(handshake.Evidence!.AgentCapabilities.LoadSession);

        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = "sess-test-42",
            WorkingDirectory = WorkingDirectory
        });

        Assert.Equal(CursorAcpSessionFailureKind.UnsupportedCapability, result.FailureKind);
        Assert.Contains("loadSession", result.Blocker, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.Guidance));
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LoadSessionAsync_EmptySessionId_IsInvalidSessionIdWithoutSending(string sessionId)
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadSessionLoadResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = sessionId,
            WorkingDirectory = WorkingDirectory
        });

        Assert.Equal(CursorAcpSessionFailureKind.InvalidSessionId, result.FailureKind);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("workspace\\demo-app")]
    public async Task LoadSessionAsync_InvalidWorkingDirectory_IsDegradedWithoutSending(string workingDirectory)
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadSessionLoadResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = "sess-test-42",
            WorkingDirectory = workingDirectory
        });

        Assert.Equal(CursorAcpSessionFailureKind.InvalidWorkingDirectory, result.FailureKind);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task LoadSessionAsync_AgentError_IsDegradedAgentError()
    {
        var transport = CreateReadyTransportWithError(-32602, "unknown session");
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = "sess-test-42",
            WorkingDirectory = WorkingDirectory
        });

        Assert.Equal(CursorAcpSessionFailureKind.AgentError, result.FailureKind);
        Assert.Contains("unknown session", result.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadSessionAsync_TransportTimeout_IsDegradedSessionTimeout()
    {
        var transport = CreateReadyTransportFailingWith(
            new JsonRpcTransportException(JsonRpcTransportFailureKind.RequestTimedOut, "timed out"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = "sess-test-42",
            WorkingDirectory = WorkingDirectory
        });

        Assert.Equal(CursorAcpSessionFailureKind.SessionTimeout, result.FailureKind);
    }

    [Fact]
    public async Task LoadSessionAsync_TransportClosed_IsDegradedTransportFailure()
    {
        var transport = CreateReadyTransportFailingWith(
            new JsonRpcTransportException(JsonRpcTransportFailureKind.TransportClosed, "eof"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = "sess-test-42",
            WorkingDirectory = WorkingDirectory
        });

        Assert.Equal(CursorAcpSessionFailureKind.TransportFailure, result.FailureKind);
    }

    [Fact]
    public async Task LoadSessionAsync_MalformedResult_IsMalformedResponse()
    {
        var transport = CreateReadyTransport(JsonSerializer.SerializeToElement("not-an-object"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest
        {
            SessionId = "sess-test-42",
            WorkingDirectory = WorkingDirectory
        });

        Assert.Equal(CursorAcpSessionFailureKind.MalformedResponse, result.FailureKind);
    }

    [Fact]
    public async Task LoadSessionAsync_CallerCancellation_PropagatesOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var transport = new FakeJsonRpcTransport((method, _, _, token) => method == CursorAcpClient.InitializeMethod
            ? Task.FromResult(FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult()))
            : Task.FromException<JsonRpcResponse>(new OperationCanceledException(token)));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LoadSessionAsync(
            new CursorAcpLoadSessionRequest
            {
                SessionId = "sess-test-42",
                WorkingDirectory = WorkingDirectory
            },
            cancellation.Token));
    }

    private static CursorAcpClient CreateClient(FakeJsonRpcTransport transport) =>
        new(transport, new CursorAcpOptions { RequestTimeout = TimeSpan.FromSeconds(17) });

    private static FakeJsonRpcTransport CreateReadyTransport(JsonElement sessionResult) =>
        FakeJsonRpcTransport.RespondingByMethod(method => method switch
        {
            CursorAcpClient.InitializeMethod => FakeJsonRpcTransport.CreateResultResponse(
                CursorAcpTestData.ReadHandshakeResult()),
            CursorAcpClient.LoadSessionMethod => FakeJsonRpcTransport.CreateResultResponse(sessionResult),
            _ => throw new InvalidOperationException($"Unexpected ACP method '{method}'.")
        });

    private static FakeJsonRpcTransport CreateReadyTransportWithError(int code, string message) =>
        FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : new JsonRpcResponse
            {
                Id = JsonSerializer.SerializeToElement(1),
                Error = new JsonRpcError { Code = code, Message = message }
            });

    private static FakeJsonRpcTransport CreateReadyTransportFailingWith(Exception exception) =>
        FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : throw exception);

    private sealed class LoadSessionUnsupportedValidator : CursorAcpHandshakeValidator
    {
        public override CursorAcpHandshakeEvidence ValidateInitializeResult(JsonElement resultElement)
        {
            var evidence = base.ValidateInitializeResult(resultElement);

            return evidence with
            {
                AgentCapabilities = evidence.AgentCapabilities with { LoadSession = false }
            };
        }
    }
}
