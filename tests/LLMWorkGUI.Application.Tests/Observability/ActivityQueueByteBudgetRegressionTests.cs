using LLMWorkGUI.Application.Observability;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class ActivityQueueByteBudgetRegressionTests
{
    [Theory]
    [InlineData("description")]
    [InlineData("diff")]
    [InlineData("artifact")]
    public async Task StalledJournalRefusesByteHeavyBacklogBeforeItsRowCapacity(string payloadField)
    {
        var journal = new DeferredJournal();
        await using var queue = new ActivityJournalWriteQueue(journal, capacity: 1000, batchSize: 1);
        var payload = new string('x', 64 * 1024);
        var accepted = 0;
        try
        {
            Assert.True(queue.TryEnqueue(Event("in-flight", payloadField, payload)));
            accepted++;
            await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 600; index++)
                if (queue.TryEnqueue(Event($"backlog-{index}", payloadField, payload))) accepted++;

            Assert.True(queue.DroppedCount > 0, "The queue must bound bytes as well as object count.");
            Assert.InRange(accepted, 1, 512); // 64 MiB of UTF-16 payload, including the in-flight row.
            Assert.Equal(601, queue.OfferedCount);
            Assert.Equal(accepted, queue.PendingCount);
            Assert.InRange(queue.PendingBytes, 1, queue.ByteCapacity);
        }
        finally { journal.Release.TrySetResult(); }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.DrainAsync(deadline.Token);
        Assert.Equal(accepted, queue.WrittenCount);
        Assert.Equal(0, queue.FailedCount);
        Assert.Equal(0, queue.PendingBytes);
        Assert.True(queue.IsBalanced);
        Assert.True(queue.TryEnqueue(Event("after-drain", payloadField, "small")));
        await queue.DrainAsync(deadline.Token);
        Assert.Equal(accepted + 1, queue.WrittenCount);
        Assert.True(queue.IsBalanced);
    }

    [Fact]
    public async Task FailedDurableWritesReleaseTheByteBudgetWithoutClaimingPersistence()
    {
        var journal = new DeferredJournal { FailWrites = true };
        await using var queue = new ActivityJournalWriteQueue(journal, capacity: 100, batchSize: 1,
            byteCapacity: 4096);
        var accepted = 0;
        try
        {
            Assert.True(queue.TryEnqueue(Event("in-flight", "description", new string('x', 1000))));
            accepted++;
            await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 10; index++)
                if (queue.TryEnqueue(Event($"backlog-{index}", "description", new string('x', 1000)))) accepted++;
            Assert.True(queue.ByteBudgetDroppedCount > 0);
        }
        finally { journal.Release.TrySetResult(); }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await queue.DrainAsync(deadline.Token);
        Assert.Equal(accepted, queue.FailedCount);
        Assert.Equal(0, queue.WrittenCount);
        Assert.Equal(0, queue.PendingBytes);
        Assert.True(queue.IsBalanced);
        journal.FailWrites = false;
        Assert.True(queue.TryEnqueue(Event("after-failure", "description", "small")));
        await queue.DrainAsync(deadline.Token);
        Assert.Equal(1, queue.WrittenCount);
        Assert.Equal(0, queue.PendingBytes);
        Assert.True(queue.IsBalanced);
        Assert.True(queue.IsLossy);
    }

    private static ActivityEvent Event(string id, string field, string payload) => new(
        id, DateTimeOffset.UnixEpoch, ActivityEventKind.System, ActivityRoleNames.System,
        ActivityEventState.Completed, ActivityEventSource.Synthetic, "Fixture",
        field == "description" ? payload : "Metadata", diffText: field == "diff" ? payload : null,
        artifactContent: field == "artifact" ? payload : null);

    private sealed class DeferredJournal : IActivityEventJournal
    {
        public bool FailWrites { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task AppendAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
            => AppendRangeAsync([activityEvent], cancellationToken);
        public async Task AppendRangeAsync(IReadOnlyList<ActivityEvent> events, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (FailWrites) throw new IOException("Synthetic durable write failure.");
        }
        public Task<IReadOnlyList<ActivityEvent>> LoadNewestAsync(int limit, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<long> CountAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ActivityJournalTrimResult> TrimAsync(int limit, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ActivityJournalPage QueryPage(ActivityJournalQuery query) => throw new NotSupportedException();
    }
}
