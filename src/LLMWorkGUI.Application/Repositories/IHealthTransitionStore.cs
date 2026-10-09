namespace LLMWorkGUI.Application.Repositories;

/// <summary>Commits a health snapshot and its audit as one operation. Null means no write to that table.</summary>
public interface IHealthTransitionStore
{
    Task SaveAsync(HealthStateRecord? state, HealthEventRecord? healthEvent,
        CancellationToken cancellationToken = default);
}
