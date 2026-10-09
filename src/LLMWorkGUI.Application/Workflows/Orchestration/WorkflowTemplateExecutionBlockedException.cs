namespace LLMWorkGUI.Application.Workflows.Orchestration;

/// <summary>
/// A run was refused because the template version assigned to its project is not executable in this
/// slice. <see cref="Blocker"/> is always one of the <see cref="WorkflowTemplateExecutionBlockers"/>
/// names, so the refusal is a specific, reportable reason rather than a generic failure.
/// </summary>
public sealed class WorkflowTemplateExecutionBlockedException : Exception
{
    public WorkflowTemplateExecutionBlockedException(string blocker, string message)
        : base(message)
    {
        Blocker = ApplicationGuard.NotBlank(blocker, nameof(blocker));
    }

    public string Blocker { get; }
}
