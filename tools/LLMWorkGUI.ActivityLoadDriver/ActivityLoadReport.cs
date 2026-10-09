using System.Globalization;
using System.Text;
using LLMWorkGUI.Application.Observability;

namespace LLMWorkGUI.ActivityLoadDriver;

/// <summary>One memory observation at a named checkpoint of the run.</summary>
internal sealed record MemoryCheckpoint(string Name, long ManagedBytes, long PrivateBytes, long WorkingSetBytes)
{
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Name}: managed {ManagedBytes / (1024d * 1024d):N1} MiB, "
        + $"private {PrivateBytes / (1024d * 1024d):N1} MiB, "
        + $"working set {WorkingSetBytes / (1024d * 1024d):N1} MiB");
}

/// <summary>One timed interaction performed against the shipped screen during the run.</summary>
internal sealed record UiActionResult(string Name, double Milliseconds, string Note);

/// <summary>One secret-fixture probe: what was searched and what was found (which must be nothing).</summary>
internal sealed record SecretProbeResult(
    string FixtureId,
    string Surface,
    string SearchTerm,
    bool Found,
    string Note);

/// <summary>
/// The measurement report of a load run.
/// <para>
/// The structure is fixed by the Phase 11 exit criteria, and every line carries a measured number rather
/// than an expectation. A criterion that belongs to the 30-minute profile stays
/// <see cref="ActivityCriterionOutcome.NotTested"/> when a smoke was run: reporting a twenty-second run
/// as a pass for a thirty-minute requirement would be a false claim, not a shortcut.
/// </para>
/// </summary>
internal sealed class ActivityLoadReport
{
    private readonly List<MemoryCheckpoint> _checkpoints = new();
    private readonly List<UiActionResult> _uiActions = new();
    private readonly List<string> _uiActionsNotPerformed = new();
    private readonly List<SecretProbeResult> _secretProbes = new();
    private readonly List<ActivityCriterionResult> _criteria = new();
    private readonly Dictionary<string, string> _facts = new(StringComparer.Ordinal);
    private readonly List<string> _evidence = new();

    public ActivityLoadReport(ActivityLoadOptions options, DateTimeOffset startedAtUtc)
    {
        Options = options;
        StartedAtUtc = startedAtUtc;
    }

