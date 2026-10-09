using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpNativeLoadTests
{
    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"modes\":{\"currentModeId\":\"ask\"}}", true)]
    [InlineData("{\"sessionId\":\"native-session\"}", true)]
    [InlineData("{\"sessionId\":\"different\"}", false)]
    [InlineData("{\"id\":null}", false)]
    [InlineData("{\"sessionId\":42}", false)]
    [InlineData("{\"sessionId\":\"different\",\"sessionId\":\"native-session\"}", false)]
    [InlineData("null", false)]
    [InlineData("[]", false)]
    public async Task Load_AcknowledgementUsesRequestedIdentity_AndRejectsContradictions(string payload, bool accepted)
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => FakeJsonRpcTransport.CreateResultResponse(
            method == "initialize" ? CursorAcpTestData.ReadHandshakeResult() : JsonDocument.Parse(payload).RootElement.Clone()));
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest { SessionId = "native-session", WorkingDirectory = Path.GetTempPath() });
        Assert.Equal(accepted, result.IsReady);
        if (accepted) Assert.Equal("native-session", result.Evidence!.SessionId);
        else { Assert.Null(result.Evidence); Assert.Equal(CursorAcpSessionFailureKind.MalformedResponse, result.FailureKind); }
        Assert.Equal(new[] { "initialize", "session/load" }, transport.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task Load_AgentErrorNeverConfirmsRequestedIdentity()
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => method == "initialize"
            ? FakeJsonRpcTransport.CreateResultResponse(CursorAcpTestData.ReadHandshakeResult())
            : new JsonRpcResponse { Id = JsonSerializer.SerializeToElement(2), Error = new JsonRpcError { Code = -32602, Message = "unknown session" } });
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        var result = await client.LoadSessionAsync(new CursorAcpLoadSessionRequest { SessionId = "native-session", WorkingDirectory = Path.GetTempPath() });
        Assert.False(result.IsReady);
        Assert.Null(result.Evidence);
    }
}
