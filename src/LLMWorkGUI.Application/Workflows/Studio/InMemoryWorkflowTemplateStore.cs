using System.Collections.Concurrent;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Workflows.Studio;

/// <summary>
/// In-memory immutable version store. A template version, once saved, can never be overwritten: an
/// attempt to save the same (template id, version) pair fails instead of mutating the stored graph. This
/// is what keeps an active run and the imported ZIP byte-identical while the Studio edits templates.
/// </summary>
public sealed class InMemoryWorkflowTemplateStore : IWorkflowTemplateStore
{
    private readonly ConcurrentDictionary<string, WorkflowTemplateDefinition> _templates =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkflowTemplateAssignment> _assignments =
        new(StringComparer.Ordinal);

    public InMemoryWorkflowTemplateStore(IEnumerable<WorkflowTemplateDefinition>? seed = null)
    {
        if (seed is null)
        {
            return;
        }

        foreach (var template in seed)
        {
            ArgumentNullException.ThrowIfNull(template);
            if (!_templates.TryAdd(CreateKey(template.TemplateId, template.Version), template))
            {
                throw new WorkflowValidationException(
                    $"Template '{template.TemplateId}' version {template.Version} is duplicated in the seed. "
                    + "Saved template versions are immutable.");
            }
        }
    }

    public Task SaveAsync(
        WorkflowTemplateDefinition template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        cancellationToken.ThrowIfCancellationRequested();

        var key = CreateKey(template.TemplateId, template.Version);

        if (!_templates.TryAdd(key, template))
        {
            throw new WorkflowValidationException(
                $"Template '{template.TemplateId}' version {template.Version} already exists. Saved "
                + "template versions are immutable; create a new version instead of overwriting one.");
        }

        return Task.CompletedTask;
    }

    public Task<WorkflowTemplateDefinition?> GetAsync(
        string templateId,
        int version,
        CancellationToken cancellationToken = default)
    {
        ApplicationGuard.NotBlank(templateId, nameof(templateId));
        cancellationToken.ThrowIfCancellationRequested();

        _templates.TryGetValue(CreateKey(templateId, version), out var template);

        return Task.FromResult(template);
    }

    public Task<WorkflowTemplateDefinition?> GetLatestAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        ApplicationGuard.NotBlank(templateId, nameof(templateId));
        cancellationToken.ThrowIfCancellationRequested();

        var latest = _templates.Values
            .Where(template => string.Equals(template.TemplateId, templateId, StringComparison.Ordinal))
            .OrderByDescending(template => template.Version)
            .FirstOrDefault();

        return Task.FromResult(latest);
    }

    public Task<IReadOnlyList<WorkflowTemplateDefinition>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<WorkflowTemplateDefinition> templates = _templates.Values
            .OrderBy(template => template.IsBuiltIn ? 0 : 1)
            .ThenBy(template => template.TemplateId, StringComparer.Ordinal)
            .ThenBy(template => template.Version)
            .ToArray();

        return Task.FromResult(templates);
    }

    public Task SaveAssignmentAsync(
        WorkflowTemplateAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        cancellationToken.ThrowIfCancellationRequested();

        _assignments[assignment.ProjectId] = assignment;

        return Task.CompletedTask;
    }

    public Task<WorkflowTemplateAssignment?> GetAssignmentAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ApplicationGuard.NotBlank(projectId, nameof(projectId));
        cancellationToken.ThrowIfCancellationRequested();

        _assignments.TryGetValue(projectId, out var assignment);

        return Task.FromResult(assignment);
    }

    private static string CreateKey(string templateId, int version) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{templateId}@{version}");
}
