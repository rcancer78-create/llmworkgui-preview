using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Binds immutable workflow packages to projects. Every operation only ever writes the binding row: the
/// package, the version and the blob stay untouched (ADR-0006 §1, §2, §3).
/// </summary>
public interface IWorkflowBindingService
{
    Task<WorkflowBindingResult> BindWorkflowToProjectAsync(
        string projectId,
        string workflowPackageId,
        string activeVersionId,
        string? routePolicyId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Low-level pointer move reserved for the activation engine, which re-validates the version
    /// immediately before calling it. UI commands must go through
    /// <see cref="IWorkflowActivationService"/> so a candidate with unacknowledged blockers can never
    /// become active (fail-closed gate).
    /// </summary>
    Task<WorkflowBindingResult> SetActiveVersionAsync(
        string projectId,
        string workflowPackageId,
        string activeVersionId,
        CancellationToken cancellationToken = default);

    Task<bool> UnbindWorkflowAsync(
        string projectId,
        string workflowPackageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowBinding>> GetBindingsForProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default);
}
