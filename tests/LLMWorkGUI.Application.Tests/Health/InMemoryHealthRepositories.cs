using LLMWorkGUI.Application.Repositories;

namespace LLMWorkGUI.Application.Tests.Health;

/// <summary>
/// Controllable clock for the health tests. Cooldown and rolling-window behaviour is time-dependent,
/// and a real clock would make those cases either slow or flaky.
/// </summary>
internal sealed class HealthTestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}

/// <summary>
/// Deterministic in-memory health state store. It mirrors the SQLite uniqueness rule
/// (one record per scope) so the service is exercised against the same contract.
/// </summary>
internal sealed class InMemoryHealthStateRepository : IHealthStateRepository
{
    private readonly Dictionary<(string ScopeType, string ScopeId), HealthStateRecord> _records = new();

    public int UpsertCount { get; private set; }

    public Task UpsertAsync(HealthStateRecord healthState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(healthState);

        UpsertCount++;
        _records[(healthState.ScopeType, healthState.ScopeId)] = healthState;

        return Task.CompletedTask;
    }

    public Task<HealthStateRecord?> GetAsync(
        string scopeType,
        string scopeId,
        CancellationToken cancellationToken = default)
    {
        _records.TryGetValue((scopeType, scopeId), out var record);

        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<HealthStateRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HealthStateRecord>>(_records.Values.ToList());
}

/// <summary>
/// Deterministic append-only audit store. Appending the same id twice throws, matching the SQLite
/// primary key, so a test can never silently overwrite an audit entry.
/// </summary>
internal sealed class InMemoryHealthEventRepository : IHealthEventRepository
{
    private readonly List<HealthEventRecord> _events = new();

    public IReadOnlyList<HealthEventRecord> Events => _events;

    /// <summary>
    /// Insertion index of each event, used as the tie-breaker for transitions sharing a timestamp.
    /// It mirrors the <c>rowid</c> ordering of the SQLite store.
    /// </summary>
    private readonly Dictionary<string, int> _sequence = new(StringComparer.Ordinal);

    public Task AppendAsync(HealthEventRecord healthEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(healthEvent);

        if (_events.Any(existing => existing.Id == healthEvent.Id))
        {
            throw new InvalidOperationException(
                $"The health event '{healthEvent.Id}' has already been appended; the audit is append-only.");
        }

        _sequence[healthEvent.Id] = _events.Count;
        _events.Add(healthEvent);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HealthEventRecord>> ListByScopeAsync(
        string scopeType,
        string scopeId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<HealthEventRecord> query = _events
            .Where(record => record.ScopeType == scopeType && record.ScopeId == scopeId)
            .OrderByDescending(record => record.OccurredAt)
            .ThenByDescending(record => _sequence[record.Id]);

        if (limit is not null)
        {
            query = query.Take(limit.Value);
        }

        return Task.FromResult<IReadOnlyList<HealthEventRecord>>(query.ToList());
    }

    public Task<IReadOnlyList<HealthEventRecord>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var recent = _events
            .OrderByDescending(record => record.OccurredAt)
            .ThenByDescending(record => _sequence[record.Id])
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<HealthEventRecord>>(recent);
    }
}
