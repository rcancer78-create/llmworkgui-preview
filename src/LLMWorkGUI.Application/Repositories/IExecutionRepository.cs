using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Repositories;

public interface IExecutionRepository
{
    Task UpsertAsync(Execution execution, CancellationToken cancellationToken = default);

    Task<Execution?> GetByIdAsync(string executionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Execution>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task AppendEventAsync(ExecutionEventRecord executionEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExecutionEventRecord>> ListEventsAsync(string executionId, CancellationToken cancellationToken = default);
}
