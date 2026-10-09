using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed partial class HealthAuthenticationFanoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompatibilityAccountCascadeDoesNotChargeOriginalPersistedRouteTwice(bool transactionalStore)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var modelScope = HealthScope.ForModelRoute("account-1", "model-1");
        // Even identical opaque IDs cannot merge route and model-route scope identities.
        await database.SeedRouteChainAsync(routeId: modelScope.ScopeId);
        var states = new SqliteHealthStateRepository(database.Factory);
        var events = new SqliteHealthEventRepository(database.Factory);
        IHealthTransitionStore? store = transactionalStore
            ? new CompatibilityTransitionStore(new SqliteHealthTransitionStore(database.Factory))
            : null;
        var health = new HealthCenterService(states, events, transitionStore: store,
            routes: new SqliteRouteRepository(database.Factory));
        var route = HealthScope.ForRoute(modelScope.ScopeId);
        var account = HealthScope.ForAccount("account-1");
        var sibling = HealthScope.ForModelRoute("account-1", "sibling");
        foreach (var scope in new[] { route, modelScope, sibling })
            await health.ReportSuccessAsync(scope);
        var notifications = new List<HealthScope>();
        health.TransitionRecorded += (_, transition) => notifications.Add(transition.Scope);

        var result = await health.ReportFailureAsync(route, HealthErrorClass.AuthenticationOrRefresh);

        Assert.Equal(route, result.Snapshot.Scope);
        foreach (var scope in new[] { account, modelScope, sibling, route })
        {
            var snapshot = await health.GetSnapshotAsync(scope);
            Assert.False(snapshot.IsRoutable);
            Assert.Equal(1, snapshot.AccountedFailureCount);
            var audit = Assert.Single(await health.GetAuditAsync(scope));
            Assert.Equal(HealthErrorClass.AuthenticationOrRefresh, audit.ErrorClass);
            Assert.Single(notifications.Where(observed => observed == scope));
        }
    }

    // Deliberately exposes only the compatibility contract while retaining real atomic SQLite writes.
    private sealed class CompatibilityTransitionStore(SqliteHealthTransitionStore inner) : IHealthTransitionStore
    {
        public Task SaveAsync(HealthStateRecord? state, HealthEventRecord? healthEvent,
            CancellationToken cancellationToken = default) => inner.SaveAsync(state, healthEvent, cancellationToken);
    }
}
