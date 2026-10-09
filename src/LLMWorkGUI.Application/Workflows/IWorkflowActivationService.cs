using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Candidate activation and rollback engine of ТЗ §6.14. Activation always re-validates the candidate
/// against the live sanitized catalog immediately before the pointer moves and fails closed while
/// blockers exist. Only the <c>WorkflowBindings</c> row is written: blobs and
/// <c>WorkflowVersion</c> records stay byte-identical (ADR-0006 §1.7). Every operation runs only on an
/// explicit user command.
/// </summary>
public interface IWorkflowActivationService
{
    /// <summary>
    /// Re-validates a version against the live catalog: extracted scratch secrets, model/capability
    /// references and the semantic guard. The temporary scratch workspace is always cleaned up.
    /// </summary>
    Task<WorkflowActivationValidationResult> ValidateForActivationAsync(
        string workflowVersionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates a version for a project through <see cref="IWorkflowBindingService"/>. A blocked
    /// candidate is refused unless the operator recorded an explicit decision for exactly the issue list
    /// the fresh validation returned; neither a blanket flag nor a per-kind set authorizes an activation.
    /// </summary>
    Task<WorkflowActivationResult> ActivateVersionAsync(
        WorkflowActivationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-points the active-version pointer of an existing binding to an earlier or existing version of
    /// the same package. The target is re-validated with the same fail-closed gate as activation, so
    /// rollback cannot broaden activation; blobs and version records are never touched (ADR-0006 §1.7).
    /// </summary>
    Task<WorkflowRollbackResult> RollbackToVersionAsync(
        string projectId,
        string workflowPackageId,
        string targetVersionId,
        bool acknowledgeBlockers = false,
        IReadOnlyCollection<AdaptationBlockerKind>? acknowledgedBlockerKinds = null,
        IReadOnlyCollection<AdaptationValidationIssue>? acknowledgedBlockerIssues = null,
        CancellationToken cancellationToken = default);
}
