using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// Bounded, ordered hand-off between the synchronous Activity Center ingestion boundary and the
/// asynchronous durable journal.
/// <para>
/// <see cref="IActivityCenterService.Append"/> is synchronous because it is the product ingestion
/// boundary called from execution, health and UI code alike; the journal is asynchronous because a
/// durable write may not be allowed to block an append. This queue is the seam: appends are stamped and
/// counted synchronously, then written in batches by one background reader in exactly the accepted order.
/// </para>
/// <para>
/// The queue is bounded on purpose. If the journal cannot keep up, an offer is dropped and
/// <see cref="DroppedCount"/> increases - it is never silently discarded, because a report that claims
/// "every offered event was persisted" while the queue quietly overflowed would be exactly the kind of
/// silent loss this scenario exists to catch.
/// </para>
/// <para>
/// <see cref="DrainAsync"/> settles on a single counter, <see cref="PendingCount"/>, that is incremented
/// before an offer reaches the channel and decremented only after that row is committed or explicitly
/// counted as failed. An earlier version settled on "channel empty and nothing in flight", which is not
/// an atomic pair: the reader could dequeue a batch and be preempted before publishing the in-flight
/// count, and a drain could then return while 256 rows were still uncommitted. Counting at the moment of
/// the offer removes the gap entirely - there is no window in which a row exists somewhere the drain
/// cannot see it.
/// </para>
/// </summary>
public sealed class ActivityJournalWriteQueue : IAsyncDisposable
{
    /// <summary>Number of rows written per durable transaction.</summary>
    public const int BatchSize = 256;
    /// <summary>Conservative managed payload budget for queued and in-flight writes together.</summary>
    public const long DefaultByteCapacity = 64L * 1024 * 1024;

    /// <summary>How often a drain re-checks for quiescence.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);
    private readonly Channel<JournalWrite> _channel;
    private readonly IActivityEventJournal _journal;
    private readonly Task _reader;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ILogger<ActivityJournalWriteQueue> _logger;

    private long _offered;
    private long _written;
    private long _dropped;
    private long _failed;
    private long _pending;
    private long _pendingBytes;
    private long _byteBudgetDropped;
    private int _completed;

    public ActivityJournalWriteQueue(
        IActivityEventJournal journal,
        int capacity = 20_000,
        int batchSize = BatchSize,
        ILogger<ActivityJournalWriteQueue>? logger = null,
        long byteCapacity = DefaultByteCapacity)
    {
        ArgumentNullException.ThrowIfNull(journal);

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be positive.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCapacity);

        _journal = journal;
        _logger = logger ?? NullLogger<ActivityJournalWriteQueue>.Instance;
        BatchCapacity = batchSize;
        QueueCapacity = capacity;
        ByteCapacity = byteCapacity;

