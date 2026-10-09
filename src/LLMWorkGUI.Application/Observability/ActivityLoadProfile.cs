namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// The normative synthetic load profile of ТЗ §9.2, as one reviewable object.
/// <para>
/// These numbers are quoted by the Phase 11 exit criteria, so they live here rather than being retyped
/// by a driver, a test and a report. Changing one changes every consumer, and a report can state which
/// profile it ran against instead of asserting that it "matched the spec".
/// </para>
/// </summary>
public sealed class ActivityLoadProfile
{
    /// <summary>Concurrent execution identities emitting events in the normative profile.</summary>
    public const int NormativeExecutionIdentities = 8;

    /// <summary>Combined offered rate across all execution identities, in events per second.</summary>
    public const int NormativeEventsPerSecond = 50;

    /// <summary>Duration of the normative full profile, in minutes.</summary>
    public const int NormativeFullDurationMinutes = 30;

    /// <summary>Offered events of the full profile: 50/second for 30 minutes.</summary>
    public const int NormativeFullOfferedEvents = NormativeEventsPerSecond * NormativeFullDurationMinutes * 60;

    /// <summary>Already-saved events the operator has in front of them when the stream starts.</summary>
    public const int NormativeSeedEvents = 100_000;

    /// <summary>Largest message the profile may offer, in bytes (256 KiB).</summary>
    public const int MaxMessageBytes = 256 * 1024;

    /// <summary>Normative UI event latency ceiling: p95 event-to-visible must stay at or below it.</summary>
    public const int UiEventLatencyBudgetMilliseconds = 200;

    public ActivityLoadProfile(
        int seedEvents = NormativeSeedEvents,
        int eventsPerSecond = NormativeEventsPerSecond,
        TimeSpan? duration = null,
        int executionIdentities = NormativeExecutionIdentities,
        int capacity = 0)
    {
        if (seedEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seedEvents), seedEvents, "Seed count must be positive.");
        }

        if (eventsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventsPerSecond),
                eventsPerSecond,
                "Rate must be positive.");
        }

        if (executionIdentities <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(executionIdentities),
                executionIdentities,
                "Execution identities must be positive.");
        }

        var resolvedDuration = duration ?? TimeSpan.FromMinutes(NormativeFullDurationMinutes);
        if (resolvedDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Load duration must be positive.");
        var offered = Math.Round(eventsPerSecond * resolvedDuration.TotalSeconds);
        if (!double.IsFinite(offered) || offered < 1 || offered > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration), "Offered event count must fit a positive integer.");
        var offeredEvents = checked((int)offered);
        var defaultCapacity = (long)seedEvents + offeredEvents + 1;
        if (capacity <= 0 && defaultCapacity > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(seedEvents), "Default load capacity exceeds the supported integer range.");

        SeedEvents = seedEvents;
        EventsPerSecond = eventsPerSecond;
        Duration = resolvedDuration;
        ExecutionIdentities = executionIdentities;
        OfferedEvents = offeredEvents;

        // How many events have to be *available* at the end of the run: the already-saved seed plus
        // everything the stream offers. This is a durable-availability figure, not an in-memory bound.
        // The in-memory window is the product's own bound, and it stays there: growing it to hold the
        // stream would trade a memory requirement for a capacity choice, which is the opposite of what the
        // profile is for. One slot of headroom keeps the negative control (capacity + 1) meaningful.
        Capacity = capacity > 0 ? capacity : checked((int)defaultCapacity);
    }

    public int SeedEvents { get; }

    public int EventsPerSecond { get; }

    public TimeSpan Duration { get; }

    public int ExecutionIdentities { get; }

    public int OfferedEvents { get; }

    /// <summary>
    /// Events that must be reachable at the end of the run - durably searchable and pageable. The shipped
    /// in-memory window is smaller than this on purpose, and the durable journal is what makes the
    /// difference invisible to an operator.
    /// </summary>
    public int Capacity { get; }

    /// <summary>The bound the product itself composes for the in-memory window.</summary>
    public int ShippedWindowCapacity => ActivityCenterService.DefaultCapacity;

    /// <summary>True when this profile is the exact normative one of ТЗ §9.2.</summary>
    public bool IsNormativeFullProfile =>
        SeedEvents == NormativeSeedEvents
        && EventsPerSecond == NormativeEventsPerSecond
        && ExecutionIdentities == NormativeExecutionIdentities
        && Duration == TimeSpan.FromMinutes(NormativeFullDurationMinutes)
        && OfferedEvents == NormativeFullOfferedEvents;

    /// <summary>True when the duration is the normative 30 minutes.</summary>
    public bool IsFullDuration => Duration >= TimeSpan.FromMinutes(NormativeFullDurationMinutes);

    public string Describe() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{SeedEvents:N0} already-saved events; {EventsPerSecond} events/second over {ExecutionIdentities} "
        + $"execution identities for {Duration.TotalMinutes:N0} minute(s) = {OfferedEvents:N0} offered "
        + $"events; in-memory capacity {Capacity:N0}; messages up to {MaxMessageBytes / 1024} KiB; "
        + $"p95 event-to-visible budget {UiEventLatencyBudgetMilliseconds} ms.");
}

