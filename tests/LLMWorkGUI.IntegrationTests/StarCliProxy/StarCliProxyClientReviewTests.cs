using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.StarCliProxy;

public sealed class StarCliProxyClientReviewTests
{
    private const int BodyLimit = 4 * 1024 * 1024;
    private const int LineLimit = 1024 * 1024;
    private const int ErrorReadLimit = 64 * 1024;
    private static readonly StarCliProxyEndpoint Endpoint = StarCliProxyEndpoint.Loopback(8300, "synthetic-proxy-key");

    [Fact]
    public async Task OversizedCatalogStopsReadingBeforeAllocatingTheWholeBody()
    {
        using var input = PaddingBody("{\"data\":[],\"padding\":\"", BodyLimit);
        using var http = CreateHttp(input, "application/json");
        var client = new StarCliProxyClient(http);

        var failure = await Record.ExceptionAsync(() => client.ListModelsAsync(Endpoint));

        Assert.NotNull(failure);
        Assert.InRange(input.BytesRead, 0, BodyLimit + 8192);
    }

    [Fact]
    public async Task OversizedHealthCatalogIsUnhealthyAndStopsReading()
    {
        using var input = PaddingBody("{\"data\":[],\"padding\":\"", BodyLimit);
        using var http = CreateHttp(input, "application/json");
        var client = new StarCliProxyClient(http);

        var health = await client.CheckHealthAsync(Endpoint);

        Assert.False(health.IsHealthy);
        Assert.InRange(input.BytesRead, 0, BodyLimit + 8192);
    }

    [Fact]
    public async Task OversizedNonSseCompletionCannotProduceSuccessfulTerminal()
    {
        using var input = PaddingBody("{\"choices\":[],\"padding\":\"", BodyLimit);
        using var http = CreateHttp(input, "application/json");
        var client = new StarCliProxyClient(http);

        var events = await CollectAsync(client.StreamChatCompletionAsync(Endpoint, Request()));

        Assert.Contains(events, entry => entry.Kind is StarCliProxyStreamEventKind.Error or StarCliProxyStreamEventKind.Malformed);
        Assert.DoesNotContain(events, entry => entry.Kind == StarCliProxyStreamEventKind.Completed);
        Assert.InRange(input.BytesRead, 0, BodyLimit + 8192);
    }

    [Fact]
    public async Task SseLineWithoutNewlineIsBoundedWhileReading()
    {
        using var input = new GeneratedStream("data: ", "x", LineLimit + 32768, "");
        using var http = CreateHttp(input, "text/event-stream");
        var client = new StarCliProxyClient(http);

        var events = await CollectAsync(client.StreamChatCompletionAsync(Endpoint, Request()));

        Assert.Contains(events, entry => entry.Kind is StarCliProxyStreamEventKind.Error or StarCliProxyStreamEventKind.Malformed);
        Assert.DoesNotContain(events, entry => entry.Kind == StarCliProxyStreamEventKind.Completed);
        Assert.InRange(input.BytesRead, 0, LineLimit + 8192);
    }

    [Fact]
    public async Task SseMultilineEventIsBoundedAcrossIndividuallySmallLines()
    {
        var line = "data: " + new string('x', 16384) + "\n";
        using var input = new GeneratedStream("", line, BodyLimit + 65536, "\n");
        using var http = CreateHttp(input, "text/event-stream");
        var client = new StarCliProxyClient(http);

        var events = await CollectAsync(client.StreamChatCompletionAsync(Endpoint, Request()));

        Assert.Contains(events, entry => entry.Kind is StarCliProxyStreamEventKind.Error or StarCliProxyStreamEventKind.Malformed);
        Assert.DoesNotContain(events, entry => entry.Kind == StarCliProxyStreamEventKind.Completed);
        Assert.InRange(input.BytesRead, 0, BodyLimit + 32768);
    }

