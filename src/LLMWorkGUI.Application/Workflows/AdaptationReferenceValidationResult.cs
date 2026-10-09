using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

public sealed record AdaptationReferenceValidationResult
{
    public static readonly AdaptationReferenceValidationResult Empty =
        new(Array.Empty<AdaptationValidationIssue>());

    public AdaptationReferenceValidationResult(IReadOnlyList<AdaptationValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        Issues = issues.ToArray();
        BlockerKinds = Issues
            .Select(issue => issue.Kind)
            .Distinct()
            .ToArray();
    }

    public IReadOnlyList<AdaptationValidationIssue> Issues { get; }

    public IReadOnlyList<AdaptationBlockerKind> BlockerKinds { get; }

    public bool IsValid => Issues.Count == 0;
}
