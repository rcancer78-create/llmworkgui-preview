using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Outcome of an explicit rollback command. Rollback only moves the active-version pointer of the
/// binding; the package, the versions and the stored blobs are never modified (ADR-0006 §1.7).
/// Rollback applies the same fail-closed activation validation to the target, so it cannot broaden
/// activation: an unacknowledged blocker leaves the existing pointer in place.
/// </summary>
public sealed record WorkflowRollbackResult
{
    private WorkflowRollbackResult(
        bool isSuccess,
        bool isBlocked,
        WorkflowBinding? binding,
        string? previousVersionId,
        string targetVersionId,
        WorkflowActivationValidationResult? validation,
        string? errorMessage)
    {
        IsSuccess = isSuccess;
        IsBlocked = isBlocked;
        Binding = binding;
        PreviousVersionId = previousVersionId;
        TargetVersionId = targetVersionId;
        Validation = validation;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccess { get; }

    /// <summary>True when the validation found unacknowledged blockers and no pointer moved.</summary>
    public bool IsBlocked { get; }

    public WorkflowBinding? Binding { get; }

    /// <summary>Active version that was replaced, when a binding existed before the rollback.</summary>
    public string? PreviousVersionId { get; }

    public string TargetVersionId { get; }

    /// <summary>Fresh validation of the rollback target when the command was blocked or failed late.</summary>
    public WorkflowActivationValidationResult? Validation { get; }

    public string? ErrorMessage { get; }

    public static WorkflowRollbackResult Success(
        WorkflowBinding binding,
        string? previousVersionId,
        string targetVersionId)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return new WorkflowRollbackResult(
            isSuccess: true,
            isBlocked: false,
            binding,
            previousVersionId,
            ApplicationGuard.NotBlank(targetVersionId, nameof(targetVersionId)),
            validation: null,
            errorMessage: null);
    }

    public static WorkflowRollbackResult Blocked(
        WorkflowActivationValidationResult validation,
        string targetVersionId,
        string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(validation);

        return new WorkflowRollbackResult(
            isSuccess: false,
            isBlocked: true,
            binding: null,
            previousVersionId: null,
            ApplicationGuard.NotBlank(targetVersionId, nameof(targetVersionId)),
            validation,
            ApplicationGuard.NotBlank(errorMessage, nameof(errorMessage)));
    }

    public static WorkflowRollbackResult Failed(string targetVersionId, string errorMessage)
    {
        return new WorkflowRollbackResult(
            isSuccess: false,
            isBlocked: false,
            binding: null,
            previousVersionId: null,
            ApplicationGuard.NotBlank(targetVersionId, nameof(targetVersionId)),
            validation: null,
            ApplicationGuard.NotBlank(errorMessage, nameof(errorMessage)));
    }

    public static WorkflowRollbackResult Failed(
        string targetVersionId,
        string errorMessage,
        WorkflowActivationValidationResult? validation)
    {
        return new WorkflowRollbackResult(
            isSuccess: false,
            isBlocked: false,
            binding: null,
            previousVersionId: null,
            ApplicationGuard.NotBlank(targetVersionId, nameof(targetVersionId)),
            validation,
            ApplicationGuard.NotBlank(errorMessage, nameof(errorMessage)));
    }
}
