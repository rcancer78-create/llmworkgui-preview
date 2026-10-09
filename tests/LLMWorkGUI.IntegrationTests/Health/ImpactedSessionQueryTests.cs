using System;
using System.Linq;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

/// <summary>
/// The impacted-session view over real SQLite. This proves the new account-scoped session query and the
/// classification together, because a wrong WHERE clause would silently report "nothing is affected".
/// </summary>
public sealed class ImpactedSessionQueryTests
{
    private const string AccountId = "acc-impacted-live";
    private const string OtherAccountId = "acc-other-live";
    private const string ProviderProfileId = "prov-impacted-live";
    private const string ModelId = "model-impacted-live";

    private static readonly HealthScope AccountScope = HealthScope.ForAccount(AccountId);
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListByAccountAsync_ReturnsOnlyThatAccountsSessions_NewestActivityFirst()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedProjectAndProviderAsync(database);

        var sessions = new SqliteSessionRepository(database.Factory);

        await sessions.UpsertAsync(CreateSession("session-old", lastEventAt: Now.AddMinutes(-30)));
        await sessions.UpsertAsync(CreateSession("session-new", lastEventAt: Now));
        await sessions.UpsertAsync(CreateSession("session-other", lastEventAt: Now, accountId: OtherAccountId));

        var result = await sessions.ListByAccountAsync(AccountId);

        Assert.Equal(new[] { "session-new", "session-old" }, result.Select(session => session.Id).ToArray());
    }

    [Fact]
    public async Task ListByAccountAsync_SpansProjects()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedProjectAndProviderAsync(database);
        await SeedProjectAsync(database, "project-second");

        var sessions = new SqliteSessionRepository(database.Factory);

        await sessions.UpsertAsync(CreateSession("session-a", lastEventAt: Now));
        await sessions.UpsertAsync(CreateSession("session-b", lastEventAt: Now.AddMinutes(-1), projectId: "project-second"));

        var result = await sessions.ListByAccountAsync(AccountId);

        // An account is not scoped to a project, so both must be returned.
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task AnUnhealthyAccount_ReportsItsStoppedTurnAndItsBlockedSession()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedProjectAndProviderAsync(database);

        var sessions = new SqliteSessionRepository(database.Factory);
        var health = new HealthCenterService(
            new SqliteHealthStateRepository(database.Factory),
            new SqliteHealthEventRepository(database.Factory),
            TimeProvider.System,
            new HealthPolicy { FailureThreshold = 1 });

        await sessions.UpsertAsync(CreateSession("session-running", activeExecutionId: "exec-live", lastEventAt: Now));
        await sessions.UpsertAsync(CreateSession("session-waiting", state: SessionState.Idle, lastEventAt: Now.AddMinutes(-5)));

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.Provider4xx5xx);

        var service = new ImpactedSessionService(health, sessions);
        var report = await service.GetImpactedSessionsAsync(AccountScope);

        Assert.True(report.IsComplete);
        Assert.False(report.IsRoutable);
        Assert.Equal(2, report.Sessions.Count);

        var stopped = Assert.Single(report.RequiringReplacementSession);
        Assert.Equal("session-running", stopped.SessionId);
        Assert.Equal(SessionImpact.RunningTurnRequiresStop, stopped.Impact);

        var blocked = report.Sessions.Single(session => session.SessionId == "session-waiting");
        Assert.Equal(SessionImpact.NextTurnBlocked, blocked.Impact);
    }

    [Fact]
    public async Task AnAccountWithNoSessions_ReportsACompleteEmptyResult()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await SeedProjectAndProviderAsync(database);

        var health = new HealthCenterService(
            new SqliteHealthStateRepository(database.Factory),
            new SqliteHealthEventRepository(database.Factory),
            TimeProvider.System,
            new HealthPolicy { FailureThreshold = 1 });

        await health.ReportFailureAsync(AccountScope, HealthErrorClass.Provider4xx5xx);

        var service = new ImpactedSessionService(health, new SqliteSessionRepository(database.Factory));
        var report = await service.GetImpactedSessionsAsync(AccountScope);

        // Genuinely nothing is affected, and the report says so as a *complete* result.
        Assert.True(report.IsComplete);
        Assert.False(report.HasImpactedSessions);
    }

    private static Session CreateSession(
        string id,
        SessionState state = SessionState.Active,
        string? activeExecutionId = null,
        string accountId = AccountId,
        string projectId = "project-1",
        DateTimeOffset? lastEventAt = null) =>
        new(
            id,
            new SessionBinding(BackendType.OpenCode, ProviderProfileId, accountId, ModelId, null, null, null),
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
            Now.AddHours(-1),
            lastEventAt ?? Now);

    /// <summary>
    /// Seeds the full referential chain a session needs (provider, account, model, route, project).
    /// Sessions carry foreign keys to all of them, so a partial seed fails the constraint rather than
    /// silently inserting.
    /// </summary>
    private static async Task SeedProjectAndProviderAsync(TestDatabase database)
    {
        await database.SeedRouteChainAsync(
            providerProfileId: ProviderProfileId,
            accountId: AccountId,
            modelId: ModelId);

        // A second account on the same provider, used to prove the query is account-scoped.
        var accounts = new SqliteAccountRepository(database.Factory);

        await accounts.SaveAsync(new Account(
            OtherAccountId,
            ProviderProfileId,
            "Other Account",
            null,
            AuthState.Valid,
            10,
            true,
            HealthState.Healthy,
            null,
            null,
            2,
            null));
    }

    private static async Task SeedProjectAsync(TestDatabase database, string projectId)
    {
        var projects = new SqliteProjectRepository(database.Factory);

        await projects.UpsertAsync(new Project(
            projectId,
            $"Project {projectId}",
            database.GetWorkspacePath(projectId),
            gitBranch: null,
            isDirty: false,
            hasRequiredInstructions: false,
            defaultWorkflowId: null,
            defaultRoutePolicyId: null,
            dataClassification: DataClassification.PublicSource));
    }
}
