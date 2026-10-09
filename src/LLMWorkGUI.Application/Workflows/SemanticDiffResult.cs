using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public sealed record SemanticDiffResult
{
    public SemanticDiffResult(
        IReadOnlyList<AdaptationValidationIssue> issues,
        IReadOnlyList<string> changes,
        bool hasSemanticChanges,
        IReadOnlyList<SemanticChange>? detectedChanges = null)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(changes);

        Issues = issues.ToArray();
        Changes = changes.ToArray();
        HasSemanticChanges = hasSemanticChanges;
        DetectedChanges = (detectedChanges ?? Array.Empty<SemanticChange>()).ToArray();
    }

    public IReadOnlyList<AdaptationValidationIssue> Issues { get; }

    public IReadOnlyList<string> Changes { get; }

    public bool HasSemanticChanges { get; }

    /// <summary>
    /// The semantic differences found by comparing the real source and candidate packages. The session shows
    /// exactly this set, so an operator decision always refers to a difference that was actually displayed.
    /// </summary>
    public IReadOnlyList<SemanticChange> DetectedChanges { get; }

    public bool HasBlockers => Issues.Count > 0;

    public IReadOnlyList<AdaptationBlockerKind> BlockerKinds => Issues
        .Select(issue => issue.Kind)
        .Distinct()
        .ToArray();
}
