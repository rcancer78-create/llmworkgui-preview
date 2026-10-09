using System.Net;
using System.Net.Sockets;
using System.Text;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using Microsoft.Extensions.Options;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

/// <summary>
/// Contract fixture tests for the star-cliproxy HTTP/SSE wire protocol over a real loopback TCP
/// socket (ТЗ §6.4, §6.11a, ADR-0007). The fixture uses an in-process <see cref="HttpListener"/>
/// on 127.0.0.1 instead of an <c>HttpMessageHandler</c> stub, so the actual TCP transport,
/// headers, chunked SSE framing and route-mismatch termination are proven end to end.
/// </summary>
public sealed class StarCliProxyContractFixtureTests
{
    private const string RequestedSessionId = "session-contract";

    [Fact]
    public async Task ListModels_OverLoopbackTcp_ParsesCatalogAndSendsBearerAuthorization()
    {
        await using var server = await LoopbackCliproxyServer.StartAsync();
        var client = CreateClient();

        var models = await client.ListModelsAsync(server.Endpoint);

        Assert.Equal(2, models.Count);
        Assert.Equal("gpt-5.5", models[0].Id);
        Assert.Equal("codex", models[0].ProviderId);
        Assert.Equal("antigravity", models[1].Id);
        Assert.Equal("agy", models[1].ProviderId);

        Assert.Equal($"Bearer {LoopbackCliproxyServer.ApiKey}", server.LastAuthorization);
    }

    [Fact]
    public async Task StreamChat_OverLoopbackTcp_NormalizesDeltasUsageEvidenceAndCompleted()
    {
        await using var server = await LoopbackCliproxyServer.StartAsync();
        var client = CreateClient();

        var events = await CollectAsync(client.StreamChatCompletionAsync(
            server.Endpoint,
            StarCliProxyChatRequest.Create(
                "gpt-5.5",
                [new StarCliProxyChatMessage("user", "hi")],
                requestedProviderId: "codex",
                requestedAccountId: "work",
                requestedSessionId: RequestedSessionId)));

        Assert.Equal(
            new[]
            {
                StarCliProxyStreamEventKind.ContentDelta,
                StarCliProxyStreamEventKind.ContentDelta,
                StarCliProxyStreamEventKind.Usage,
                StarCliProxyStreamEventKind.Completed
            },
            events.Select(streamEvent => streamEvent.Kind).ToArray());

        Assert.Equal("Hello", events[0].Content);
        Assert.Equal(" world", events[1].Content);
        Assert.Equal(5, events[2].PromptTokens);
        Assert.Equal(2, events[2].CompletionTokens);
        Assert.Equal("stop", events[3].FinishReason);

        // Claims are read from the real loopback response headers and the echoed body model, and
        // none of them is verified: this gateway proves nothing about which native route it used.
        Assert.Equal("codex", events[3].Evidence.ProviderId);
        Assert.Equal("work", events[3].Evidence.AccountId);
        Assert.Equal(RequestedSessionId, events[3].Evidence.SessionId);
        Assert.Equal("gpt-5.5", events[3].Evidence.ModelId);

        Assert.False(events[3].Evidence.IsProviderVerified);
        Assert.False(events[3].Evidence.IsAccountVerified);
        Assert.False(events[3].Evidence.IsModelVerified);
        Assert.False(events[3].Evidence.IsSessionVerified);

        // The same holds for every event of the turn, not only the terminal one.
        Assert.All(events, streamEvent =>
        {
            Assert.False(streamEvent.Evidence.IsProviderVerified);
            Assert.False(streamEvent.Evidence.IsAccountVerified);
            Assert.False(streamEvent.Evidence.IsModelVerified);
            Assert.False(streamEvent.Evidence.IsSessionVerified);
        });

        Assert.False(StarCliProxyRouteEvidence.ForRequest(
            StarCliProxyChatRequest.Create(
                "gpt-5.5",
                [new StarCliProxyChatMessage("user", "hi")],
                requestedProviderId: "codex",
                requestedAccountId: "work"),
            events[3].Evidence).IsFullyObserved);

        // The request carried the requested session id and the SSE accept header over the wire.
        Assert.Equal(RequestedSessionId, server.LastSessionHeader);
        Assert.Contains("text/event-stream", server.LastAccept);
    }

