using System.Text.Json;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Infrastructure.CursorAcp;
using Xunit;

namespace LLMWorkGUI.Backends.ContractTests.CursorAcp;

public sealed class JsonRpcNotificationOverflowReviewTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SaturatedNotificationQueueCannotReturnSuccessfulPromptAfterLosingEvents()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = new JsonRpcStdioTransport(harness.TransportInput, harness.TransportOutput,
            new CursorAcpOptions { NotificationQueueCapacity = 2 });
        var pending = transport.SendRequestAsync("session/prompt", null, Deadline);
        using var request = JsonDocument.Parse(await harness.ReadFrameAsync(Deadline));
        var id = request.RootElement.GetProperty("id").GetInt32();
        for (var index = 0; index < 8; index++)
            await harness.WriteRawLineAsync(Notification(index));
        await harness.WriteRawLineAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id, result = new { stopReason = "end_turn" } }));

        var failure = await Assert.ThrowsAsync<JsonRpcTransportException>(async () => await pending.WaitAsync(Deadline));
        Assert.Equal(JsonRpcTransportFailureKind.ReadFailed, failure.Kind);
        Assert.Equal(0, transport.PendingRequestCount);
        var observed = new List<int>();
        while (transport.Notifications.TryRead(out var notification))
            observed.Add(notification.Parameters!.Value.GetProperty("index").GetInt32());
        Assert.Equal(new[] { 0, 1 }, observed);
        await Assert.ThrowsAsync<JsonRpcTransportException>(async () => await transport.Notifications.Completion.WaitAsync(Deadline));
        await Assert.ThrowsAsync<JsonRpcTransportException>(() => transport.SendRequestAsync("another", null, Deadline));
    }

    [Fact]
    public async Task AdequatelySizedQueuePreservesEventOrderAndCorrelatedTerminalResponse()
    {
        await using var harness = new LoopbackJsonRpcHarness();
        await using var transport = new JsonRpcStdioTransport(harness.TransportInput, harness.TransportOutput,
            new CursorAcpOptions { NotificationQueueCapacity = 16 });
        var pending = transport.SendRequestAsync("session/prompt", null, Deadline);
        using var request = JsonDocument.Parse(await harness.ReadFrameAsync(Deadline));
        var id = request.RootElement.GetProperty("id").GetInt32();
        for (var index = 0; index < 8; index++)
            await harness.WriteRawLineAsync(Notification(index));
        await harness.WriteRawLineAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id, result = new { stopReason = "end_turn" } }));
        Assert.False((await pending.WaitAsync(Deadline)).IsError);
        for (var index = 0; index < 8; index++)
        {
            var notification = await transport.Notifications.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.Equal(index, notification.Parameters!.Value.GetProperty("index").GetInt32());
        }
        Assert.Equal(0, transport.DroppedNotificationCount);
    }

    private static string Notification(int index) => JsonSerializer.Serialize(new
        { jsonrpc = "2.0", method = "session/update", @params = new { index } });
}