        _channel = Channel.CreateBounded<JournalWrite>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _reader = Task.Run(ReadAsync);
    }

    /// <summary>Maximum number of queued writes before an offer is dropped and counted.</summary>
    public int QueueCapacity { get; }

    /// <summary>Rows per durable transaction.</summary>
    public int BatchCapacity { get; }

    /// <summary>Raised after a durable batch commits, before its pending count settles.</summary>
    public event EventHandler? Persisted;

    /// <summary>Writes offered to the queue.</summary>
    public long OfferedCount => Interlocked.Read(ref _offered);

    /// <summary>Rows durably written.</summary>
    public long WrittenCount => Interlocked.Read(ref _written);

    /// <summary>Offers refused because the queue was full; visible, never silent.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Durable write attempts that faulted; visible, never silent.</summary>
    public long FailedCount => Interlocked.Read(ref _failed);

    /// <summary>
    /// Rows that are neither committed nor explicitly counted as failed: still queued, or dequeued into a
    /// batch whose durable write has not returned yet. A drain that returns has seen this reach zero.
    /// </summary>
    public long PendingCount => Interlocked.Read(ref _pending);
    public long ByteCapacity { get; }
    public long PendingBytes => Interlocked.Read(ref _pendingBytes);
    public long ByteBudgetDroppedCount => Interlocked.Read(ref _byteBudgetDropped);

    /// <summary>True when nothing is queued and no batch is between the channel and the journal.</summary>
    public bool IsSettled => PendingCount == 0;

    /// <summary>True once the reader loop has ended, which only happens on disposal or shutdown.</summary>
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    /// <summary>
    /// Observes closed accounting: every offered row is written, dropped or faulted, with nothing
    /// pending. Counters are read separately; a stable global balance requires quiesced producers.
    /// A fully persisted report additionally requires no dropped or failed writes.
    /// </summary>
    public bool IsBalanced =>
        IsSettled && OfferedCount == WrittenCount + DroppedCount + FailedCount;

    /// <summary>True when any offer was dropped or any write faulted.</summary>
    public bool IsLossy => DroppedCount > 0 || FailedCount > 0;

    /// <summary>Machine-readable facts for the measurement report.</summary>
    public IReadOnlyDictionary<string, string> ToFacts() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["queueCapacity"] = QueueCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["batchSize"] = BatchCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["offered"] = OfferedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["written"] = WrittenCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["dropped"] = DroppedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["failed"] = FailedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["pending"] = PendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["byteCapacity"] = ByteCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["pendingBytes"] = PendingBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["byteBudgetDropped"] = ByteBudgetDroppedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["balanced"] = IsBalanced ? "true" : "false"
    };

    /// <summary>
    /// Offers one already-redacted event for durable storage. Never blocks the caller and never throws:
    /// a full queue or a faulting journal is counted, because the ingestion boundary must stay
    /// available to the operator even when storage is unhealthy.
    /// </summary>
    public bool TryEnqueue(ActivityEvent activityEvent) => TryEnqueue(activityEvent, WriteOperation.Append);

    public bool TryEnqueueProjection(ActivityEvent activityEvent) => TryEnqueue(activityEvent, WriteOperation.Projection);

    public bool TryEnqueueReplay(ActivityEvent activityEvent)
    {
        ActivityJournalReplay.Validate(activityEvent);
        return TryEnqueue(activityEvent, WriteOperation.Replay);
    }

    private bool TryEnqueue(ActivityEvent activityEvent, WriteOperation operation)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);
        Interlocked.Increment(ref _offered);

        if (_shutdown.IsCancellationRequested)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        var weight = EstimateRetainedBytes(activityEvent);
        if (!TryReserveBytes(weight))
        {
            Interlocked.Increment(ref _byteBudgetDropped);
            Interlocked.Increment(ref _dropped);
            return false;
        }

        // Counted before the offer, never after: the increment has to be visible to a concurrent drain
        // before the row becomes visible to it, or the drain could settle on a row that is already queued.
        Interlocked.Increment(ref _pending);

        if (_channel.Writer.TryWrite(new JournalWrite(activityEvent, operation, weight)))
        {
            return true;
        }

        Interlocked.Decrement(ref _pending);
        Interlocked.Add(ref _pendingBytes, -weight);
        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>
    /// Waits for an observation of zero pending writes. Rows accepted before this call have then
    /// committed or been counted as failed; refused offers are counted as dropped.
    /// <para>
    /// Drain observes <em>quiescence</em>, not reader completion. The reader task is a long-running loop
    /// that only ends when the channel is completed, and completion only happens on disposal, so awaiting
    /// the reader here would deadlock: the caller waits for a task that is itself waiting for the caller.
    /// Instead the queue publishes <see cref="PendingCount"/>, which is incremented before an offer
    /// reaches the channel and decremented only after that row's durable write has returned, and a drain
    /// settles at the final zero observation. This is the drain's cut point, not a producer shutdown:
    /// a concurrent offer after that observation can be pending when the caller resumes. Stable global
    /// accounting and <see cref="IsBalanced"/> assertions require quiesced producers. For physical
    /// shutdown, retire admission with <see cref="BeginShutdown"/> and await <see cref="Completion"/>.
    /// </para>
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (PendingCount == 0)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Retires new offers; accepted writes still finish on the same physical reader.</summary>
    public void BeginShutdown() => _channel.Writer.TryComplete();

    /// <summary>Completes once the physical reader loop has finished.</summary>
    public Task Completion => _reader;

    public async ValueTask DisposeAsync()
    {
        BeginShutdown();
        await _reader.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task ReadAsync()
    {
        var batch = new List<ActivityEvent>(BatchCapacity);

        try
        {
            while (await _channel.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                batch.Clear();

                if (!_channel.Reader.TryRead(out var first))
                {
                    continue;
                }

                // TryEnqueue already counted these rows, so there is nothing to publish here: the batch
                // becomes visible to a drain at the moment its first row left the channel, which is
                // strictly earlier than any increment this loop could perform afterwards.
                batch.Add(first.Event);
                var batchBytes = first.Weight;

                // Keep operation groups in accepted order. A projection update must never turn an
                // ordinary observation into an upsert or bypass a preceding write.
                while (batch.Count < BatchCapacity && _channel.Reader.TryPeek(out var next)
                    && next.Operation == first.Operation && _channel.Reader.TryRead(out var write))
                {
                    batch.Add(write.Event);
                    batchBytes += write.Weight;
                }

                try
                {
                    if (first.Operation == WriteOperation.Replay)
                        await _journal.ReplayJournalEventsAsync(batch, _shutdown.Token).ConfigureAwait(false);
                    else if (first.Operation == WriteOperation.Projection)
                        await _journal.UpsertProjectionsAsync(batch, _shutdown.Token).ConfigureAwait(false);
                    else
                        await _journal.AppendRangeAsync(batch, _shutdown.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _written, batch.Count);
                    NotifyPersisted();
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    // Shutdown: the rows were never committed, so they leave the pending count as
                    // unpersisted rather than being reported as durable.
                    Interlocked.Add(ref _failed, batch.Count);
                    return;
                }
                catch (Exception)
                {
                    // A durable failure is counted, never swallowed into a silent "all persisted" claim.
                    Interlocked.Add(ref _failed, batch.Count);
                }
                finally
                {
                    // The reservation follows the write out of the channel into the actual durable
                    // operation; dequeue alone cannot make room for another unbounded payload batch.
                    var completedCount = batch.Count;
                    batch.Clear();
                    Interlocked.Add(ref _pendingBytes, -batchBytes);
                    Interlocked.Add(ref _pending, -completedCount);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            Volatile.Write(ref _completed, 1);
        }
    }

    private void NotifyPersisted()
    {
        var handlers = Persisted;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception)
            {
                // Diagnostics cannot undo the acknowledged journal write or suppress later observers.
                try
                {
                    _logger.LogWarning("Activity persistence subscriber failed with {ExceptionType}.", exception.GetType().Name);
                }
                catch (Exception) { }
            }
        }
    }

    private bool TryReserveBytes(long weight)
    {
        if (weight > ByteCapacity) return false;
        while (true)
        {
            var current = Interlocked.Read(ref _pendingBytes);
            if (current > ByteCapacity - weight) return false;
            if (Interlocked.CompareExchange(ref _pendingBytes, current + weight, current) == current) return true;
        }
    }

    private static long EstimateRetainedBytes(ActivityEvent item) => 256
        + StringBytes(item.Id) + StringBytes(item.Role) + StringBytes(item.Title) + StringBytes(item.Description)
        + StringBytes(item.SessionId) + StringBytes(item.ExecutionId) + StringBytes(item.RouteId)
        + StringBytes(item.DiffText) + StringBytes(item.ArtifactName) + StringBytes(item.ArtifactContent)
        + StringBytes(item.ArtifactSha256) + StringBytes(item.ArtifactChangeStatus);

    // Conservative even when strings share storage: no caller's allocation pattern can evade the bound.
    private static long StringBytes(string? value) => value is null ? 0 : 24L + (2L * value.Length);

    private enum WriteOperation { Append, Projection, Replay }
    private sealed record JournalWrite(ActivityEvent Event, WriteOperation Operation, long Weight);
}