    [Fact]
    public async Task StreamChat_OverLoopbackTcp_Cancellation_StopsEnumeration()
    {
        await using var server = await LoopbackCliproxyServer.StartAsync();
        server.DelayAfterFirstChunk = true;

        var client = CreateClient();
        using var cancellation = new CancellationTokenSource();

        var received = new List<StarCliProxyStreamEvent>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var streamEvent in client.StreamChatCompletionAsync(
                server.Endpoint,
                CreateRequest(),
                cancellation.Token))
            {
                received.Add(streamEvent);
                cancellation.Cancel();
            }
        });

        Assert.Equal(StarCliProxyStreamEventKind.ContentDelta, Assert.Single(received).Kind);
        Assert.DoesNotContain(received, streamEvent => streamEvent.Kind == StarCliProxyStreamEventKind.Completed);
    }

    [Fact]
    public async Task StreamChat_OverLoopbackTcp_SessionSubstitution_TerminatesAsRouteMismatch()
    {
        await using var server = await LoopbackCliproxyServer.StartAsync();
        server.ObservedSession = "session-hijacked";

        var client = CreateClient();

        var events = await CollectAsync(client.StreamChatCompletionAsync(
            server.Endpoint,
            StarCliProxyChatRequest.Create(
                "gpt-5.5",
                [new StarCliProxyChatMessage("user", "hi")],
                requestedProviderId: "codex",
                requestedAccountId: "work",
                requestedSessionId: RequestedSessionId)));

        var error = Assert.Single(events);
        Assert.Equal(StarCliProxyStreamEventKind.Error, error.Kind);
        Assert.Contains("route mismatch", error.ErrorMessage);
        Assert.Contains("Observed session 'session-hijacked'", error.ErrorMessage);
        Assert.DoesNotContain(events, streamEvent => streamEvent.Kind == StarCliProxyStreamEventKind.Completed);
    }

    [Fact]
    public async Task StreamChat_OverLoopbackTcp_HeaderBodySessionConflict_EmitsSingleTerminalRouteMismatch()
    {
        await using var server = await LoopbackCliproxyServer.StartAsync();
        server.ObservedSession = "session-foreign";
        server.ChunkSessionId = RequestedSessionId;

        var client = CreateClient();

        var events = await CollectAsync(client.StreamChatCompletionAsync(
            server.Endpoint,
            StarCliProxyChatRequest.Create(
                "gpt-5.5",
                [new StarCliProxyChatMessage("user", "hi")],
                requestedProviderId: "codex",
                requestedAccountId: "work",
                requestedSessionId: RequestedSessionId)));

        // The foreign header plus a requested session in the body must never produce a
        // Completed followed by a second terminal Error: exactly one terminal event.
        var error = Assert.Single(events);
        Assert.Equal(StarCliProxyStreamEventKind.Error, error.Kind);
        Assert.Contains("route mismatch", error.ErrorMessage);
        Assert.Contains("session-foreign", error.ErrorMessage);
        Assert.DoesNotContain(events, streamEvent => streamEvent.Kind == StarCliProxyStreamEventKind.Completed);
    }

    [Fact]
    public async Task StreamChat_OverLoopbackTcp_ProviderSubstitution_TerminatesAsRouteMismatch()
    {
        await using var server = await LoopbackCliproxyServer.StartAsync();
        server.ObservedProvider = "agy";

        var client = CreateClient();

        var events = await CollectAsync(client.StreamChatCompletionAsync(
            server.Endpoint,
            StarCliProxyChatRequest.Create(
                "gpt-5.5",
                [new StarCliProxyChatMessage("user", "hi")],
                requestedProviderId: "codex",
                requestedAccountId: "work",
                requestedSessionId: RequestedSessionId)));

        var error = Assert.Single(events);
        Assert.Equal(StarCliProxyStreamEventKind.Error, error.Kind);
        Assert.Contains("Observed provider 'agy'", error.ErrorMessage);
        Assert.DoesNotContain(events, streamEvent => streamEvent.Kind == StarCliProxyStreamEventKind.Completed);
    }

    private static StarCliProxyChatRequest CreateRequest() =>
        StarCliProxyChatRequest.Create(
            "gpt-5.5",
            [new StarCliProxyChatMessage("user", "hi")]);

    private static StarCliProxyClient CreateClient()
    {
        var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        return new StarCliProxyClient(httpClient, Options.Create(new StarCliProxyOptions()));
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

    /// <summary>
    /// Minimal in-process star-cliproxy stand-in served over a real loopback TCP socket. It
    /// implements <c>/v1/models</c> and a chunked <c>text/event-stream</c> chat completion with
    /// provider/account/session evidence headers, optional post-first-chunk delay and configurable
    /// observed route substitution.
    /// </summary>
    private sealed class LoopbackCliproxyServer : IAsyncDisposable
    {
        public const string ApiKey = "sk-contract-fixture-key";

        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task? _listenTask;

        private LoopbackCliproxyServer(int port)
        {
            Port = port;
            Endpoint = StarCliProxyEndpoint.Loopback(port, ApiKey);

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public int Port { get; }

        public StarCliProxyEndpoint Endpoint { get; }

        public string ObservedProvider { get; set; } = "codex";

        public string ObservedAccount { get; set; } = "work";

        public string ObservedSession { get; set; } = "session-contract";

        public string ObservedModel { get; set; } = "gpt-5.5";

        public string? ChunkSessionId { get; set; }

        public bool DelayAfterFirstChunk { get; set; }

        public string? LastAuthorization { get; private set; }

        public string? LastSessionHeader { get; private set; }

        public string? LastAccept { get; private set; }

        public static Task<LoopbackCliproxyServer> StartAsync()
        {
            var server = new LoopbackCliproxyServer(GetAvailablePort());

            server._listener.Start();
            server._listenTask = Task.Run(server.ListenLoopAsync);

            return Task.FromResult(server);
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);

                    _ = Task.Run(() => HandleRequestAsync(context, _cts.Token));
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException || _cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                var request = context.Request;

                LastAuthorization = request.Headers["Authorization"];
                LastSessionHeader = request.Headers["X-Cliproxy-Session-Id"];
                LastAccept = request.Headers["Accept"];

                var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

                if (string.Equals(path, "/v1/models", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json; charset=utf-8";

                    await WriteBodyAsync(
                        context.Response,
                        """
                        {
                          "object": "list",
                          "data": [
                            {"id": "gpt-5.5", "object": "model", "owned_by": "codex", "provider": "codex"},
                            {"id": "antigravity", "object": "model", "provider": "agy"}
                          ]
                        }
                        """,
                        cancellationToken).ConfigureAwait(false);

                    return;
                }

                if (string.Equals(path, "/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/event-stream; charset=utf-8";
                    context.Response.SendChunked = true;
                    context.Response.Headers["X-Cliproxy-Provider"] = ObservedProvider;
                    context.Response.Headers["X-Cliproxy-Account"] = ObservedAccount;
                    context.Response.Headers["X-Cliproxy-Session-Id"] = ObservedSession;

                    await WriteSseFrameAsync(
                        context.Response,
                        $"{{\"id\":\"chunk-1\",\"model\":\"{ObservedModel}\",\"choices\":[{{\"delta\":{{\"content\":\"Hello\"}}}}]}}",
                        cancellationToken).ConfigureAwait(false);

                    if (DelayAfterFirstChunk)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    }

                    await WriteSseFrameAsync(
                        context.Response,
                        $"{{\"id\":\"chunk-2\",\"model\":\"{ObservedModel}\",\"choices\":[{{\"delta\":{{\"content\":\" world\"}}}}]}}",
                        cancellationToken).ConfigureAwait(false);

                    var chunkSessionJson = ChunkSessionId is null
                        ? string.Empty
                        : $",\"session_id\":\"{ChunkSessionId}\"";

                    await WriteSseFrameAsync(
                        context.Response,
                        $"{{\"id\":\"chunk-3\",\"model\":\"{ObservedModel}\"{chunkSessionJson},\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":5,\"completion_tokens\":2}}}}",
                        cancellationToken).ConfigureAwait(false);

                    await WriteSseFrameAsync(context.Response, "[DONE]", cancellationToken).ConfigureAwait(false);

                    context.Response.Close();

                    return;
                }

                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json; charset=utf-8";

                await WriteBodyAsync(
                    context.Response,
                    """{"error":{"message":"not found"}}""",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                try
                {
                    context.Response.Abort();
                }
                catch
                {
                    // The client disconnected or the server is shutting down.
                }
            }
        }

        private static async Task WriteSseFrameAsync(
            HttpListenerResponse response,
            string payload,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");

            await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteBodyAsync(
            HttpListenerResponse response,
            string json,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(json);

            response.ContentLength64 = bytes.Length;

            await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();

            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            if (_listenTask is not null)
            {
                try
                {
                    await _listenTask.ConfigureAwait(false);
                }
                catch
                {
                    // Listener shutdown.
                }
            }

            _listener.Close();
            _cts.Dispose();
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);

            listener.Start();

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            listener.Stop();

            return port;
        }
    }
}
