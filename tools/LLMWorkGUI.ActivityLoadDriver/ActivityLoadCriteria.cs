using LLMWorkGUI.Application.Observability;

namespace LLMWorkGUI.ActivityLoadDriver;

/// <summary>
/// Decides each Phase 11 exit criterion from the measurements a run actually produced.
/// <para>
/// The rule that matters here is the one about honesty: a criterion that requires the 30-minute profile
/// is <see cref="ActivityCriterionOutcome.NotTested"/> after a smoke, never
/// <see cref="ActivityCriterionOutcome.Pass"/>. Every other criterion is decided by its measured number
/// against the normative threshold, and the measurement travels into the report either way.
/// </para>
/// </summary>
internal static class ActivityLoadCriteria
{
    public static void Evaluate(ActivityLoadReport report, IReadOnlyList<ActivityCriterionResult> runtimeCriteria)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(runtimeCriteria);

        foreach (var criterion in runtimeCriteria)
        {
            report.Add(criterion);
        }

        var profile = report.Profile;
        var executedFull = report.ExecutedFullProfile;
        var latency = report.VisibilityLatency;

        report.Add(
            new ActivityCriterionResult(
                "profile-conformance",
                "The run offered the normative §9.2 profile: 8 executions, 50 events/second, 30 minutes, "
                + "100 000 already-saved events, messages up to 256 KiB.",
                report.AchievedNormativeProfile
                    ? ActivityCriterionOutcome.Pass
                    : ActivityCriterionOutcome.NotTested,
                $"mode {report.Options.ModeDisplay}; {profile.Describe()} Actual stream duration "
                + $"{report.ActualStreamDuration.TotalMinutes:N2} minute(s) at "
                + $"{report.OfferedRatePerSecond:N2} events/second achieved; "
                + $"{report.OfferedEvents:N0} offers, {report.AcceptedEvents:N0} completed ingestion calls, "
                + $"{report.DistinctExecutionIdentities} identities measured. Rate and elapsed-time tolerance "
                + $"{ActivityLoadReport.MeasuredProfileTolerance:P0}; the final offer may precede the duration by one event interval.",
                requiresFullProfile: true));

        // The two parts of the size criterion behave differently. "Several 256 KiB messages and a capped
        // aggregate" can be shown at any volume, so it is enforced always. "At least 99.9% at or below
        // 1 KiB" is a share, and a share over a few hundred messages is dominated by the over-small classes
        // rather than by the distribution - so it is only decided at a volume where the share means
        // something, and stays NOT_TESTED below that instead of failing or passing on a meaningless ratio.
        var reachedNormativeVolume = report.OfferedEvents >= 10_000;
        var shareKnown = report.Options.SizeDistribution.MeetsSmallShareCriterion;
        var capped = report.AggregateLargePayloadBytes <= report.Options.SizeDistribution.LargePayloadByteCap;
        var sawMaximum = report.MaxMessageBytesObserved >= ActivityLoadProfile.MaxMessageBytes;

        report.Add(
            new ActivityCriterionResult(
                "size-distribution",
                "A documented size distribution with several 256 KiB messages, at least 99.9% of messages "
                + "at or below 1 KiB, and a capped aggregate of large-payload bytes.",
                !sawMaximum || !capped
                    ? ActivityCriterionOutcome.Fail
                    : reachedNormativeVolume && !shareKnown
                        ? ActivityCriterionOutcome.Fail
                        : reachedNormativeVolume
                            ? ActivityCriterionOutcome.Pass
                            : ActivityCriterionOutcome.NotTested,
                $"{report.Options.SizeDistribution.Describe()} Observed largest message "
                + $"{report.MaxMessageBytesObserved:N0} bytes; aggregate large-payload bytes "
                + $"{report.AggregateLargePayloadBytes:N0} of a "
                + $"{report.Options.SizeDistribution.LargePayloadByteCap:N0} cap; the small-message share "
                + $"({report.Options.SizeDistribution.SmallShare * 100:N3}%) is only decided at a volume of "
                + $"at least 10 000 offered events, and this run offered {report.OfferedEvents:N0}."));

