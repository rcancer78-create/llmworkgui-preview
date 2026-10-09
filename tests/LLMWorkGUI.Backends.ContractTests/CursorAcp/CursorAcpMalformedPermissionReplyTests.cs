using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class CursorAcpMalformedPermissionReplyTests
{
    [Theory]
    [InlineData("7")]
    [InlineData("\"7\"")]
    public async Task MalformedRequestReceivesOneExactTypedErrorWithoutPermissionGrant(string rawId)
    {
        var transport = FakeJsonRpcTransport.RespondingWith(CursorAcpTestData.ReadHandshakeResult());
        var client = new CursorAcpClient(transport);
        Assert.True((await client.InitializeAsync()).IsReady);
        using var id = JsonDocument.Parse(rawId);
        transport.PublishNotification(new JsonRpcNotification
        {
            Method = "session/request_permission", Id = id.RootElement.Clone(),
            Parameters = JsonSerializer.SerializeToElement(new
                { sessionId = "session", toolCall = new { title = "synthetic-private-prompt" }, options = Array.Empty<object>() })
        });
        transport.CompleteNotifications();
        await foreach (var unexpected in client.SubscribeEventsAsync())
            Assert.Fail("Malformed permission must not reach approval UI.");

        var reply = Assert.Single(transport.Responses);
        Assert.Equal(rawId, reply.Id.GetRawText());
        Assert.Null(reply.Result);
        Assert.NotNull(reply.Error);
        Assert.Equal(-32602, reply.Error!.Code);
        Assert.DoesNotContain("synthetic-private-prompt", reply.Error.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.SkippedStreamEventCount);
        Assert.False((await client.ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
            { PermissionId = "7", SessionId = "session", Decision = CursorAcpPermissionDecision.AllowOnce })).IsSent);
        Assert.Single(transport.Responses);
    }
}
