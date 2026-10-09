using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// The stage gates a <see cref="WorkflowNodeDefinition"/> carries, preserved verbatim from the
/// <see cref="WorkflowStageDefinition"/> it was derived from.
///
/// A graph node's <see cref="WorkflowNodeKind"/> and role binding name what a node is meant to do; they
/// do not state what has to be satisfied before its result is accepted. The stage kind, the reviewer
/// roles that must sign off, the user approval that may be required and the artifact that must exist are
/// the gate, and none of them can be recovered from a kind or a role name without guessing. Guessing is
/// what this value exists to prevent, so a template graph that is persisted or pinned carries the four
/// fields explicitly or carries nothing at all.
///
/// Nothing here is a permission. The value is data a resolver reads to decide whether it can execute a
/// node faithfully; a node whose gate is present and not empty is refused by that resolver rather than
/// being executed without its gate.
/// </summary>
public sealed class WorkflowNodeGateMetadata
{
    public WorkflowNodeGateMetadata(
        WorkflowStageKind stageKind,
        IReadOnlyList<string> requiredReviewerRoles,
        bool requiresUserApproval,
        string? artifactRequirement)
    {
        if (!Enum.IsDefined(stageKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(stageKind),
                stageKind,
                $"A node gate must name a declared workflow stage kind; '{stageKind}' is not one.");
        }

        StageKind = stageKind;
        RequiredReviewerRoles = CopyReviewerRoles(requiredReviewerRoles);
        RequiresUserApproval = requiresUserApproval;
        ArtifactRequirement = DomainGuard.OptionalNotBlank(
            artifactRequirement,
            nameof(artifactRequirement));
    }

    /// <summary>The exact stage kind of the source stage.</summary>
    public WorkflowStageKind StageKind { get; }

    /// <summary>The exact reviewer roles the source stage required, in their declared order.</summary>
    public IReadOnlyList<string> RequiredReviewerRoles { get; }

    /// <summary>Exactly whether the source stage required a user approval.</summary>
    public bool RequiresUserApproval { get; }

    /// <summary>Exactly the artifact the source stage required, or null when it required none.</summary>
    public string? ArtifactRequirement { get; }

    /// <summary>
    /// True only for the explicit gate-free tuple: a custom stage kind, no required reviewer, no user
    /// approval and no artifact requirement. This is the single tuple a resolver may map onto a plain
    /// scheme stage, and it is stated rather than inferred from the absence of any of the four fields.
    /// </summary>
    public bool IsNoGate =>
        StageKind == WorkflowStageKind.Custom
        && RequiredReviewerRoles.Count == 0
        && !RequiresUserApproval
        && ArtifactRequirement is null;

    /// <summary>
    /// Copies the four gate fields of a stage exactly. The copy is a value of its own, so mutating the
    /// stage afterwards cannot change what the node states it requires.
    /// </summary>
    public static WorkflowNodeGateMetadata CreateFrom(WorkflowStageDefinition stage)
    {
        ArgumentNullException.ThrowIfNull(stage);

        return new WorkflowNodeGateMetadata(
            stage.StageKind,
            stage.RequiredReviewerRoles,
            stage.RequiresUserApproval,
            stage.ArtifactRequirement);
    }

    private static IReadOnlyList<string> CopyReviewerRoles(IReadOnlyList<string> requiredReviewerRoles)
    {
        var copy = DomainGuard.NotNullList(requiredReviewerRoles, nameof(requiredReviewerRoles));

        foreach (var role in copy)
        {
            DomainGuard.NotBlank(role, nameof(requiredReviewerRoles));
        }

        return Array.AsReadOnly(copy.ToArray());
    }
}
