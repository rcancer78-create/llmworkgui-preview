using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Processes;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Events;
using LLMWorkGUI.Backends.OpenCode;
using LLMWorkGUI.Backends.OpenCode.DependencyInjection;
using LLMWorkGUI.Backends.OpenCode.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests;

public sealed class OpenCodeSseContractTests
{
    private static readonly string[] ExpectedFixtureEventTypes =
    {
        "server.connected",
        "session.updated",
        "message.updated",
        "message.part.updated",
        "message.updated",
        "message.part.updated",
        "message.part.updated",
        "message.part.updated",
        "message.part.updated",
        "message.part.updated",
        "message.part.updated",
        "message.updated",
        "session.updated",
        "session.idle"
    };

    [Fact]
    public async Task Parser_ParsesAllEventsFromFixtureAsSseFrames()
    {
        var payloads = ReadFixturePayloads();

        Assert.Equal(14, payloads.Length);

        var envelopes = await ParseAllAsync(ToSse(payloads));

        Assert.Equal(ExpectedFixtureEventTypes, envelopes.Select(envelope => envelope.Type));
        Assert.All(envelopes, envelope => Assert.Equal(DateTimeKind.Utc, envelope.ReceivedAtUtc.Kind));
        Assert.Equal(payloads, envelopes.Select(envelope => envelope.RawJson));
        Assert.All(envelopes, envelope => Assert.Equal(JsonValueKind.Object, envelope.Properties.ValueKind));
    }

    [Fact]
    public async Task Parser_HandlesCommentsEmptyLinesAndMultilineData()
    {
        const string Sse = """
        : keep-alive comment
        event: message.part.updated

        data: {"type":"message.part.updated","properties":
        data: {"part":{"id":"prt_1","sessionID":"ses_1","messageID":"msg_1","type":"text","text":"hello"}}}

        data: {"type":"session.idle","properties":{"sessionID":"ses_1"}}
        """;

        var envelopes = await ParseAllAsync(Sse);

        Assert.Equal(2, envelopes.Count);

        Assert.True(MessagePartUpdatedEvent.TryParse(envelopes[0], out var part));
        Assert.NotNull(part);
        Assert.Equal("prt_1", part.PartId);
        Assert.Equal("text", part.PartType);
        Assert.Equal("hello", part.Text);

        Assert.True(SessionIdleEvent.TryParse(envelopes[1], out var idle));
        Assert.NotNull(idle);
        Assert.Equal("ses_1", idle.SessionId);
    }

    [Fact]
    public async Task Parser_WhenJsonIsMalformed_EmitsStructuredMalformedEventAndContinues()
    {
        const string Sse = """
        data: {"type":"server.connected","properties":{}}

        data: { this is not json

        data: {"type":"session.idle","properties":{"sessionID":"ses_1"}}
        """;

        var envelopes = await ParseAllAsync(Sse);

        Assert.Equal(3, envelopes.Count);
        Assert.Equal("server.connected", envelopes[0].Type);
        Assert.Equal("malformed", envelopes[1].Type);
        Assert.True(envelopes[1].IsMalformed);
        Assert.Equal("{ this is not json", envelopes[1].RawJson);
        Assert.Equal("session.idle", envelopes[2].Type);
    }

    [Fact]
    public async Task Parser_WhenPayloadHasNoType_FallsBackToEventField()
    {
        const string Sse = """
        event: server.connected
        data: {"properties":{}}
        """;

        var envelopes = await ParseAllAsync(Sse);

        var envelope = Assert.Single(envelopes);
        Assert.Equal("server.connected", envelope.Type);
        Assert.True(ServerConnectedEvent.TryParse(envelope, out var connected));
        Assert.NotNull(connected);
    }

    [Fact]
    public async Task Parser_WhenStreamBreaks_UsesOnlyCompleteEvents()
    {
        var payloads = ReadFixturePayloads();
        var completeEvent = ToSse(new[] { payloads[0] });
        var bytes = Encoding.UTF8.GetBytes(completeEvent);
        var stream = new FaultingStream(bytes, bytes.Length);

        var envelopes = await ParseAllAsync(stream);

        var envelope = Assert.Single(envelopes);
        Assert.Equal("server.connected", envelope.Type);
    }

    [Fact]
    public async Task Parser_HandlesChunkedStreamBoundaries()
    {
        var payloads = ReadFixturePayloads();
        var sse = ToSse(payloads);
        var bytes = Encoding.UTF8.GetBytes(sse);
        var stream = new ChunkedStream(bytes, chunkSize: 3);

        var envelopes = await ParseAllAsync(stream);

        Assert.Equal(ExpectedFixtureEventTypes, envelopes.Select(envelope => envelope.Type));
    }

