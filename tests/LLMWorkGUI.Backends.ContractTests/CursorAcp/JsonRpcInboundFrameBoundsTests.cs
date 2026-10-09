using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class JsonRpcInboundFrameBoundsTests
{
    [Fact]
    public async Task MutatingOptionsCannotIncreaseTheAlreadyValidatedTransportFrameLimit()
    {
        var output = new RepeatingAgentOutput();
        var options = new CursorAcpOptions { MaximumInboundFrameCharacters = 64 };
        await using var transport = new JsonRpcStdioTransport(output, new MemoryStream(), options);
        var pending = transport.SendRequestAsync("immutable-bound", timeout: TimeSpan.FromSeconds(10));
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        options.MaximumInboundFrameCharacters = int.MaxValue;
        output.Release.TrySetResult();
        var failure = await Assert.ThrowsAsync<JsonRpcTransportException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(JsonRpcTransportFailureKind.ReadFailed, failure.Kind);
        Assert.InRange(output.BytesRead, 65, 128);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("")]
    public async Task FrameExactlyAtLimitStillDeliversBeforeLineEndingOrEof(string ending)
    {
        var json = "{\"jsonrpc\":\"2.0\",\"method\":\"boundary\"}";
        var output = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json.PadRight(64) + ending));
        await using var transport = new JsonRpcStdioTransport(output, new MemoryStream(),
            new CursorAcpOptions { MaximumInboundFrameCharacters = 64 });
        var notification = await transport.Notifications.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("boundary", notification.Method);
        Assert.Equal(0, transport.MalformedFrameCount);
    }

    [Fact]
    public async Task NonTerminatedOversizedFrameFailsPendingRequestWithoutReadingAnUnboundedLine()
    {
        var output = new RepeatingAgentOutput();
        var input = new MemoryStream();
        await using var transport = new JsonRpcStdioTransport(output, input,
            new CursorAcpOptions { MaximumInboundFrameCharacters = 64 });
        var pending = transport.SendRequestAsync("bounded-read", timeout: TimeSpan.FromSeconds(10));
        Assert.Equal(1, transport.PendingRequestCount);
        output.Release.TrySetResult();
        var failure = await Assert.ThrowsAsync<JsonRpcTransportException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(JsonRpcTransportFailureKind.ReadFailed, failure.Kind);
        Assert.Equal(0, transport.PendingRequestCount);
        Assert.InRange(output.BytesRead, 65, 128);
        Assert.False(output.Disposed);
        await Assert.ThrowsAsync<JsonRpcTransportException>(() => transport.SendNotificationAsync("must-refuse"));
        await Assert.ThrowsAsync<JsonRpcTransportException>(() => transport.Notifications.Completion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InboundFrameLimitMustBePositive(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CursorAcpOptions { MaximumInboundFrameCharacters = limit }.Validate());
    }

    private sealed class RepeatingAgentOutput : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            // Fixed chunks exercise a never-terminated stream without allocating a huge fixture.
            if (BytesRead >= 128)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            buffer.Span[..32].Fill((byte)'x');
            BytesRead += 32;
            return 32;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }
}
