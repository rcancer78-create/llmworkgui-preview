using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

public interface IProjectLockRepository
{
    Task<ProjectLock?> GetActiveByRootPathAsync(string rootPath, CancellationToken cancellationToken = default);

    Task<bool> TryAcquireAsync(ProjectLock projectLock, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(
        string lockId,
        DateTimeOffset releasedAt,
        string reason,
        CancellationToken cancellationToken = default);
}