    [Fact]
    public async Task TypedEvents_ProjectFixtureEvents()
    {
        var envelopes = await ParseAllAsync(ToSse(ReadFixturePayloads()));

        Assert.True(ServerConnectedEvent.TryParse(envelopes[0], out _));

        Assert.True(SessionUpdatedEvent.TryParse(envelopes[1], out var session));
        Assert.NotNull(session);
        Assert.Equal("ses_test01sanitized0000000000000001", session.SessionId);
        Assert.Equal("Sanitized demo session", session.Title);
        Assert.Equal(@"C:\workspace\demo-app", session.Directory);
        Assert.Equal(1789948801000L, session.UpdatedAtUnixMilliseconds);
        Assert.NotNull(session.UpdatedAtUtc);

        Assert.True(MessageUpdatedEvent.TryParse(envelopes[2], out var userMessage));
        Assert.NotNull(userMessage);
        Assert.Equal("msg_test01user00000000000000000001", userMessage.MessageId);
        Assert.Equal("ses_test01sanitized0000000000000001", userMessage.SessionId);
        Assert.Equal("user", userMessage.Role);
        Assert.Null(userMessage.CompletedAtUtc);

        Assert.True(MessagePartUpdatedEvent.TryParse(envelopes[3], out var userText));
        Assert.NotNull(userText);
        Assert.Equal("text", userText.PartType);
        Assert.StartsWith("List the files", userText.Text, StringComparison.Ordinal);

        Assert.True(MessagePartUpdatedEvent.TryParse(envelopes[5], out var stepStart));
        Assert.NotNull(stepStart);
        Assert.Equal("step-start", stepStart.PartType);

        Assert.True(MessagePartUpdatedEvent.TryParse(envelopes[7], out var runningTool));
        Assert.NotNull(runningTool);
        Assert.Equal("tool", runningTool.PartType);
        Assert.Equal("call_test01read0000000000000001", runningTool.CallId);
        Assert.Equal("read", runningTool.Tool);
        Assert.Equal("running", runningTool.ToolStatus);
        Assert.Equal(
            @"C:\workspace\demo-app\README.md",
            runningTool.ToolInput.GetProperty("filePath").GetString());

        Assert.True(MessagePartUpdatedEvent.TryParse(envelopes[8], out var completedTool));
        Assert.NotNull(completedTool);
        Assert.Equal("completed", completedTool.ToolStatus);
        Assert.StartsWith("Sanitized README placeholder", completedTool.ToolOutput.GetString(), StringComparison.Ordinal);

        Assert.True(MessagePartUpdatedEvent.TryParse(envelopes[10], out var stepFinish));
        Assert.NotNull(stepFinish);
        Assert.Equal("step-finish", stepFinish.PartType);
        Assert.Equal("stop", stepFinish.Reason);
        Assert.NotNull(stepFinish.Tokens);
        Assert.Equal(1234, stepFinish.Tokens.Input);
        Assert.Equal(567, stepFinish.Tokens.Output);
        Assert.Equal(123, stepFinish.Tokens.Reasoning);
        Assert.Equal(800, stepFinish.Tokens.CacheRead);
        Assert.Equal(200, stepFinish.Tokens.CacheWrite);

        Assert.True(MessageUpdatedEvent.TryParse(envelopes[11], out var assistantMessage));
        Assert.NotNull(assistantMessage);
        Assert.Equal("assistant", assistantMessage.Role);
        Assert.Equal("stop", assistantMessage.Finish);
        Assert.NotNull(assistantMessage.CompletedAtUtc);

        Assert.True(SessionIdleEvent.TryParse(envelopes[13], out var idle));
        Assert.NotNull(idle);
        Assert.Equal("ses_test01sanitized0000000000000001", idle.SessionId);
    }

    [Fact]
    public void PermissionRequestedEvent_ParsesSanitizedPermissionPayload()
    {
        const string Payload = """
        {
          "type": "permission.updated",
          "properties": {
            "id": "per_test01sanitized0000000000000001",
            "type": "read",
            "pattern": "C:\\workspace\\demo-app\\**",
            "sessionID": "ses_test01sanitized0000000000000001",
            "messageID": "msg_test01assistant0000000000000001",
            "callID": "call_test01read0000000000000001",
            "title": "Read file",
            "time": { "created": 1789948802000 }
          }
        }
        """;

        using var document = JsonDocument.Parse(Payload);
        var envelope = new OpenCodeEventEnvelope(
            "permission.updated",
            document.RootElement.GetProperty("properties").Clone(),
            Payload,
            DateTime.UtcNow);

        Assert.True(PermissionRequestedEvent.TryParse(envelope, out var permission));
        Assert.NotNull(permission);
        Assert.Equal("per_test01sanitized0000000000000001", permission.RequestId);
        Assert.Equal("read", permission.Kind);
        Assert.Equal("ses_test01sanitized0000000000000001", permission.SessionId);
        Assert.Equal("call_test01read0000000000000001", permission.CallId);
        Assert.Equal("Read file", permission.Title);
        Assert.Equal(1789948802000L, permission.CreatedAtUnixMilliseconds);
    }