/// <summary>Size class of one offered message; the distribution is documented, not incidental.</summary>
public enum ActivityMessageSizeClass
{
    /// <summary>Ordinary message, comfortably inside 1 KiB.</summary>
    Small,

    /// <summary>Above 1 KiB but far below the supported maximum.</summary>
    Medium,

    /// <summary>Large message in the tens-of-KiB band.</summary>
    Large,

    /// <summary>Exactly the supported maximum, 256 KiB.</summary>
    Maximum
}

/// <summary>
/// Documented size distribution of the offered stream.
/// <para>
/// The normative profile demands both extremes be present: the operator has to experience the
/// supported 256 KiB maximum, and the overwhelming majority of real traffic is tiny. So the distribution
/// is fixed and counted rather than sampled:
/// </para>
/// <list type="bullet">
///   <item>at least a handful of messages at exactly <see cref="ActivityLoadProfile.MaxMessageBytes"/>;</item>
///   <item>a small number of large messages, to exercise the tens-of-KiB band;</item>
///   <item>a small number above 1 KiB, so the "over 1 KiB" boundary is genuinely crossed;</item>
///   <item>everything else at or below 1 KiB, keeping at least 99.9% of the stream small;</item>
///   <item>the aggregate of the large messages is capped, so the run cannot quietly become an
///   I/O benchmark instead of a UI responsiveness benchmark.</item>
/// </list>
/// </summary>
public sealed class ActivityMessageSizeDistribution
{
    /// <summary>Bound on the total bytes contributed by messages above 1 KiB.</summary>
    public const long DefaultLargePayloadByteCap = 8L * 1024 * 1024;

