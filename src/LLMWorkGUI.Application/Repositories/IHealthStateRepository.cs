namespace LLMWorkGUI.Application.Repositories;

public interface IHealthStateRepository
{
    Task UpsertAsync(HealthStateRecord healthState, CancellationToken cancellationToken = default);

    Task<HealthStateRecord?> GetAsync(
        string scopeType,
        string scopeId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HealthStateRecord>> ListAsync(CancellationToken cancellationToken = default);
}
