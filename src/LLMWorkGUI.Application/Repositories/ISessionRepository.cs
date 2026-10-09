using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

public interface ISessionRepository
{
    Task UpsertAsync(Session session, CancellationToken cancellationToken = default);

    Task<Session?> GetByIdAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Session>> ListByProjectAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sessions bound to a given account, regardless of project. This is the query the Health Center
    /// needs to answer "which sessions does this unhealthy account affect?", which cannot be derived
    /// from <see cref="ListByProjectAsync"/> because an account spans projects.
    /// </summary>
    Task<IReadOnlyList<Session>> ListByAccountAsync(string accountId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
}
