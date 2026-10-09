using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.Events;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.OpenCode;

public sealed class OpenCodeEventStreamTests
{
    private static readonly Uri BaseUrl = new("http://127.0.0.1:54321");

    [Fact]
    public async Task EventSpool_RedactsSecretsBeforePersistence()
    {
        using var directory = new TestDirectory();
        await using var spool = new OpenCodeEventSpool(directory.Root, 4);
        await spool.EnqueueAsync("""{"type":"message.updated","properties":{"headers":{"Authorization":"plain-canary"},"nested":[{"password":"nested-canary"}]}}""");
        await spool.CompleteAsync();
        var stored = await File.ReadAllTextAsync(spool.FilePath);
        Assert.DoesNotContain("plain-canary", stored);
        Assert.DoesNotContain("nested-canary", stored);
        Assert.Contains("message.updated", stored);
    }

    private const string ServerConnected = """{"type":"server.connected","properties":{}}""";

    private const string TextPart =
        """{"type":"message.part.updated","properties":{"part":{"id":"prt_1","sessionID":"ses_test","messageID":"msg_1","type":"text","text":"hello"}}}""";

    private const string SessionIdle = """{"type":"session.idle","properties":{"sessionID":"ses_test"}}""";

    [Fact]
    public async Task SubscribeAsync_OverlappingSubscriptions_PersistToSeparateFiles()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var client = CreateHttpClient(new StubHttpMessageHandler(
                (_, _) => Task.FromResult(SseResponse(ServerConnected, TextPart, SessionIdle))));
            var service = CreateService(client, directory);
            string? firstPath;
            string? secondPath;
            await using (var first = service.SubscribeAsync("ses_test").GetAsyncEnumerator())
            await using (var second = service.SubscribeAsync("ses_test").GetAsyncEnumerator())
            {
                Assert.True(await first.MoveNextAsync());
                firstPath = service.SpoolFilePath;
                Assert.True(await second.MoveNextAsync());
                secondPath = service.SpoolFilePath;
                for (var index = 0; index < 2; index++)
                {
                    Assert.True(await first.MoveNextAsync());
                    Assert.True(await second.MoveNextAsync());
                }
            }

