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
    public async Task PendingCompletionBetweenReadsCannotCombineOldHealthyStateWithNewEmptyQueue(bool list)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var scope = HealthScope.ForAccount("snapshot-account");
        var states = new SqliteHealthStateRepository(database.Factory);
        var events = new SqliteHealthEventRepository(database.Factory);
        var inner = new SqliteHealthTransitionStore(database.Factory);
        await new HealthCenterService(states, events, transitionStore: inner).ReportSuccessAsync(scope);
        var old = (await states.GetAsync(scope.ScopeType, scope.ScopeId))!;
        var blocked = old with { State = HealthState.CoolingDown, ErrorClass = HealthErrorClass.AuthenticationOrRefresh,
            CooldownUntil = DateTimeOffset.UtcNow.AddMinutes(5) };
        var audit = new HealthEventRecord("snapshot-event", scope.ScopeType, scope.ScopeId, old.State,
            blocked.State, blocked.ErrorClass, "Synthetic pending completion", null, DateTimeOffset.UtcNow);
        await inner.EnqueueAsync([new HealthAuthenticationProjection("snapshot-projection", scope, scope.ScopeId,
            "Synthetic auth failure", DateTimeOffset.UtcNow)], default);
        var racing = new CompleteBeforePendingCheck(inner, blocked, audit);
        var health = new HealthCenterService(states, events, transitionStore: racing);
        var snapshot = list ? Assert.Single(await health.ListAsync()) : await health.GetSnapshotAsync(scope);
        Assert.False(snapshot.IsRoutable);
        // Either pending or the committed blocked state is valid. Healthy without pending never was:
        // the queue already existed before this read began.
        Assert.True(snapshot.AuthenticationFanoutPending || snapshot.State == HealthState.CoolingDown);
    }

    private sealed class CompleteBeforePendingCheck(SqliteHealthTransitionStore inner, HealthStateRecord blocked,
        HealthEventRecord audit) : IHealthTransitionStore, IHealthAuthenticationFanoutStore
    {
        private bool _completed;
        public Task SaveAsync(HealthStateRecord? state, HealthEventRecord? healthEvent, CancellationToken token = default)
            => inner.SaveAsync(state, healthEvent, token);
        public Task EnqueueAsync(IReadOnlyList<HealthAuthenticationProjection> projections, CancellationToken token)
            => inner.EnqueueAsync(projections, token);
        public Task<IReadOnlyList<HealthAuthenticationProjection>> ListPendingAsync(CancellationToken token) => inner.ListPendingAsync(token);
        public async Task<bool> HasPendingAsync(HealthScope scope, CancellationToken token)
        {
            if (!_completed)
            {
                await inner.SaveAndCompleteAsync(blocked, audit, "snapshot-projection", token);
                _completed = true;
            }
            return await inner.HasPendingAsync(scope, token);
        }
        public Task<(HealthStateRecord? State, bool Pending)> ReadSnapshotAsync(HealthScope scope, CancellationToken token)
            => inner.ReadSnapshotAsync(scope, token);
        public Task SaveAndCompleteAsync(HealthStateRecord state, HealthEventRecord healthEvent, string id, CancellationToken token)
            => inner.SaveAndCompleteAsync(state, healthEvent, id, token);
    }
}
