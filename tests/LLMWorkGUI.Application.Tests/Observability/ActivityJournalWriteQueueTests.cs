using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

/// <summary>
/// Behaviour of the bounded hand-off between the synchronous Activity Center ingestion boundary and the
/// asynchronous durable journal.
/// <para>
/// The load driver hung for minutes after saving about two thousand rows. The cause was here and not in
/// the driver: <c>DrainAsync</c> awaited the reader task, and that task only ends when the channel is
/// completed, which only happens on disposal. A caller asking "are all my rows durable yet?" therefore
/// waited for a task that was itself waiting for the caller to finish. These tests pin the corrected
/// contract, because the deadlock is silent - the process simply stops making progress.
/// </para>
/// </summary>
public sealed class ActivityJournalWriteQueueTests
{
    [Fact]
    public async Task CommittedBatchRemainsWrittenWhenSubscriberAndItsDiagnosticLoggerThrow()
    {
        var journal = new RecordingJournal();
        var logger = new ThrowingNotificationLogger();
        await using var queue = new ActivityJournalWriteQueue(journal, batchSize: 1, logger: logger);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Persisted += (_, _) => throw new InvalidOperationException("owned subscriber failure");
        queue.Persisted += (_, _) => observed.TrySetResult();
        Assert.True(queue.TryEnqueue(Event("committed-notification-fault")));
        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, journal.Count);
        Assert.Equal(1, queue.WrittenCount);
        Assert.Equal(0, queue.FailedCount);
        Assert.Equal(0, queue.PendingBytes);
        Assert.True(queue.IsBalanced);
        Assert.True(logger.Calls > 0);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OfferAfterActualDisposalIsCountedAsDroppedWithoutThrowing()
    {
        var journal = new RecordingJournal();
        var queue = new ActivityJournalWriteQueue(journal);
        await queue.DisposeAsync();
        Assert.False(queue.TryEnqueue(Event("after-disposal")));
        Assert.Equal(1, queue.OfferedCount);
        Assert.Equal(1, queue.DroppedCount);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.PendingBytes);
        Assert.Equal(0, journal.Count);
        Assert.True(queue.IsBalanced);
    }

    private sealed class ThrowingNotificationLogger : ILogger<ActivityJournalWriteQueue>
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("owned diagnostic sink failure");
        }
    }

    [Fact]
    public async Task DrainAsync_ReturnsOnceEveryOfferedRowIsDurablyWritten()
    {
        var journal = new RecordingJournal();
        await using var queue = new ActivityJournalWriteQueue(journal, batchSize: 8);

        for (var index = 0; index < 500; index++)
        {
            Assert.True(queue.TryEnqueue(Event($"event-{index}")));
        }

        // Bounded: this must return rather than wait for disposal, which is what the old implementation
        // effectively required and what hung the load run.
        var drained = await DrainWithTimeoutAsync(queue, TimeSpan.FromSeconds(20));

        Assert.True(drained, "DrainAsync did not settle while the queue was still open.");
        Assert.Equal(500, journal.Count);
        Assert.Equal(500, queue.WrittenCount);
        Assert.Equal(0, queue.DroppedCount);
        Assert.Equal(0, queue.FailedCount);
    }

    [Fact]
    public async Task DrainAsync_OnAnIdleQueue_ReturnsImmediately()
    {
        var journal = new RecordingJournal();
        await using var queue = new ActivityJournalWriteQueue(journal);

        Assert.True(await DrainWithTimeoutAsync(queue, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, journal.Count);
        Assert.Equal(0, queue.WrittenCount);
    }

    [Fact]
    public async Task DrainAsync_WritesInBatchesAndPreservesTheAcceptedOrder()
    {
        var journal = new RecordingJournal();
        await using var queue = new ActivityJournalWriteQueue(journal, batchSize: 4);

        for (var index = 0; index < 37; index++)
        {
            queue.TryEnqueue(Event($"ordered-{index:D3}"));
        }

        Assert.True(await DrainWithTimeoutAsync(queue, TimeSpan.FromSeconds(20)));

        // Order matters: the journal is the durable record of what the operator saw, in the order it
        // happened. A reordering writer would silently rewrite that history.
        var expected = new List<string>();

        for (var index = 0; index < 37; index++)
        {
            expected.Add($"ordered-{index:D3}");
        }

        Assert.Equal(expected, journal.Ids);
        Assert.True(journal.BatchCount > 1, "the batch size was not honoured.");
    }

    [Fact]
    public async Task DrainAsync_CountsAFaultingJournalInsteadOfThrowingOrClaimingSuccess()
    {
        var journal = new RecordingJournal { FailEveryWrite = true };
        await using var queue = new ActivityJournalWriteQueue(journal, batchSize: 4);

        for (var index = 0; index < 20; index++)
        {
            queue.TryEnqueue(Event($"faulted-{index}"));
        }

        Assert.True(await DrainWithTimeoutAsync(queue, TimeSpan.FromSeconds(20)));

        Assert.Equal(20, queue.OfferedCount);
        Assert.Equal(0, queue.WrittenCount);
        Assert.Equal(20, queue.FailedCount);
        Assert.True(queue.IsLossy);
    }

    [Fact]
    public async Task JournalCancellation_DoesNotStrandQueuedOrFutureWritesOrRetryTheFailedBatch()
    {
        var journal = new FirstWriteCancellationJournal();
        await using var queue = new ActivityJournalWriteQueue(journal, capacity: 4, batchSize: 1);
        try
        {
            Assert.True(queue.TryEnqueue(Event("cancelled-write")));
            await journal.FirstWriteEntered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(queue.TryEnqueue(Event("already-queued")));
            journal.CancelFirstWrite();

            // Detect a stopped reader directly instead of waiting for a drain timeout.
            await Task.WhenAny(queue.Completion, journal.FirstSuccessfulWrite).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(queue.IsCompleted, "A journal's own cancellation must not terminate the queue reader.");
            Assert.True(await DrainWithTimeoutAsync(queue, TimeSpan.FromSeconds(5)));

            Assert.True(queue.TryEnqueue(Event("future-write")));
            Assert.True(await DrainWithTimeoutAsync(queue, TimeSpan.FromSeconds(5)));

            Assert.Equal(new[] { "already-queued", "future-write" }, journal.Ids);
            Assert.Equal(3, journal.WriteAttempts);
            Assert.Equal(3, queue.OfferedCount);
            Assert.Equal(2, queue.WrittenCount);
            Assert.Equal(1, queue.FailedCount);
            Assert.Equal(0, queue.DroppedCount);
            Assert.Equal(0, queue.PendingCount);
            Assert.True(queue.IsBalanced);
            Assert.True(queue.IsLossy);
            Assert.False(queue.IsCompleted);
        }
        finally
        {
            journal.CancelFirstWrite();
        }
    }

    [Fact]
    public async Task TryEnqueue_AfterShutdown_IsCountedAsDropped()
    {
        var journal = new RecordingJournal();
        var queue = new ActivityJournalWriteQueue(journal);

        queue.TryEnqueue(Event("before-shutdown"));
        Assert.True(await DrainWithTimeoutAsync(queue, TimeSpan.FromSeconds(10)));

        await queue.DisposeAsync();

        Assert.False(queue.TryEnqueue(Event("after-shutdown")));
        Assert.Equal(1, queue.DroppedCount);
    }

    [Fact]
    public void Constructor_RejectsANonPositiveCapacityOrBatchSize()
    {
        var journal = new RecordingJournal();

        Assert.Throws<ArgumentNullException>(() => new ActivityJournalWriteQueue(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityJournalWriteQueue(journal, capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityJournalWriteQueue(journal, batchSize: 0));
    }

    [Fact]
    public async Task TheQueueReportsItsRealCapacityFacts()
    {
        var journal = new RecordingJournal();
        var queue = new ActivityJournalWriteQueue(journal, capacity: 64, batchSize: 16);

        // The capacity was never assigned in the previous implementation, so every measurement report
        // printed a queue depth of 0 and "the queue overflowed" had nothing to overflow.
        Assert.Equal(64, queue.QueueCapacity);
        Assert.Equal(16, queue.BatchCapacity);

        var facts = queue.ToFacts();

        Assert.Equal("64", facts["queueCapacity"]);
        Assert.Equal("16", facts["batchSize"]);
        Assert.Equal("0", facts["dropped"]);
        Assert.Equal("0", facts["failed"]);
        Assert.Equal("true", facts["balanced"]);

        await queue.DisposeAsync();
    }

    /// <summary>
    /// The race this closes: a drain used to settle on "channel empty and nothing in flight", and the
    /// reader only published its in-flight count <em>after</em> dequeuing a batch. A drain that ran in
    /// that window returned while a whole batch was still uncommitted, so a restart immediately after it
    /// could read a journal that was missing rows the run had already counted as accepted.
    /// </summary>
    [Fact]
    public async Task DrainAsyncDoesNotReturnWhileABatchIsStillUncommitted()
    {
        var journal = new BlockingJournal();
        await using var queue = new ActivityJournalWriteQueue(journal, capacity: 1_000, batchSize: 8);

        for (var index = 0; index < 8; index++)
        {
            Assert.True(queue.TryEnqueue(Event($"racy-{index}")));
        }

        // The reader is now sitting inside the durable write with a full batch dequeued.
        await journal.WaitForBlockedWriteAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, journal.Count);

        var drain = queue.DrainAsync();
        var settledEarly = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromMilliseconds(400)));

        Assert.NotSame(drain, settledEarly);
        Assert.False(queue.IsSettled);
        Assert.Equal(8, queue.PendingCount);

        journal.ReleaseBlockedWrite();
        await drain;

        Assert.Equal(8, journal.Count);
        Assert.Equal(8, queue.WrittenCount);
        Assert.Equal(0, queue.PendingCount);
        Assert.True(queue.IsSettled);

        // "Drained" therefore means committed or explicitly failed, and the arithmetic closes.
        Assert.True(queue.IsBalanced);
        Assert.Equal(queue.OfferedCount, queue.WrittenCount + queue.DroppedCount + queue.FailedCount);
    }

    /// <summary>
    /// The negative control for the criterion above: a queue that overflows and a journal that faults must
    /// both end up counted, never silently absent. A drain that "settles" with rows neither written nor
    /// counted is the failure mode this whole queue exists to make visible.
    /// </summary>
    [Fact]
    public async Task AFullOrFaultingQueueIsCountedAndNeverReportedAsFullyPersisted()
    {
        var blocking = new BlockingJournal();
        var overflowing = new ActivityJournalWriteQueue(blocking, capacity: 2, batchSize: 2);

        var accepted = 0;
        var refused = 0;

        for (var index = 0; index < 50; index++)
        {
            if (overflowing.TryEnqueue(Event($"overflow-{index:D3}")))
            {
                accepted++;
            }
            else
            {
                refused++;
            }
        }

        blocking.ReleaseBlockedWrite();
        Assert.True(await DrainWithTimeoutAsync(overflowing, TimeSpan.FromSeconds(20)));
        await overflowing.DisposeAsync();

        Assert.True(refused > 0, "a two-deep queue accepted fifty rows without dropping any.");
        Assert.Equal(overflowing.OfferedCount, accepted + refused);
        Assert.Equal(refused, overflowing.DroppedCount);
        Assert.Equal(accepted, overflowing.WrittenCount);
        Assert.True(overflowing.IsLossy);
        Assert.True(overflowing.IsBalanced);
        Assert.Equal("true", overflowing.ToFacts()["balanced"]);

        var faulting = new ActivityJournalWriteQueue(
            new RecordingJournal { FailEveryWrite = true },
            capacity: 100,
            batchSize: 4);

        for (var index = 0; index < 30; index++)
        {
            faulting.TryEnqueue(Event($"fault-{index:D3}"));
        }

        Assert.True(await DrainWithTimeoutAsync(faulting, TimeSpan.FromSeconds(20)));
        await faulting.DisposeAsync();

        Assert.Equal(30, faulting.OfferedCount);
        Assert.Equal(0, faulting.WrittenCount);
        Assert.Equal(30, faulting.FailedCount);
        Assert.Equal(0, faulting.PendingCount);
        Assert.True(faulting.IsLossy);

        // Balanced, but not lossless: the accounting closes and the loss is still reported. A report may
        // only claim "fully persisted" when IsLossy is false, which is a separate question from whether
        // the counters add up.
        Assert.True(faulting.IsBalanced);
        Assert.False(faulting.IsBalanced && !faulting.IsLossy);
    }

    /// <summary>
    /// Awaits the drain under a hard timeout. Every test here is a hang test, so the assertion that matters
    /// is that the await completes at all: without the timeout a regression would stall the whole suite
    /// instead of failing it.
    /// </summary>
    private static async Task<bool> DrainWithTimeoutAsync(
        ActivityJournalWriteQueue queue,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);

        try
        {
            await queue.DrainAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static ActivityEvent Event(string id) => new(
        id,
        new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
        ActivityEventKind.Execution,
        ActivityRoleNames.Coder,
        ActivityEventState.Running,
        ActivityEventSource.Native,
        $"title {id}",
        $"description {id}");

    private class RecordingJournal : IActivityEventJournal
    {
        private readonly List<string> _ids = new();
        private readonly object _gate = new();

        public bool FailEveryWrite { get; init; }

        public int BatchCount { get; private set; }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _ids.Count;
                }
            }
        }

        public IReadOnlyList<string> Ids
        {
            get
            {
                lock (_gate)
                {
                    return _ids.ToArray();
                }
            }
        }

        public Task AppendAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default) =>
            AppendRangeAsync(new[] { activityEvent }, cancellationToken);

        public virtual Task AppendRangeAsync(
            IReadOnlyList<ActivityEvent> activityEvents,
            CancellationToken cancellationToken = default)
        {
            if (FailEveryWrite)
            {
                throw new InvalidOperationException("the durable store is unavailable");
            }

            lock (_gate)
            {
                BatchCount++;

                foreach (var activityEvent in activityEvents)
                {
                    _ids.Add(activityEvent.Id);
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ActivityEvent>> LoadNewestAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActivityEvent>>(Array.Empty<ActivityEvent>());

        public Task<long> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((long)Count);

        public Task<ActivityJournalTrimResult> TrimAsync(
            int retentionLimit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActivityJournalTrimResult(Count, Count, 0));

        // The queue is a write path; it never queries. A test that needed an answer here would be testing
        // the wrong thing, so the double refuses rather than inventing a plausible page.
        public ActivityJournalPage QueryPage(ActivityJournalQuery query) =>
            throw new NotSupportedException(
                "The write queue never queries the journal; assert on Offered/Written/Dropped/Failed instead.");
    }

    private sealed class FirstWriteCancellationJournal : RecordingJournal
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeAttempts;

        public Task FirstWriteEntered => _entered.Task;
        public Task FirstSuccessfulWrite => _written.Task;
        public int WriteAttempts => Volatile.Read(ref _writeAttempts);
        public void CancelFirstWrite() => _cancel.TrySetResult();

        public override async Task AppendRangeAsync(
            IReadOnlyList<ActivityEvent> activityEvents,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeAttempts) == 1)
            {
                _entered.TrySetResult();
                await _cancel.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                // This cancellation belongs to the journal, not to the queue's shutdown token.
                throw new OperationCanceledException(new CancellationToken(canceled: true));
            }

            await base.AppendRangeAsync(activityEvents, cancellationToken).ConfigureAwait(false);
            _written.TrySetResult();
        }
    }

    /// <summary>
    /// A journal whose durable write blocks until the test releases it. This is what makes the
    /// dequeue/in-flight window observable: the reader is inside the write with a batch already taken out
    /// of the channel, which is exactly the state a drain must not mistake for "settled".
    /// </summary>
    private sealed class BlockingJournal : IActivityEventJournal
    {
        private readonly List<string> _ids = new();
        private readonly object _gate = new();
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _ids.Count;
                }
            }
        }

        public Task WaitForBlockedWriteAsync(TimeSpan timeout) =>
            _blocked.Task.WaitAsync(timeout);
        public void ReleaseBlockedWrite() => _release.TrySetResult(true);

        public Task AppendAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default) =>
            AppendRangeAsync(new[] { activityEvent }, cancellationToken);

        public async Task AppendRangeAsync(
            IReadOnlyList<ActivityEvent> activityEvents,
            CancellationToken cancellationToken = default)
        {
            _blocked.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                foreach (var activityEvent in activityEvents)
                {
                    _ids.Add(activityEvent.Id);
                }
            }
        }

        public Task<IReadOnlyList<ActivityEvent>> LoadNewestAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActivityEvent>>(Array.Empty<ActivityEvent>());

        public Task<long> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((long)Count);

        public Task<ActivityJournalTrimResult> TrimAsync(
            int retentionLimit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActivityJournalTrimResult(Count, Count, 0));

        public ActivityJournalPage QueryPage(ActivityJournalQuery query) =>
            throw new NotSupportedException("This double is a write-path probe.");
    }
}
