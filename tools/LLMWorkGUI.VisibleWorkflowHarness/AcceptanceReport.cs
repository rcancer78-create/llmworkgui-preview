using System;
using System.Collections.Generic;
using System.Globalization;

namespace LLMWorkGUI.VisibleWorkflowHarness;

/// <summary>One visible acceptance step and what was actually observed on the shown window.</summary>
internal sealed record AcceptanceStep(
    string Id,
    string Title,
    string Action,
    string Expected,
    string? Observed = null,
    AcceptanceOutcome Outcome = AcceptanceOutcome.Pending,
    string? Evidence = null,
    string? Detail = null)
{
    public AcceptanceStep Complete(
        AcceptanceOutcome outcome,
        string? observed,
        string? evidence = null,
        string? detail = null) =>
        this with
        {
            Outcome = outcome,
            Observed = observed,
            Evidence = evidence,
            Detail = detail
        };
}

internal enum AcceptanceOutcome
{
    Pending,
    Pass,
    Fail,

    /// <summary>The step could not be exercised; the exact missing control or evidence is named.</summary>
    NotTested
}

/// <summary>The whole run: the environment it ran in and every step it took.</summary>
internal sealed class AcceptanceReport
{
    private readonly List<AcceptanceStep> _steps = new();

    public AcceptanceReport(HarnessOptions options, DateTimeOffset startedAtUtc)
    {
        Options = options;
        StartedAtUtc = startedAtUtc;
    }

    public HarnessOptions Options { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public IReadOnlyList<AcceptanceStep> Steps => _steps;

    public string? RunId { get; set; }

    public string? ObservedStageId { get; set; }

    public string? ObservedTemplateDisplay { get; set; }

    public bool ProductionReviewEvidenceEmpty { get; set; } = true;

    public string? ReviewEvidenceNote { get; set; }

    public Dictionary<string, string> Facts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a step. A step is keyed by its id, so re-adding the same step with its outcome replaces the
    /// pending row instead of appending a duplicate.
    /// </summary>
    public AcceptanceStep Add(AcceptanceStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var index = _steps.FindIndex(candidate =>
            string.Equals(candidate.Id, step.Id, StringComparison.Ordinal));

        if (index >= 0)
        {
            _steps[index] = step;
        }
        else
        {
            _steps.Add(step);
        }

        return step;
    }

    public void Complete(DateTimeOffset completedAtUtc) => CompletedAtUtc = completedAtUtc;

    public int Count(AcceptanceOutcome outcome) => _steps.Count(step => step.Outcome == outcome);

    public bool HasFailures => Count(AcceptanceOutcome.Fail) > 0;

    /// <summary>Exit code: 0 when nothing failed, 1 when a visible step failed.</summary>
    public int ExitCode => HasFailures ? 1 : 0;

    public string ToMarkdown()
    {
        var builder = new System.Text.StringBuilder();

        builder.AppendLine(Options.AllUi ? "# Full UI and workflow acceptance" : "# Phase 10 visible workflow acceptance");
        builder.AppendLine();
        builder.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Run root: `{Options.RunRoot}`"));
        builder.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Mode: `{Options.Mode}` (hold timeout {Options.HoldTimeout.TotalSeconds:0} s)"));
        builder.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Started: {StartedAtUtc:u}"));
        builder.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Completed: {(CompletedAtUtc is { } completed ? completed.ToString("u", CultureInfo.InvariantCulture) : "not completed")}"));
        builder.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Result: **{Count(AcceptanceOutcome.Pass)} PASS**, **{Count(AcceptanceOutcome.Fail)} FAIL**, "
            + $"**{Count(AcceptanceOutcome.NotTested)} NOT_TESTED**"));
        builder.AppendLine();

        if (RunId is not null || ObservedStageId is not null || ObservedTemplateDisplay is not null)
        {
            builder.AppendLine("## Observed run");
            builder.AppendLine();
            builder.AppendLine($"- Run id: `{RunId ?? "not reported"}`");
            builder.AppendLine($"- Current stage: `{ObservedStageId ?? "not reported"}`");
            builder.AppendLine($"- Pinned template: `{ObservedTemplateDisplay ?? "not reported"}`");
            builder.AppendLine(
                $"- Production `IWorkflowReviewEvidenceRepository` empty: "
                + $"**{(ProductionReviewEvidenceEmpty ? "yes" : "no")}**"
                + (ReviewEvidenceNote is null ? string.Empty : $" — {ReviewEvidenceNote}"));
            builder.AppendLine();
        }

        if (Facts.Count > 0)
        {
            builder.AppendLine("## Environment facts");
            builder.AppendLine();
            builder.AppendLine("| Fact | Value |");
            builder.AppendLine("| --- | --- |");
            foreach (var fact in Facts)
            {
                builder.AppendLine($"| {fact.Key} | `{fact.Value.Replace("|", "\\|")}` |");
            }

            builder.AppendLine();
        }

        builder.AppendLine("## Steps");
        builder.AppendLine();
        builder.AppendLine("| # | Step | Outcome | Expected | Observed | Evidence |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");

        for (var index = 0; index < _steps.Count; index++)
        {
            var step = _steps[index];
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {index + 1} | {Escape(step.Title)} | **{step.Outcome}** | {Escape(step.Expected)} "
                + $"| {Escape(step.Observed ?? "-")} | {(step.Evidence is null ? "-" : $"`{step.Evidence}`")} |"));
        }

        var notTested = _steps.Where(step => step.Outcome == AcceptanceOutcome.NotTested).ToArray();
        if (notTested.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Blocked steps");
            builder.AppendLine();
            foreach (var step in notTested)
            {
                builder.AppendLine($"- **{step.Title}** — {step.Detail ?? step.Expected}");
            }
        }

        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
