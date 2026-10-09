using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// The single closed source of required model capabilities for <see cref="WorkflowRole"/> in the
/// adaptation and activation reference seam.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SemanticRoleMapping"/> carries no capability field, and the only capability lists that
/// exist in the codebase today are the free-form operator strings on
/// <c>RoleBindingDefinition.RequiredCapabilities</c> and
/// <c>WorkflowNodeDefinition.RequiredCapabilities</c>. Those are never validated against
/// <see cref="ModelCapabilityFlags"/> and belong to the declarative execution seam, not to a role of
/// a <see cref="SemanticRoleMapping"/>. They are therefore not used as a requirement source, and no
/// ToolCalling/ReasoningVariants/Vision requirement is inferred from a role name or a node string.
/// </para>
/// <para>
/// <see cref="ModelCapabilityFlags.Chat"/> is consequently the only required flag, and it is the same
/// mask for every known role. This asserts chat reachability of a target route; it does not assert
/// any tool-calling, reasoning or vision capability, because no mapping in this seam requests one.
/// </para>
/// </remarks>
public static class WorkflowRoleRequiredCapabilities
{
    /// <summary>The closed minimum mask required of every <see cref="WorkflowRole"/>.</summary>
    public const ModelCapabilityFlags RequiredMask = ModelCapabilityFlags.Chat;

    /// <summary>
    /// Returns the required mask for a role. The table is closed over all known roles and is
    /// intentionally uniform; <see cref="WorkflowRole.Unknown"/> is not a mappable role
    /// (<c>SemanticRoleMapping</c> rejects it) and is refused rather than defaulted.
    /// </summary>
    public static ModelCapabilityFlags RequiredFor(WorkflowRole role) => role switch
    {
        WorkflowRole.Coordinator
        or WorkflowRole.Executor
        or WorkflowRole.Reviewer
        or WorkflowRole.Escalation => RequiredMask,
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "No required capability mask is defined for this workflow role.")
    };

    /// <summary>True when <paramref name="capabilities"/> satisfies the required mask of the role.</summary>
    public static bool IsSatisfiedBy(WorkflowRole role, ModelCapabilityFlags capabilities) =>
        (capabilities & RequiredFor(role)) == RequiredFor(role);
}
