using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Explicit operator command to activate one immutable version for one project. Blockers refuse the
/// activation until the operator records a decision for the exact issue list the fresh validation
/// returned: <see cref="AcknowledgedBlockerIssues"/> must cover that list as a whole. The legacy
/// blanket inputs <see cref="AcknowledgeBlockers"/> and <see cref="AcknowledgedBlockerKinds"/> are kept
/// for source compatibility only and never authorize an activation; no field defaults to true.
/// </summary>
public sealed record WorkflowActivationRequest
{
    public WorkflowActivationRequest(
        string projectId,
        string workflowPackageId,
        string workflowVersionId,
        bool acknowledgeBlockers = false,
        string? routePolicyId = null,
        IReadOnlyCollection<AdaptationBlockerKind>? acknowledgedBlockerKinds = null,
        IReadOnlyCollection<AdaptationValidationIssue>? acknowledgedBlockerIssues = null,
        bool preserveExistingRoutePolicy = false)
    {
        ProjectId = ApplicationGuard.NotBlank(projectId, nameof(projectId));
        WorkflowPackageId = ApplicationGuard.NotBlank(workflowPackageId, nameof(workflowPackageId));
        WorkflowVersionId = ApplicationGuard.NotBlank(workflowVersionId, nameof(workflowVersionId));
        AcknowledgeBlockers = acknowledgeBlockers;
        RoutePolicyId = ApplicationGuard.OptionalNotBlank(routePolicyId, nameof(routePolicyId));
        if (preserveExistingRoutePolicy && RoutePolicyId is not null)
            throw new ArgumentException("Preserving the existing policy cannot also request a replacement policy.", nameof(routePolicyId));
        PreserveExistingRoutePolicy = preserveExistingRoutePolicy;
        AcknowledgedBlockerKinds = NormalizeBlockerKinds(acknowledgedBlockerKinds);
        AcknowledgedBlockerIssues = NormalizeBlockerIssues(acknowledgedBlockerIssues);
    }

    public string ProjectId { get; }

    public string WorkflowPackageId { get; }

    public string WorkflowVersionId { get; }

    /// <summary>
    /// Legacy blanket operator confirmation. It is never set by the library commands and it no longer
    /// authorizes an activation: the gate requires <see cref="AcknowledgedBlockerIssues"/> instead.
    /// </summary>
    public bool AcknowledgeBlockers { get; }

    public string? RoutePolicyId { get; }

    /// <summary>Moves an existing binding pointer atomically, preserving its policy at the commit.</summary>
    public bool PreserveExistingRoutePolicy { get; }

    /// <summary>
    /// Legacy per-kind decisions. They are recorded for diagnostics only and no longer authorize an
    /// activation, because a kind alone cannot tell one reported issue from another.
    /// </summary>
    public IReadOnlyCollection<AdaptationBlockerKind> AcknowledgedBlockerKinds { get; }

    /// <summary>
    /// The individual blocker issues the operator decided on. Activation stays refused until this set is
    /// exactly the issue list of the fresh validation; anything added, removed or changed invalidates it.
    /// </summary>
    public IReadOnlyCollection<AdaptationValidationIssue> AcknowledgedBlockerIssues { get; }

    private static IReadOnlyCollection<AdaptationBlockerKind> NormalizeBlockerKinds(
        IReadOnlyCollection<AdaptationBlockerKind>? kinds)
    {
        if (kinds is null || kinds.Count == 0)
        {
            return Array.Empty<AdaptationBlockerKind>();
        }

        var distinct = new HashSet<AdaptationBlockerKind>();

        foreach (var kind in kinds)
        {
            if (!Enum.IsDefined(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kinds), kind, "Unknown adaptation blocker kind.");
            }

            distinct.Add(kind);
        }

        return distinct.Count == 0
            ? Array.Empty<AdaptationBlockerKind>()
            : distinct.ToArray();
    }

    private static IReadOnlyCollection<AdaptationValidationIssue> NormalizeBlockerIssues(
        IReadOnlyCollection<AdaptationValidationIssue>? issues)
    {
        if (issues is null || issues.Count == 0)
        {
            return Array.Empty<AdaptationValidationIssue>();
        }

        var distinct = new Dictionary<string, AdaptationValidationIssue>(StringComparer.Ordinal);

        foreach (var issue in issues)
        {
            ArgumentNullException.ThrowIfNull(issue, nameof(issues));

            distinct.TryAdd(issue.Identity, issue);
        }

        return distinct.Count == 0
            ? Array.Empty<AdaptationValidationIssue>()
            : distinct.Values.ToArray();
    }
}
