using System.Net;
using System.Text;
using System.Text.Json;
using LLMGateway.Core;
using LLMGateway.Core.Client;

namespace LLMGateway.Tests;

public sealed class RemoteOriginAndAuthorizationReviewTests
{
    [Theory]
    [InlineData(false, "absent", 6)]
    [InlineData(true, "absent", 6)]
    [InlineData(false, "missing-provider", 6)]
    [InlineData(true, "missing-provider", 6)]
    [InlineData(false, "codex", 0)]
    [InlineData(true, "codex", 0)]
    [InlineData(false, "grok", 3)]
    [InlineData(true, "grok", 3)]
    public async Task ResponseOnlyAttributesProviderActuallyPresentOnWire(bool stream, string metadata, int provider)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = "chatcmpl-owned", ["created"] = 1, ["model"] = "observed-model",
            ["choices"] = stream
                ? new[] { new { index = 0, delta = new { content = "owned answer" }, finish_reason = "stop" } }
                : (object)new[] { new { index = 0, message = new { role = "assistant", content = "owned answer" }, finish_reason = "stop" } }
        };
        if (metadata != "absent")
        {
            var info = new Dictionary<string, object?> { ["account_id"] = "observed-account", ["native_model"] = "observed-native" };
            if (metadata != "missing-provider") info["provider"] = metadata;
            payload["x_gateway"] = info;
        }
        var json = JsonSerializer.Serialize(payload);
        var handler = new Handler(stream ? "data: " + json + "\n\ndata: [DONE]\n\n" : json, stream);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://owned-gateway.invalid/") };
        using var gateway = new OpenAiGatewayClient(http);
        ChatResult result;
        if (stream)
        {
            var updates = new List<ChatUpdate>();
            await foreach (var update in gateway.StreamAsync(Request())) updates.Add(update);
            result = Assert.Single(updates, update => update.Kind == ChatUpdateKind.Completed).Result!;
        }
        else result = await gateway.CompleteAsync(Request());
        Assert.Equal((ProviderKind)provider, result.Provider);
        Assert.Equal(metadata == "absent" ? string.Empty : "observed-account", result.AccountId);
        Assert.Equal(metadata == "absent" ? string.Empty : "observed-native", result.NativeModel);
        Assert.Equal("observed-model", result.Model);
        Assert.Equal("owned answer", result.Content);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("stream")]
    [InlineData("wire")]
    public async Task LocalAuthorizationCannotBeDroppedAtHttpBoundary(string entry)
    {
        var handler = new Handler("{}", false);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://owned-gateway.invalid/") };
        using var gateway = new OpenAiGatewayClient(http);
        var authorization = new TrapAuthorization();
        var request = Request(); request.DispatchAuthorization = authorization;
        var error = await Record.ExceptionAsync(async () =>
        {
            if (entry == "complete") await gateway.CompleteAsync(request);
            else if (entry == "stream") { await foreach (var _ in gateway.StreamAsync(request)) { } }
            else OpenAiGatewayClient.ToWire(request, false);
        });
        Assert.Equal(GatewayErrorKind.Unsupported, Assert.IsType<GatewayException>(error).Kind);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, authorization.Calls);
        Assert.Same(authorization, request.DispatchAuthorization);
        Assert.Null(request.ExecutionContext);
    }

    [Fact]
    public async Task RemoteUnknownCannotBecomeStoredAccountOrExactExecutionBinding()
    {
        var unknown = (ProviderKind)6;
        Assert.Equal(GatewayErrorKind.InvalidRequest, Assert.Throws<GatewayException>(() =>
            JsonAccountStore.InMemory([new() { Id = "owned", Provider = unknown }])).Kind);
        using var gateway = new LlmGateway(JsonAccountStore.InMemory([]), [], new GatewayOptions { DiscoverProfiles = false });
        var request = Request();
        request.AccountId = "owned";
        request.ExecutionContext = new(unknown, "owned", "owned-model", Environment.CurrentDirectory);
        Assert.Equal(GatewayErrorKind.InvalidRequest,
            (await Assert.ThrowsAsync<GatewayException>(() => gateway.CompleteAsync(request))).Kind);
    }

    private static ChatRequest Request() => new() { Model = "requested-model", Messages = [ChatMessage.User("owned request")] };
    private sealed class Handler(string payload, bool stream) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(payload, Encoding.UTF8, stream ? "text/event-stream" : "application/json") });
        }
    }
    private sealed class TrapAuthorization : INativeDispatchAuthorization
    {
        public int Calls;
        public DateTimeOffset ExpiresAtUtc { get { Calls++; throw new InvalidOperationException("Local authority cannot be exported"); } }
        public Task ValidatePreparedAsync(AccountProfile account, NativeChatRequest request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Local authority cannot be used by HTTP"); }
        public Task AuthorizeTransportAsync(AccountProfile account, NativeChatRequest request, string wire, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Local authority cannot be used by HTTP"); }
    }
}
