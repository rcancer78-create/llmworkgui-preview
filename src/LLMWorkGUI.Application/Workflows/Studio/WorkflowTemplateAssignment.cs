namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>
/// A durable project-to-template assignment. It points a project at one immutable template version; it
/// never modifies the assigned template, the active workflow run or the imported workflow ZIP.
/// </summary>
public sealed record WorkflowTemplateAssignment(
    string AssignmentId,
    string ProjectId,
    string TemplateId,
    int TemplateVersion,
    DateTimeOffset AssignedAtUtc);
