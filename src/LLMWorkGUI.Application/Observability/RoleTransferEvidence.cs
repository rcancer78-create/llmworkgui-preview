using System.Globalization;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// Evidence of one observed role hand-off of a workflow run: which role produced the task, which role
/// received it, when it was received and last changed, and the full verbatim work text recorded by the
/// domain. Every field the domain did not report stays null and is displayed by the UI as
/// <see cref="ObservableRunProjection.NotReportedPlaceholder"/> instead of being invented (ROADMAP 10D).
/// </summary>
public sealed class RoleTransferEvidence
{
    public RoleTransferEvidence(
        string toRole,
        string? fromRole,
        string? workText,
        DateTimeOffset? receivedAtUtc,
        DateTimeOffset? updatedAtUtc,
        string evidenceSource)
    {
        ToRole = ApplicationGuard.NotBlank(toRole, nameof(toRole));
        FromRole = ApplicationGuard.OptionalNotBlank(fromRole, nameof(fromRole));
        WorkText = ApplicationGuard.OptionalNotBlank(workText, nameof(workText));
        ReceivedAtUtc = receivedAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        EvidenceSource = ApplicationGuard.NotBlank(evidenceSource, nameof(evidenceSource));
    }

    public string ToRole { get; }

    /// <summary>The role the task was handed over from, or null when the domain did not report it.</summary>
    public string? FromRole { get; }

    /// <summary>The full work text recorded by the domain, never truncated. Null when not reported.</summary>
    public string? WorkText { get; }

    public DateTimeOffset? ReceivedAtUtc { get; }

    public DateTimeOffset? UpdatedAtUtc { get; }

    /// <summary>Where this evidence actually comes from; never a synthesized assumption.</summary>
    public string EvidenceSource { get; }

    public bool HasWorkText => WorkText is not null;

    public string FromRoleDisplay => FromRole ?? ObservableRunProjection.NotReportedPlaceholder;

    public string WorkTextDisplay => WorkText ?? ObservableRunProjection.NotReportedPlaceholder;

    public string ReceivedAtDisplay => FormatTimestamp(ReceivedAtUtc);

    public string UpdatedAtDisplay => FormatTimestamp(UpdatedAtUtc);

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("u", CultureInfo.InvariantCulture) ?? ObservableRunProjection.NotReportedPlaceholder;
}

/// <summary>
/// Projects the role hand-offs of a workflow run strictly from the run aggregate and the observable run
/// projections of the same run. No session, route, timestamp or work text is ever invented: an absent
/// field is projected as null and displayed as "Not reported" (ROADMAP 10D).
/// </summary>
public sealed class RoleTransferEvidenceProjector
{
    public const string ProjectionEvidenceSource = "Observable run projection";

    public const string RunEvidenceSource = "Workflow run record";

    public const string NotReportedEvidenceSource = ObservableRunProjection.NotReportedPlaceholder;

    public IReadOnlyList<RoleTransferEvidence> Project(
        WorkflowRun run,
        IReadOnlyList<ObservableRunProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(projections);

        var orderedRoles = BuildObservedRoleOrder(run, projections);
        var evidence = new List<RoleTransferEvidence>(orderedRoles.Count);

        for (var index = 0; index < orderedRoles.Count; index++)
        {
            var role = orderedRoles[index];
            var fromRole = index == 0 ? null : orderedRoles[index - 1];

            var roleProjections = projections
                .Where(projection => ProjectionMatchesRole(projection, role))
                .OrderBy(projection => projection.LastActivityAtUtc)
                .ThenBy(projection => projection.ExecutionId, StringComparer.Ordinal)
                .ToArray();

            var isCurrentRole = IsCurrentRole(run, role);
            var receivedAt = roleProjections.Select(projection => projection.StartedAtUtc).Min();

            if (receivedAt is null && isCurrentRole)
            {
                receivedAt = ResolveEnteredCurrentStageAtUtc(run);
            }

            var updatedAt = roleProjections
                .Select(projection => (DateTimeOffset?)projection.LastActivityAtUtc)
                .Max()
                ?? (isCurrentRole ? run.EndedAtUtc ?? receivedAt : null);

            evidence.Add(new RoleTransferEvidence(
                role,
                fromRole,
                BuildWorkText(run, role),
                receivedAt,
                updatedAt,
                roleProjections.Length > 0
                    ? ProjectionEvidenceSource
                    : isCurrentRole ? RunEvidenceSource : NotReportedEvidenceSource));
        }

        return evidence;
    }

