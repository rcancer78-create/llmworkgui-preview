namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// What one startup reload of the Activity Center actually found and did.
/// <para>
/// This exists so a restart can be asserted rather than asserted-adjacent. "The journal has 100 000 rows"
/// and "the shipped Activity Center shows those 100 000 rows after a restart" are different claims, and
/// only the second one is what a user sees. A run or a test that wants to claim the second one reads
/// <see cref="Reloaded"/> and <see cref="RetainedRows"/> from here.
/// </para>
/// </summary>
public sealed class ActivityCenterLoadReport
{
    /// <summary>Returned when no durable journal is composed; the window stays empty and nothing was read.</summary>
    public static ActivityCenterLoadReport NotDurable { get; } = new(
        retainedRows: 0,
        trimmedRows: 0,
        reloaded: 0,
        windowCapacity: 0,
        queue: null);

    public ActivityCenterLoadReport(
        long retainedRows,
        long trimmedRows,
        int reloaded,
        int windowCapacity,
        ActivityJournalWriteQueueFacts? queue)
    {
        if (retainedRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedRows), retainedRows, "Must not be negative.");
        }

        if (trimmedRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trimmedRows), trimmedRows, "Must not be negative.");
        }

        if (reloaded < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reloaded), reloaded, "Must not be negative.");
        }

        if (windowCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowCapacity),
                windowCapacity,
                "Must not be negative.");
        }

        RetainedRows = retainedRows;
        TrimmedRows = trimmedRows;
        Reloaded = reloaded;
        WindowCapacity = windowCapacity;
        Queue = queue;
    }

    /// <summary>Rows the durable journal holds after the retention rule ran.</summary>
    public long RetainedRows { get; }

    /// <summary>Rows the retention rule removed during this load.</summary>
    public long TrimmedRows { get; }

    /// <summary>Rows replayed into the bounded in-memory window.</summary>
    public int Reloaded { get; }

    /// <summary>Bound of the in-memory window this load filled.</summary>
    public int WindowCapacity { get; }

    /// <summary>Durable write queue accounting at the moment of the load, or null when none is composed.</summary>
    public ActivityJournalWriteQueueFacts? Queue { get; }

    /// <summary>
    /// True when every retained durable row is reachable from the shipped screen: the window holds as
    /// much as it can, and everything it cannot hold is still searchable and pageable durably.
    /// </summary>
    public bool ReachedWindowCapacity => Reloaded >= WindowCapacity && WindowCapacity > 0;

    public string Describe() =>
        $"retained {RetainedRows}, trimmed {TrimmedRows}, reloaded {Reloaded} into a window of {WindowCapacity}"
        + (Queue is null
            ? string.Empty
            : $"; queue capacity {Queue.Capacity} batch {Queue.BatchSize}, offered {Queue.Offered} "
                + $"written {Queue.Written} dropped {Queue.Dropped} failed {Queue.Failed} "
                + $"balanced {(Queue.IsBalanced ? "yes" : "no")}");
}

/// <summary>Snapshot of the durable write queue's accounting, carried into a measurement report.</summary>
public sealed class ActivityJournalWriteQueueFacts
{
    public ActivityJournalWriteQueueFacts(
        int capacity,
        int batchSize,
        long offered,
        long written,
        long dropped,
        long failed,
        bool isBalanced)
    {
        Capacity = capacity;
        BatchSize = batchSize;
        Offered = offered;
        Written = written;
        Dropped = dropped;
        Failed = failed;
        IsBalanced = isBalanced;
    }

    /// <summary>Bounded queue depth: how many offers can wait before one is dropped and counted.</summary>
    public int Capacity { get; }

    public int BatchSize { get; }

    public long Offered { get; }

    public long Written { get; }

    public long Dropped { get; }

    public long Failed { get; }

    /// <summary>True when every offered row is written, dropped or faulted, with nothing pending.</summary>
    public bool IsBalanced { get; }

    public bool IsLossy => Dropped > 0 || Failed > 0;
}