        report.Add(
            new ActivityCriterionResult(
                "unique-identity-stream",
                "The stream used unique event ids and eight execution identities, not AppendProjection.",
                report.AcceptedEvents == profile.OfferedEvents
                    && report.DistinctExecutionIdentities == profile.ExecutionIdentities
                        ? ActivityCriterionOutcome.Pass
                        : ActivityCriterionOutcome.Fail,
                $"{report.AcceptedEvents:N0} events accepted of {profile.OfferedEvents:N0} offered, "
                + $"across {report.DistinctExecutionIdentities} distinct execution identities "
                + $"(expected {profile.ExecutionIdentities}); ids are 'stream:{{sequence}}' and unique by "
                + "construction."));

        report.Add(
            new ActivityCriterionResult(
                "ui-event-latency",
                "p95 event-to-visible UI latency at or below 200 ms, measured from the ingestion stamp to "
                + "the post-dispatch WPF-visible stamp, with the sample count reported.",
                latency.SampleCount == 0 || !latency.IsComplete
                    ? ActivityCriterionOutcome.NotTested
                    : latency.P95Milliseconds <= ActivityLoadProfile.UiEventLatencyBudgetMilliseconds
                        ? ActivityCriterionOutcome.Pass
                        : ActivityCriterionOutcome.Fail,
                latency.Describe("event-to-visible")
                + $"; budget {ActivityLoadProfile.UiEventLatencyBudgetMilliseconds} ms.",
                requiresFullProfile: false));

        report.Add(
            new ActivityCriterionResult(
                "ui-stays-interactive",
                "The UI stayed interactive under the load: search, filter, paging, selection and detail all "
                + "worked on the shipped screen while the stream was growing.",
                report.UiActionCount > 0 && report.UiActionsAllSucceeded
                    ? ActivityCriterionOutcome.Pass
                    : ActivityCriterionOutcome.Fail,
                $"{report.UiActionCount} shipped-screen actions attempted against a journal of "
                + $"{report.PersistedRows:N0} rows; {report.UiActionsPerformed} performed, "
                + $"{report.UiActionsNotPerformed} could not be performed; slowest "
                + $"{report.SlowestUiActionMilliseconds:N1} ms. "
                + $"Not performed: {report.NotPerformedNotesText}"));

        report.Add(
            new ActivityCriterionResult(
                "dispatcher-latency-reported",
                "Dispatcher interaction latency is reported separately from event-to-visible latency.",
                report.DispatcherLatency.SampleCount > 0
                    ? ActivityCriterionOutcome.Pass
                    : ActivityCriterionOutcome.NotTested,
                report.DispatcherLatency.Describe("dispatcher interaction")));

        report.Add(
            new ActivityCriterionResult(
                "search-at-100000",
                "Full-text search works over the whole redacted retained set, secret fixtures are not indexed, "
                + "and the exact match counts are reported rather than merely recorded.",
                report.ReloadedAfterRestart < ActivityLoadProfile.NormativeSeedEvents
                    ? ActivityCriterionOutcome.NotTested
                    : report.SecretProbeCount > 0 && !report.AnySecretFound && report.DurableSearchCoversTheRetainedSet
                        ? ActivityCriterionOutcome.Pass
                        : ActivityCriterionOutcome.Fail,
                $"{report.ReloadedAfterRestart:N0} events reloaded from the durable journal "
                + $"(the criterion is stated at {ActivityLoadProfile.NormativeSeedEvents:N0}); "
                + $"{report.SecretProbeCount} secret probes across durable search, both the newest and the "
                + "oldest durable page, the selected row, the rendered WPF text tree and the persisted journal "
                + $"bytes; {report.SecretFoundCount} value(s) found; "
                + $"every-retained-row search complete: {(report.DurableSearchCoversTheRetainedSet ? "yes" : "NO")}; "
                + $"oldest saved row findable: {(report.OldestRetainedRowIsSearchable ? "yes" : "NO")}."));

