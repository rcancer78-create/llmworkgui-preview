using System.Net;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.StarCliProxy;

public sealed class StarCliProxyClientTests
{
    private static readonly StarCliProxyEndpoint Endpoint =
        StarCliProxyEndpoint.Loopback(8300, "sk-proxy-test");

    [Fact]
    public async Task CheckHealth_ReportsHealthyWithModelCount()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(JsonResponse(
            """{"object":"list","data":[{"id":"gpt-5.5"},{"id":"antigravity"}]}""")));

        var client = CreateClient(handler);

        var health = await client.CheckHealthAsync(Endpoint);

        Assert.True(health.IsHealthy);
        Assert.Equal(2, health.ModelCount);
        Assert.Null(health.Detail);
    }

    [Fact]
    public async Task CheckHealth_Unauthorized_ReportsApiKeyRejection()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            JsonResponse("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized)));

        var client = CreateClient(handler);

        var health = await client.CheckHealthAsync(Endpoint);

        Assert.False(health.IsHealthy);
        Assert.Contains("proxy API key", health.Detail);
        Assert.Contains("401", health.Detail);
    }

    [Fact]
    public async Task CheckHealth_Unreachable_ReportsUnhealthy()
    {
        var handler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("connection refused"));

        var client = CreateClient(handler);

        var health = await client.CheckHealthAsync(Endpoint);

        Assert.False(health.IsHealthy);
        Assert.Contains("unreachable on loopback", health.Detail);
    }

    [Fact]
    public async Task CheckHealth_Timeout_ReportsUnhealthyAndHonoursCancellation()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

            return JsonResponse("""{"object":"list","data":[]}""");
        });

        var client = CreateClient(
            handler,
            new StarCliProxyOptions { HealthCheckTimeout = TimeSpan.FromMilliseconds(80) });

        var health = await client.CheckHealthAsync(Endpoint);

        Assert.False(health.IsHealthy);
        Assert.Contains("timed out", health.Detail);
    }

    [Fact]
    public async Task ListModels_ParsesCatalogAndSendsBearerAuthorization()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(JsonResponse(
            """
            {
              "object": "list",
              "data": [
                {"id": "gpt-5.5", "object": "model", "owned_by": "codex", "provider": "codex", "created": 1700000000},
                {"id": "antigravity", "object": "model", "provider": "agy"}
              ]
            }
            """)));

        var client = CreateClient(handler);

        var models = await client.ListModelsAsync(Endpoint);

        Assert.Equal(2, models.Count);
        Assert.Equal("gpt-5.5", models[0].Id);
        Assert.Equal("codex", models[0].ProviderId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), models[0].CreatedAtUtc!.Value);
        Assert.Equal("antigravity", models[1].Id);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://127.0.0.1:8300/v1/models", request.RequestUri);
        Assert.Equal("Bearer sk-proxy-test", request.Authorization);
    }

    [Fact]
    public async Task ListModels_NonSuccess_ThrowsClientException()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            JsonResponse("""{"error":"boom"}""", HttpStatusCode.InternalServerError)));

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<StarCliProxyClientException>(
            () => client.ListModelsAsync(Endpoint));

        Assert.Contains("500", exception.Message);
    }

    [Fact]
    public async Task StreamChat_NormalizesSseEventsWithEvidence()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(SseResponse(
            """{"id":"chunk-1","model":"gpt-5.5","choices":[{"delta":{"role":"assistant"}}]}""",
            """{"id":"chunk-2","model":"gpt-5.5","choices":[{"delta":{"reasoning_content":"thinking"}}]}""",
            """{"id":"chunk-3","model":"gpt-5.5","choices":[{"delta":{"content":"Hello"}}]}""",
            """{"id":"chunk-4","model":"gpt-5.5","choices":[{"delta":{"content":" world"}}]}""",
            """{"id":"chunk-5","model":"gpt-5.5","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}]}}]}""",
            """{"id":"chunk-6","model":"gpt-5.5","choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2}}""",
            "[DONE]")));

        var client = CreateClient(handler);

        var events = await CollectAsync(client.StreamChatCompletionAsync(
            Endpoint,
            StarCliProxyChatRequest.Create(
                "gpt-5.5",
                [new StarCliProxyChatMessage("user", "hi")],
                requestedProviderId: "codex",
                requestedAccountId: "work",
                requestedSessionId: "session-abc")));

        Assert.Equal(
            new[]
            {
                StarCliProxyStreamEventKind.ReasoningDelta,
                StarCliProxyStreamEventKind.ContentDelta,
                StarCliProxyStreamEventKind.ContentDelta,
                StarCliProxyStreamEventKind.ToolCall,
                StarCliProxyStreamEventKind.Usage,
                StarCliProxyStreamEventKind.Completed
            },
            events.Select(streamEvent => streamEvent.Kind).ToArray());

        Assert.Equal("thinking", events[0].Content);
        Assert.Equal("Hello", events[1].Content);
        Assert.Equal(" world", events[2].Content);
        Assert.Equal("read_file", events[3].ToolName);

        Assert.Equal(10, events[4].PromptTokens);
        Assert.Equal(2, events[4].CompletionTokens);
        Assert.Equal("stop", events[5].FinishReason);

        // Provider/account/session claims come from the loopback headers and the model claim from the
        // echoed body, but none of them is verified: a matching request value cannot promote a
        // response claim to observed native identity.
        var last = events[^1];
        Assert.Equal("codex", last.Evidence.ProviderId);
        Assert.Equal("work", last.Evidence.AccountId);
        Assert.Equal("session-abc", last.Evidence.SessionId);
        Assert.Equal("gpt-5.5", last.Evidence.ModelId);

        Assert.All(events, streamEvent =>
        {
            Assert.False(streamEvent.Evidence.IsProviderVerified);
            Assert.False(streamEvent.Evidence.IsAccountVerified);
            Assert.False(streamEvent.Evidence.IsModelVerified);
            Assert.False(streamEvent.Evidence.IsSessionVerified);
        });
    }

    [Fact]
    public async Task StreamChat_RequestCarriesModelMessagesStreamFlagAndSessionHeader()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(SseResponse("[DONE]")));
        var client = CreateClient(handler);

        await CollectAsync(client.StreamChatCompletionAsync(
            Endpoint,
            StarCliProxyChatRequest.Create(
                "gpt-5.5",
                [new StarCliProxyChatMessage("system", "be brief"), new StarCliProxyChatMessage("user", "hi")],
                requestedSessionId: "session-42",
                reasoningEffort: "high")));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:8300/v1/chat/completions", request.RequestUri);
        Assert.Equal("session-42", request.SessionHeader);
        Assert.Equal("text/event-stream", request.Accept);

        using var document = JsonDocument.Parse(request.Body!);
        var root = document.RootElement;

        Assert.Equal("gpt-5.5", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("be brief", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());

        // Secrets never appear in the request body.
        Assert.DoesNotContain("sk-proxy-test", request.Body);
    }

    [Fact]
    public async Task StreamChat_MalformedChunk_YieldsStructuredMalformedEvent()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(SseResponse(
            "{not-json",
            "[DONE]")));

        var client = CreateClient(handler);

        var events = await CollectAsync(client.StreamChatCompletionAsync(
            Endpoint,
            CreateRequest()));

        Assert.Equal(StarCliProxyStreamEventKind.Malformed, events[0].Kind);
        Assert.Equal("{not-json", events[0].RawJson);
        Assert.Equal(StarCliProxyStreamEventKind.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task StreamChat_ErrorObject_YieldsErrorEvent()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(SseResponse(
            """{"error":{"message":"provider quota exceeded"}}""",
            "[DONE]")));

        var client = CreateClient(handler);

        var events = await CollectAsync(client.StreamChatCompletionAsync(Endpoint, CreateRequest()));

        Assert.Equal(StarCliProxyStreamEventKind.Error, events[0].Kind);
        Assert.Contains("provider quota exceeded", events[0].ErrorMessage);
    }

    [Fact]
    public async Task StreamChat_NonSuccessStatus_YieldsErrorEventWithBoundedBody()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(
            JsonResponse("""{"error":"rate limited"}""", HttpStatusCode.TooManyRequests)));

        var client = CreateClient(handler);

        var events = await CollectAsync(client.StreamChatCompletionAsync(Endpoint, CreateRequest()));

        var error = Assert.Single(events);
        Assert.Equal(StarCliProxyStreamEventKind.Error, error.Kind);
        Assert.Contains("429", error.ErrorMessage);
        Assert.Contains("rate limited", error.RawJson);
    }

    [Fact]
    public async Task StreamChat_JsonResponseWithoutEventStream_ParsesMessageContent()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(JsonResponse(
            """
            {
              "id": "chatcmpl-1",
              "model": "gpt-5.5",
              "choices": [
                {"index": 0, "message": {"role": "assistant", "content": "Hi there"}, "finish_reason": "stop"}
              ],
              "usage": {"prompt_tokens": 3, "completion_tokens": 2}
            }
            """)));

        var client = CreateClient(handler);

        var events = await CollectAsync(client.StreamChatCompletionAsync(Endpoint, CreateRequest()));

        Assert.Equal(StarCliProxyStreamEventKind.ContentDelta, events[0].Kind);
        Assert.Equal("Hi there", events[0].Content);
        Assert.Contains(events, streamEvent => streamEvent.Kind == StarCliProxyStreamEventKind.Usage);
        Assert.Contains(events, streamEvent => streamEvent.Kind == StarCliProxyStreamEventKind.Completed);
    }

    [Fact]
    public async Task StreamChat_SessionRouteMismatch_TerminatesWithErrorAndNoCompleted()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(SseResponse(
            """{"id":"chunk-1","model":"gpt-5.5","choices":[{"delta":{"content":"Hello"}}]}""",
            """{"id":"chunk-2","model":"gpt-5.5","choices":[{"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]")));

        var client = CreateClient(handler);

        var events = await CollectAsync(client.StreamChatCompletionAsync(
            Endpoint,
            StarCliProxyChatRequest.Create(
                "gpt-5.5",
                [new StarCliProxyChatMessage("user", "hi")],
                requestedProviderId: "codex",
                requestedAccountId: "work",
                requestedSessionId: "session-other")));

        var error = Assert.Single(events);
        Assert.Equal(StarCliProxyStreamEventKind.Error, error.Kind);
        Assert.Contains("route mismatch", error.ErrorMessage);
        Assert.Contains("Observed session 'session-abc'", error.ErrorMessage);
        Assert.DoesNotContain(events, streamEvent => streamEvent.Kind == StarCliProxyStreamEventKind.Completed);
    }

    [Fact]
    public async Task StreamChat_JsonResponseRouteMismatch_TerminatesWithError()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(JsonResponse(
            """
            {
              "id": "chatcmpl-1",
              "model": "gpt-5.5-mini",
              "choices": [
                {"index": 0, "message": {"role": "assistant", "content": "Hi there"}, "finish_reason": "stop"}
              ]
            }
            """)));

        var client = CreateClient(handler);

        var events = await CollectAsync(client.StreamChatCompletionAsync(Endpoint, CreateRequest()));

        var error = Assert.Single(events);
        Assert.Equal(StarCliProxyStreamEventKind.Error, error.Kind);
        Assert.Contains("Observed model 'gpt-5.5-mini'", error.ErrorMessage);
        Assert.DoesNotContain(events, streamEvent => streamEvent.Kind == StarCliProxyStreamEventKind.Completed);
    }

    [Fact]
    public async Task StreamChat_Cancellation_StopsEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var handler = new StubHttpMessageHandler(_ => Task.FromResult(SseResponse("[DONE]")));
        var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamChatCompletionAsync(Endpoint, CreateRequest(), cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public void RouteEvidence_DetectsMismatchAndKeepsUnknownOpaque()
    {
        var request = StarCliProxyChatRequest.Create(
            "gpt-5.5",
            [new StarCliProxyChatMessage("user", "hi")],
            requestedProviderId: "codex",
            requestedAccountId: "codex:work");

        var matching = StarCliProxyRouteEvidence.ForRequest(
            request,
            new StarCliProxyObservedEvidence("codex", "codex:work", "gpt-5.5", "session-1"));

        Assert.False(matching.HasMismatch);

        // Claims that agree with the request are still only claims, so completeness is never
        // reported: a gateway that echoed back the model we sent produces the same evidence.
        Assert.False(matching.IsFullyObserved);

        var mismatched = StarCliProxyRouteEvidence.ForRequest(
            request,
            new StarCliProxyObservedEvidence("agy", "codex:work", "gemini-3.1-pro", "session-1"));

        Assert.True(mismatched.HasMismatch);
        Assert.Contains("Observed provider 'agy'", mismatched.MismatchReason);

        var opaque = StarCliProxyRouteEvidence.ForRequest(request, StarCliProxyObservedEvidence.Empty);

        Assert.False(opaque.HasMismatch);
        Assert.False(opaque.IsFullyObserved);

        var sessionRequest = StarCliProxyChatRequest.Create(
            "gpt-5.5",
            [new StarCliProxyChatMessage("user", "hi")],
            requestedProviderId: "codex",
            requestedAccountId: "work",
            requestedSessionId: "session-1");

        var sessionMismatch = StarCliProxyRouteEvidence.ForRequest(
            sessionRequest,
            new StarCliProxyObservedEvidence("codex", "work", "gpt-5.5", "session-2"));

        Assert.True(sessionMismatch.HasMismatch);
        Assert.Contains("Observed session 'session-2'", sessionMismatch.MismatchReason);

        var sessionMatch = StarCliProxyRouteEvidence.ForRequest(
            sessionRequest,
            new StarCliProxyObservedEvidence("codex", "work", "gpt-5.5", "session-1"));

        Assert.False(sessionMatch.HasMismatch);
        Assert.False(sessionMatch.IsFullyObserved);
    }

    [Fact]
    public void ObservedEvidence_LegacyClaimsStayUnverified()
    {
        var legacy = new StarCliProxyObservedEvidence("codex", "codex:work", "gpt-5.5", "session-1");

        Assert.False(legacy.IsProviderVerified);
        Assert.False(legacy.IsAccountVerified);
        Assert.False(legacy.IsModelVerified);
        Assert.False(legacy.IsSessionVerified);

        Assert.True(StarCliProxyObservedEvidence.Empty.IsEmpty);
        Assert.False(StarCliProxyObservedEvidence.Empty.IsModelVerified);
    }

    [Fact]
    public void ObservedEvidence_MergeAssociatesEachFlagOnlyWithItsOwnSurvivingValue()
    {
        var existing = new StarCliProxyObservedEvidence(
            "codex",
            "codex:work",
            "gpt-5.5",
            "session-1",
            IsProviderVerified: true,
            IsAccountVerified: true,
            IsModelVerified: true,
            IsSessionVerified: true);

        // An absent newer value keeps the older value together with the older flag.
        var retained = existing.Merge(new StarCliProxyObservedEvidence(
            AccountId: "agy:other",
            IsAccountVerified: true));

        Assert.Equal("codex", retained.ProviderId);
        Assert.True(retained.IsProviderVerified);
        Assert.Equal("gpt-5.5", retained.ModelId);
        Assert.True(retained.IsModelVerified);
        Assert.Equal("session-1", retained.SessionId);
        Assert.True(retained.IsSessionVerified);

        // A non-null newer value takes the newer flag, so a verified field never lends its
        // verification to a different field that arrived in the same record.
        Assert.Equal("agy:other", retained.AccountId);
        Assert.True(retained.IsAccountVerified);

        var downgraded = existing.Merge(new StarCliProxyObservedEvidence(
            "agy",
            "agy:other",
            "gemini-3.1-pro",
            "session-2"));

        Assert.Equal("agy", downgraded.ProviderId);
        Assert.False(downgraded.IsProviderVerified);
        Assert.Equal("gemini-3.1-pro", downgraded.ModelId);
        Assert.False(downgraded.IsModelVerified);
        Assert.Equal("session-2", downgraded.SessionId);
        Assert.False(downgraded.IsSessionVerified);

        // An empty newer record leaves every surviving value and flag untouched.
        var untouched = existing.Merge(StarCliProxyObservedEvidence.Empty);

        Assert.Equal("codex", untouched.ProviderId);
        Assert.Equal("codex:work", untouched.AccountId);
        Assert.Equal("gpt-5.5", untouched.ModelId);
        Assert.Equal("session-1", untouched.SessionId);
        Assert.True(untouched.IsProviderVerified);
        Assert.True(untouched.IsAccountVerified);
        Assert.True(untouched.IsModelVerified);
        Assert.True(untouched.IsSessionVerified);

        // A null value is never verified, no matter what flag either record carries: an absent
        // claim cannot be proven into existence by an incoming record that asserts verification.
        var nullClaim = new StarCliProxyObservedEvidence(IsProviderVerified: true);

        Assert.Null(nullClaim.ProviderId);

        var merged = nullClaim.Merge(new StarCliProxyObservedEvidence(
            IsProviderVerified: true,
            IsAccountVerified: true,
            IsModelVerified: true,
            IsSessionVerified: true));

        Assert.Null(merged.ProviderId);
        Assert.False(merged.IsProviderVerified);
        Assert.False(merged.IsAccountVerified);
        Assert.False(merged.IsModelVerified);
        Assert.False(merged.IsSessionVerified);
    }

    private static StarCliProxyChatRequest CreateRequest() =>
        StarCliProxyChatRequest.Create(
            "gpt-5.5",
            [new StarCliProxyChatMessage("user", "hi")]);

    private static StarCliProxyClient CreateClient(
        StubHttpMessageHandler handler,
        StarCliProxyOptions? options = null)
    {
        var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        return new StarCliProxyClient(httpClient, Options.Create(options ?? new StarCliProxyOptions()));
    }

    private static async Task<IReadOnlyList<StarCliProxyStreamEvent>> CollectAsync(
        IAsyncEnumerable<StarCliProxyStreamEvent> events)
    {
        var collected = new List<StarCliProxyStreamEvent>();

        await foreach (var streamEvent in events)
        {
            collected.Add(streamEvent);
        }

        return collected;
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage SseResponse(params string[] dataLines)
    {
        var builder = new StringBuilder();

        foreach (var dataLine in dataLines)
        {
            builder.Append("data: ").Append(dataLine).Append('\n').Append('\n');
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream")
        };

        response.Headers.TryAddWithoutValidation("X-Cliproxy-Provider", "codex");
        response.Headers.TryAddWithoutValidation("X-Cliproxy-Account", "work");
        response.Headers.TryAddWithoutValidation("X-Cliproxy-Session-Id", "session-abc");

        return response;
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string? RequestUri,
        string? Authorization,
        string? SessionHeader,
        string? Accept,
        string? Body);

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = (request, _) => responder(request);
        }

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Cliproxy-Session-Id", out var sessionValues)
                    ? sessionValues.FirstOrDefault()
                    : null,
                string.Join(", ", request.Headers.Accept.Select(header => header.MediaType)),
                body));

            return await _responder(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
