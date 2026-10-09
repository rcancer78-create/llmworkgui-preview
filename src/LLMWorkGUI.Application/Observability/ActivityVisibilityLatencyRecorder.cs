namespace LLMWorkGUI.Application.Observability;

/// <summary>One measured ingestion-to-visible latency sample, in milliseconds.</summary>
public readonly record struct ActivityLatencySample(string EventId, double Milliseconds);

/// <summary>
/// Measures the normative "UI event latency" of ТЗ §9.2: the time from the moment the product accepted an
/// event at the ingestion boundary to the moment the dispatched UI work had actually made it visible.
/// <para>
/// Three properties make the number honest rather than decorative:
/// </para>
/// <list type="bullet">
///   <item>Both stamps come from one monotonic clock (<see cref="TimeProvider.GetTimestamp"/>). The
///   ingest stamp is taken inside <see cref="IActivityCenterService.Append"/>; the visible stamp is taken
///   after the dispatcher work has rebuilt the list, so a wall-clock adjustment cannot manufacture a
///   negative or an inflated sample.</item>
///   <item>Pending stamps are matched by event identity, so a coalesced refresh that makes fifty events
///   visible at once produces fifty samples rather than one averaged sample - coalescing must not be
///   able to hide the latency of the events it merged.</item>
///   <item>Samples are harvested by <see cref="DrainSamples"/>, which the workload driver calls outside
///   its send loop. Measuring latency inside the loop that produces the load would time the producer as
///   well as the product.</item>
/// </list>
/// </summary>
public sealed class ActivityVisibilityLatencyRecorder
{
    /// <summary>Number of samples kept for exact percentiles before the reservoir starts recycling.</summary>
    public const int DefaultSampleCapacity = 200_000;

