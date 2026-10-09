using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

public interface IWorkflowPackageRepository
{
    Task UpsertAsync(WorkflowPackage package, CancellationToken cancellationToken = default);

    Task<WorkflowPackage?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<WorkflowPackage?> GetByOriginalHashAsync(string originalHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowPackage>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