    /// <summary>
    /// Resolves the observed role label of a projection: the concrete session role label is preferred
    /// because it is what the operator actually saw, and the parsed role enum is the fallback.
    /// </summary>
    public static string? ResolveRoleLabel(ObservableRunProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        if (projection.Role != WorkflowRole.Unknown)
        {
            return projection.Role.ToString();
        }

        return string.Equals(
            projection.DisplayLabel,
            nameof(WorkflowRole.Unknown),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : projection.DisplayLabel;
    }

    public static bool ProjectionMatchesRole(ObservableRunProjection projection, string role)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var label = ResolveRoleLabel(projection);

        return (label is not null && RoleEquals(label, role))
            || RoleEquals(projection.DisplayLabel, role)
            || RoleEquals(projection.Role.ToString(), role);
    }

    private static IReadOnlyList<string> BuildObservedRoleOrder(
        WorkflowRun run,
        IReadOnlyList<ObservableRunProjection> projections)
    {
        var roles = new List<string>();

        // The observed chain follows when a turn actually started; the last activity time breaks ties
        // when a start time was not reported.
        foreach (var projection in projections
                     .OrderBy(projection => projection.StartedAtUtc ?? projection.LastActivityAtUtc)
                     .ThenBy(projection => projection.LastActivityAtUtc)
                     .ThenBy(projection => projection.ExecutionId, StringComparer.Ordinal))
        {
            AddRole(roles, ResolveRoleLabel(projection));
        }

        AddRole(roles, run.CurrentRole);

        foreach (var transition in run.Transitions)
        {
            foreach (var verdict in transition.ReviewerVerdicts)
            {
                AddRole(roles, verdict.ReviewerRole);
            }
        }

        return roles;
    }

    private static void AddRole(List<string> roles, string? role)
    {
        var candidate = role?.Trim();

        if (!string.IsNullOrEmpty(candidate)
            && !roles.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            roles.Add(candidate);
        }
    }

    private static bool IsCurrentRole(WorkflowRun run, string role) =>
        RoleEquals(run.CurrentRole, role);

    private static bool RoleEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset ResolveEnteredCurrentStageAtUtc(WorkflowRun run)
    {
        var enteringTransition = run.Transitions
            .Where(transition => string.Equals(
                transition.ToStageId,
                run.CurrentStageId,
                StringComparison.Ordinal))
            .OrderByDescending(transition => transition.TriggeredAtUtc)
            .FirstOrDefault();

        return enteringTransition?.TriggeredAtUtc ?? run.StartedAtUtc;
    }

    /// <summary>
    /// Composes the full, untruncated work text of a role from the run's own records: the transition
    /// that brought the run to the current stage, the explicit user approvals of that stage, and every
    /// reviewer verdict recorded by the role. Nothing is synthesized when no record exists.
    /// </summary>
    private static string? BuildWorkText(WorkflowRun run, string role)
    {
        var lines = new List<string>();

        if (IsCurrentRole(run, role))
        {
            if (run.TerminalReason is not null)
            {
                lines.Add(run.TerminalReason);
            }

            var enteringTransition = run.Transitions
                .Where(transition => string.Equals(
                    transition.ToStageId,
                    run.CurrentStageId,
                    StringComparison.Ordinal))
                .OrderByDescending(transition => transition.TriggeredAtUtc)
                .FirstOrDefault();

            if (enteringTransition is not null)
            {
                lines.Add(enteringTransition.Reason);
            }

            foreach (var approval in run.Approvals
                         .Where(approval => RoleEquals(approval.StageId, run.CurrentStageId))
                         .OrderBy(approval => approval.DecidedAtUtc))
            {
                lines.Add(approval.Comment);
            }
        }

        // Transitions snapshot authorizing verdicts; deduplicate exact record copies, not their text.
        foreach (var verdict in run.Verdicts.Concat(run.Transitions.SelectMany(transition => transition.ReviewerVerdicts))
                     .Where(verdict => RoleEquals(verdict.ReviewerRole, role))
                     .DistinctBy(verdict => (verdict.ReviewerRole, verdict.RouteId, verdict.DocumentHash,
                         verdict.Verdict, verdict.EvidenceSummary, verdict.RecordedAtUtc, verdict.ExecutionId,
                         verdict.StageId, verdict.ReviewedArtifactId))
                     .OrderBy(verdict => verdict.RecordedAtUtc))
        {
            lines.Add(verdict.EvidenceSummary);
        }

        return lines.Count == 0
            ? null
            : string.Join(Environment.NewLine, lines);
    }
}
