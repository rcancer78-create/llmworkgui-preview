using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeMessageErrorShapeDeltaTests
{
    [Theory]
    [InlineData("message")]
    [InlineData("error")]
    [InlineData("error-data")]
    public async Task BoundAssistantRequiresAnErrorFieldOrConfirmedCompletionRatherThanUnknownMessageMetadata(string shape)
    {
        using var directory = new TestDirectory();
        var events = new ErrorShapeEvents(shape);
        var sends = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/session")
                return Task.FromResult(StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
                {
                    id = "ses_owned", slug = "owned", projectID = "owned-project", directory = directory.Root,
                    title = "Owned", version = "1.18.31", time = new { created = 1, updated = 1 }
                })));
            Assert.Equal("/session/ses_owned/prompt_async", request.RequestUri.AbsolutePath);
            Interlocked.Increment(ref sends);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new OpenCodeSessionLifecycleService(new OpenCodeClient(http,
            new Uri("http://127.0.0.1:4970"), eventStreamService: events));
        var session = await service.CreateAndConfirmSessionAsync(new() { Directory = directory.Root, Model = "model" });
        var result = await service.ExecuteTurnAsync(session.Id,
            new() { MessageId = "msg_owned", Prompt = "Owned synthetic input", Model = "model" }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, sends);
        Assert.True(events.Disposed);
        if (shape == "message")
        {
            Assert.True(events.CompletionYielded, "Unknown info.message metadata must not end an in-progress assistant.");
            Assert.Equal(TurnResult.CompletedStatus, result.Status);
            Assert.Equal("owned reply", result.OutputText);
            Assert.Null(result.ErrorMessage);
        }
        else
        {
            Assert.False(events.CompletionYielded);
            Assert.Equal(TurnResult.FailedStatus, result.Status);
            Assert.Equal("owned explicit error", result.ErrorMessage);
        }
    }

    private sealed class ErrorShapeEvents(string shape) : IOpenCodeEventStreamService
    {
        public bool IsOverflowed => false;
        public string? SpoolFilePath => null;
        public bool CompletionYielded { get; private set; }
        public bool Disposed { get; private set; }
        public IReadOnlyList<OpenCodeEventEnvelope> GetRecentEvents(int? maxCount = null) => [];
        public async IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeAsync(string? sessionId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            try
            {
                yield return Event("server.connected", new { });
                var info = new Dictionary<string, object?>
                {
                    ["id"] = "msg_assistant", ["sessionID"] = "ses_owned", ["role"] = "assistant",
                    ["parentID"] = "msg_owned", ["time"] = new { created = 1 }
                };
                if (shape == "message") info["message"] = "owned nonterminal metadata";
                else if (shape == "error") info["error"] = new { name = "OwnedError", message = "owned explicit error" };
                else info["error"] = new { name = "OwnedError", data = new { message = "owned explicit error" } };
                yield return Event("message.updated", new { info });
                cancellationToken.ThrowIfCancellationRequested();
                yield return Event("message.part.updated", new
                {
                    part = new { id = "part_owned", sessionID = "ses_owned", messageID = "msg_assistant", type = "text", text = "owned reply" }
                });
                CompletionYielded = true;
                yield return Event("message.updated", new
                {
                    info = new { id = "msg_assistant", sessionID = "ses_owned", role = "assistant", parentID = "msg_owned",
                        finish = "stop", time = new { created = 1, completed = 2 } }
                });
            }
            finally { Disposed = true; }
        }
        private static OpenCodeEventEnvelope Event(string type, object properties) =>
            new(type, JsonSerializer.SerializeToElement(properties), "Owned synthetic event", DateTime.UtcNow);
    }
}
