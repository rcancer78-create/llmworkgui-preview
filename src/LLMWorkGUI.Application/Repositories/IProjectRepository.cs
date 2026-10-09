using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

public interface IProjectRepository
{
    Task UpsertAsync(Project project, CancellationToken cancellationToken = default);

    Task<Project?> GetByIdAsync(string projectId, CancellationToken cancellationToken = default);

    Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string projectId, CancellationToken cancellationToken = default);
}
