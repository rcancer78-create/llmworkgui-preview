namespace LLMWorkGUI.Application.Workflows;

public sealed class WorkflowValidationException : Exception
{
    public WorkflowValidationFailure Failure { get; }

    public WorkflowValidationException(WorkflowValidationFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public WorkflowValidationException(string message)
        : base(message)
    {
    }

    public WorkflowValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Closed user-facing reasons; exception text remains private diagnostic data.</summary>
public enum WorkflowValidationFailure
{
    Unspecified,
    InvalidArchive,
    MissingModelResponse
}
