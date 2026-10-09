using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeClientTests
{
    private static readonly Uri BaseUrl = new("http://127.0.0.1:54321");

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PromptAuthenticationFailure_PreservesTypedStatus(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(StubHttpMessageHandler.Json("{}", status)));
        using var http = CreateHttpClient(handler);
        var client = new OpenCodeClient(http, BaseUrl);
        var exception = await Assert.ThrowsAsync<OpenCodeClientException>(() =>
            client.SendPromptAsync("session", new OpenCodePromptRequest { Prompt = "probe" }));
        Assert.Equal(status, exception.StatusCode);
    }

    private const string SampleDoc = """
    {
      "openapi": "3.1.0",
      "info": { "title": "opencode", "version": "1.18.31" },
      "paths": {
        "/session": { "get": {}, "post": {} },
        "/instance/dispose": { "post": {} },
        "/doc": { "get": {} }
      }
    }
    """;

    [Fact]
    public async Task PingAsync_WhenServerIsHealthy_ReturnsTrue()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json("{}")));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var alive = await client.PingAsync();

        Assert.True(alive);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("/global/health", handler.Requests[0].Path);
    }

    [Fact]
    public async Task PingAsync_UsesManagedServerPassword()
    {
        var credential = new OpenCodeManagedServerCredential();
        credential.Publish("synthetic-password");
        AuthenticationHeaderValue? header = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            header = request.Headers.Authorization;
            return Task.FromResult(StubHttpMessageHandler.Json("{}"));
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl, managedCredential: credential);

        Assert.True(await client.PingAsync());
        Assert.Equal("Basic", header?.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:synthetic-password")),
            header?.Parameter);
    }

    [Fact]
    public async Task SessionOnAProviderProfile_UsesThatProfileServer()
    {
        var seen = new List<Uri>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            seen.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            var body = path == "/session"
                ? """{"id":"ses_a"}"""
                : "{}";
            return Task.FromResult(StubHttpMessageHandler.Json(body));
        });
        using var httpClient = CreateHttpClient(handler);
        var sharedCalls = 0;
        var client = new OpenCodeClient(
            httpClient,
            BaseUrl,
            resolveBaseUrl: _ =>
            {
                sharedCalls++;
                return Task.FromResult(new Uri("http://127.0.0.1:19001"));
            },
            resolveProfileBaseUrl: (profile, _) => Task.FromResult(
                profile == "profile-a"
                    ? new Uri("http://127.0.0.1:19011")
                    : throw new InvalidOperationException(profile)));

        var session = await client.CreateSessionAsync(new OpenCodeCreateSessionRequest
        {
            ProviderProfileId = "profile-a"
        });
        Assert.True(await client.SendPromptAsync(session.Id, new OpenCodePromptRequest { Prompt = "probe" }));
        Assert.True(await client.PingAsync());

        Assert.Equal(1, sharedCalls);
        Assert.All(seen.Take(2), uri => Assert.Equal(19011, uri.Port));
        Assert.Equal(19001, seen[2].Port);
    }

    [Fact]
    public async Task ProfileOnAFixedEndpoint_StaysOnThatEndpoint()
    {
        var seen = new List<Uri>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            seen.Add(request.RequestUri!);
            return Task.FromResult(StubHttpMessageHandler.Json("""{"id":"ses_fixed"}"""));
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);
        var session = await client.CreateSessionAsync(new OpenCodeCreateSessionRequest
        {
            ProviderProfileId = "profile-a"
        });
        Assert.Equal("ses_fixed", session.Id);
        Assert.Equal(54321, seen[0].Port);
    }

    [Fact]
    public async Task ProfileWithoutAProfileResolver_DoesNotUseTheUnnamedServer()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(StubHttpMessageHandler.Json("""{"id":"ses_x"}""")));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(
            httpClient,
            BaseUrl,
            resolveBaseUrl: _ => Task.FromResult(new Uri("http://127.0.0.1:19001")));
        var exception = await Assert.ThrowsAsync<OpenCodeClientException>(() =>
            client.CreateSessionAsync(new OpenCodeCreateSessionRequest { ProviderProfileId = "profile-a" }));
        Assert.Null(exception.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PingAsync_WhenServerReturnsError_ReturnsFalse()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        Assert.False(await client.PingAsync());
    }

    [Fact]
    public async Task PingAsync_WhenNetworkFails_ReturnsFalse()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => throw new HttpRequestException("connection refused"));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        Assert.False(await client.PingAsync());
    }

    [Fact]
    public async Task PingAsync_WhenRequestTimesOut_ReturnsFalse()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return StubHttpMessageHandler.Json("{}");
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl, TimeSpan.FromMilliseconds(50));

        Assert.False(await client.PingAsync());
    }

    [Fact]
    public async Task PingAsync_WhenCallerCancels_Throws()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json("{}")));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.PingAsync(cancellation.Token));
    }

    [Fact]
    public async Task GetDocAsync_ParsesOpenApiDocument()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json(SampleDoc)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        var response = await client.GetDocAsync();

        Assert.Equal("3.1.0", response.OpenApiSpecification);
        Assert.Equal(SampleDoc, response.RawJson);
        Assert.True(response.HasOperation("get", "/session"));
        Assert.True(response.HasOperation("post", "/instance/dispose"));
        Assert.False(response.HasOperation("delete", "/session"));
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("/doc", handler.Requests[0].Path);
    }

    [Fact]
    public async Task GetDocAsync_WhenServerReturnsError_ThrowsClientException()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json("{}", HttpStatusCode.NotFound)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        await Assert.ThrowsAsync<OpenCodeClientException>(() => client.GetDocAsync());
    }

    [Fact]
    public async Task GetDocAsync_WhenResponseIsMalformedJson_ThrowsClientException()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json("{ not json")));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        await Assert.ThrowsAsync<OpenCodeClientException>(() => client.GetDocAsync());
    }

    [Fact]
    public async Task GetDocAsync_WhenNetworkFails_ThrowsClientException()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => throw new HttpRequestException("connection refused"));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);

        await Assert.ThrowsAsync<OpenCodeClientException>(() => client.GetDocAsync());
    }

    [Fact]
    public async Task GetDocAsync_WhenRequestTimesOut_ThrowsClientException()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return StubHttpMessageHandler.Json(SampleDoc);
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OpenCodeClientException>(() => client.GetDocAsync());
    }

    [Fact]
    public async Task GetDocAsync_WhenCallerCancels_Throws()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(StubHttpMessageHandler.Json(SampleDoc)));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetDocAsync(cancellation.Token));
    }

    [Theory]
    [InlineData("doc")]
    [InlineData("sessions")]
    [InlineData("permission")]
    public async Task RequestTimeout_CoversStalledResponseBody(string endpoint)
    {
        using var body = new StalledContent();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = body }));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl, TimeSpan.FromMilliseconds(50));
        // The caller budget is only a test cleanup bound. The client's shorter budget must
        // produce its own typed timeout before the caller cancels the stalled response.
        using var caller = new CancellationTokenSource();

        Task operation = endpoint switch
        {
            "doc" => client.GetDocAsync(caller.Token),
            "sessions" => client.ListSessionsAsync(caller.Token),
            "permission" => client.ReplyPermissionAsync("synthetic-permission", OpenCodePermissionReply.Deny(), caller.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };

        OpenCodeClientException error;
        try
        {
            // Bound the transport task, not an assertion/async delegate queued on xUnit's context.
            error = await Assert.ThrowsAsync<OpenCodeClientException>(() => operation.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        catch
        {
            caller.Cancel();
            throw;
        }

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(caller.IsCancellationRequested);
        Assert.True(body.WasDisposed);
    }

    [Fact]
    public async Task CallerCancellation_DuringResponseBody_RemainsCancellation()
    {
        using var body = new StalledContent();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = body }));
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenCodeClient(httpClient, BaseUrl, TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();
        var operation = client.GetDocAsync(caller.Token);
        try
        {
            await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.True(body.WasDisposed);
        }
        finally
        {
            caller.Cancel();
        }
    }

    private sealed class StalledContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            // A transport body must cancel independently of the test runner's throttled context.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
