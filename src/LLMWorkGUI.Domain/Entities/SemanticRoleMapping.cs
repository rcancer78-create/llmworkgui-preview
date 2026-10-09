using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.Entities;

public sealed record SemanticRoleMapping
{
    public SemanticRoleMapping(
        WorkflowRole role,
        string originalRoute,
        string targetRoute,
        string targetModelId,
        string rationale,
        bool isSemanticChange,
        AdaptationBlockerKind? blockerKind = null)
    {
        if (role is not (WorkflowRole.Coordinator or WorkflowRole.Executor or WorkflowRole.Reviewer or WorkflowRole.Escalation))
        {
            throw new ArgumentException(
                "Semantic role mapping role must be a known role other than Unknown.",
                nameof(role));
        }

        if (blockerKind.HasValue && !Enum.IsDefined(blockerKind.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(blockerKind), blockerKind, "Unknown adaptation blocker kind.");
        }

        Role = role;
        OriginalRoute = DomainGuard.NotBlank(originalRoute, nameof(originalRoute));
        TargetRoute = DomainGuard.NotBlank(targetRoute, nameof(targetRoute));
        TargetModelId = DomainGuard.NotBlank(targetModelId, nameof(targetModelId));
        Rationale = DomainGuard.NotBlank(rationale, nameof(rationale));
        IsSemanticChange = isSemanticChange;
        BlockerKind = blockerKind;
    }

    public WorkflowRole Role { get; }

    public string OriginalRoute { get; }

    public string TargetRoute { get; }

    public string TargetModelId { get; }

    public string Rationale { get; }

    public bool IsSemanticChange { get; }

    public AdaptationBlockerKind? BlockerKind { get; }
}
