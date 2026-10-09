using System.Net;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodePermissionTransportTests
{
    [Theory]
    [InlineData("once", true, "ses_owned")]
    [InlineData("reject", true, "ses_owned")]
    [InlineData("custom", false, "ses_owned")]
    [InlineData("once", false, "ses_unknown")]
    public async Task InvalidScopeOrReply_IsRefusedBeforeHttp(string response, bool remember, string scope)
    {
        var posts = 0;
        using var http = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json("{\"id\":\"ses_owned\",\"directory\":\"D:/owned\"}"));
            posts++; throw new InvalidOperationException("Invalid request must never reach transport.");
        }));
        var client = new OpenCodeClient(http, new Uri("http://127.0.0.1:54321"));
        await client.CreateSessionAsync(new OpenCodeCreateSessionRequest { Directory = "D:/owned" });
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReplyPermissionAsync("per_fixture",
            new() { Response = response, Remember = remember, ScopeSessionId = scope }));
        Assert.Equal(0, posts);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData(" false ", false)]
    [InlineData("", false)]
    [InlineData("{}", false)]
    [InlineData("\"true\"", false)]
    public async Task Acknowledgement_RequiresNativeBooleanTrue(string body, bool acknowledged)
    {
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(StubHttpMessageHandler.Json(body))));
        var client = new OpenCodeClient(http, new Uri("http://127.0.0.1:54321"));
        Assert.Equal(acknowledged, await client.ReplyPermissionAsync("per_fixture", new() { Response = "reject" }));
    }
}