    public ActivityLoadOptions Options { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public ActivityLoadProfile Profile => Options.Profile;

    public ActivityRetentionStatistics Retention { get; set; } = ActivityRetentionStatistics.Empty;

    public long OfferedEvents { get; set; }

    public long AcceptedEvents { get; set; }

    public long PersistedRows { get; set; }

    public long DisplayedRows { get; set; }

    public long EvictedEvents { get; set; }

    public long JournalEvictedRows { get; set; }

    public long JournalDroppedOffers { get; set; }

    /// <summary>Rows the durable write queue accepted and then failed to commit.</summary>
    public long JournalFailedOffers { get; set; }

    public long ReloadedAfterRestart { get; set; }

    /// <summary>Exact event total the reloaded Activity Center screen reports after the restart.</summary>
    public long ReloadedScreenTotal { get; set; }

    /// <summary>Rows the reloaded screen materialized for one page.</summary>
    public long ReloadedScreenRows { get; set; }

    /// <summary>Events the restarted process held in its bounded in-memory window.</summary>
    public long ReloadedWindowCount { get; set; }

    /// <summary>Matches a restarted screen's durable search returned for the oldest saved row.</summary>
    public long ReloadedOldestSearchMatched { get; set; }

    /// <summary>Exact match counts measured for each free-text query, by query text.</summary>
    public IReadOnlyDictionary<string, long> SearchMatchCounts { get; set; } =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Events this run offered through the ingestion boundary, seed plus stream.</summary>
    public long DriverEventsExpected { get; set; }

    /// <summary>The in-memory window bound the search measurement was taken under.</summary>
    public long WindowCapacityDuringRun { get; set; }

    /// <summary>
    /// True when every query that names a token present in every offered event matched every retained row.
    /// A search that silently covers only the newest slice of the retained set is the defect this catches.
    /// </summary>
    public bool DurableSearchCoversTheRetainedSet { get; set; }

    /// <summary>
    /// True when a query for the oldest retained row found it. Before the durable index existed this was
    /// false: the oldest rows were pageable and unfindable.
    /// </summary>
    public bool OldestRetainedRowIsSearchable { get; set; }

    public int DistinctExecutionIdentities { get; set; }

    public int MaxMessageBytesObserved { get; set; }

    public long AggregateOfferedBytes { get; set; }

    public long AggregateLargePayloadBytes { get; set; }

    public TimeSpan ActualDuration { get; set; }

    public TimeSpan ActualStreamDuration { get; set; }

    public double OfferedRatePerSecond { get; set; }

    public ActivityLatencyStatistics VisibilityLatency { get; set; } = ActivityLatencyStatistics.Empty;

    /// <summary>
    /// Latency of the bulk pre-load that establishes the already-saved history. Reported separately from
    /// <see cref="VisibilityLatency"/> because it measures filling an empty page, not the responsiveness
    /// of a full one.
    /// </summary>
    public ActivityLatencyStatistics PreLoadVisibilityLatency { get; set; } = ActivityLatencyStatistics.Empty;

    public ActivityLatencyStatistics SearchLatency { get; set; } = ActivityLatencyStatistics.Empty;

    public ActivityLatencyStatistics DispatcherLatency { get; set; } = ActivityLatencyStatistics.Empty;

    /// <summary>
    /// Cost of the durable Activity Center queries themselves, measured on the query worker.
    /// <para>
    /// Kept out of <see cref="DispatcherLatency"/> on purpose. The queries no longer run on the WPF
    /// dispatcher, so folding their time into the dispatcher series would report work the dispatcher never
    /// did - and the search responsiveness change would look like it had made the dispatcher slower.
    /// </para>
    /// </summary>
    public ActivityLatencyStatistics QueryLatency { get; set; } = ActivityLatencyStatistics.Empty;

    public string OverflowLabelSeen { get; set; } = string.Empty;

    public string NegativeControlSummary { get; set; } = "not executed";

    /// <summary>What the lossy write-queue negative control measured.</summary>
    public string LossyQueueControlSummary { get; set; } = "not executed";

    /// <summary>
    /// Production event sources wired into the Activity Center, as the product itself reports them at
    /// startup. A run whose list is empty has proven nothing about production ingestion, no matter how much
    /// synthetic traffic it drove.
    /// </summary>
    public IReadOnlyList<string> ProductionIngestionSources { get; set; } = Array.Empty<string>();

    /// <summary>Notifications the product's own sources delivered into the Activity Center during the run.</summary>
    public long ProductionIngestedEvents { get; set; }

    /// <summary>Row count the journal held at the very start, before any driver traffic.</summary>
    public long ProductionRowsAtStartup { get; set; }

    /// <summary>
    /// True when the run can say anything at all about production ingestion: the product's own sources are
    /// wired, and the journal was not empty before the driver produced its first event.
    /// </summary>
    public bool ProductionIngestionIsProven =>
        ProductionIngestionSources.Count > 0 && ProductionRowsAtStartup > 0;

    // The last offer is scheduled one interval before the configured end. This is a measured phase,
    // not the configured mode. A slower completed phase still ran for the required duration.
    public bool ExecutedFullProfile => Profile.IsFullDuration
        && ActualStreamDuration.TotalSeconds >= TimeSpan.FromMinutes(ActivityLoadProfile.NormativeFullDurationMinutes).TotalSeconds
            - 1d / Profile.EventsPerSecond;

    /// <summary>Explicit 1% measurement tolerance for scheduler drift; never a substitute for count or duration.</summary>
    public const double MeasuredProfileTolerance = 0.01;

    public bool AchievedNormativeProfile => ExecutedFullProfile && Profile.IsNormativeFullProfile
        && OfferedEvents == Profile.OfferedEvents && AcceptedEvents == Profile.OfferedEvents
        && DistinctExecutionIdentities == Profile.ExecutionIdentities
        && ActualStreamDuration.TotalSeconds <= Profile.Duration.TotalSeconds * (1 + MeasuredProfileTolerance)
        && double.IsFinite(OfferedRatePerSecond)
        && Math.Abs(OfferedRatePerSecond - Profile.EventsPerSecond) <= Profile.EventsPerSecond * MeasuredProfileTolerance;

    public void AddCheckpoint(MemoryCheckpoint checkpoint) => _checkpoints.Add(checkpoint);

    public void AddUiAction(UiActionResult action)
    {
        _uiActions.Add(action);

        if (action.Note.StartsWith("NOT PERFORMED", StringComparison.Ordinal))
        {
            _uiActionsNotPerformed.Add($"{action.Name} ({action.Note})");
        }
    }

    /// <summary>Timings of the shipped-screen actions, used as the dispatcher interaction samples.</summary>
    public IReadOnlyList<double> UiActionSamples =>
        _uiActions.Select(action => action.Milliseconds).ToArray();

    public void AddSecretProbe(SecretProbeResult probe) => _secretProbes.Add(probe);

    public void AddEvidence(string path) => _evidence.Add(path);

    public void SetFact(string key, string value) => _facts[key] = value;

    public void Add(ActivityCriterionResult criterion) => _criteria.Add(criterion);

    public void Complete(DateTimeOffset completedAtUtc)
    {
        CompletedAtUtc = completedAtUtc;
        ActualDuration = completedAtUtc - StartedAtUtc;
    }

    public int PassedCount => _criteria.Count(c => c.Outcome == ActivityCriterionOutcome.Pass);

    public int FailedCount => _criteria.Count(c => c.Outcome == ActivityCriterionOutcome.Fail);

    public int NotTestedCount => _criteria.Count(c => c.Outcome == ActivityCriterionOutcome.NotTested);

    public int ExitCode => FailedCount > 0 || (!Options.IsSmoke
        && (!AchievedNormativeProfile || _criteria.Count == 0 || NotTestedCount > 0)) ? 1 : 0;

    /// <summary>Shipped-screen actions attempted during the run.</summary>
    public int UiActionCount => _uiActions.Count;

    /// <summary>True when every attempted action reported a real result rather than an empty note.</summary>
    public bool UiActionsAllSucceeded => _uiActions.Count > 0 && _uiActionsNotPerformed.Count == 0;

    /// <summary>Number of shipped-screen actions that were actually performed.</summary>
    public int UiActionsPerformed => _uiActions.Count - _uiActionsNotPerformed.Count;

    /// <summary>Names of the shipped-screen actions that could not be performed, with the reason.</summary>
    public IReadOnlyList<string> NotPerformedNotes => _uiActionsNotPerformed.ToArray();

    /// <summary>The not-performed actions as one readable sentence, or "none".</summary>
    public string NotPerformedNotesText =>
        _uiActionsNotPerformed.Count == 0 ? "none" : string.Join("; ", _uiActionsNotPerformed);

    /// <summary>How many shipped-screen actions could not be performed.</summary>
    public int UiActionsNotPerformed => _uiActionsNotPerformed.Count;

    /// <summary>Slowest shipped-screen action, in milliseconds.</summary>
    public double SlowestUiActionMilliseconds =>
        _uiActions.Count == 0 ? 0 : _uiActions.Max(action => action.Milliseconds);

    /// <summary>Number of secret probes executed across every surface.</summary>
    public int SecretProbeCount => _secretProbes.Count;

    /// <summary>Number of probes that actually found a raw secret; any value above zero is a failure.</summary>
    public int SecretFoundCount => _secretProbes.Count(probe => probe.Found);

    public bool AnySecretFound => SecretFoundCount > 0;

    /// <summary>Number of PNG screenshots captured from the shown window.</summary>
    public int ScreenshotCount => _evidence.Count(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

    public bool HasScreenshot => ScreenshotCount > 0;

    /// <summary>Rendered text elements read off the shown window, as a UI state reading.</summary>
    public int RenderedTextElementCount =>
        int.TryParse(GetFact("screenshot.renderedTextElements"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? count
            : 0;

    /// <summary>
    /// The post-GC managed memory a run may add between the post-seed checkpoint and the end of the stream
    /// without failing the §9.2 requirement.
    /// <para>
    /// This is an absolute allowance, not a multiple of the seed footprint. The previous gate allowed ten
    /// times the seed, and the run it "passed" grew from 263.7 MiB to 585.2 MiB managed - a 321.5 MiB
    /// increase over 90 000 events that a ten-times bound happily absorbs. A multiple of the starting
    /// point is also self-serving: the more a run leaks, the more leakage the gate allows.
    /// </para>
    /// </summary>
    public const long ManagedGrowthAllowanceBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The private-bytes allowance over the same window. Larger than the managed one because the CLR keeps
    /// native arenas the managed heap does not account for, but still a ceiling rather than a multiple: the
    /// run that grew private bytes from 337.9 MiB to 1555.8 MiB added 1217.9 MiB and must not pass.
    /// </summary>
    public const long PrivateGrowthAllowanceBytes = 192L * 1024 * 1024;

    /// <summary>
    /// True when post-GC memory growth over the stream stays inside the absolute allowances above.
    /// <para>
    /// A linear leak shows up here as an absolute increase proportional to the stream, and the allowance
    /// does not grow with the stream, so no volume of extra events can hide one. The gate also requires the
    /// in-memory window to have stayed inside the bound the run declared: a run that grew the product's
    /// capacity to retain everything would report bounded growth by not being bounded at all, and that is
    /// exactly the substitution the previous run made.
    /// </para>
    /// </summary>
    public bool MemoryGrowthIsBounded
    {
        get
        {
            var seed = SeedCheckpoint;
            var end = _checkpoints.LastOrDefault();

            if (seed is null || end is null || ReferenceEquals(seed, end)
                || seed.ManagedBytes <= 0 || seed.PrivateBytes <= 0
                || end.ManagedBytes <= 0 || end.PrivateBytes <= 0)
            {
                return false;
            }

            if (!UsedShippedWindowCapacity || WindowStayedWithinCapacity <= 0
                || WindowStayedWithinCapacity > ComposedWindowCapacity)
            {
                return false;
            }

            return ManagedGrowthBytes(end) <= ManagedGrowthAllowanceBytes
                && PrivateGrowthBytes(end) <= PrivateGrowthAllowanceBytes;
        }
    }

    /// <summary>Post-GC managed memory added between the post-seed checkpoint and the end of the run.</summary>
    public long ManagedGrowthBytes(MemoryCheckpoint? end = null)
    {
        var seed = SeedCheckpoint;
        end ??= _checkpoints.LastOrDefault();

        if (seed is null || end is null)
        {
            return long.MaxValue;
        }

        return end.ManagedBytes - seed.ManagedBytes;
    }

    /// <summary>Private bytes added between the post-seed checkpoint and the end of the run.</summary>
    public long PrivateGrowthBytes(MemoryCheckpoint? end = null)
    {
        var seed = SeedCheckpoint;
        end ??= _checkpoints.LastOrDefault();

        if (seed is null || end is null)
        {
            return long.MaxValue;
        }

        return end.PrivateBytes - seed.PrivateBytes;
    }

    /// <summary>Managed bytes the stream added per offered event; the shape of a linear leak.</summary>
    public double ManagedBytesPerOfferedEvent =>
        OfferedEvents <= 0 ? 0 : ManagedGrowthBytes() / (double)OfferedEvents;

    /// <summary>
    /// Peak number of events the in-memory window held, and whether it stayed inside the bound the run
    /// composed. Zero means the run never reported the fact, which the gate treats as a failure rather than
    /// as an absence of evidence.
    /// </summary>
    public int WindowStayedWithinCapacity { get; set; }

    /// <summary>The bound the run actually ran with, in events.</summary>
    public long ComposedWindowCapacity { get; set; }

    /// <summary>The bound the shipped product composes, in events.</summary>
    public long ShippedWindowCapacity { get; set; }

    /// <summary>True when the run used the shipped bound rather than substituting its own.</summary>
    public bool UsedShippedWindowCapacity =>
        ComposedWindowCapacity > 0 && ShippedWindowCapacity > 0
        && ComposedWindowCapacity == ShippedWindowCapacity;

    private MemoryCheckpoint? SeedCheckpoint =>
        _checkpoints.FirstOrDefault(checkpoint =>
            checkpoint.Name.StartsWith("after seed", StringComparison.Ordinal));

    public string DescribeMemoryCheckpoints()
    {
        if (_checkpoints.Count == 0)
        {
            return "no memory checkpoint was taken";
        }

        var builder = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        foreach (var checkpoint in _checkpoints)
        {
            builder.Append(checkpoint.Describe()).Append("; ");
        }

        var seed = SeedCheckpoint;
        var end = _checkpoints.LastOrDefault();

        if (seed is not null && end is not null && seed.ManagedBytes > 0)
        {
            builder.Append(string.Create(
                culture,
                $"post-GC managed growth over the stream "
                + $"{ManagedGrowthBytes(end) / (1024d * 1024d):N1} MiB "
                + $"(seed {seed.ManagedBytes / (1024d * 1024d):N1} MiB, end "
                + $"{end.ManagedBytes / (1024d * 1024d):N1} MiB; allowance "
                + $"{ManagedGrowthAllowanceBytes / (1024d * 1024d):N0} MiB, "
                + $"{ManagedBytesPerOfferedEvent:N0} bytes per offered event); "
                + $"private growth {PrivateGrowthBytes(end) / (1024d * 1024d):N1} MiB "
                + $"(allowance {PrivateGrowthAllowanceBytes / (1024d * 1024d):N0} MiB); "
                + $"window peak {WindowStayedWithinCapacity} of a composed bound of {ComposedWindowCapacity} "
                + $"(shipped bound {ShippedWindowCapacity})"));
        }

        return builder.ToString();
    }

    public string GetFact(string key) =>
        _facts.TryGetValue(key, out var value) ? value : string.Empty;

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        builder.AppendLine(AchievedNormativeProfile
            ? "# Phase 11 normative event-load report"
            : "# Phase 11 diagnostic event-load report");
        builder.AppendLine();
        builder.AppendLine($"**Mode: {Options.ModeDisplay}.** "
            + (Options.IsSmoke
                ? "A short deterministic smoke over the same code paths. It is explicitly NOT the "
                  + "30-minute acceptance run; every criterion that requires the full profile is left NOT_TESTED."
                : AchievedNormativeProfile
                    ? "The measured normative 30-minute acceptance run."
                    : "A full-mode diagnostic run; the normative profile has not been demonstrated.")
            + (Options.IsReduced
                ? " **REDUCED**: the seed and/or stream duration were overridden for diagnosis, so this run is "
                  + "even weaker than the profile it names and must not be cited as a profile result."
                : string.Empty));
        builder.AppendLine();

        builder.AppendLine("## Machine and runtime");
        builder.AppendLine();
        builder.AppendLine("| Fact | Value |");
        builder.AppendLine("| --- | --- |");
        AppendFact(builder, "os", Environment.OSVersion.ToString());
        AppendFact(builder, "runtime", Environment.Version.ToString());
        AppendFact(builder, "process64bit", Environment.Is64BitProcess.ToString());
        AppendFact(builder, "processorCount", Environment.ProcessorCount.ToString());
        AppendFact(builder, "machine", Environment.MachineName);
        AppendFact(builder, "configuration", GetConfiguration());
        AppendFact(builder, "startedAtUtc", StartedAtUtc.ToString("O", culture));
        AppendFact(builder, "runRoot", Options.RunRoot);
        foreach (var fact in _facts)
        {
            AppendFact(builder, fact.Key, fact.Value);
        }

        builder.AppendLine();

        builder.AppendLine("## Profile actually run");
        builder.AppendLine();
        builder.AppendLine($"- {Profile.Describe()}");
        builder.AppendLine($"- Size distribution: {Options.SizeDistribution.Describe()}");
        builder.AppendLine(
            $"- Normative §9.2 profile matched exactly: {(Profile.IsNormativeFullProfile ? "yes" : "no")}.");
        builder.AppendLine(
            $"- Normative 30-minute duration executed: {(ExecutedFullProfile ? "yes" : "no")}.");
        builder.AppendLine();

        builder.AppendLine("## Durations and rates");
        builder.AppendLine();
        builder.AppendLine($"- Total wall duration: {ActualDuration.TotalSeconds:N1} s "
            + $"(started {StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC).");
        builder.AppendLine($"- Stream phase duration: {ActualStreamDuration.TotalSeconds:N1} s.");
        builder.AppendLine(
            $"- Offered rate achieved: {OfferedRatePerSecond:N2} events/second "
            + $"(target {Profile.EventsPerSecond}).");
        builder.AppendLine();

        builder.AppendLine("## Counts");
        builder.AppendLine();
        builder.AppendLine("| Counter | Value |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| Seeded already-saved events (before restart) | {Profile.SeedEvents:N0} |");
        builder.AppendLine($"| Reloaded from the durable journal after restart | {ReloadedAfterRestart:N0} |");
        builder.AppendLine($"| Exact total the reloaded screen reports | {ReloadedScreenTotal:N0} |");
        builder.AppendLine($"| Rows the reloaded screen materialized for one page | {ReloadedScreenRows:N0} |");
        builder.AppendLine($"| Events the restarted process held in its in-memory window | {ReloadedWindowCount:N0} |");
        builder.AppendLine($"| Oldest saved row found by the restarted screen's search | {ReloadedOldestSearchMatched:N0} |");
        builder.AppendLine($"| Stream events offered | {OfferedEvents:N0} |");
        builder.AppendLine($"| Stream events accepted by the ingestion boundary | {AcceptedEvents:N0} |");
        builder.AppendLine($"| Rows persisted in the durable journal | {PersistedRows:N0} |");
        builder.AppendLine($"| Rows displayed on one page of the shipped list | {DisplayedRows:N0} |");
        builder.AppendLine($"| Events evicted from the in-memory window | {EvictedEvents:N0} |");
        builder.AppendLine($"| Rows removed by the durable retention rule | {JournalEvictedRows:N0} |");
        builder.AppendLine($"| Write-queue offers dropped (queue was full) | {JournalDroppedOffers:N0} |");
        builder.AppendLine($"| Write-queue rows faulted (never committed) | {JournalFailedOffers:N0} |");
        builder.AppendLine($"| Distinct execution identities offered | {DistinctExecutionIdentities:N0} |");
        builder.AppendLine(
            $"| Events the run must keep available | {Profile.Capacity:N0} |");
        builder.AppendLine($"| In-memory window bound the run actually used | {ComposedWindowCapacity:N0} |");
        builder.AppendLine($"| In-memory window bound the product ships | {ShippedWindowCapacity:N0} |");
        builder.AppendLine(
            $"| Window peak at end of run | {WindowStayedWithinCapacity:N0} |");
        builder.AppendLine();

        builder.AppendLine("## Production ingestion, before any synthetic traffic");
        builder.AppendLine();
        builder.AppendLine(
            "- Wired product sources: "
            + (ProductionIngestionSources.Count == 0
                ? "**none**"
                : string.Join("; ", ProductionIngestionSources)));
        builder.AppendLine(
            $"- Rows already in the durable journal before the first synthetic append: "
            + $"{ProductionRowsAtStartup:N0}.");
        builder.AppendLine(
            "- Notifications delivered by the product's own sources during the run: "
            + $"{ProductionIngestedEvents:N0}.");
        builder.AppendLine(
            "- Everything below this line is load-driver traffic. It is never counted towards production "
            + "ingestion.");
        builder.AppendLine();

        builder.AppendLine("## Durable search over the retained set");
        builder.AppendLine();

        if (SearchMatchCounts.Count == 0)
        {
            builder.AppendLine("No durable search was measured.");
        }
        else
        {
            builder.AppendLine("| Query | Exact matches |");
            builder.AppendLine("| --- | --- |");

            foreach (var pair in SearchMatchCounts)
            {
                builder.AppendLine(string.Create(
                    culture,
                    $"| `{Escape(pair.Key)}` | {pair.Value:N0} |"));
            }

            builder.AppendLine();
            builder.AppendLine(
                $"- Every-retained-row coverage: "
                + $"**{(DurableSearchCoversTheRetainedSet ? "yes" : "NO")}**.");
            builder.AppendLine(
                $"- Oldest retained row findable: "
                + $"**{(OldestRetainedRowIsSearchable ? "yes" : "NO")}**.");
        }

        builder.AppendLine();

        builder.AppendLine("## Message size distribution actually offered");
        builder.AppendLine();
        builder.AppendLine($"- Aggregate offered bytes: {AggregateOfferedBytes:N0} "
            + $"({AggregateOfferedBytes / (1024d * 1024d):N1} MiB).");
        builder.AppendLine($"- Aggregate bytes from messages above 1 KiB: {AggregateLargePayloadBytes:N0} "
            + $"(cap {Options.SizeDistribution.LargePayloadByteCap:N0}, "
            + $"within cap: {(AggregateLargePayloadBytes <= Options.SizeDistribution.LargePayloadByteCap ? "yes" : "NO")}).");
        builder.AppendLine($"- Largest single message observed: {MaxMessageBytesObserved:N0} bytes "
            + $"({MaxMessageBytesObserved / 1024d:N1} KiB; supported maximum "
            + $"{ActivityLoadProfile.MaxMessageBytes / 1024d:N0} KiB).");
        builder.AppendLine();

        builder.AppendLine("## Latency");
        builder.AppendLine();
        builder.AppendLine($"- {VisibilityLatency.Describe("Event-to-visible under the load (ingestion stamp to post-dispatch WPF-visible stamp)")}");
        builder.AppendLine($"  - Budget: p95 at or below {ActivityLoadProfile.UiEventLatencyBudgetMilliseconds} ms. "
            + $"Verdict: {(VisibilityLatency.P95Milliseconds <= ActivityLoadProfile.UiEventLatencyBudgetMilliseconds ? "within budget" : "OVER BUDGET")}.");
        builder.AppendLine($"- {PreLoadVisibilityLatency.Describe("Event-to-visible during the bulk pre-load (reported separately, not the §9.2 figure)")}");
        builder.AppendLine($"- {SearchLatency.Describe("Free-text search over the redacted index")}");
        builder.AppendLine($"- {DispatcherLatency.Describe("Dispatcher interaction (list rebuild plus publication, excluding the off-dispatcher durable query)")}");
        builder.AppendLine($"- {QueryLatency.Describe("Durable query cost on the query worker (never on the dispatcher)")}");
        builder.AppendLine();

        builder.AppendLine("## Memory");
        builder.AppendLine();
        builder.AppendLine("| Checkpoint | Managed | Private | Working set |");
        builder.AppendLine("| --- | --- | --- | --- |");

        foreach (var checkpoint in _checkpoints)
        {
            builder.AppendLine(string.Create(
                culture,
                $"| {checkpoint.Name} | {checkpoint.ManagedBytes / (1024d * 1024d):N1} MiB "
                + $"| {checkpoint.PrivateBytes / (1024d * 1024d):N1} MiB "
                + $"| {checkpoint.WorkingSetBytes / (1024d * 1024d):N1} MiB |"));
        }

        builder.AppendLine();

        builder.AppendLine("## Retention and overflow");
        builder.AppendLine();
        builder.AppendLine($"- Statistics at end of run: {Retention.OverflowDisplay}");
        builder.AppendLine($"- Overflow label read from the shipped screen: \"{OverflowLabelSeen}\"");
        builder.AppendLine($"- Capacity+1 negative control: {NegativeControlSummary}");
        builder.AppendLine($"- Lossy write-queue negative control: {LossyQueueControlSummary}");
        builder.AppendLine();

        builder.AppendLine("## UI actions performed on the shipped screen");
        builder.AppendLine();
        builder.AppendLine("| Action | Elapsed (ms) | Note |");
        builder.AppendLine("| --- | --- | --- |");

        foreach (var action in _uiActions)
        {
            builder.AppendLine(string.Create(
                culture,
                $"| {action.Name} | {action.Milliseconds:N1} | {action.Note} |"));
        }

        builder.AppendLine();

        builder.AppendLine("## Secret fixtures");
        builder.AppendLine();
        builder.AppendLine("Every surface below must report `not found`. A value found on any surface fails "
            + "the redaction criterion.");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Surface | Search term | Found | Note |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var probe in _secretProbes)
        {
            builder.AppendLine(string.Create(
                culture,
                $"| {probe.FixtureId} | {probe.Surface} | `{Escape(probe.SearchTerm)}` "
                + $"| {(probe.Found ? "FOUND" : "not found")} | {Escape(probe.Note)} |"));
        }

        builder.AppendLine();

        builder.AppendLine("## Evidence");
        builder.AppendLine();

        foreach (var path in _evidence)
        {
            builder.AppendLine($"- `{path}`");
        }

        builder.AppendLine();

        builder.AppendLine("## Criteria");
        builder.AppendLine();
        builder.AppendLine("| Criterion | Requirement | Outcome | Measurement |");
        builder.AppendLine("| --- | --- | --- | --- |");

        foreach (var criterion in _criteria)
        {
            builder.AppendLine(string.Create(
                culture,
                $"| {criterion.Id} | {Escape(criterion.Requirement)} | **{criterion.OutcomeDisplay}** "
                + $"| {Escape(criterion.Measurement)} |"));
        }

        builder.AppendLine();
        builder.AppendLine($"**Summary: {PassedCount} PASS, {FailedCount} FAIL, {NotTestedCount} NOT_TESTED.**");
        builder.AppendLine();
        builder.AppendLine("This report is a local deterministic load scenario. It is not evidence of an "
            + "external model, route or live backend, and it does not claim Phase 11 or release completion.");

        return builder.ToString();
    }

    private static void AppendFact(StringBuilder builder, string key, string value) =>
        builder.AppendLine($"| {key} | {Escape(value)} |");

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string GetConfiguration() =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}
