using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Workflows;

/// <summary>
/// Outcome of an explicit activation command. A blocked or failed activation never writes the binding
/// row; only <see cref="Success"/> carries the moved pointer.
/// </summary>
public sealed record WorkflowActivationResult
{
    private WorkflowActivationResult(
        bool isSuccess,
        bool isBlocked,
        WorkflowBinding? binding,
        WorkflowActivationValidationResult? validation,
        string? errorMessage)
    {
        IsSuccess = isSuccess;
        IsBlocked = isBlocked;
        Binding = binding;
        Validation = validation;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccess { get; }

    public bool IsBlocked { get; }

    public WorkflowBinding? Binding { get; }

    public WorkflowActivationValidationResult? Validation { get; }

    public string? ErrorMessage { get; }

    public static WorkflowActivationResult Success(
        WorkflowBinding binding,
        WorkflowActivationValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(validation);

        return new WorkflowActivationResult(
            isSuccess: true,
            isBlocked: false,
            binding,
            validation,
            errorMessage: null);
    }

    public static WorkflowActivationResult Blocked(
        WorkflowActivationValidationResult validation,
        string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(validation);

        return new WorkflowActivationResult(
            isSuccess: false,
            isBlocked: true,
            binding: null,
            validation,
            ApplicationGuard.NotBlank(errorMessage, nameof(errorMessage)));
    }

    public static WorkflowActivationResult Failed(
        string errorMessage,
        WorkflowActivationValidationResult? validation = null)
    {
        return new WorkflowActivationResult(
            isSuccess: false,
            isBlocked: false,
            binding: null,
            validation,
            ApplicationGuard.NotBlank(errorMessage, nameof(errorMessage)));
    }
}
