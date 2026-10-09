using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

/// <summary>
/// Real-SQLite behaviour of the health recovery audit. The ordering guarantee and the append-only rule
/// are asserted against the actual database, not against an in-memory double.
/// </summary>
public sealed class HealthEventRepositoryTests : IAsyncLifetime
{
    private readonly TestDatabase _database = new();

    private SqliteHealthEventRepository _events = null!;
    private SqliteHealthStateRepository _states = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();

        _events = new SqliteHealthEventRepository(_database.Factory);
        _states = new SqliteHealthStateRepository(_database.Factory);
    }

    public Task DisposeAsync()
    {
        _database.Dispose();

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AppendAsync_PersistsEveryFieldOfTheTransition()
    {
        var occurredAt = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        await _events.AppendAsync(new HealthEventRecord(
            "event-1",
            HealthScope.RouteScopeType,
            "route-1",
            HealthState.Degraded,
            HealthState.CoolingDown,
            HealthErrorClass.QuotaOrRateLimit,
            "The quota was exhausted.",
            """{"remaining":0}""",
            occurredAt));

        var stored = Assert.Single(
            await _events.ListByScopeAsync(HealthScope.RouteScopeType, "route-1"));

        Assert.Equal("event-1", stored.Id);
        Assert.Equal(HealthState.Degraded, stored.PreviousState);
        Assert.Equal(HealthState.CoolingDown, stored.NewState);
        Assert.Equal(HealthErrorClass.QuotaOrRateLimit, stored.ErrorClass);
        Assert.Equal("The quota was exhausted.", stored.Reason);
        Assert.Equal("""{"remaining":0}""", stored.EvidenceRedactedJson);
        Assert.Equal(occurredAt, stored.OccurredAt);
    }

    [Fact]
    public async Task AppendAsync_WithAnUnobservedHistory_KeepsThePreviousStateNull()
    {
        await _events.AppendAsync(CreateEvent("event-1", previousState: null));

        var stored = Assert.Single(
            await _events.ListByScopeAsync(HealthScope.RouteScopeType, "route-1"));

        // An unobserved history must not be reported as a healthy one.
        Assert.Null(stored.PreviousState);
    }

    [Fact]
    public async Task AppendAsync_WithADuplicateId_FailsInsteadOfOverwritingTheAudit()
    {
        await _events.AppendAsync(CreateEvent("event-1"));

        // The audit is append-only: silently replacing an entry would destroy evidence.
        await Assert.ThrowsAsync<SqliteException>(
            () => _events.AppendAsync(CreateEvent("event-1")));
    }

    [Fact]
    public async Task ListByScopeAsync_OrdersSameInstantTransitionsByInsertionOrder()
    {
        // All three share one timestamp, which happens whenever a cascade is processed in one tick.
        var sameInstant = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        await _events.AppendAsync(CreateEvent("zzz-first", occurredAt: sameInstant));
        await _events.AppendAsync(CreateEvent("aaa-second", occurredAt: sameInstant));
        await _events.AppendAsync(CreateEvent("mmm-third", occurredAt: sameInstant));

        var audit = await _events.ListByScopeAsync(HealthScope.RouteScopeType, "route-1");

        // Newest first means reverse insertion order, never alphabetical id order: the ids here are
        // deliberately not sorted in insertion order so an id-based sort would fail.
        Assert.Equal(
            new[] { "mmm-third", "aaa-second", "zzz-first" },
            audit.Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public async Task ListByScopeAsync_ReturnsNewestFirstAcrossTimestamps()
    {
        var start = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        await _events.AppendAsync(CreateEvent("event-1", occurredAt: start));
        await _events.AppendAsync(CreateEvent("event-2", occurredAt: start.AddMinutes(5)));
        await _events.AppendAsync(CreateEvent("event-3", occurredAt: start.AddMinutes(10)));

        var audit = await _events.ListByScopeAsync(HealthScope.RouteScopeType, "route-1");

        Assert.Equal(
            new[] { "event-3", "event-2", "event-1" },
            audit.Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public async Task ListByScopeAsync_IsolatesScopes()
    {
        await _events.AppendAsync(CreateEvent("route-event", scopeId: "route-1"));
        await _events.AppendAsync(CreateEvent("other-event", scopeId: "route-2"));

        var audit = await _events.ListByScopeAsync(HealthScope.RouteScopeType, "route-1");

        Assert.Equal("route-event", Assert.Single(audit).Id);
    }

    [Fact]
    public async Task ListByScopeAsync_WithALimit_ReturnsOnlyTheNewestEntries()
    {
        var start = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
        {
            await _events.AppendAsync(
                CreateEvent($"event-{i}", occurredAt: start.AddMinutes(i)));
        }

        var audit = await _events.ListByScopeAsync(HealthScope.RouteScopeType, "route-1", limit: 2);

        Assert.Equal(new[] { "event-4", "event-3" }, audit.Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public async Task ListByScopeAsync_WithANonPositiveLimit_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _events.ListByScopeAsync(HealthScope.RouteScopeType, "route-1", limit: 0));
    }

    [Fact]
    public async Task ListRecentAsync_SpansEveryScope()
    {
        var start = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        await _events.AppendAsync(CreateEvent("route-event", scopeId: "route-1", occurredAt: start));
        await _events.AppendAsync(
            CreateEvent("account-event", scopeType: HealthScope.AccountScopeType, scopeId: "account-1", occurredAt: start.AddMinutes(1)));

        var recent = await _events.ListRecentAsync(limit: 10);

        Assert.Equal(
            new[] { "account-event", "route-event" },
            recent.Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public async Task HealthCenterService_OverRealSqlite_AuditsAVerifiedRecoveryEndToEnd()
    {
        var time = new SqliteHealthTimeProvider();
        var service = new HealthCenterService(
            _states,
            _events,
            time,
            new HealthPolicy { FailureThreshold = 1, CooldownDuration = TimeSpan.FromMinutes(5) });

        var scope = HealthScope.ForRoute("route-1");

        await service.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        time.Advance(TimeSpan.FromMinutes(6));
        await service.ExpireCooldownAsync(scope);
        var attempt = await service.TryBeginProbeAttemptAsync(scope, true, "Pinned model admission");
        var recovered = (await service.CompleteProbeAttemptAsync(attempt!,
            HealthProbeCompletionKind.ModelSucceeded, "Pinned model success")).Snapshot;

        Assert.Equal(HealthState.Healthy, recovered.State);
        Assert.True(recovered.IsRoutable);

        // The persisted state and the audit must agree after a full round trip through SQLite.
        var persisted = await service.GetSnapshotAsync(scope);
        Assert.Equal(HealthState.Healthy, persisted.State);
        Assert.Equal(0, persisted.AccountedFailureCount);

        var audit = await service.GetAuditAsync(scope);
        Assert.Equal(4, audit.Count);
        Assert.True(audit[0].IsVerifiedRecovery);
    }

    private static HealthEventRecord CreateEvent(
        string id,
        string scopeType = HealthScope.RouteScopeType,
        string scopeId = "route-1",
        HealthState? previousState = HealthState.Healthy,
        DateTimeOffset? occurredAt = null) =>
        new(
            id,
            scopeType,
            scopeId,
            previousState,
            HealthState.Degraded,
            HealthErrorClass.NetworkOrTimeout,
            "A failure was observed.",
            null,
            occurredAt ?? new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero));

    private sealed class SqliteHealthTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
