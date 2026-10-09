using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

public interface IWorkflowBindingRepository
{
    Task UpsertAsync(WorkflowBinding binding, CancellationToken cancellationToken = default);

    /// <summary>Atomically moves the expected binding pointer while preserving the current route policy.
    /// Missing support refuses the mutation; read followed by upsert cannot supply this guarantee.</summary>
    Task<WorkflowBinding?> TrySetActiveVersionAsync(WorkflowBinding expected, string activeVersionId,
        DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default) =>
        Task.FromException<WorkflowBinding?>(new NotSupportedException("Atomic workflow pointer mutation is unavailable."));

    Task<WorkflowBinding?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<WorkflowBinding?> GetByProjectAndPackageAsync(
        string projectId,
        string workflowPackageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowBinding>> ListByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
