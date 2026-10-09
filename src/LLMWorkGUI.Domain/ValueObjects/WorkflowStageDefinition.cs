using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.ValueObjects;

/// <summary>
/// A declarative stage of the active workflow scheme. The engine never hard-codes the development
/// chain: stages, roles, gates and routes are data of the scheme (ТЗ §6.15).
/// </summary>
public sealed class WorkflowStageDefinition
{
    public WorkflowStageDefinition(
        string stageId,
        string displayName,
        string requiredRole,
        WorkflowStageKind stageKind,
        IReadOnlyList<string> requiredReviewerRoles,
        bool requiresUserApproval,
        string? artifactRequirement,
        string? nextStageId,
        string? failureStageId)
    {
        StageId = DomainGuard.NotBlank(stageId, nameof(stageId));
        DisplayName = DomainGuard.NotBlank(displayName, nameof(displayName));
        RequiredRole = DomainGuard.NotBlank(requiredRole, nameof(requiredRole));
        if (!Enum.IsDefined(stageKind)) throw new ArgumentOutOfRangeException(nameof(stageKind));
        StageKind = stageKind;
        RequiredReviewerRoles = CopyReviewerRoles(requiredReviewerRoles);
        RequiresUserApproval = requiresUserApproval;
        ArtifactRequirement = DomainGuard.OptionalNotBlank(artifactRequirement, nameof(artifactRequirement));
        NextStageId = DomainGuard.OptionalNotBlank(nextStageId, nameof(nextStageId));
        FailureStageId = DomainGuard.OptionalNotBlank(failureStageId, nameof(failureStageId));
    }

    public string StageId { get; }

    public string DisplayName { get; }

    public string RequiredRole { get; }

    public WorkflowStageKind StageKind { get; }

    public IReadOnlyList<string> RequiredReviewerRoles { get; }

    public bool RequiresUserApproval { get; }

    public string? ArtifactRequirement { get; }

    public string? NextStageId { get; }

    public string? FailureStageId { get; }

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
