namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// One accepted append at the product ingestion boundary of the Activity Center.
/// <para>
/// The notice is the only way the UI learns that the stream grew: <see cref="IActivityCenterService"/>
/// has never had a pull-free notification, so the Activity Center screen could only ever be refreshed by
/// the operator pressing the refresh button. It carries the monotonic ingestion stamp taken inside
/// <c>Append</c>, because the normative "UI event latency" of ТЗ §9.2 is measured from the moment the
/// product accepted the event to the moment the dispatched UI work marked the row visible - never from
/// the moment a load driver happened to call the method.
/// </para>
/// </summary>
public sealed class ActivityEventAppendedEventArgs : EventArgs
{
    public ActivityEventAppendedEventArgs(
        string eventId,
        long ingestedTimestamp,
        int retainedCount,
        long offeredCount,
        long evictedCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        EventId = eventId;
        IngestedTimestamp = ingestedTimestamp;
        RetainedCount = retainedCount;
        OfferedCount = offeredCount;
        EvictedCount = evictedCount;
    }

    /// <summary>Identity of the accepted event; unique per accepted append.</summary>
    public string EventId { get; }

    /// <summary>
    /// Monotonic timestamp taken at the ingestion boundary, in the tick base of
    /// <see cref="TimeProvider.GetTimestamp"/>. Paired with the UI-visible stamp of the same clock it
    /// yields the normative event-to-visible latency, so both stamps must come from one clock.
    /// </summary>
    public long IngestedTimestamp { get; }

    /// <summary>Number of events retained after this append.</summary>
    public int RetainedCount { get; }

    /// <summary>Total number of events ever offered to <c>Append</c> since the service was created.</summary>
    public long OfferedCount { get; }

    /// <summary>Total number of events dropped by capacity eviction since the service was created.</summary>
    public long EvictedCount { get; }

    /// <summary>True when at least one event has been dropped by capacity eviction.</summary>
    public bool HasOverflowed => EvictedCount > 0;
}

/// <summary>
/// Counters that make bounded retention auditable instead of silent. The normative profile of ТЗ §9.2
/// requires every offered, accepted and evicted count to be reported, so the service exposes them
/// directly rather than letting the oldest rows disappear with no trace.
/// <para>
/// Three different losses are reported separately and are never merged into one number:
/// <see cref="Evicted"/> is the in-memory window dropping its oldest rows, <see cref="QueueDropped"/> and
/// <see cref="QueueFailed"/> are offers the durable write queue could not accept or could not commit, and
/// <see cref="JournalEvicted"/> is the durable retention rule removing rows from the journal. Labelling a
/// full write queue as "journal eviction" would tell an operator their history was deliberately trimmed
/// when in fact the product failed to write it.
/// </para>
/// </summary>
public sealed class ActivityRetentionStatistics
{
    public static ActivityRetentionStatistics Empty { get; } =
        new(
            capacity: 0,
            retained: 0,
            offered: 0,
            evicted: 0,
            journalRetained: null,
            journalEvicted: 0,
            queueDropped: 0,
            queueFailed: 0);

    public ActivityRetentionStatistics(
        int capacity,
        int retained,
        long offered,
        long evicted,
        long? journalRetained,
        long journalEvicted,
        long queueDropped = 0,
        long queueFailed = 0,
        long replaced = 0,
        long removed = 0)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must not be negative.");
        }

        if (retained < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retained), retained, "Retained must not be negative.");
        }

        if (offered < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offered), offered, "Offered must not be negative.");
        }

        if (evicted < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(evicted), evicted, "Evicted must not be negative.");
        }

        if (journalEvicted < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(journalEvicted),
                journalEvicted,
                "Journal eviction must not be negative.");
        }

        if (queueDropped < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queueDropped),
                queueDropped,
                "Queue drops must not be negative.");
        }

        if (queueFailed < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queueFailed),
                queueFailed,
                "Queue failures must not be negative.");
        }

        Capacity = capacity;
        ArgumentOutOfRangeException.ThrowIfNegative(replaced);
        ArgumentOutOfRangeException.ThrowIfNegative(removed);
        Retained = retained;
        Offered = offered;
        Evicted = evicted;
        JournalRetained = journalRetained;
        JournalEvicted = journalEvicted;
        QueueDropped = queueDropped;
        QueueFailed = queueFailed;
        Replaced = replaced;
        Removed = removed;
    }

    /// <summary>Maximum number of events retained in memory; the oldest are evicted first.</summary>
    public int Capacity { get; }

    /// <summary>Events currently retained in the in-memory window.</summary>
    public int Retained { get; }

    /// <summary>Events ever offered to the ingestion boundary.</summary>
    public long Offered { get; }

    /// <summary>Events dropped from the in-memory window by its capacity bound.</summary>
    public long Evicted { get; }

    /// <summary>Accepted updates that replaced a currently retained identity.</summary>
    public long Replaced { get; }

    /// <summary>Retained rows deliberately removed or cleared by the caller.</summary>
    public long Removed { get; }

    /// <summary>Rows currently in the durable journal, or null when no journal is composed.</summary>
    public long? JournalRetained { get; }

    /// <summary>Rows the durable retention rule has removed from the journal.</summary>
    public long JournalEvicted { get; }

    /// <summary>Offers the durable write queue refused because it was full.</summary>
    public long QueueDropped { get; }

    /// <summary>Rows whose durable write faulted.</summary>
    public long QueueFailed { get; }

    /// <summary>True when anything at all has been dropped, evicted or faulted.</summary>
    public bool HasOverflowed => Evicted > 0 || JournalEvicted > 0 || QueueDropped > 0 || QueueFailed > 0;

    /// <summary>True when the durable side of the product lost an event it accepted.</summary>
    public bool IsDurablyLossy => QueueDropped > 0 || QueueFailed > 0;

    /// <summary>True when <c>offered</c> is larger than what the product actually retained in memory.</summary>
    public bool IsLossy => Offered > Retained + Evicted + Replaced + Removed;

    /// <summary>Operator-visible overflow label; never empty, so silence is not an option.</summary>
    public string OverflowDisplay
    {
        get
        {
            // Each clause appears only when it has something true to say. A label that always mentions the
            // in-memory bound would report "evicted 0" for a run whose real problem was a full write queue,
            // and the reader would have to work out which number was the one that mattered.
            var memory = Evicted > 0 ? $"; вытеснено из памяти {Evicted}" : string.Empty;
            var journal = JournalRetained is { } rows
                ? $"; в журнале {rows} (предел хранения удалил {JournalEvicted})"
                : "; журнал не подключён, история только в памяти";
            var queue = QueueDropped > 0 || QueueFailed > 0
                ? $"; очередь записи отбросила {QueueDropped} и не записала {QueueFailed}"
                : string.Empty;

            return HasOverflowed
                ? $"Переполнение: удержано {Retained} из {Offered}{memory}{journal}{queue}"
                : $"Удержано {Retained} из {Offered}; переполнения нет{journal}";
        }
    }

    /// <summary>Machine-readable facts for the measurement report.</summary>
    public IReadOnlyDictionary<string, string> ToFacts() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["capacity"] = Capacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["retained"] = Retained.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["offered"] = Offered.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["evicted"] = Evicted.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["replaced"] = Replaced.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["removed"] = Removed.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["journalRetained"] = JournalRetained?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
        ["journalEvicted"] = JournalEvicted.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["queueDropped"] = QueueDropped.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["queueFailed"] = QueueFailed.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}
