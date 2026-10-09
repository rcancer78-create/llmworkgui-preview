using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

/// <summary>
/// Deterministic in-memory health state store, mirroring the SQLite uniqueness rule of one record per
/// scope so the UI is exercised against the real service contract.
/// </summary>
internal sealed class InMemoryHealthStateStore : IHealthStateRepository
{
    private readonly Dictionary<(string ScopeType, string ScopeId), HealthStateRecord> _records = new();

    public Task UpsertAsync(HealthStateRecord healthState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(healthState);

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
/// Deterministic append-only audit store. Same-timestamp entries are ordered by insertion, matching the
/// <c>rowid</c> tie-breaker of the SQLite store.
/// </summary>
internal sealed class InMemoryHealthEventStore : IHealthEventRepository
{
    private readonly List<HealthEventRecord> _events = new();
    private readonly Dictionary<string, int> _sequence = new(StringComparer.Ordinal);

    public Task AppendAsync(HealthEventRecord healthEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(healthEvent);

        if (_sequence.ContainsKey(healthEvent.Id))
        {
            throw new InvalidOperationException(
                $"The health event '{healthEvent.Id}' was already appended; the audit is append-only.");
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

/// <summary>
/// Deterministic session store for the impacted-session view. <see cref="ListByAccountAsync"/> mirrors
/// the SQLite ordering (newest activity first) so the UI is exercised against the real contract.
/// </summary>
internal sealed class InMemorySessionStore : ISessionRepository
{
    private const string DefaultAccountId = "account-1";

    private readonly List<Session> _sessions = new();
    private DateTimeOffset _nextTimestamp = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    public void Add(
        string sessionId,
        SessionState state,
        string? activeExecutionId = null,
        string accountId = DefaultAccountId,
        string projectId = "project-1")
    {
        // Each added session is newer than the previous one, so the ordering assertion is meaningful.
        _nextTimestamp = _nextTimestamp.AddMinutes(1);

        _sessions.Add(new Session(
            sessionId,
            new SessionBinding(BackendType.OpenCode, "prov-1", accountId, "gpt-4o", null, null, null),
            projectId,
            @"C:\work\checkout",
            $"native-{sessionId}",
            state,
            ReconciliationOutcome.None,
            CloseReason.None,
            null,
            null,
            null,
            "Executor",
            activeExecutionId,
            _nextTimestamp,
            _nextTimestamp));
    }

    public Task UpsertAsync(Session session, CancellationToken cancellationToken = default)
    {
        _sessions.RemoveAll(existing => existing.Id == session.Id);
        _sessions.Add(session);

        return Task.CompletedTask;
    }

    public Task<Session?> GetByIdAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.FirstOrDefault(session => session.Id == sessionId));

    public Task<IReadOnlyList<Session>> ListByProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Session>>(
            _sessions.Where(session => session.ProjectId == projectId).ToList());

    public Task<IReadOnlyList<Session>> ListByAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Session>>(
            _sessions
                .Where(session => session.Binding.AccountId == accountId)
                .OrderByDescending(session => session.LastEventAt)
                .ThenBy(session => session.Id, StringComparer.Ordinal)
                .ToList());

    public Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.RemoveAll(session => session.Id == sessionId) > 0);
}

/// <summary>Controllable clock so cooldown-driven states are deterministic.</summary>
internal sealed class HealthUiTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}
