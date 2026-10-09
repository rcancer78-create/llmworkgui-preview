using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpClientTests
{
    [Fact]
    public async Task InitializeAsync_ValidResponse_ReturnsReadyEvidenceAndSetsReadiness()
    {
        var transport = FakeJsonRpcTransport.RespondingWith(CursorAcpTestData.ReadHandshakeResult());
        var client = new CursorAcpClient(transport);

        Assert.Null(client.CurrentReadiness);

        var result = await client.InitializeAsync();

        Assert.True(result.IsReady);
        Assert.False(result.IsDegraded);
        Assert.Null(result.FailureKind);
        Assert.Null(result.Blocker);
        Assert.NotNull(result.Evidence);
        Assert.Equal(1, result.Evidence!.ProtocolVersion);
        Assert.True(result.Evidence.AgentCapabilities.LoadSession);
        Assert.Equal("cursor_login", Assert.Single(result.Evidence.AuthMethods).Id);

        Assert.NotNull(client.CurrentReadiness);
        Assert.True(client.CurrentReadiness!.IsReady);
    }

    [Fact]
    public async Task InitializeAsync_SendsNumericProtocolVersionAndClientInfo()
    {
        var transport = FakeJsonRpcTransport.RespondingWith(CursorAcpTestData.ReadHandshakeResult());
        var client = new CursorAcpClient(
            transport,
            new CursorAcpOptions
            {
                ClientName = "LLMWorkGUI",
                ClientVersion = "9.9.9"
            });

        await client.InitializeAsync();

        var request = Assert.Single(transport.Requests);
        Assert.Equal("initialize", request.Method);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);

        var parameters = request.Parameters;
        Assert.NotNull(parameters);

        var root = parameters!.Value;
        Assert.Equal(JsonValueKind.Number, root.GetProperty("protocolVersion").ValueKind);
        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("LLMWorkGUI", root.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.Equal("9.9.9", root.GetProperty("clientInfo").GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("capabilities").ValueKind);
    }

    [Fact]
    public async Task InitializeAsync_StringProtocolVersion_ReportsDegradedUnsupportedVersion()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(node => node["protocolVersion"] = "1");
        var client = new CursorAcpClient(FakeJsonRpcTransport.RespondingWith(result));

        var handshake = await client.InitializeAsync();

        Assert.True(handshake.IsDegraded);
        Assert.Equal(CursorAcpHandshakeFailureKind.UnsupportedVersion, handshake.FailureKind);
        Assert.Contains("protocolVersion", handshake.Blocker, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(handshake.Guidance));
        Assert.Null(handshake.Evidence);
    }

    [Fact]
    public async Task InitializeAsync_MissingRequiredCapability_ReportsDegradedCapability()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(node =>
        {
            var capabilities = (System.Text.Json.Nodes.JsonObject)node["agentCapabilities"]!;
            capabilities["loadSession"] = false;
        });

        var client = new CursorAcpClient(FakeJsonRpcTransport.RespondingWith(result));

        var handshake = await client.InitializeAsync();

        Assert.Equal(CursorAcpHandshakeFailureKind.MissingRequiredCapability, handshake.FailureKind);
        Assert.Contains("loadSession", handshake.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_AgentErrorResponse_ReportsDegradedAgentError()
    {
        var client = new CursorAcpClient(
            FakeJsonRpcTransport.RespondingWithError(-32601, "Method not found"));

        var handshake = await client.InitializeAsync();

        Assert.Equal(CursorAcpHandshakeFailureKind.AgentError, handshake.FailureKind);
        Assert.Contains("-32601", handshake.Blocker, StringComparison.Ordinal);
        Assert.Contains("Method not found", handshake.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_ResponseWithoutResultOrError_ReportsMalformedResponse()
    {
        var client = new CursorAcpClient(FakeJsonRpcTransport.RespondingWithEmptyPayload());

        var handshake = await client.InitializeAsync();

        Assert.Equal(CursorAcpHandshakeFailureKind.MalformedResponse, handshake.FailureKind);
    }

    [Fact]
    public async Task InitializeAsync_TransportTimeout_ReportsDegradedHandshakeTimeout()
    {
        var client = new CursorAcpClient(FakeJsonRpcTransport.FailingWith(
            new JsonRpcTransportException(JsonRpcTransportFailureKind.RequestTimedOut, "timed out")));

        var handshake = await client.InitializeAsync();

        Assert.Equal(CursorAcpHandshakeFailureKind.HandshakeTimeout, handshake.FailureKind);
        Assert.False(string.IsNullOrWhiteSpace(handshake.Guidance));
    }

    [Fact]
    public async Task InitializeAsync_TransportClosed_ReportsDegradedTransportFailure()
    {
        var client = new CursorAcpClient(FakeJsonRpcTransport.FailingWith(
            new JsonRpcTransportException(JsonRpcTransportFailureKind.TransportClosed, "eof")));

        var handshake = await client.InitializeAsync();

        Assert.Equal(CursorAcpHandshakeFailureKind.TransportFailure, handshake.FailureKind);
    }

    [Fact]
    public async Task InitializeAsync_UnexpectedTransportException_IsMappedWithoutThrowing()
    {
        var client = new CursorAcpClient(FakeJsonRpcTransport.FailingWith(new IOException("broken pipe")));

        var handshake = await client.InitializeAsync();

        Assert.True(handshake.IsDegraded);
        Assert.Equal(CursorAcpHandshakeFailureKind.TransportFailure, handshake.FailureKind);
        Assert.Contains("broken pipe", handshake.Blocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_CallerCancellation_PropagatesOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var client = new CursorAcpClient(
            FakeJsonRpcTransport.FailingWith(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.InitializeAsync(cancellation.Token));
    }

    [Fact]
    public async Task InitializeAsync_DegradedResult_DoesNotMentionCliPrintModeFallback()
    {
        var result = CursorAcpTestData.CreateHandshakeResult(node => node["protocolVersion"] = 2);
        var client = new CursorAcpClient(FakeJsonRpcTransport.RespondingWith(result));

        var handshake = await client.InitializeAsync();

        Assert.DoesNotContain("--print", handshake.Blocker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--print", handshake.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no silent cli print-mode fallback", handshake.Guidance, StringComparison.OrdinalIgnoreCase);
    }
}
