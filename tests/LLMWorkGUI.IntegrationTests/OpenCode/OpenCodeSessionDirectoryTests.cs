using System.Net;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Events;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeSessionDirectoryTests
{
    [Theory]
    [InlineData(@"D:\projects\first folder")]
    [InlineData(@"D:\проект\a+b & c#d")]
    [InlineData(@"\\server\share\project")]
    public async Task RequestsAndEvents_KeepSeparateSessionDirectories_AndUseNativeCreateModelSchema(string firstDirectory)
    {
        const string secondDirectory = @"D:\other\project";
        var directories = new Dictionary<string, string> { ["ses_first"] = firstDirectory, ["ses_second"] = secondDirectory, ["ses_fork"] = firstDirectory };
        var scopedRequests = new List<(string Path, string Directory)>();
        var createCount = 0;
        using var http = new HttpClient(new StubHttpMessageHandler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            Assert.StartsWith("?directory=", uri.Query, StringComparison.Ordinal);
            var directory = Uri.UnescapeDataString(uri.Query["?directory=".Length..]);
            scopedRequests.Add((uri.AbsolutePath, directory));
            var path = uri.AbsolutePath;
            if (path == "/event")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"server.connected\",\"properties\":{}}\n\n", Encoding.UTF8, "text/event-stream") };
            if (path == "/session")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var root = body.RootElement;
                Assert.False(root.TryGetProperty("directory", out _));
                var model = root.GetProperty("model");
                Assert.Equal("provider", model.GetProperty("providerID").GetString());
                Assert.Equal("native-model", model.GetProperty("id").GetString());
                Assert.False(model.TryGetProperty("modelID", out _));
                Assert.Equal(2, model.EnumerateObject().Count());
                var id = createCount++ == 0 ? "ses_first" : "ses_second";
                Assert.Equal(directories[id], directory);
                return StubHttpMessageHandler.Json(JsonSerializer.Serialize(new { id, directory }));
            }
            var sessionId = path.Split('/')[2];
            Assert.Equal(directories[sessionId], directory);
            if (path.EndsWith("/prompt_async", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("native-model", body.RootElement.GetProperty("model").GetProperty("modelID").GetString());
                return StubHttpMessageHandler.Json("true");
            }
            if (path.EndsWith("/abort", StringComparison.Ordinal)) return StubHttpMessageHandler.Json("true");
            var returnedId = path.EndsWith("/fork", StringComparison.Ordinal) ? "ses_fork" : sessionId;
            return StubHttpMessageHandler.Json(JsonSerializer.Serialize(new { id = returnedId, directory }));
        }));
        var endpoint = new Uri("http://127.0.0.1:54321");
        var stream = new OpenCodeEventStreamService(http, endpoint);
        var client = new OpenCodeClient(http, endpoint, eventStreamService: stream);
        await client.CreateSessionAsync(new OpenCodeCreateSessionRequest { Directory = firstDirectory, Model = "provider/native-model" });
        await client.CreateSessionAsync(new OpenCodeCreateSessionRequest { Directory = secondDirectory, Model = "provider/native-model" });
        foreach (var id in new[] { "ses_first", "ses_second" })
        {
            await client.GetSessionAsync(id);
            await client.SendPromptAsync(id, new OpenCodePromptRequest { Prompt = "hello", Model = "provider/native-model" });
            await using var events = client.SubscribeEventsAsync(id).GetAsyncEnumerator();
            Assert.True(await events.MoveNextAsync());
            Assert.Equal(directories[id], scopedRequests.Last().Directory);
            await client.AbortSessionAsync(id);
        }
        await client.ForkSessionAsync("ses_first");
        await client.GetSessionAsync("ses_fork");
        Assert.Equal(12, scopedRequests.Count);
    }
}
