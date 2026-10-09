namespace LLMWorkGUI.Domain.Entities;

/// <summary>
/// Binds a project to an immutable workflow package and names the active version. The row is the only
/// mutable part of the workflow library: packages, versions and blobs are never modified by a binding
/// change (ADR-0006 §1.7).
/// </summary>
public sealed class WorkflowBinding
{
    public WorkflowBinding(
        string id,
        string projectId,
        string workflowPackageId,
        string activeVersionId,
        string? routePolicyId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        Id = GuardBindingId(id, nameof(id));
        ProjectId = DomainGuard.NotBlank(projectId, nameof(projectId));
        WorkflowPackageId = DomainGuard.NotBlank(workflowPackageId, nameof(workflowPackageId));
        ActiveVersionId = DomainGuard.NotBlank(activeVersionId, nameof(activeVersionId));
        RoutePolicyId = DomainGuard.OptionalNotBlank(routePolicyId, nameof(routePolicyId));
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string Id { get; }

    public string ProjectId { get; }

    public string WorkflowPackageId { get; }

    public string ActiveVersionId { get; }

    public string? RoutePolicyId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    private static string GuardBindingId(string value, string parameterName)
    {
        DomainGuard.NotBlank(value, parameterName);

        if (!Guid.TryParse(value, out _))
        {
            throw new ArgumentException(
                "Workflow binding id must be a GUID.",
                parameterName);
        }

        return value;
    }
}
