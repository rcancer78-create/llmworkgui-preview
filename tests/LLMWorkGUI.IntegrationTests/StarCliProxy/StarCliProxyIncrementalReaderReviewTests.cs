using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LLMWorkGUI.Backends.Abstractions.StarCliProxy;
using LLMWorkGUI.Infrastructure.StarCliProxy;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.StarCliProxy;

public sealed class StarCliProxyIncrementalReaderReviewTests
{
    [Fact]
    public async Task CompleteUnicodeFrameIsYieldedBeforeReadingHeldNativeTail()
    {
        const string prefix = "data: {\"choices\":[{\"delta\":{\"content\":\"";
        const string suffix = "\"}}]}\n\n";
        var available = 4096 - Encoding.UTF8.GetByteCount(prefix + suffix);
        var content = new string('я', available / 2) + new string('a', available % 2);
        var bytes = Encoding.UTF8.GetBytes(prefix + content + suffix);
        Assert.Equal(4096, bytes.Length);
        using var input = new HeldTailStream(bytes);
        using var http = new HttpClient(new Handler(input)) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new StarCliProxyClient(http);
        var request = StarCliProxyChatRequest.Create("synthetic-model", [new StarCliProxyChatMessage("user", "synthetic prompt")]);
        await using var events = client.StreamChatCompletionAsync(StarCliProxyEndpoint.Loopback(8300), request,
            CancellationToken.None).GetAsyncEnumerator();
        var first = events.MoveNextAsync().AsTask();
        try
        {
            await input.FirstBufferRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(StarCliProxyStreamEventKind.ContentDelta, events.Current.Kind);
            Assert.Equal(content, events.Current.Content);
            Assert.False(input.TailReleased);
        }
        finally
        {
            input.ReleaseTail();
            await first.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private sealed class Handler(Stream input) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = new StreamContent(input);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class HeldTailStream(byte[] bytes) : Stream
    {
        private readonly TaskCompletionSource _tail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        public TaskCompletionSource FirstBufferRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TailReleased => _tail.Task.IsCompleted;
        public void ReleaseTail() => _tail.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == bytes.Length)
            {
                await _tail.Task.WaitAsync(cancellationToken);
                return 0;
            }
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            if (_position == bytes.Length) FirstBufferRead.TrySetResult();
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { ReleaseTail(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
