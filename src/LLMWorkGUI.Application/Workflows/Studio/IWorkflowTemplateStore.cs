using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>
/// Stores immutable workflow template versions and the project-to-template assignment pointer. Saving a
/// version never overwrites an existing version and never touches a workflow run or an imported package.
/// </summary>
public interface IWorkflowTemplateStore
{
    Task SaveAsync(WorkflowTemplateDefinition template, CancellationToken cancellationToken = default);

    Task<WorkflowTemplateDefinition?> GetAsync(
        string templateId,
        int version,
        CancellationToken cancellationToken = default);

    Task<WorkflowTemplateDefinition?> GetLatestAsync(
        string templateId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowTemplateDefinition>> ListAsync(
        CancellationToken cancellationToken = default);

    Task SaveAssignmentAsync(
        WorkflowTemplateAssignment assignment,
        CancellationToken cancellationToken = default);

    Task<WorkflowTemplateAssignment?> GetAssignmentAsync(
        string projectId,
        CancellationToken cancellationToken = default);
}
