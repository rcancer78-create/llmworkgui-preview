using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpSessionModeDiscoveryTests
{
    [Theory]
    [InlineData("\"modes\":{\"availableModes\":[{\"id\":\"ask\"},{\"id\":\"plan\"},{\"id\":\"agent\"}]}", true)]
    [InlineData("\"modes\":{\"currentModeId\":\"ask\"}", false)]
    [InlineData("\"modes\":{\"availableModes\":null}", false)]
    [InlineData("\"modes\":{\"availableModes\":[{\"id\":\"ask\",\"id\":\"agent\"}]}", false)]
    [InlineData("\"modes\":{\"availableModes\":[{\"id\":\"ask\"},{\"id\":\"ask\"}]}", false)]
    [InlineData("\"modes\":{\"availableModes\":[{\"id\":\" ask \"}]}", false)]
    [InlineData("\"modes\":{},\"modes\":{\"availableModes\":[{\"id\":\"ask\"}]}", false)]
    public async Task Discovery_RequiresUnambiguousList_AndDoesNotBreakSession(string fields, bool supported)
    {
        var transport = FakeJsonRpcTransport.RespondingByMethod(method => FakeJsonRpcTransport.CreateResultResponse(
            method == "initialize" ? CursorAcpTestData.ReadHandshakeResult() : JsonDocument.Parse("{\"sessionId\":\"s\"," + fields + "}").RootElement.Clone()));
        var client = new CursorAcpClient(transport);
        await client.InitializeAsync();
        var result = await client.CreateSessionAsync(new CursorAcpNewSessionRequest { WorkingDirectory = Path.GetTempPath() });
        Assert.True(result.IsReady);
        if (supported) Assert.Equal(new[] { "ask", "plan", "agent" }, result.Evidence!.AvailableModeIds);
        else Assert.Null(result.Evidence!.AvailableModeIds);
    }
}