            Assert.NotEqual(firstPath, secondPath);
            Assert.Equal(new[] { ServerConnected, TextPart, SessionIdle }, File.ReadAllLines(firstPath!));
            Assert.Equal(new[] { ServerConnected, TextPart, SessionIdle }, File.ReadAllLines(secondPath!));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SubscribeAsync_OlderSubscriptionOverflow_DoesNotChangeLatestDiagnostics()
    {
        using var client = CreateHttpClient(new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(ServerConnected, TextPart, SessionIdle))));
        var service = new OpenCodeEventStreamService(client, BaseUrl,
            options: new OpenCodeStreamOptions { MaxInMemoryEvents = 1 });
        await using var first = service.SubscribeAsync().GetAsyncEnumerator();
        await using var second = service.SubscribeAsync().GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        Assert.True(await first.MoveNextAsync());

        Assert.False(service.IsOverflowed);
        Assert.Equal("server.connected", Assert.Single(service.GetRecentEvents()).Type);
        Assert.True(await second.MoveNextAsync());
        Assert.True(service.IsOverflowed);
        Assert.Equal("message.part.updated", Assert.Single(service.GetRecentEvents()).Type);
    }

    [Fact]
    public async Task SubscribeAsync_SequentialSubscriptions_DoNotAppendPreviousEvents()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var client = CreateHttpClient(new StubHttpMessageHandler(
                (_, _) => Task.FromResult(SseResponse(ServerConnected, TextPart, SessionIdle))));
            var service = CreateService(client, directory);
            await CollectAsync(service, 3);
            var firstPath = service.SpoolFilePath;
            await CollectAsync(service, 3);
            var secondPath = service.SpoolFilePath;

            Assert.NotEqual(firstPath, secondPath);
            Assert.Equal(new[] { ServerConnected, TextPart, SessionIdle }, File.ReadAllLines(firstPath!));
            Assert.Equal(new[] { ServerConnected, TextPart, SessionIdle }, File.ReadAllLines(secondPath!));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SubscribeAsync_Reconnect_RetainsSubscriptionSpool()
    {
        var directory = CreateTempDirectory();
        try
        {
            var calls = 0;
            using var client = CreateHttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromResult(Interlocked.Increment(ref calls) == 1
                    ? SseResponse(ServerConnected) : SseResponse(TextPart, SessionIdle))));
            var service = CreateService(client, directory);
            await CollectAsync(service, 3);

            Assert.Equal(2, calls);
            Assert.Single(Directory.GetFiles(directory));
            Assert.Equal(new[] { ServerConnected, TextPart, SessionIdle }, File.ReadAllLines(service.SpoolFilePath!));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task EventSpool_WriteFailure_IsVisibleBeforeQueueFillsOrDisposal()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, OpenCodeStreamOptions.DefaultSpoolFileName);
            using var lockedFile = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            await using var spool = new OpenCodeEventSpool(directory, queueCapacity: 10);

            Assert.True(spool.IsOverflowed);
            for (var index = 0; index < 3; index++)
            {
                Assert.True(await spool.EnqueueAsync(ServerConnected));
            }
            await spool.CompleteAsync();
            Assert.True(spool.IsOverflowed);
            Assert.Equal(0, lockedFile.Length);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SubscribeAsync_FailedSpool_IsReportedWithoutBufferOverflow()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var lockedFile = new FileStream(Path.Combine(directory, "failed.jsonl"),
                FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var client = CreateHttpClient(new StubHttpMessageHandler(
                (_, _) => Task.FromResult(SseResponse(ServerConnected, TextPart, SessionIdle))));
            var service = new OpenCodeEventStreamService(client, BaseUrl,
                new OpenCodeStreamOptions { SpoolDirectory = directory, MaxInMemoryEvents = 10 },
                () => new OpenCodeEventSpool(directory, 10, "failed.jsonl"));
            await using var subscription = service.SubscribeAsync().GetAsyncEnumerator();

            Assert.True(await subscription.MoveNextAsync());
            Assert.True(service.IsOverflowed);
            Assert.Single(service.GetRecentEvents());
            Assert.True(await subscription.MoveNextAsync());
            Assert.True(await subscription.MoveNextAsync());
            Assert.Equal(3, service.GetRecentEvents().Count);
            Assert.True(service.IsOverflowed);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SubscribeAsync_OlderFailedSpool_DoesNotChangeLatestDiagnostics()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var lockedFile = new FileStream(Path.Combine(directory, "failed.jsonl"),
                FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var client = CreateHttpClient(new StubHttpMessageHandler(
                (_, _) => Task.FromResult(SseResponse(ServerConnected, TextPart, SessionIdle))));
            var created = 0;
            var service = new OpenCodeEventStreamService(client, BaseUrl,
                new OpenCodeStreamOptions { SpoolDirectory = directory, MaxInMemoryEvents = 10 },
                () => new OpenCodeEventSpool(directory, 10,
                    ++created == 1 ? "failed.jsonl" : "healthy.jsonl"));
            await using var first = service.SubscribeAsync().GetAsyncEnumerator();
            await using var second = service.SubscribeAsync().GetAsyncEnumerator();
            Assert.True(await first.MoveNextAsync());
            Assert.True(service.IsOverflowed);
            Assert.True(await second.MoveNextAsync());
            Assert.False(service.IsOverflowed);
            Assert.True(await first.MoveNextAsync());
            Assert.False(service.IsOverflowed);
            Assert.Equal("server.connected", Assert.Single(service.GetRecentEvents()).Type);
            Assert.Equal(Path.Combine(directory, "healthy.jsonl"), service.SpoolFilePath);
            await second.DisposeAsync();
            await first.DisposeAsync();
            Assert.Equal(new[] { ServerConnected }, File.ReadAllLines(service.SpoolFilePath!));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SubscribeAsync_Cancellation_FlushesSpoolAndRetainsDiagnostics()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var client = CreateHttpClient(new StubHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new BlockingEventStream("data: " + ServerConnected + "\n\n"))
                })));
            var service = CreateService(client, directory);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in service.SubscribeAsync(null, cancellation.Token))
                {
                    cancellation.Cancel();
                }
            });

            Assert.Equal(new[] { ServerConnected }, File.ReadAllLines(service.SpoolFilePath!));
            Assert.Equal("server.connected", Assert.Single(service.GetRecentEvents()).Type);
            Assert.False(service.IsOverflowed);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task EventSpool_MidStreamFailure_ReleasesWaitingProducer()
    {
        var directory = CreateTempDirectory();
        try
        {
            var stream = new ControlledWriteFailureStream();
            await using var spool = new OpenCodeEventSpool(directory, 1, "midstream.jsonl", null, _ => stream);
            try
            {
                Assert.False(await spool.EnqueueAsync(ServerConnected));
                await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(await spool.EnqueueAsync(TextPart));
                var waitingProducer = spool.EnqueueAsync(SessionIdle).AsTask();
                Assert.False(waitingProducer.IsCompleted);

                stream.FailWrite.TrySetResult();
                Assert.True(await waitingProducer.WaitAsync(TimeSpan.FromSeconds(5)));
                await spool.CompleteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(spool.IsOverflowed);
            }
            finally
            {
                stream.FailWrite.TrySetResult();
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SubscribeAsync_MidStreamFailure_IsVisibleBeforeDisposalWithoutBufferOverflow()
    {
        var directory = CreateTempDirectory();
        try
        {
            var stream = new ControlledWriteFailureStream();
            using var client = CreateHttpClient(new StubHttpMessageHandler(
                (_, _) => Task.FromResult(SseResponse(ServerConnected, TextPart))));
            var service = new OpenCodeEventStreamService(client, BaseUrl,
                new OpenCodeStreamOptions { SpoolDirectory = directory, MaxInMemoryEvents = 10 },
                () => new OpenCodeEventSpool(directory, 10, "midstream.jsonl", null, _ => stream));
            await using var subscription = service.SubscribeAsync().GetAsyncEnumerator();
            try
            {
                Assert.True(await subscription.MoveNextAsync());
                await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(service.IsOverflowed);

                stream.FailWrite.TrySetResult();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!service.IsOverflowed)
                {
                    await Task.Delay(1, timeout.Token);
                }

                Assert.Single(service.GetRecentEvents());
                Assert.True(await subscription.MoveNextAsync());
                Assert.Equal(2, service.GetRecentEvents().Count);
            }
            finally
            {
                stream.FailWrite.TrySetResult();
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SubscribeAsync_StreamsParsedEventsFromMockServer()
    {
        var acceptHeaderSeen = false;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            acceptHeaderSeen = request.Headers.Accept.Any(
                header => string.Equals(header.MediaType, "text/event-stream", StringComparison.Ordinal));

            return Task.FromResult(SseResponse(ServerConnected, TextPart, SessionIdle));
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        var events = await CollectAsync(service, 3);

        Assert.True(acceptHeaderSeen);
        Assert.Equal(
            new[] { "server.connected", "message.part.updated", "session.idle" },
            events.Select(envelope => envelope.Type));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/event", request.Path);
    }

    [Fact]
    public async Task SubscribeAsync_WithSessionId_UsesDocumentedInstanceEventPath()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(ServerConnected)));
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        await CollectAsync(service, 1, sessionId: "ses_test01");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/event", request.Path);
    }

    [Fact]
    public async Task SubscribeAsync_ScopedBus_DropsForeignAndAmbiguousEventsBeforeBufferAndSpool()
    {
        var directory = CreateTempDirectory();
        try
        {
            var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(SseResponse(
                ServerConnected,
                """{"type":"session.idle","properties":{"sessionID":"ses_other"}}""",
                """{"type":"session.error","properties":{"sessionID":"ses_other","error":"foreign"}}""",
                """{"type":"session.idle","properties":{}}""",
                """{"type":"session.error","properties":{"error":{"name":"uncorrelated"}}}""",
                """{"type":"message.part.updated","properties":{"sessionID":"ses_test","part":{"sessionID":"ses_other","type":"step-finish"}}}""",
                """{"type":"message.updated","properties":{"info":{"sessionID":"ses_other","role":"assistant"}}}""",
                TextPart,
                """{"type":"message.updated","properties":{"info":{"sessionID":"ses_test","role":"assistant"}}}""",
                SessionIdle)));
            using var httpClient = CreateHttpClient(handler);
            var service = CreateService(httpClient, directory);

            var events = await CollectAsync(service, 4, "ses_test");

            Assert.Equal(new[] { "server.connected", "message.part.updated", "message.updated", "session.idle" },
                events.Select(item => item.Type));
            Assert.Equal(events, service.GetRecentEvents());
            Assert.Equal("/event", Assert.Single(handler.Requests).Path);
            var lines = File.ReadAllLines(service.SpoolFilePath!);
            Assert.Equal(4, lines.Length);
            Assert.DoesNotContain(lines, line => line.Contains("ses_other", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("foreign", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SubscribeAsync_SpoolsRawEventsToDiskAsJsonLines()
    {
        var spoolDirectory = CreateTempDirectory();

        try
        {
            var handler = new StubHttpMessageHandler(
                (_, _) => Task.FromResult(SseResponse(ServerConnected, TextPart, SessionIdle)));
            using var httpClient = CreateHttpClient(handler);
            var service = CreateService(httpClient, spoolDirectory);

            var events = await CollectAsync(service, 3);

            Assert.NotNull(service.SpoolFilePath);
            Assert.Equal(spoolDirectory, Path.GetDirectoryName(service.SpoolFilePath));
            Assert.StartsWith("opencode-events-", Path.GetFileName(service.SpoolFilePath));
            Assert.EndsWith(".jsonl", service.SpoolFilePath);
            Assert.True(File.Exists(service.SpoolFilePath));

            var lines = File.ReadAllLines(service.SpoolFilePath!);

            Assert.Equal(3, lines.Length);

            for (var index = 0; index < lines.Length; index++)
            {
                using var document = JsonDocument.Parse(lines[index]);
                Assert.Equal(events[index].Type, document.RootElement.GetProperty("type").GetString());
                Assert.Equal(events[index].RawJson, lines[index]);
            }
        }
        finally
        {
            DeleteDirectory(spoolDirectory);
        }
    }

    [Fact]
    public async Task SubscribeAsync_WithoutSpoolDirectory_KeepsBoundedBufferOnly()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(ServerConnected, TextPart, SessionIdle)));
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        await CollectAsync(service, 3);

        Assert.Null(service.SpoolFilePath);
        Assert.False(service.IsOverflowed);
        Assert.Equal(3, service.GetRecentEvents().Count);
        Assert.Equal("session.idle", service.GetRecentEvents(1)[0].Type);
    }

    [Fact]
    public void BoundedEventBuffer_WhenFull_EvictsOldestNonTerminalEvents()
    {
        var buffer = new BoundedEventBuffer(2);

        buffer.Add(Envelope("server.connected"));
        buffer.Add(Envelope("message.updated"));
        buffer.Add(Envelope("message.part.updated", "text"));

        Assert.Equal(2, buffer.Count);
        Assert.True(buffer.IsOverflowed);

        var snapshot = buffer.GetSnapshot();
        Assert.Equal("message.updated", snapshot[0].Type);
        Assert.Equal("message.part.updated", snapshot[1].Type);
    }

    [Fact]
    public void BoundedEventBuffer_WhenFull_PreservesTerminalEvents()
    {
        var buffer = new BoundedEventBuffer(2);

        var oldest = Envelope("message.part.updated", "text");
        var idle = Envelope("session.idle");

        buffer.Add(oldest);
        buffer.Add(Envelope("message.updated"));
        buffer.Add(idle);

        Assert.True(buffer.IsOverflowed);
        Assert.Equal(2, buffer.Count);
        Assert.DoesNotContain(oldest, buffer.GetSnapshot());
        Assert.Contains(idle, buffer.GetSnapshot());

        var stepFinish = Envelope("message.part.updated", "step-finish");
        buffer.Add(stepFinish);

        var snapshot = buffer.GetSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(idle, snapshot);
        Assert.Contains(stepFinish, snapshot);
        Assert.DoesNotContain(oldest, snapshot);
    }

    [Fact]
    public void BoundedEventBuffer_StepFinishCannotEvictConfirmedTurnCompletion()
    {
        var buffer = new BoundedEventBuffer(1);
        var completed = Envelope("session.idle");
        buffer.Add(completed);
        buffer.Add(Envelope("message.part.updated", "step-finish"));

        Assert.Same(completed, Assert.Single(buffer.GetSnapshot()));
        Assert.True(buffer.IsOverflowed);
    }

    [Fact]
    public async Task SubscribeAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var content = new StreamContent(new BlockingEventStream("data: " + ServerConnected + "\n\n"));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.SubscribeAsync(null, cancellation.Token))
            {
                cancellation.Cancel();
            }
        });
    }

    [Fact]
    public async Task SubscribeAsync_WhenConnectionDrops_ReconnectsWithoutResendingPrompt()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var call = Interlocked.Increment(ref callCount);

            return Task.FromResult(call == 1
                ? SseResponse(ServerConnected)
                : SseResponse(TextPart, SessionIdle));
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        var events = await CollectAsync(service, 3);

        Assert.Equal(
            new[] { "server.connected", "message.part.updated", "session.idle" },
            events.Select(envelope => envelope.Type));
        Assert.True(handler.Requests.Count >= 2);
        Assert.All(handler.Requests, request => Assert.Equal("GET", request.Method));
        Assert.All(handler.Requests, request => Assert.Equal("/event", request.Path));
        Assert.DoesNotContain(handler.Requests, request => string.Equals(request.Method, "POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenCodeClient_SubscribeEventsAsync_DelegatesToEventStreamService()
    {
        var handler = new StubHttpMessageHandler(
            (_, _) => Task.FromResult(SseResponse(ServerConnected)));
        using var httpClient = CreateHttpClient(handler);
        var streamService = new FakeEventStreamService();
        var client = new OpenCodeClient(httpClient, BaseUrl, eventStreamService: streamService);

        var events = new List<OpenCodeEventEnvelope>();

        await foreach (var envelope in client.SubscribeEventsAsync("ses_test01"))
        {
            events.Add(envelope);
        }

        var single = Assert.Single(events);
        Assert.Equal("server.connected", single.Type);
        Assert.Equal("ses_test01", streamService.LastSessionId);
    }

    [Fact]
    public void OpenCodeClient_SubscribeEventsAsync_WithoutStreamService_Throws()
    {
        using var httpClient = CreateHttpClient(
            new StubHttpMessageHandler((_, _) => Task.FromResult(SseResponse(ServerConnected))));
        var client = new OpenCodeClient(httpClient, BaseUrl);

        Assert.Throws<InvalidOperationException>(() => client.SubscribeEventsAsync());
    }

    private static OpenCodeEventStreamService CreateService(HttpClient httpClient, string? spoolDirectory = null)
    {
        var options = new OpenCodeStreamOptions
        {
            SpoolDirectory = spoolDirectory,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(1),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(10)
        };

        return new OpenCodeEventStreamService(httpClient, BaseUrl, options: options);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static HttpResponseMessage SseResponse(params string[] payloads)
    {
        var builder = new StringBuilder();

        foreach (var payload in payloads)
        {
            builder.Append("data: ").Append(payload).Append('\n').Append('\n');
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream")
        };
    }

    private static async Task<List<OpenCodeEventEnvelope>> CollectAsync(
        IOpenCodeEventStreamService service,
        int count,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var events = new List<OpenCodeEventEnvelope>();

        await foreach (var envelope in service.SubscribeAsync(sessionId, cancellationToken))
        {
            events.Add(envelope);

            if (events.Count == count)
            {
                break;
            }
        }

        return events;
    }

    private static OpenCodeEventEnvelope Envelope(string type, string? partType = null)
    {
        var payload = partType is null
            ? $"{{\"type\":\"{type}\",\"properties\":{{}}}}"
            : $"{{\"type\":\"{type}\",\"properties\":{{\"part\":{{\"type\":\"{partType}\"}}}}}}";

        using var document = JsonDocument.Parse(payload);

        return new OpenCodeEventEnvelope(
            type,
            document.RootElement.GetProperty("properties").Clone(),
            payload,
            DateTime.UtcNow);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "llmworkgui-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);

        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FakeEventStreamService : IOpenCodeEventStreamService
    {
        public string? LastSessionId { get; private set; }

        public bool IsOverflowed => false;

        public string? SpoolFilePath => null;

        public IReadOnlyList<OpenCodeEventEnvelope> GetRecentEvents(int? maxCount = null)
        {
            return Array.Empty<OpenCodeEventEnvelope>();
        }

        public async IAsyncEnumerable<OpenCodeEventEnvelope> SubscribeAsync(
            string? sessionId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastSessionId = sessionId;

            await Task.CompletedTask;

            yield return Envelope("server.connected");
        }
    }

    private sealed class ControlledWriteFailureStream : Stream
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FailWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await FailWrite.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException("Synthetic mid-stream write failure.");
        }

        protected override void Dispose(bool disposing)
        {
            FailWrite.TrySetResult();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BlockingEventStream : Stream
    {
        private readonly byte[] _prefix;
        private int _position;

        public BlockingEventStream(string prefix)
        {
            _prefix = Encoding.UTF8.GetBytes(prefix);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _position);

                _prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;

                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