    [Fact]
    public async Task HttpErrorExcerptDoesNotReadTheEntireResponse()
    {
        using var input = new GeneratedStream("", "x", ErrorReadLimit + 32768, "");
        using var http = CreateHttp(input, "text/plain", HttpStatusCode.BadGateway);
        var client = new StarCliProxyClient(http);

        var error = Assert.Single(await CollectAsync(client.StreamChatCompletionAsync(Endpoint, Request())));

        Assert.Equal(StarCliProxyStreamEventKind.Error, error.Kind);
        Assert.InRange(error.RawJson?.Length ?? 0, 0, 2051);
        Assert.InRange(input.BytesRead, 0, ErrorReadLimit + 8192);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"data\":[{\"id\":\"synthetic-model\",\"created\":9223372036854775807}]}")]
    public async Task MalformedHealthCatalogReturnsUnhealthyWithoutEscapingParserExceptions(string payload)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var http = CreateHttp(input, "application/json");

        var health = await new StarCliProxyClient(http).CheckHealthAsync(Endpoint);

        Assert.False(health.IsHealthy);
        Assert.False(string.IsNullOrWhiteSpace(health.Detail));
    }

    [Fact]
    public async Task NativeErrorTerminatesBeforeFollowingContentOrToolEvents()
    {
        using var input = Sse(
            "{\"error\":{\"message\":\"synthetic native failure\"}}",
            "{\"choices\":[{\"delta\":{\"content\":\"late content\",\"tool_calls\":[{\"function\":{\"name\":\"late-tool\"}}]}}]}",
            "[DONE]");
        using var http = CreateHttp(input, "text/event-stream");

        var error = Assert.Single(await CollectAsync(new StarCliProxyClient(http).StreamChatCompletionAsync(Endpoint, Request())));

        Assert.Equal(StarCliProxyStreamEventKind.Error, error.Kind);
        Assert.Contains("synthetic native failure", error.ErrorMessage);
    }

    [Fact]
    public async Task UsageAfterFinishIsPreservedWithoutAnotherTerminal()
    {
        using var input = Sse(
            "{\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}",
            "{\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}",
            "[DONE]");
        using var http = CreateHttp(input, "text/event-stream");

        var events = await CollectAsync(new StarCliProxyClient(http).StreamChatCompletionAsync(Endpoint, Request()));

        Assert.Equal(new[] { StarCliProxyStreamEventKind.ContentDelta, StarCliProxyStreamEventKind.Completed,
            StarCliProxyStreamEventKind.Usage }, events.Select(entry => entry.Kind));
        Assert.Equal(3, events[^1].PromptTokens);
        Assert.Equal(2, events[^1].CompletionTokens);
    }

    [Fact]
    public async Task CallerCancellationIsNotFlattenedIntoUnhealthy()
    {
        using var input = new MemoryStream("{}"u8.ToArray());
        using var http = CreateHttp(input, "application/json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StarCliProxyClient(http).CheckHealthAsync(Endpoint, cancellation.Token));
    }

    private static GeneratedStream PaddingBody(string prefix, int limit) => new(prefix, "x", limit + 32768, "\"}");
    private static StarCliProxyChatRequest Request() => StarCliProxyChatRequest.Create("synthetic-model",
        [new StarCliProxyChatMessage("user", "synthetic prompt")]);
    private static MemoryStream Sse(params string[] payloads) => new(Encoding.UTF8.GetBytes(
        string.Join("", payloads.Select(payload => "data: " + payload + "\n\n"))));
    private static HttpClient CreateHttp(Stream input, string mediaType, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new ResponseHandler(input, mediaType, status)) { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task<List<StarCliProxyStreamEvent>> CollectAsync(IAsyncEnumerable<StarCliProxyStreamEvent> source)
    {
        var events = new List<StarCliProxyStreamEvent>();
        await foreach (var entry in source) events.Add(entry);
        return events;
    }

    private sealed class ResponseHandler(Stream input, string mediaType, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var content = new StreamContent(input);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    // Generates bounded chunks on demand; large fixtures never construct an oversized source string.
    private sealed class GeneratedStream(string prefix, string pattern, int repeatedBytes, string suffix) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private readonly byte[] _pattern = Encoding.UTF8.GetBytes(pattern);
        private readonly byte[] _suffix = Encoding.UTF8.GetBytes(suffix);
        private long _position;
        public long BytesRead => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var total = _prefix.Length + (long)repeatedBytes + _suffix.Length;
            var count = (int)Math.Min(Math.Min(buffer.Length, 4096), total - _position);
            for (var index = 0; index < count; index++)
            {
                var at = _position + index;
                buffer[index] = at < _prefix.Length ? _prefix[(int)at]
                    : at < _prefix.Length + (long)repeatedBytes ? _pattern[(int)((at - _prefix.Length) % _pattern.Length)]
                    : _suffix[(int)(at - _prefix.Length - repeatedBytes)];
            }
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
