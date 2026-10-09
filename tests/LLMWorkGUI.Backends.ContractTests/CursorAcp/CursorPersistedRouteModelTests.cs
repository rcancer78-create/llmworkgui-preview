using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorPersistedRouteModelTests
{
    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"currentModelId\":\"native-model\"}", true)]
    [InlineData("{\"currentModelId\":\"other\"}", false)]
    [InlineData("null", false)]
    public async Task PersistedRoute_RequiresModelAckBeforePrompt(string modelResponse, bool succeeds)
    {
        var methods = new List<string>();
        var transport = FakeJsonRpcTransport.RespondingByMethod(method =>
        {
            methods.Add(method);
            return FakeJsonRpcTransport.CreateResultResponse(method == "initialize" ? CursorAcpTestData.ReadHandshakeResult()
                : JsonDocument.Parse(method == "session/set_model" ? modelResponse : "{\"stopReason\":\"end_turn\"}").RootElement.Clone());
        });
        var client = new CursorAcpClient(transport); await client.InitializeAsync();
        var result = await client.PromptAsync(CursorAcpPromptRequest.Create("s", "hello", "native-model") with { RequireModelAcknowledgement = true });
        Assert.Equal(succeeds, result.IsSuccess);
        Assert.Equal(succeeds, methods.Contains("session/prompt"));
        Assert.Equal("session/set_model", methods[1]);
    }
}