        report.Add(
            new ActivityCriterionResult(
                "no-linear-memory-growth",
                "Memory does not grow linearly with the full stream: post-GC managed and private growth over "
                + "the stream stay inside absolute allowances, and the in-memory window stayed inside the "
                + "shipped product bound rather than a bound the run substituted for itself.",
                executedFull
                    ? (report.MemoryGrowthIsBounded
                        ? ActivityCriterionOutcome.Pass
                        : ActivityCriterionOutcome.Fail)
                    : ActivityCriterionOutcome.NotTested,
                report.DescribeMemoryCheckpoints()
                    + $". Ran at the shipped window bound: {(report.UsedShippedWindowCapacity ? "yes" : "NO")} "
                    + $"(composed {report.ComposedWindowCapacity:N0}, shipped {report.ShippedWindowCapacity:N0}). "
                    + "The allowances are absolute bytes, not a multiple of the starting footprint, so no volume "
                    + "of extra events and no large starting footprint can make a leak look bounded.",
                requiresFullProfile: true));

        report.Add(
            new ActivityCriterionResult(
                "accounting-not-silent",
                "Every offered, accepted, persisted and evicted count is reported, queue loss is reported as "
                + "queue loss rather than as journal retention, and a visible overflow label exists.",
                report.OfferedEvents > 0
                    && report.AcceptedEvents > 0
                    && report.EvictedEvents >= 0
                    && report.OverflowLabelSeen.Length > 0
                    && report.Retention.JournalEvicted == report.JournalEvictedRows
                    && report.Retention.QueueDropped == report.JournalDroppedOffers
                    && report.Retention.QueueFailed == report.JournalFailedOffers
                        ? ActivityCriterionOutcome.Pass
                        : ActivityCriterionOutcome.Fail,
                $"offered {report.OfferedEvents:N0}, accepted {report.AcceptedEvents:N0}, persisted "
                + $"{report.PersistedRows:N0}, evicted from the window {report.EvictedEvents:N0}, "
                + $"journal retention removed {report.JournalEvictedRows:N0}, write queue dropped "
                + $"{report.JournalDroppedOffers:N0} and faulted {report.JournalFailedOffers:N0}; "
                + $"overflow label on screen: \"{report.OverflowLabelSeen}\". The three loss counters are "
                + "separate by construction: retention is what the rule removed, drops and faults are what "
                + "the product failed to write."));

        report.Add(
            new ActivityCriterionResult(
                "bounded-projection",
                "Display never materializes the whole list: one page is materialized and the list is "
                + "virtualized.",
                report.DisplayedRows > 0 && report.DisplayedRows < report.PersistedRows
                    ? ActivityCriterionOutcome.Pass
                    : ActivityCriterionOutcome.NotTested,
                $"{report.DisplayedRows:N0} rows materialized for a journal of "
                + $"{report.PersistedRows:N0} rows; the shipped ListBox keeps "
                + "VirtualizingPanel.IsVirtualizing with Recycling; the in-memory window held "
                + $"{report.WindowStayedWithinCapacity:N0} events at the end of the run."));

        report.Add(
            new ActivityCriterionResult(
                "visual-evidence",
                "Visible claims rest on the actual WPF window with at least one screenshot or UI state "
                + "reading; mock-only success is not visual acceptance.",
                report.HasScreenshot && report.SecretProbeCount > 0
                    ? ActivityCriterionOutcome.Pass
                    : ActivityCriterionOutcome.NotTested,
                $"{report.ScreenshotCount} screenshot(s) and {report.RenderedTextElementCount:N0} rendered "
                + "text elements captured from the shown window."));
    }
}
