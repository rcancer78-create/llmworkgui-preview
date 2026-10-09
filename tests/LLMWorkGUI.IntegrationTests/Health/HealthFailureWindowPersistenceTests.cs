using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Health;

public sealed class HealthFailureWindowPersistenceTests
{
    [Fact]
    public async Task RecreatedServicePreservesEachFailureClassWithoutCombiningThresholds()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var clock = new TestClock();
        var policy = new HealthPolicy { FailureThreshold = 5, RollingWindow = TimeSpan.FromMinutes(15) };
        var scope = HealthScope.ForAccount("mixed-window");
        HealthCenterService Recreate() => new(new SqliteHealthStateRepository(database.Factory),
            new SqliteHealthEventRepository(database.Factory), clock, policy,
            transitionStore: new SqliteHealthTransitionStore(database.Factory));

        for (var i = 0; i < 3; i++)
            await Recreate().ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        await Recreate().ReportFailureAsync(scope, HealthErrorClass.Provider4xx5xx);

        var fourthNetwork = await Recreate().ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        Assert.Equal(HealthState.Degraded, fourthNetwork.Snapshot.State);
        var fifthNetwork = await Recreate().ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        Assert.Equal(HealthState.CoolingDown, fifthNetwork.Snapshot.State);
    }

    [Fact]
    public async Task RecreatedServiceExpiresOldFailureWithoutDiscardingNewerFailure()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        var clock = new TestClock();
        var policy = new HealthPolicy { FailureThreshold = 3, RollingWindow = TimeSpan.FromMinutes(15) };
        var scope = HealthScope.ForAccount("staggered-window");
        HealthCenterService Recreate() => new(new SqliteHealthStateRepository(database.Factory),
            new SqliteHealthEventRepository(database.Factory), clock, policy,
            transitionStore: new SqliteHealthTransitionStore(database.Factory));

        await Recreate().ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        clock.Advance(TimeSpan.FromMinutes(14));
        await Recreate().ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        clock.Advance(TimeSpan.FromMinutes(2));
        var atSixteen = await Recreate().ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        Assert.Equal(HealthState.Degraded, atSixteen.Snapshot.State);
        clock.Advance(TimeSpan.FromMinutes(1));
        var atSeventeen = await Recreate().ReportFailureAsync(scope, HealthErrorClass.NetworkOrTimeout);
        Assert.Equal(HealthState.CoolingDown, atSeventeen.Snapshot.State);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
