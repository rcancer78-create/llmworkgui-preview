using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

/// <summary>
/// Impacted-session classification (ROADMAP Phase 7: impacted-session view, sticky-session stop for an
/// unhealthy route). The service is read-only: it explains what the operator must do and never acts.
/// </summary>
public sealed class ImpactedSessionServiceTests
{
    private const string AccountId = "acc-impacted";

    private static readonly HealthScope AccountScope = HealthScope.ForAccount(AccountId);

    private readonly InMemoryHealthStateRepository _states = new();
    private readonly InMemoryHealthEventRepository _events = new();
    private readonly HealthTestTimeProvider _time = new();
    private readonly InMemorySessionRepository _sessions = new();

    [Fact]
    public async Task AHealthyRoute_ImpactsNoSession()
    {
        var health = CreateHealthCenter();
        var service = CreateService(health);

        _sessions.Add(CreateSession("session-1", SessionState.Active, activeExecutionId: "exec-1"));
        _sessions.Add(CreateSession("session-2", SessionState.Idle));

        var report = await service.GetImpactedSessionsAsync(AccountScope);

        Assert.True(report.IsComplete);
        Assert.True(report.IsRoutable);
        Assert.False(report.HasImpactedSessions);
        Assert.Contains("No session is affected", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARouteThatLeftRouting_RecommendsStoppingTheTurnAndRequiresAReplacementSession()
    {
        var health = CreateHealthCenter();
        var service = CreateService(health);

        _sessions.Add(CreateSession("session-mid-turn", SessionState.Active, activeExecutionId: "exec-42"));

        // One failure is enough to open the breaker with this policy.
        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);

        var report = await service.GetImpactedSessionsAsync(AccountScope);

        Assert.False(report.IsRoutable);

        var session = Assert.Single(report.Sessions);

        // ТЗ §6.5: the projection recommends stopping the turn and confirming a replacement session; it
        // never re-routes silently and never claims the turn was stopped from here.
        Assert.Equal(SessionImpact.RunningTurnRequiresStop, session.Impact);
        Assert.True(session.RequiresReplacementSession);
        Assert.True(session.HasTurnInFlight);
        Assert.Equal("exec-42", session.ActiveExecutionId);
        Assert.Contains("recommendation", session.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never created automatically", session.Explanation, StringComparison.OrdinalIgnoreCase);

        Assert.Single(report.RequiringReplacementSession);
    }

    [Fact]
    public async Task ASessionWithNoTurnInFlight_IsOnlyBlockedFromItsNextTurn()
    {
        var health = CreateHealthCenter();
        var service = CreateService(health);

        _sessions.Add(CreateSession("session-idle", SessionState.Idle));

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);

        var report = await service.GetImpactedSessionsAsync(AccountScope);
        var session = Assert.Single(report.Sessions);

        // Nothing was lost, so this must not be reported as a stopped turn.
        Assert.Equal(SessionImpact.NextTurnBlocked, session.Impact);
        Assert.False(session.RequiresReplacementSession);
        Assert.False(session.HasTurnInFlight);
        Assert.Contains("No work has been lost", session.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClosedSession_IsNeverReportedAsImpacted()
    {
        var health = CreateHealthCenter();
        var service = CreateService(health);

        _sessions.Add(CreateSession("session-closed", SessionState.Closed));

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);

        var report = await service.GetImpactedSessionsAsync(AccountScope);

        // A finished session cannot be harmed further, whatever the route now does.
        Assert.False(report.HasImpactedSessions);
    }

    [Theory]
    [InlineData(SessionState.Ambiguous)]
    [InlineData(SessionState.Orphaned)]
    public async Task AnUnresolvedSession_IsLeftToReconciliationRatherThanDecidedByHealth(SessionState state)
    {
        var health = CreateHealthCenter();
        var service = CreateService(health);

        _sessions.Add(CreateSession("session-unresolved", state, activeExecutionId: "exec-7"));

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);

        var report = await service.GetImpactedSessionsAsync(AccountScope);
        var session = Assert.Single(report.Sessions);

        // Health must not resolve an ambiguous outcome in either direction: that would either lose or
        // duplicate work that may already have been applied.
        Assert.Equal(SessionImpact.AwaitingReconciliation, session.Impact);
        Assert.False(session.RequiresReplacementSession);
        Assert.Contains("reconciliation", session.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AForcedRoute_ReportsThatFurtherWorkProceedsOnUnverifiedEvidence()
    {
        var health = CreateHealthCenter();
        var service = CreateService(health);

        _sessions.Add(CreateSession("session-forced", SessionState.Active, activeExecutionId: "exec-9"));

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await health.ExpireCooldownAsync(AccountScope);
        await health.ForceEnableAsync(AccountScope, "The operator accepted the risk.");

        var report = await service.GetImpactedSessionsAsync(AccountScope);
        var session = Assert.Single(report.Sessions);

        // The route is routable again, so the turn is not stopped — but the operator must know the
        // recovery was never verified.
        Assert.True(report.IsRoutable);
        Assert.Equal(SessionImpact.ProceedingOnUnverifiedRoute, session.Impact);
        Assert.False(session.RequiresReplacementSession);
        Assert.Contains("unverified", session.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnlySessionsOfTheAffectedAccount_AreReported()
    {
        var health = CreateHealthCenter();
        var service = CreateService(health);

        _sessions.Add(CreateSession("session-mine", SessionState.Active, activeExecutionId: "exec-1"));
        _sessions.Add(CreateSession("session-other", SessionState.Active, activeExecutionId: "exec-2", accountId: "acc-other"));

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);

        var report = await service.GetImpactedSessionsAsync(AccountScope);

        var session = Assert.Single(report.Sessions);
        Assert.Equal("session-mine", session.SessionId);
    }

    [Fact]
    public async Task SessionsAcrossProjects_AreAllReportedBecauseAnAccountSpansProjects()
    {
        var health = CreateHealthCenter();
        var service = CreateService(health);

        _sessions.Add(CreateSession("session-a", SessionState.Active, activeExecutionId: "exec-1", projectId: "project-a"));
        _sessions.Add(CreateSession("session-b", SessionState.Idle, projectId: "project-b"));

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);

        var report = await service.GetImpactedSessionsAsync(AccountScope);

        Assert.Equal(2, report.Sessions.Count);
        Assert.Contains(report.Sessions, session => session.ProjectId == "project-a");
        Assert.Contains(report.Sessions, session => session.ProjectId == "project-b");

        // The summary distinguishes a turn that must be stopped from a merely blocked next turn.
        Assert.Contains("1 mid-turn with a running turn that must be stopped", report.Summary, StringComparison.Ordinal);
        Assert.Contains("1 whose next turn is refused until the route recovers", report.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HealthScope.RouteScopeType)]
    [InlineData(HealthScope.BackendScopeType)]
    public async Task AScopeThatCannotBeResolvedToSessions_ReportsIncompletenessRatherThanAnEmptyList(
        string scopeType)
    {
        var health = CreateHealthCenter();
        var service = CreateService(health);

        _sessions.Add(CreateSession("session-1", SessionState.Active, activeExecutionId: "exec-1"));

        var scope = new HealthScope(scopeType, "some-id");
        await health.ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);

        var report = await service.GetImpactedSessionsAsync(scope);

        // An empty list would read as "nothing is affected", which was never observed.
        Assert.False(report.IsComplete);
        Assert.False(report.HasImpactedSessions);
        Assert.Contains("cannot be resolved to sessions", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASessionRepository_TheReportStatesItIsIncomplete()
    {
        var health = CreateHealthCenter();
        var service = CreateService(health, withSessions: false);

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);

        var report = await service.GetImpactedSessionsAsync(AccountScope);

        Assert.False(report.IsComplete);
        Assert.False(report.HasImpactedSessions);
        Assert.Contains("could not be determined", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheService_NeverMutatesHealthOrSessions()
    {
        var health = CreateHealthCenter();
        var service = CreateService(health);

        _sessions.Add(CreateSession("session-1", SessionState.Active, activeExecutionId: "exec-1"));

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.NetworkOrTimeout);

        var before = await health.GetSnapshotAsync(AccountScope);
        var auditBefore = _events.Events.Count;

        await service.GetImpactedSessionsAsync(AccountScope);

        var after = await health.GetSnapshotAsync(AccountScope);

        // Read-only by contract: stopping a turn and creating a replacement session are the caller's
        // decisions, taken with explicit user confirmation.
        Assert.Equal(before.State, after.State);
        Assert.Equal(auditBefore, _events.Events.Count);
        Assert.Equal(SessionState.Active, (await _sessions.GetByIdAsync("session-1"))!.State);
    }

    private Session CreateSession(
        string id,
        SessionState state,
        string? activeExecutionId = null,
        string accountId = AccountId,
        string projectId = "project-1") =>
        new(
            id,
            new SessionBinding(BackendType.OpenCode, "prov-1", accountId, "gpt-4o", null, null, null),
            projectId,
            @"C:\work\checkout",
            $"native-{id}",
            state,
            ReconciliationOutcome.None,
            CloseReason.None,
            null,
            null,
            null,
            "Executor",
            activeExecutionId,
            _time.GetUtcNow(),
            _time.GetUtcNow());

    private HealthCenterService CreateHealthCenter() =>
        new(_states, _events, _time, new HealthPolicy { FailureThreshold = 1 });

    private ImpactedSessionService CreateService(
        IHealthCenterService health,
        bool withSessions = true) =>
        new(health, withSessions ? _sessions : null);
}

/// <summary>
/// Deterministic session store. <see cref="ListByAccountAsync"/> mirrors the SQLite ordering
/// (newest activity first) so the tests exercise the same contract the UI sees.
/// </summary>
internal sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly List<Session> _sessions = new();

    public void Add(Session session) => _sessions.Add(session);

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