    /// <summary>Number of un-matched ingest stamps kept before the oldest is dropped and counted.</summary>
    public const int DefaultPendingCapacity = 200_000;

    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Timestamp, LinkedListNode<string> Node)> _pending = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _pendingOrder = new();
    private readonly double[] _samples;
    private readonly int _sampleCapacity;
    private readonly double[] _dispatcherSamples;
    private readonly double[] _querySamples;
    private int _dispatcherSampleCount;
    private int _querySampleCount;
    private int _sampleCount;
    private long _observedCount;
    private long _discardedSamples;
    private long _droppedPending;
    private long _dispatcherObserved;
    private long _dispatcherDiscarded;
    private long _queryObserved;
    private long _queryDiscarded;

    public ActivityVisibilityLatencyRecorder(
        TimeProvider timeProvider,
        int sampleCapacity = DefaultSampleCapacity,
        int pendingCapacity = DefaultPendingCapacity)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (sampleCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleCapacity),
                sampleCapacity,
                "Sample capacity must be positive.");
        }

        if (pendingCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pendingCapacity),
                pendingCapacity,
                "Pending capacity must be positive.");
        }

        _timeProvider = timeProvider;
        _sampleCapacity = sampleCapacity;
        _samples = new double[sampleCapacity];
        _dispatcherSamples = new double[sampleCapacity];
        _querySamples = new double[sampleCapacity];
        PendingCapacity = pendingCapacity;
    }

    /// <summary>Maximum number of un-matched ingest stamps kept.</summary>
    public int PendingCapacity { get; }

    /// <summary>Accepted identities still awaiting an observed visibility stamp in this measurement window.</summary>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Number of latency samples currently held; the percentile source.</summary>
    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _sampleCount;
            }
        }
    }

    /// <summary>Number of events that became visible over the lifetime of the recorder.</summary>
    public long ObservedCount
    {
        get
        {
            lock (_gate)
            {
                return _observedCount;
            }
        }
    }

    /// <summary>Samples the reservoir recycled because the run exceeded its capacity.</summary>
    public long DiscardedSampleCount
    {
        get
        {
            lock (_gate)
            {
                return _discardedSamples;
            }
        }
    }

    /// <summary>Ingest stamps dropped because the pending set stayed full, so their events never reported.</summary>
    public long DroppedPendingCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedPending;
            }
        }
    }

    /// <summary>True when every observed event produced a retained sample.</summary>
    public bool IsComplete
    {
        get { lock (_gate) return _pending.Count == 0 && _discardedSamples == 0 && _droppedPending == 0
            && _dispatcherDiscarded == 0 && _queryDiscarded == 0; }
    }

    public long DispatcherObservedCount { get { lock (_gate) return _dispatcherObserved; } }
    public long DispatcherDiscardedSampleCount { get { lock (_gate) return _dispatcherDiscarded; } }
    public long QueryObservedCount { get { lock (_gate) return _queryObserved; } }
    public long QueryDiscardedSampleCount { get { lock (_gate) return _queryDiscarded; } }

    /// <summary>Records the stamp taken inside the ingestion boundary for one accepted event.</summary>
    public void RecordIngested(string eventId, long ingestedTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        lock (_gate)
        {
            if (_pending.Remove(eventId, out var superseded))
            {
                // A coalesced projection displays the latest accepted update. Its clock must start at
                // that update, and it is newest in the bounded pending order as well.
                _pendingOrder.Remove(superseded.Node);
            }

            // Remove completed identities immediately, so a visible stream cannot leave an
            // unbounded queue of obsolete ids behind the bounded latency samples.
            while (_pending.Count >= PendingCapacity && _pendingOrder.First is { } oldest)
            {
                _pendingOrder.RemoveFirst();
                if (_pending.Remove(oldest.Value)) _droppedPending++;
            }
            _pending[eventId] = (ingestedTimestamp, _pendingOrder.AddLast(eventId));
        }
    }

    /// <summary>
    /// Marks one event visible and records its latency. Called after the dispatcher work that put the row
    /// on screen has finished, never before.
    /// </summary>
    public void MarkVisible(string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        lock (_gate)
        {
            if (!_pending.Remove(eventId, out var ingestedAt))
            {
                // Not an error: the screen may make an event visible again on a page change, and a second
                // mark for the same event must not inflate the percentile.
                return;
            }

            _pendingOrder.Remove(ingestedAt.Node);
            _observedCount++;
            AddSampleLocked(_timeProvider.GetElapsedTime(ingestedAt.Timestamp).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Marks a batch of events visible at once - the normal case after a coalesced refresh - and records
    /// one sample per event, so merged events are measured individually.
    /// </summary>
    public void MarkVisible(IReadOnlyList<string> eventIds)
    {
        ArgumentNullException.ThrowIfNull(eventIds);

        foreach (var eventId in eventIds)
        {
            MarkVisible(eventId);
        }
    }

    /// <summary>
    /// Removes the harvested samples and returns them. Called outside the generator's send loop so the
    /// producer is never charged for the consumer's bookkeeping.
    /// </summary>
    public IReadOnlyList<ActivityLatencySample> DrainSamples()
    {
        lock (_gate)
        {
            if (_sampleCount == 0)
            {
                return Array.Empty<ActivityLatencySample>();
            }

            var drained = new List<ActivityLatencySample>(_sampleCount);
            Array.Sort(_samples, 0, _sampleCount);
            var id = "harvested";

            for (var index = 0; index < _sampleCount; index++)
            {
                drained.Add(new ActivityLatencySample(id, _samples[index]));
            }

            Array.Clear(_samples, 0, _sampleCount);
            _sampleCount = 0;

            return drained;
        }
    }

    /// <summary>Adds one latency observation.</summary>
    public void AddSample(double milliseconds)
    {
        ValidateSample(milliseconds);
        lock (_gate)
        {
            AddSampleLocked(milliseconds);
        }
    }

    /// <summary>
    /// Records how long one dispatched refresh occupied the UI thread, separately from how long an event
    /// waited for it. The two answer different questions - "how expensive is a refresh" and "how long does
    /// an event wait" - and conflating them makes a fast product look slow when the queue behind it is deep,
    /// or a slow product look busy.
    /// </summary>
    public void AddDispatcherWorkSample(double milliseconds)
    {
        ValidateSample(milliseconds);
        lock (_gate)
        {
            _dispatcherObserved++;
            if (_dispatcherSampleCount >= _dispatcherSamples.Length)
            {
                _dispatcherDiscarded++;
                return;
            }

            _dispatcherSamples[_dispatcherSampleCount++] = milliseconds;
        }
    }

    /// <summary>Number of dispatched refreshes measured so far.</summary>
    public int DispatcherSampleCount
    {
        get
        {
            lock (_gate)
            {
                return _dispatcherSampleCount;
            }
        }
    }

    /// <summary>Removes the harvested dispatcher-work samples and returns them.</summary>
    public IReadOnlyList<double> DrainDispatcherWorkSamples()
    {
        lock (_gate)
        {
            if (_dispatcherSampleCount == 0)
            {
                return Array.Empty<double>();
            }

            var drained = new double[_dispatcherSampleCount];
            Array.Copy(_dispatcherSamples, drained, _dispatcherSampleCount);
            Array.Clear(_dispatcherSamples, 0, _dispatcherSampleCount);
            _dispatcherSampleCount = 0;

            return drained;
        }
    }

    /// <summary>
    /// Records how long one durable query occupied the query worker.
    /// <para>
    /// This is deliberately a <em>third</em> series rather than a second reading of the dispatcher series.
    /// The queries of the Activity Center no longer run on the UI thread at all, so folding their cost into
    /// the dispatcher figure would report work the dispatcher never did, and leaving them out of the report
    /// entirely would hide the dominant cost of a 190 000-row search. Kept apart, a reader can see the two
    /// facts that matter: what the dispatcher was charged, and what the query cost the worker.
    /// </para>
    /// </summary>
    public void AddQueryWorkSample(double milliseconds)
    {
        ValidateSample(milliseconds);
        lock (_gate)
        {
            _queryObserved++;
            if (_querySampleCount >= _querySamples.Length)
            {
                _queryDiscarded++;
                return;
            }

            _querySamples[_querySampleCount++] = milliseconds;
        }
    }

    /// <summary>Number of durable queries measured so far.</summary>
    public int QuerySampleCount
    {
        get
        {
            lock (_gate)
            {
                return _querySampleCount;
            }
        }
    }

    /// <summary>Removes the harvested off-dispatcher query samples and returns them.</summary>
    public IReadOnlyList<double> DrainQueryWorkSamples()
    {
        lock (_gate)
        {
            if (_querySampleCount == 0)
            {
                return Array.Empty<double>();
            }

            var drained = new double[_querySampleCount];
            Array.Copy(_querySamples, drained, _querySampleCount);
            Array.Clear(_querySamples, 0, _querySampleCount);
            _querySampleCount = 0;

            return drained;
        }
    }

    /// <summary>
    /// Discards everything recorded so far, keeping the clock.
    /// <para>
    /// Used to open the measurement window. The normative figure of ТЗ §9.2 is the latency of an event
    /// arriving <em>under the load</em>; the bulk pre-load that establishes the already-saved history is a
    /// separate thing, and folding it in would measure how long it takes to fill an empty page rather than
    /// how responsive the screen is once it is full. The pre-load samples are harvested into the report
    /// separately instead of being thrown away.
    /// </para>
    /// </summary>
    public IReadOnlyList<ActivityLatencySample> Reset()
    {
        lock (_gate)
        {
            var harvested = SnapshotLocked();

            _pending.Clear();
            _pendingOrder.Clear();
            _discardedSamples = 0;
            _droppedPending = 0;
            _observedCount = 0;
            Array.Clear(_dispatcherSamples, 0, _dispatcherSampleCount);
            Array.Clear(_querySamples, 0, _querySampleCount);
            _dispatcherSampleCount = 0;
            _querySampleCount = 0;
            _dispatcherObserved = 0;
            _dispatcherDiscarded = 0;
            _queryObserved = 0;
            _queryDiscarded = 0;

            return harvested;
        }
    }

    private List<ActivityLatencySample> SnapshotLocked()
    {
        if (_sampleCount == 0)
        {
            return new List<ActivityLatencySample>();
        }

        Array.Sort(_samples, 0, _sampleCount);
        var harvested = new List<ActivityLatencySample>(_sampleCount);

        for (var index = 0; index < _sampleCount; index++)
        {
            harvested.Add(new ActivityLatencySample("pre-load", _samples[index]));
        }

        Array.Clear(_samples, 0, _sampleCount);
        _sampleCount = 0;

        return harvested;
    }

    private void AddSampleLocked(double milliseconds)
    {
        if (_sampleCount >= _sampleCapacity)
        {
            _discardedSamples++;
            return;
        }

        _samples[_sampleCount++] = milliseconds;
    }

    private static void ValidateSample(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds), "Latency must be finite and nonnegative.");
    }

    /// <summary>Elapsed milliseconds of one monotonic stamp pair on this recorder's clock.</summary>
    public double ElapsedMilliseconds(long fromTimestamp, long toTimestamp) =>
        _timeProvider.GetElapsedTime(fromTimestamp, toTimestamp).TotalMilliseconds;

    /// <summary>Current monotonic stamp of this recorder's clock.</summary>
    public long Stamp() => _timeProvider.GetTimestamp();

    /// <summary>Machine-readable facts for the measurement report.</summary>
    public IReadOnlyDictionary<string, string> ToFacts() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["observed"] = ObservedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["sampleCount"] = SampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["discarded"] = DiscardedSampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["droppedPending"] = DroppedPendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["pending"] = PendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["pendingCapacity"] = PendingCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["dispatcherObserved"] = DispatcherObservedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["dispatcherDiscarded"] = DispatcherDiscardedSampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["queryObserved"] = QueryObservedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["queryDiscarded"] = QueryDiscardedSampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}
