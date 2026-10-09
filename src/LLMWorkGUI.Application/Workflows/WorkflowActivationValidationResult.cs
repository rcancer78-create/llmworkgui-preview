using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Result of re-validating a candidate version against the live sanitized catalog directly before the
/// active-version pointer moves. Any blocker fails the activation gate closed until the operator
/// explicitly acknowledges every issue of this exact result. An issue that reports content the bounded
/// analysis could not read is never acknowledgeable: the gate stays closed until the candidate is
/// re-packaged so the comparison is complete.
/// </summary>
public sealed record WorkflowActivationValidationResult
{
    public static readonly WorkflowActivationValidationResult Valid =
        new(Array.Empty<AdaptationValidationIssue>());

    public WorkflowActivationValidationResult(IReadOnlyList<AdaptationValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        Issues = issues.ToArray();
        Blockers = Issues
            .Select(issue => issue.Kind)
            .Distinct()
            .ToArray();
    }

    public bool IsValid => !HasBlockers;

    public IReadOnlyList<AdaptationValidationIssue> Issues { get; }

    public IReadOnlyList<AdaptationBlockerKind> Blockers { get; }

    public bool HasBlockers => Blockers.Count > 0;

    /// <summary>
    /// True when at least one reported issue describes content the bounded analysis could not read. Those
    /// issues report an incomplete verification, not a difference the operator may accept, so no decision
    /// set can ever clear them.
    /// </summary>
    public bool HasUnclearableBlockers => Issues.Any(issue => issue.IsNotClearable);

    /// <summary>
    /// True only when <paramref name="acknowledgedIssues"/> is this exact issue list: the same issues
    /// with the same identities, nothing added, removed or changed. Comparing the whole list is what
    /// keeps a decision for one blocker from covering a freshly reported issue of the same kind — for
    /// example <c>MissingModel</c> confirmed for one model never authorizes a different model. A result
    /// without blockers needs no decision at all, and a result that carries an unclearable issue is never
    /// acknowledged at all.
    /// </summary>
    public bool IsFullyAcknowledgedBy(IReadOnlyCollection<AdaptationValidationIssue>? acknowledgedIssues)
    {
        if (!HasBlockers)
        {
            return true;
        }

        if (HasUnclearableBlockers)
        {
            return false;
        }

        if (acknowledgedIssues is null || acknowledgedIssues.Count == 0)
        {
            return false;
        }

        return OrderIdentities(Issues).SequenceEqual(
            OrderIdentities(acknowledgedIssues),
            StringComparer.Ordinal);
    }

    private static string[] OrderIdentities(IEnumerable<AdaptationValidationIssue> issues) =>
        issues
            .Select(issue => issue.Identity)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
}
