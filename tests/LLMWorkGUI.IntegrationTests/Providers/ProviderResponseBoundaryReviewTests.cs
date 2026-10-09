using System.Net;
using System.Text;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Infrastructure.Providers;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

// Deterministic HTTP-content boundaries, not native provider or productive account evidence.
public sealed class ProviderResponseBoundaryReviewTests
{
    [Fact]
    public async Task ConnectionDeadlineIncludesReadingTheResponseBody()
    {
        using var stream = new HeldBodyStream();
        using var http = new HttpClient(new ContentHandler(() => new StreamContent(stream)));
        using var caller = new CancellationTokenSource();
        var pending = new ProviderConnectionTestService(httpClient: http).TestConnectionAsync(
            new CustomProviderSettings("owned", "Owned", "http://127.0.0.1/owned"),
            timeout: TimeSpan.FromMilliseconds(50), cancellationToken: caller.Token);
        try
        {
            var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(pending, finished);
            Assert.Equal(ProviderConnectionStatus.TimedOut, (await pending).Status);
        }
        finally
        {
            caller.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task OversizedModelResponseIsRefusedBeforeUnboundedBuffering()
    {
        using var stream = new TrackedMemoryStream(Encoding.UTF8.GetBytes(
            new string(' ', 4 * 1024 * 1024 + 64 * 1024) + "{\"data\":[]}"));
        using var http = new HttpClient(new ContentHandler(() => new StreamContent(stream)));
        var result = await new ProviderConnectionTestService(httpClient: http).TestConnectionAsync(
            new CustomProviderSettings("owned", "Owned", "http://127.0.0.1/owned"));
        Assert.Equal(ProviderConnectionStatus.UnknownError, result.Status);
        Assert.True(stream.PositionAtDisposal <= 4 * 1024 * 1024 + 8192);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void InvalidExistingConfigurationCannotBeReplacedByAManagedOnlyDocument(string existing)
    {
        Assert.ThrowsAny<Exception>(() => new OpenCodeConfigService().MergeConfig(existing,
            new CustomProviderSettings("owned", "Owned", "http://127.0.0.1/owned")));
    }

    [Fact]
    public async Task CancellationWhileReadingPluginConfigurationRemainsCancellation()
    {
        using var directory = new TestDirectory();
        var path = directory.GetPath("config.json");
        await File.WriteAllTextAsync(path, "{\"plugin\":[\"ordinary\"]}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OpenCodePluginInventoryService().LoadPluginsFromConfigFileAsync(path, cancellation.Token));
    }

    private sealed class ContentHandler(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
    }

    private sealed class TrackedMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long PositionAtDisposal;
        protected override void Dispose(bool disposing)
        {
            if (disposing && CanRead) PositionAtDisposal = Position;
            base.Dispose(disposing);
        }
    }

    private sealed class HeldBodyStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
