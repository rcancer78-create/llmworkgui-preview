using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

public sealed partial class MirasimEgressTests
{
    [Fact]
    public async Task PersistedMirasimBackendExclusionBlocksTransportAndRecoversAfterExplicitEnable()
    {
        using var f = await Fixture.Create();
        await f.CreateSession();
        var health = new HealthCenterService(new SqliteHealthStateRepository(f.Factory),
            new SqliteHealthEventRepository(f.Factory), instanceGuard: f.Guard,
            transitionStore: new SqliteHealthTransitionStore(f.Factory));
        await health.DisableManuallyAsync(HealthScope.ForBackend("mirasim"), "owned exclusion control");
        var result = await f.Service.ExecuteTurnAsync(f.Request);
        Assert.Equal(MirasimTurnStatus.RefusedByPolicy, result.Status);
        Assert.Single(f.Handler.Requests); // session creation only; prompt was not sent
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Executions"));
        await health.EnableAsync(HealthScope.ForBackend("mirasim"), "owned recovery control");
        await health.ForceEnableAsync(HealthScope.ForBackend("mirasim"), "explicit unverified recovery control");
        Assert.Equal(MirasimTurnStatus.Completed, (await f.Service.ExecuteTurnAsync(f.Request)).Status);
        Assert.Equal(2, f.Handler.Requests.Count);
    }
}
