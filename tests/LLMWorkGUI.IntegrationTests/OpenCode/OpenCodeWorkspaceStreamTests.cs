using System.Net;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Events;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeWorkspaceStreamTests
{
    private const string Connected = """{"type":"server.connected","properties":{}}""";
    private const string Idle = """{"type":"session.idle","properties":{"sessionID":"ses_stream"}}""";
    private const string Assistant = """{"type":"message.updated","properties":{"info":{"id":"msg_assistant","sessionID":"ses_stream","role":"assistant","parentID":"msg_user"}}}""";
    private const string Finished = """{"type":"message.updated","properties":{"info":{"id":"msg_assistant","sessionID":"ses_stream","role":"assistant","parentID":"msg_user","finish":"stop","time":{"completed":1}}}}""";
    private const string User = """{"type":"message.updated","properties":{"info":{"id":"msg_user","sessionID":"ses_stream","role":"user"}}}""";
    private const string UserText = """{"type":"message.part.updated","properties":{"part":{"id":"prt_user","messageID":"msg_user","sessionID":"ses_stream","type":"text","text":"Do not echo this prompt"}}}""";
    private const string EmptyAssistantText = """{"type":"message.part.updated","properties":{"part":{"id":"prt_text","messageID":"msg_assistant","sessionID":"ses_stream","type":"text","text":""}}}""";
    private const string Delta = """{"type":"message.part.delta","properties":{"sessionID":"ses_stream","messageID":"msg_assistant","partID":"prt_text","field":"text","delta":"answer"}}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserParts_AreExcludedEvenWhenRoleMetadataArrivesLater(bool roleAfterPart)
    {
        var events = new[] { Connected, roleAfterPart ? UserText : User, roleAfterPart ? User : UserText, Assistant, EmptyAssistantText, Delta, Finished, Idle };
        var result = await ExecuteAsync(events);
        Assert.Equal(TurnResult.CompletedStatus, result.Status);
        Assert.Equal("answer", result.OutputText);
    }

    [Theory]
    [InlineData("sessionID", "ses_foreign")]
    [InlineData("messageID", "msg_user")]
    [InlineData("partID", "prt_foreign")]
    [InlineData("field", "reasoning")]
    public async Task ForeignOrNonTextDeltas_DoNotChangeAssistantOutput(string field, string invalidValue)
    {
        using var delta = JsonDocument.Parse(Delta);
        var properties = delta.RootElement.GetProperty("properties").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString());
        properties[field] = invalidValue;
        properties["delta"] = "wrong";
        var invalidDelta = JsonSerializer.Serialize(new { type = "message.part.delta", properties });
        var result = await ExecuteAsync(new[] { Connected, Assistant, EmptyAssistantText, Delta, invalidDelta, Finished, Idle });
        Assert.Equal("answer", result.OutputText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeApiError_ExposesNestedReasonWithoutEchoingTheUserPrompt(bool messageError)
    {
        var failure = messageError
            ? """{"type":"message.updated","properties":{"info":{"id":"msg_assistant","sessionID":"ses_stream","role":"assistant","parentID":"msg_user","error":{"name":"APIError","data":{"statusCode":402,"message":"Payment required"}}}}}"""
            : """{"type":"session.error","properties":{"sessionID":"ses_stream","error":{"name":"APIError","data":{"statusCode":402,"message":"Payment required"}}}}""";
        var result = await ExecuteAsync(new[] { Connected, User, UserText, failure, Idle });
        Assert.Equal(TurnResult.FailedStatus, result.Status);
        Assert.Equal("Payment required", result.ErrorMessage);
        Assert.Empty(result.OutputText);
    }

    [Fact]
    public async Task CapturedNativeWorkspaceEvents_ProduceOnlyAssistantReplyAndReportedUsage()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LLMWorkGUI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "docs/protocols/opencode/workspace-sse-sample-20261002.json")));
        var result = await ExecuteAsync(sample.RootElement.EnumerateArray().Select(item => item.GetRawText()).ToArray());
        Assert.Equal(TurnResult.CompletedStatus, result.Status);
        Assert.Equal("OPENCODE_WORKSPACE_OK", result.OutputText);
        Assert.NotNull(result.Tokens);
        Assert.True(result.Tokens.Input > 0);
        Assert.Empty(result.ToolCalls);
    }

    private static async Task<TurnResult> ExecuteAsync(string[] events)
    {
        using var http = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(path switch
            {
                "/session" => StubHttpMessageHandler.Json("""{"id":"ses_stream"}"""),
                "/session/ses_stream/prompt_async" => StubHttpMessageHandler.Json("true"),
                "/event" => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(string.Join("", events.Select(item => "data: " + item.Replace("\r", "").Replace("\n", "") + "\n\n")), Encoding.UTF8, "text/event-stream") },
                _ => throw new InvalidOperationException("Unexpected endpoint: " + path)
            });
        }));
        var endpoint = new Uri("http://127.0.0.1:54321");
        var stream = new OpenCodeEventStreamService(http, endpoint);
        var service = new OpenCodeSessionLifecycleService(new OpenCodeClient(http, endpoint, eventStreamService: stream));
        await service.CreateAndConfirmSessionAsync(new OpenCodeCreateSessionRequest { Model = "provider/native-model" });
        return await service.ExecuteTurnAsync("ses_stream", new OpenCodePromptRequest { MessageId = "msg_user", Prompt = "Do not echo this prompt", Model = "provider/native-model" });
    }
}
