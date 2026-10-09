using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class JsonRpcTransportCleanupRaceTests
{
    [Fact]
    public async Task ResponseWriteFailureFailsQueuedResponsesAndPendingRequests()
    {
        var input = new HeldWriteStream { FailOnWrite = true };
        await using var transport = new JsonRpcStdioTransport(new HeldReadStream(), input);
        var first = transport.SendResponseAsync(JsonSerializer.SerializeToElement(1), JsonSerializer.SerializeToElement(false));
        try
        {
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = transport.SendResponseAsync(JsonSerializer.SerializeToElement(2), JsonSerializer.SerializeToElement(false));
            var request = transport.SendRequestAsync("initialize", timeout: TimeSpan.FromSeconds(10));
            input.Release.TrySetResult();
            foreach (var response in new[] { first, second })
                Assert.Equal(JsonRpcTransportFailureKind.WriteFailed,
                    (await Assert.ThrowsAsync<JsonRpcTransportException>(() => response.WaitAsync(TimeSpan.FromSeconds(5)))).Kind);
            Assert.Equal(JsonRpcTransportFailureKind.WriteFailed,
                (await Assert.ThrowsAsync<JsonRpcTransportException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)))).Kind);
            Assert.Equal(1, input.WriteCount);
            Assert.Equal(0, input.Length);
            Assert.Equal(0, transport.PendingRequestCount);
        }
        finally { input.Release.TrySetResult(); await transport.CloseAsync(); }
    }

    [Fact]
    public async Task CloseFailsWritingAndQueuedResponseWaitersWithoutClaimingSuccess()
    {
        var input = new HeldWriteStream();
        await using var transport = new JsonRpcStdioTransport(new HeldReadStream(), input);
        var first = transport.SendResponseAsync(JsonSerializer.SerializeToElement(1), JsonSerializer.SerializeToElement(false));
        Task? closing = null;
        try
        {
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = transport.SendResponseAsync(JsonSerializer.SerializeToElement(2), JsonSerializer.SerializeToElement(false));
            closing = transport.CloseAsync();
            foreach (var response in new[] { first, second })
                Assert.Equal(JsonRpcTransportFailureKind.TransportClosed,
                    (await Assert.ThrowsAsync<JsonRpcTransportException>(() => response.WaitAsync(TimeSpan.FromSeconds(5)))).Kind);
            Assert.False(closing.IsCompleted);
            Assert.False(input.Disposed);
        }
        finally
        {
            input.Release.TrySetResult();
            await (closing ?? transport.CloseAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task SendResponseWaitsForItsActualStdioWriteBeforeClaimingCompletion()
    {
        var input = new HeldWriteStream();
        var output = new HeldReadStream();
        await using var transport = new JsonRpcStdioTransport(output, input);
        var sending = transport.SendResponseAsync(JsonSerializer.SerializeToElement(7),
            JsonSerializer.SerializeToElement(new { outcome = "cancelled" }));
        try
        {
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(sending.IsCompleted,
                "A queued response cannot release exact-ID ownership before its physical write finishes.");
            Assert.False(input.Disposed);
        }
        finally
        {
            input.Release.TrySetResult();
            await sending.WaitAsync(TimeSpan.FromSeconds(5));
            await transport.CloseAsync();
        }
    }

    [Fact]
    public async Task ConcurrentCloseWaitsForSameOutstandingWriterDrain()
    {
        var input = new HeldWriteStream();
        var output = new HeldReadStream();
        var transport = new JsonRpcStdioTransport(output, input);
        await transport.SendNotificationAsync("held-write");
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstClose = transport.CloseAsync();
        var secondClose = transport.CloseAsync();
        try
        {
            Assert.False(secondClose.IsCompleted,
                "A second close must await the owned writer instead of claiming cleanup completed.");
            Assert.False(input.Disposed);
        }
        finally
        {
            input.Release.TrySetResult();
            await Task.WhenAll(firstClose, secondClose).WaitAsync(TimeSpan.FromSeconds(10));
            await transport.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeDuringCloseDoesNotDisposeTheActiveWriter()
    {
        var input = new HeldWriteStream();
        var output = new HeldReadStream();
        var transport = new JsonRpcStdioTransport(output, input);
        await transport.SendNotificationAsync("held-write");
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = transport.CloseAsync();
        var disposing = transport.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposing.IsCompleted);
            Assert.False(input.Disposed);
        }
        finally
        {
            input.Release.TrySetResult();
            await Task.WhenAll(closing, disposing).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(input.Disposed);
        Assert.Equal(1, input.DisposeCount);
    }

    private sealed class HeldReadStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class HeldWriteStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public bool FailOnWrite { get; init; }
        public int WriteCount { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (FailOnWrite) throw new IOException("Synthetic physical write failure.");
            await base.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposed = true; DisposeCount++; }
            base.Dispose(disposing);
        }
    }
}
