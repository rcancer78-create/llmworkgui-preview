using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpClientPromptTests
{
    private const string SessionId = "sess-test-42";

    [Theory]
    [InlineData("{\"stopReason\":\"unknown\",\"stopReason\":\"endTurn\"}")]
    [InlineData("{\"stopReason\":\"cancelled\",\"stopReason\":\"endTurn\"}")]
    public async Task PromptAsync_DuplicateTerminalClaimIsMalformedAfterDispatch(string json)
    {
        using var document = JsonDocument.Parse(json);
        var transport = CreateReadyTransport(document.RootElement.Clone());
        var client = CreateClient(transport);
        await client.InitializeAsync();
        var observations = new List<bool>();
        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(SessionId, "fixture") with
            { DispatchObserver = observations.Add });
        Assert.Equal(new[] { true }, observations);
        Assert.False(result.IsSuccess);
        Assert.Equal(CursorAcpPromptFailureKind.MalformedResponse, result.FailureKind);
    }

    [Theory]
    [InlineData("{\"stopReason\":\"cancelled\",\"stopReason\":\"endTurn\"}")]
    [InlineData("{\"stopReason\":42}")]
    [InlineData("{\"missingStopReason\":true}")]
    public async Task MalformedTerminalResponseAfterPromptAttemptRetainsWriterOwnership(string json)
    {
        using var document = JsonDocument.Parse(json);
        var transport = FakeJsonRpcTransport.RespondingByMethod(method =>
            FakeJsonRpcTransport.CreateResultResponse(method switch
            {
                CursorAcpClient.InitializeMethod => CursorAcpTestData.ReadHandshakeResult(),
                CursorAcpClient.SetModeMethod => JsonSerializer.SerializeToElement(new { }),
                CursorAcpClient.PromptMethod => document.RootElement.Clone(),
                _ => throw new InvalidOperationException("Unexpected method: " + method)
            }));
        var client = CreateClient(transport);
        await client.InitializeAsync();
        var token = new FakeCheckoutLockToken();
        var supervisor = new CursorAcpTurnSupervisor(client, new CursorAcpModePolicy(), usesExternalStream: true);
        var result = await supervisor.ExecuteTurnAsync(new CursorAcpModeDecision
        {
            Mode = CursorAcpMode.Agent, ModeId = "agent", Access = CursorAcpModeAccess.Write,
            State = LLMWorkGUI.Domain.Enums.CapabilityState.Supported, CanSend = true, RequiresWriterLock = true
        }, token, CursorAcpPromptRequest.Create(SessionId, "fixture"));

        Assert.Single(transport.Requests.Where(item => item.Method == CursorAcpClient.PromptMethod));
        Assert.Equal(CursorAcpTurnOutcome.Ambiguous, result.Outcome);
        Assert.True(result.LockRetained);
        Assert.False(result.LockReleased);
        Assert.True(token.IsHeld);
        Assert.Equal(0, token.ReleaseCount);
    }

    [Fact]
    public async Task PromptAsync_DispatchObserverReceivesOnlyFirstObservation()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadPromptResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();
        var observations = new List<bool>();

        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(SessionId, "hello") with
        {
            DispatchObserver = observations.Add
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { true }, observations);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("request-id")]
    public async Task PromptAsync_ForgedAccountingIdentityIsRefusedBeforeDispatch(string field)
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadPromptResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();
        var valid = CursorAcpPromptRequest.Create(SessionId, "hello");
        var forged = field == "hash"
            ? valid with { PromptHash = CursorAcpPromptRequest.ComputePromptHash("different bytes") }
            : valid with { ClientRequestId = "not-a-uuid" };

        var result = await client.PromptAsync(forged);

        Assert.Equal(CursorAcpPromptFailureKind.InvalidRequest, result.FailureKind);
        Assert.DoesNotContain(transport.Requests, request => request.Method == CursorAcpClient.PromptMethod);
    }

    [Fact]
    public async Task PromptAsync_ReadyHandshake_SendsFixturePayloadAndParsesStopReason()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadPromptResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(
            SessionId,
            "Inspect the repository structure.",
            "claude-3-7-sonnet"));

        Assert.True(result.IsSuccess);
        Assert.Equal("endTurn", result.StopReason);
        Assert.Null(result.FailureKind);
        Assert.Null(result.Blocker);

        var request = transport.Requests[1];
        Assert.Equal("session/prompt", request.Method);
        // A prompt response is returned at turn completion, not at control-request acknowledgement.
        Assert.Equal(TimeSpan.FromMinutes(15), request.Timeout);

        var parameters = request.Parameters!.Value;
        Assert.Equal(SessionId, parameters.GetProperty("sessionId").GetString());
        Assert.Equal("claude-3-7-sonnet", parameters.GetProperty("model").GetString());

        var block = Assert.Single(parameters.GetProperty("prompt").EnumerateArray());
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal("Inspect the repository structure.", block.GetProperty("text").GetString());
    }

    [Fact]
    public async Task PromptAsync_WithoutModel_OmitsModelProperty()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadPromptResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        await client.PromptAsync(CursorAcpPromptRequest.Create(SessionId, "hello"));

        var parameters = transport.Requests[1].Parameters!.Value;
        Assert.False(parameters.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task PromptAsync_CancelledStopReason_IsSuccessWithCancelledReason()
    {
        var transport = CreateReadyTransport(JsonSerializer.SerializeToElement(new
        {
            stopReason = "cancelled"
        }));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(SessionId, "hello"));

        Assert.True(result.IsSuccess);
        Assert.True(result.IsCancelled);
    }

    [Fact]
    public async Task PromptAsync_TurnAndMessageIds_AreParsedWhenReturned()
    {
        var transport = CreateReadyTransport(JsonSerializer.SerializeToElement(new
        {
            stopReason = "endTurn",
            turnId = "turn-7",
            messageId = "msg-9"
        }));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(SessionId, "hello"));

        Assert.Equal("turn-7", result.TurnId);
        Assert.Equal("msg-9", result.MessageId);
    }

    [Fact]
    public async Task PromptAsync_BeforeReadyHandshake_IsNotReadyWithoutSending()
    {
        var transport = FakeJsonRpcTransport.RespondingWith(CursorAcpTestData.ReadPromptResult());
        var client = CreateClient(transport);

        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(SessionId, "hello"));

        Assert.Equal(CursorAcpPromptFailureKind.NotReady, result.FailureKind);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PromptAsync_EmptySessionId_IsInvalidSessionIdWithoutSending(string sessionId)
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadPromptResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(sessionId, "hello"));

        Assert.Equal(CursorAcpPromptFailureKind.InvalidSessionId, result.FailureKind);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task PromptAsync_EmptyPrompt_IsInvalidPromptWithoutSending()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadPromptResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.PromptAsync(new CursorAcpPromptRequest
        {
            SessionId = SessionId,
            Prompt = "   ",
            ClientRequestId = Guid.NewGuid().ToString("D"),
            PromptHash = CursorAcpPromptRequest.ComputePromptHash("   ")
        });

        Assert.Equal(CursorAcpPromptFailureKind.InvalidPrompt, result.FailureKind);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task PromptAsync_MissingExecutionIdentity_IsInvalidRequestWithoutSending()
    {
        var transport = CreateReadyTransport(CursorAcpTestData.ReadPromptResult());
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.PromptAsync(new CursorAcpPromptRequest
        {
            SessionId = SessionId,
            Prompt = "hello",
            ClientRequestId = string.Empty,
            PromptHash = string.Empty
        });

        Assert.Equal(CursorAcpPromptFailureKind.InvalidRequest, result.FailureKind);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task PromptAsync_AgentError_IsDegradedAgentError()
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : new JsonRpcResponse
            {
                Id = JsonSerializer.SerializeToElement(1),
                Error = new JsonRpcError { Code = -32000, Message = "busy" }
            });
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(SessionId, "hello"));

        Assert.Equal(CursorAcpPromptFailureKind.AgentError, result.FailureKind);
        Assert.Contains("busy", result.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptAsync_TransportTimeout_IsDegradedPromptTimeout()
    {
        var transport = CreateReadyTransportFailingWith(
            new JsonRpcTransportException(JsonRpcTransportFailureKind.RequestTimedOut, "timed out"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(SessionId, "hello"));

        Assert.Equal(CursorAcpPromptFailureKind.PromptTimeout, result.FailureKind);
    }

    [Fact]
    public async Task PromptAsync_TransportClosed_IsDegradedTransportFailure()
    {
        var transport = CreateReadyTransportFailingWith(
            new JsonRpcTransportException(JsonRpcTransportFailureKind.TransportClosed, "eof"));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(SessionId, "hello"));

        Assert.Equal(CursorAcpPromptFailureKind.TransportFailure, result.FailureKind);
    }

    [Fact]
    public async Task PromptAsync_ResultWithoutStopReason_IsMalformedResponse()
    {
        var transport = CreateReadyTransport(JsonSerializer.SerializeToElement(new { turnId = "turn-1" }));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        var result = await client.PromptAsync(CursorAcpPromptRequest.Create(SessionId, "hello"));

        Assert.Equal(CursorAcpPromptFailureKind.MalformedResponse, result.FailureKind);
    }

    [Fact]
    public async Task PromptAsync_CallerCancellation_PropagatesOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var transport = new FakeJsonRpcTransport((method, _, _, token) => method == CursorAcpClient.InitializeMethod
            ? Task.FromResult(FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult()))
            : Task.FromException<JsonRpcResponse>(new OperationCanceledException(token)));
        var client = CreateClient(transport);
        await client.InitializeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PromptAsync(
            CursorAcpPromptRequest.Create(SessionId, "hello"),
            cancellation.Token));
    }

    [Fact]
    public void PromptRequest_Create_ComputesCanonicalHashAndUniqueRequestId()
    {
        var first = CursorAcpPromptRequest.Create(SessionId, "hello");
        var second = CursorAcpPromptRequest.Create(SessionId, "hello");

        Assert.Equal(CursorAcpPromptRequest.ComputePromptHash("hello"), first.PromptHash);
        Assert.NotEqual(first.ClientRequestId, second.ClientRequestId);
        Assert.True(Guid.TryParse(first.ClientRequestId, out _));
    }

    private static CursorAcpClient CreateClient(FakeJsonRpcTransport transport) =>
        new(transport, new CursorAcpOptions { RequestTimeout = TimeSpan.FromSeconds(17) });

    private static FakeJsonRpcTransport CreateReadyTransport(JsonElement promptResult) =>
        FakeJsonRpcTransport.RespondingByMethod(method => method switch
        {
            CursorAcpClient.InitializeMethod => FakeJsonRpcTransport.CreateResultResponse(
                CursorAcpTestData.ReadHandshakeResult()),
            CursorAcpClient.PromptMethod => FakeJsonRpcTransport.CreateResultResponse(promptResult),
            _ => throw new InvalidOperationException($"Unexpected ACP method '{method}'.")
        });

    private static FakeJsonRpcTransport CreateReadyTransportFailingWith(Exception exception) =>
        FakeJsonRpcTransport.RespondingByMethod(method => method == CursorAcpClient.InitializeMethod
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : throw exception);
}