    [Fact]
    public void StreamOptions_DefaultsAndValidation()
    {
        var options = new OpenCodeStreamOptions();

        Assert.Equal(1000, options.MaxInMemoryEvents);
        Assert.Null(options.SpoolDirectory);
        Assert.Equal(TimeSpan.FromSeconds(1), options.ReconnectInitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ReconnectMaxDelay);

        options.Validate();
    }

    [Fact]
    public void StreamOptions_WhenInvalid_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenCodeStreamOptions { MaxInMemoryEvents = 0 }.Validate());

        Assert.Throws<ArgumentException>(
            () => new OpenCodeStreamOptions { SpoolDirectory = "  " }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenCodeStreamOptions { ReconnectInitialDelay = TimeSpan.Zero }.Validate());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenCodeStreamOptions
            {
                ReconnectInitialDelay = TimeSpan.FromSeconds(5),
                ReconnectMaxDelay = TimeSpan.FromSeconds(1)
            }.Validate());
    }

    [Fact]
    public void ReconnectDelay_StaysWithinConfiguredBounds()
    {
        var initialDelay = TimeSpan.FromMilliseconds(100);
        var maxDelay = TimeSpan.FromSeconds(10);

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var delay = OpenCodeEventStreamService.CalculateReconnectDelay(attempt, initialDelay, maxDelay);

            Assert.InRange(delay, TimeSpan.FromMilliseconds(1), maxDelay);
        }
    }

    [Fact]
    public void AddOpenCodeBackend_RegistersEventStreamingServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessSupervisor, StubProcessSupervisor>();
        services.AddOpenCodeBackend();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<OpenCodeSseParser>(provider.GetRequiredService<IOpenCodeSseParser>());
        Assert.IsType<OpenCodeEventStreamService>(provider.GetRequiredService<IOpenCodeEventStreamService>());
        Assert.IsType<OpenCodeClient>(provider.GetRequiredService<IOpenCodeClient>());
    }

    private static async Task<List<OpenCodeEventEnvelope>> ParseAllAsync(string sse)
    {
        return await ParseAllAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
    }

    private static async Task<List<OpenCodeEventEnvelope>> ParseAllAsync(Stream stream)
    {
        var parser = new OpenCodeSseParser();
        var envelopes = new List<OpenCodeEventEnvelope>();

        await foreach (var envelope in parser.ParseAsync(stream))
        {
            envelopes.Add(envelope);
        }

        return envelopes;
    }

    private static string ToSse(IEnumerable<string> payloads)
    {
        var builder = new StringBuilder();

        foreach (var payload in payloads)
        {
            builder.Append("data: ").Append(payload).Append('\n').Append('\n');
        }

        return builder.ToString();
    }

    private static string[] ReadFixturePayloads()
    {
        return File.ReadAllLines(FixturePath("event-stream-sample.jsonl"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(FindRepositoryRoot(), "docs", "protocols", "opencode", fileName);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LLMWorkGUI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root containing 'LLMWorkGUI.sln' was not found.");
    }

    private sealed class StubProcessSupervisor : IProcessSupervisor
    {
        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessStartSpecification specification,
            IProgress<ProcessOutputEvent>? outputProgress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FaultingStream : Stream
    {
        private readonly byte[] _content;
        private readonly int _availableBytes;
        private int _position;

        public FaultingStream(byte[] content, int availableBytes)
        {
            _content = content;
            _availableBytes = availableBytes;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_position >= _availableBytes)
            {
                throw new IOException("Simulated network failure.");
            }

            var count = Math.Min(buffer.Length, _availableBytes - _position);

            _content.AsMemory(_position, count).CopyTo(buffer);
            _position += count;

            return ValueTask.FromResult(count);
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

    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _content;
        private readonly int _chunkSize;
        private int _position;

        public ChunkedStream(byte[] content, int chunkSize)
        {
            _content = content;
            _chunkSize = chunkSize;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_position >= _content.Length)
            {
                return ValueTask.FromResult(0);
            }

            var count = Math.Min(Math.Min(buffer.Length, _chunkSize), _content.Length - _position);

            _content.AsMemory(_position, count).CopyTo(buffer);
            _position += count;

            return ValueTask.FromResult(count);
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
