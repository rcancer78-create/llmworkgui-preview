namespace LLMWorkGUI.Application.Observability;

/// <summary>Outcome of one measured criterion. A criterion that was never exercised is NOT_TESTED.</summary>
public enum ActivityCriterionOutcome
{
    Pass,
    Fail,

    /// <summary>
    /// The criterion belongs to the full 30-minute profile and that profile was not executed. Reporting
    /// a short run as a pass for a 30-minute criterion would be a false claim, so the outcome stays
    /// explicitly untested.
    /// </summary>
    NotTested
}

/// <summary>One criterion of the Phase 11 exit list with the measurement that decided it.</summary>
public sealed class ActivityCriterionResult
{
    public ActivityCriterionResult(
        string id,
        string requirement,
        ActivityCriterionOutcome outcome,
        string measurement,
        bool requiresFullProfile = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement);
        ArgumentException.ThrowIfNullOrWhiteSpace(measurement);

        Id = id;
        Requirement = requirement;
        Outcome = outcome;
        Measurement = measurement;
        RequiresFullProfile = requiresFullProfile;
    }

    public string Id { get; }

    public string Requirement { get; }

    public ActivityCriterionOutcome Outcome { get; }

    /// <summary>The actual observed number, never the expectation.</summary>
    public string Measurement { get; }

    /// <summary>True when only the full 30-minute profile can decide this criterion.</summary>
    public bool RequiresFullProfile { get; }

    public string OutcomeDisplay => Outcome switch
    {
        ActivityCriterionOutcome.Pass => "PASS",
        ActivityCriterionOutcome.Fail => "FAIL",
        _ => "NOT_TESTED"
    };
}

/// <summary>
/// Percentiles of a latency sample set, reported together with the sample count.
/// <para>
/// The sample count travels with the percentiles on purpose: a p95 over twelve samples is a different
/// claim from a p95 over ninety thousand, and a report that prints only "p95 18 ms" invites the reader to
/// assume the stronger of the two.
/// </para>
/// </summary>
public sealed class ActivityLatencyStatistics
{
    public static ActivityLatencyStatistics Empty { get; } = new(
        sampleCount: 0,
        observedCount: 0,
        p50Milliseconds: 0,
        p95Milliseconds: 0,
        maxMilliseconds: 0,
        minMilliseconds: 0,
        meanMilliseconds: 0,
        discardedSamples: 0,
        droppedPending: 0,
        isComplete: true);

    public ActivityLatencyStatistics(
        int sampleCount,
        long observedCount,
        double p50Milliseconds,
        double p95Milliseconds,
        double maxMilliseconds,
        double minMilliseconds,
        double meanMilliseconds,
        long discardedSamples,
        long droppedPending,
        bool isComplete,
        long pendingCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegative(observedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(discardedSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(droppedPending);
        ArgumentOutOfRangeException.ThrowIfNegative(pendingCount);
        foreach (var value in new[] { p50Milliseconds, p95Milliseconds, maxMilliseconds, minMilliseconds, meanMilliseconds })
            if (!double.IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException(nameof(p50Milliseconds), "Latency statistics must be finite and nonnegative.");
        SampleCount = sampleCount;
        ObservedCount = observedCount;
        P50Milliseconds = p50Milliseconds;
        P95Milliseconds = p95Milliseconds;
        MaxMilliseconds = maxMilliseconds;
        MinMilliseconds = minMilliseconds;
        MeanMilliseconds = meanMilliseconds;
        DiscardedSamples = discardedSamples;
        DroppedPending = droppedPending;
        PendingCount = pendingCount;
        IsComplete = isComplete && sampleCount == observedCount && discardedSamples == 0 && droppedPending == 0 && pendingCount == 0;
    }

    public int SampleCount { get; }

    /// <summary>Events that actually became visible; the denominator of the claim.</summary>
    public long ObservedCount { get; }

    public double P50Milliseconds { get; }

    public double P95Milliseconds { get; }

    public double MaxMilliseconds { get; }

    public double MinMilliseconds { get; }

    public double MeanMilliseconds { get; }

    public long DiscardedSamples { get; }

    public long DroppedPending { get; }

    public long PendingCount { get; }

    /// <summary>True when every observed event contributed a retained sample.</summary>
    public bool IsComplete { get; }

    public static ActivityLatencyStatistics FromSamples(
        IReadOnlyList<double> milliseconds,
        long observedCount = 0,
        long discardedSamples = 0,
        long droppedPending = 0,
        long pendingCount = 0)
    {
        ArgumentNullException.ThrowIfNull(milliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(observedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(discardedSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(droppedPending);
        ArgumentOutOfRangeException.ThrowIfNegative(pendingCount);
        foreach (var value in milliseconds)
            if (!double.IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException(nameof(milliseconds), "Latency samples must be finite and nonnegative.");

        if (milliseconds.Count == 0)
        {
            return new ActivityLatencyStatistics(
                0,
                observedCount,
                0,
                0,
                0,
                0,
                0,
                discardedSamples,
                droppedPending,
                isComplete: discardedSamples == 0 && droppedPending == 0,
                pendingCount: pendingCount);
        }

        var sorted = milliseconds.ToArray();
        Array.Sort(sorted);
        var total = 0d;

        foreach (var value in sorted)
        {
            total += value;
        }

        return new ActivityLatencyStatistics(
            sorted.Length,
            observedCount == 0 ? sorted.Length : observedCount,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            sorted[^1],
            sorted[0],
            total / sorted.Length,
            discardedSamples,
            droppedPending,
            isComplete: discardedSamples == 0 && droppedPending == 0,
            pendingCount: pendingCount);
    }

    /// <summary>
    /// Nearest-rank percentile over an ascending array. Nearest-rank is used rather than interpolation so
    /// the reported value is always a value that was actually observed.
    /// </summary>
    public static double Percentile(double[] ascending, double quantile)
    {
        ArgumentNullException.ThrowIfNull(ascending);

        if (ascending.Length == 0)
        {
            return 0;
        }

        if (quantile <= 0)
        {
            return ascending[0];
        }

        if (quantile >= 1)
        {
            return ascending[^1];
        }

        var rank = (int)Math.Ceiling(quantile * ascending.Length);
        var index = Math.Clamp(rank - 1, 0, ascending.Length - 1);

        return ascending[index];
    }

    public string Describe(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var completeness = IsComplete
            ? string.Empty
            : string.Create(
                culture,
                $"; INCOMPLETE: {SampleCount:N0} retained of {ObservedCount:N0} observed; "
                + $"{DiscardedSamples:N0} samples discarded, {DroppedPending:N0} pending dropped, {PendingCount:N0} still awaiting visibility");

        return string.Create(
            culture,
            $"{label}: p50 {P50Milliseconds:N1} ms, p95 {P95Milliseconds:N1} ms, max {MaxMilliseconds:N1} ms "
            + $"(min {MinMilliseconds:N1} ms, mean {MeanMilliseconds:N1} ms) over {SampleCount:N0} samples "
            + $"of {ObservedCount:N0} visible events")
            + completeness;
    }
}