    public ActivityMessageSizeDistribution(
        int maximumMessageCount,
        int largeMessageCount,
        int mediumMessageCount,
        int offeredMessageCount,
        long largePayloadByteCap = DefaultLargePayloadByteCap)
    {
        if (offeredMessageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offeredMessageCount),
                offeredMessageCount,
                "Offered count must be positive.");
        }

        if (maximumMessageCount < 0 || largeMessageCount < 0 || mediumMessageCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMessageCount),
                "Size class counts must not be negative.");
        }

        var overSmall = maximumMessageCount + largeMessageCount + mediumMessageCount;

        if (overSmall > offeredMessageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMessageCount),
                "The size classes cannot exceed the offered message count.");
        }

        if (largePayloadByteCap <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(largePayloadByteCap),
                largePayloadByteCap,
                "The cap must be positive.");
        }

        MaximumMessageCount = maximumMessageCount;
        LargeMessageCount = largeMessageCount;
        MediumMessageCount = mediumMessageCount;
        OfferedMessageCount = offeredMessageCount;
        LargePayloadByteCap = largePayloadByteCap;
    }

    /// <summary>The normative distribution for a 90 000-event stream.</summary>
    public static ActivityMessageSizeDistribution ForNormativeStream(int offeredMessageCount) =>
        Create(offeredMessageCount, maximum: 8, large: 12, medium: 10);

    /// <summary>
    /// Builds a distribution whose over-small classes never exceed one percent of a short stream.
    /// <para>
    /// A reduced run has to keep the same shape as the normative one without distorting it. With a fixed
    /// count of 8 + 12 + 10 over-small messages, a fifty-event diagnostic run would place a third of its
    /// events at 256 KiB and would then measure the redactor and the tokenizer rather than the UI - a
    /// number that looks alarming and means nothing. The nominal counts are used whenever the stream is
    /// long enough for them to be rare, and scaled down proportionally below that.
    /// </para>
    /// </summary>
    public static ActivityMessageSizeDistribution ForStream(int offeredMessageCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(offeredMessageCount, 0);

        var allowance = Math.Max(1, offeredMessageCount / 100);

        return Create(
            offeredMessageCount,
            maximum: Math.Min(8, allowance),
            large: Math.Min(12, allowance),
            medium: Math.Min(10, allowance));
    }

    private static ActivityMessageSizeDistribution Create(
        int offeredMessageCount,
        int maximum,
        int large,
        int medium)
    {
        var overSmall = Math.Max(1, maximum + large + medium);
        var capped = Math.Min(overSmall, Math.Max(1, offeredMessageCount / 4));

        if (capped < overSmall)
        {
            // Shrink the tail classes first: a run that cannot afford a 256 KiB message keeps one, because
            // exercising the supported maximum matters more than the ratio at tiny volumes.
            maximum = Math.Min(maximum, capped);
            var remaining = capped - maximum;
            large = Math.Min(large, remaining);
            medium = Math.Min(medium, remaining - large);
        }

        return new ActivityMessageSizeDistribution(maximum, large, medium, offeredMessageCount);
    }

    public int MaximumMessageCount { get; }

    public int LargeMessageCount { get; }

    public int MediumMessageCount { get; }

    public int OfferedMessageCount { get; }

    public long LargePayloadByteCap { get; }

    /// <summary>Messages above 1 KiB.</summary>
    public int OverSmallKiBCount => MaximumMessageCount + LargeMessageCount + MediumMessageCount;

    /// <summary>Messages at or below 1 KiB.</summary>
    public int SmallMessageCount => OfferedMessageCount - OverSmallKiBCount;

    /// <summary>Share of the stream at or below 1 KiB; the criterion is "at least 99.9%".</summary>
    public double SmallShare => SmallMessageCount / (double)OfferedMessageCount;

    /// <summary>True when at least a handful of messages reach the supported 256 KiB maximum.</summary>
    public bool HasSeveralMaximumMessages => MaximumMessageCount >= 3;

    /// <summary>True when at least 99.9% of the stream is at or below 1 KiB.</summary>
    public bool MeetsSmallShareCriterion => SmallShare >= 0.999;

    /// <summary>Deterministic target size of the n-th over-small message, walking the classes in order.</summary>
    public int SizeOfLargeMessage(int index)
    {
        if (index < 0 || index >= OverSmallKiBCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                "Index is outside the over-small message range.");
        }

        if (index < MaximumMessageCount)
        {
            return ActivityLoadProfile.MaxMessageBytes;
        }

        if (index < MaximumMessageCount + LargeMessageCount)
        {
            // 8 KiB .. 64 KiB, deterministic.
            var step = index - MaximumMessageCount;
            return (8 * 1024) + (step * 4096);
        }

        // 1.25 KiB .. 2 KiB, deterministic.
        var small = index - MaximumMessageCount - LargeMessageCount;
        return 1280 + (small * 64);
    }

    /// <summary>Total bytes the over-small messages contribute under this distribution.</summary>
    public long TotalLargePayloadBytes()
    {
        long total = 0;

        for (var index = 0; index < OverSmallKiBCount; index++)
        {
            total += SizeOfLargeMessage(index);
        }

        return total;
    }

    public bool RespectsByteCap => TotalLargePayloadBytes() <= LargePayloadByteCap;

    /// <summary>
    /// How many offered messages separate two over-small ones, so the over-small messages are spread
    /// evenly across the stream instead of clustered at its start.
    /// </summary>
    public int Stride => OverSmallKiBCount == 0 ? OfferedMessageCount : OfferedMessageCount / OverSmallKiBCount;

    /// <summary>
    /// Index of the over-small message offered at <paramref name="sequence"/>, or -1 when that message is
    /// a small one.
    /// <para>
    /// The mapping is a strict round-robin: over-small messages occupy sequences 0, stride, 2*stride and
    /// so on, which places exactly <see cref="OverSmallKiBCount"/> of them at even intervals. An earlier
    /// version derived the position by truncating <c>sequence * overSmall / offered</c>, which puts the
    /// first bucket in the first offered/overSmall sequences - so a fifty-event run got seventeen
    /// 256 KiB messages and four megabytes of payload. The percentile criteria of the normative profile
    /// are only meaningful if the ratio is real.
    /// </para>
    /// </summary>
    public int OverSmallIndexOf(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        if (OverSmallKiBCount == 0)
        {
            return -1;
        }

        var stride = Stride;

        if (stride < 1 || sequence % stride != 0)
        {
            return -1;
        }

        var index = (int)(sequence / stride);

        return index < OverSmallKiBCount ? index : -1;
    }

    /// <summary>Size class of one offered message, chosen deterministically by its sequence number.</summary>
    public ActivityMessageSizeClass ClassOf(long sequence)
    {
        var index = OverSmallIndexOf(sequence);

        if (index < 0)
        {
            return ActivityMessageSizeClass.Small;
        }

        if (index < MaximumMessageCount)
        {
            return ActivityMessageSizeClass.Maximum;
        }

        return index < MaximumMessageCount + LargeMessageCount
            ? ActivityMessageSizeClass.Large
            : ActivityMessageSizeClass.Medium;
    }

    public string Describe() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"of {OfferedMessageCount:N0} offered messages: {SmallMessageCount:N0} at or below 1 KiB "
        + $"({SmallShare * 100:N3}%), {MediumMessageCount} just above 1 KiB, {LargeMessageCount} in the "
        + $"tens-of-KiB band, {MaximumMessageCount} at exactly {ActivityLoadProfile.MaxMessageBytes / 1024} KiB; "
        + $"aggregate large-payload bytes {TotalLargePayloadBytes():N0} of a {LargePayloadByteCap:N0} cap.");
}
