using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

public interface IWorkflowVersionRepository
{
    Task UpsertAsync(WorkflowVersion version, CancellationToken cancellationToken = default);

    /// <summary>Allocates the next package number and inserts one immutable candidate atomically.
    /// Replaying the same candidate identity returns its allocated number; missing storage support refuses.</summary>
    Task<WorkflowVersion> InsertCandidateAsync(WorkflowVersion candidate, CancellationToken cancellationToken = default)
        => Task.FromException<WorkflowVersion>(new NotSupportedException("Atomic candidate persistence is unavailable."));

    Task<WorkflowVersion?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<WorkflowVersion?> GetByPackageAndVersionAsync(
        string packageId,
        int versionNumber,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowVersion>> ListByPackageIdAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
