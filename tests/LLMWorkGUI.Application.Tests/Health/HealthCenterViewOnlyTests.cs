using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

public sealed class HealthCenterViewOnlyTests
{
    private static readonly HealthScope Scope = HealthScope.ForRoute("view-only-health-route");

    [Theory]
    [InlineData("failure", HealthState.Healthy)]
    [InlineData("success", HealthState.Healthy)]
    [InlineData("expire", HealthState.CoolingDown)]
    [InlineData("start-probe", HealthState.ProbeRequired)]
    [InlineData("complete-probe", HealthState.Recovering)]
    [InlineData("observation", HealthState.Recovering)]
    [InlineData("disable", HealthState.Healthy)]
    [InlineData("enable", HealthState.DisabledManual)]
    [InlineData("force", HealthState.CoolingDown)]
    [InlineData("require-probe", HealthState.ForcedEnabled)]
    public async Task ViewOnlyInstance_RefusesEveryHealthMutation(string operation, HealthState initialState)
    {
        var states = new InMemoryHealthStateRepository();
        var events = new InMemoryHealthEventRepository();
        var time = new HealthTestTimeProvider();
        await states.UpsertAsync(Record(initialState, time.GetUtcNow()));
        using var provider = CreateServices(states, events, time);
        var service = provider.GetRequiredService<IHealthCenterService>();

        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(async () =>
        {
            switch (operation)
            {
                case "failure": await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout); break;
                case "success": await service.ReportSuccessAsync(Scope); break;
                case "expire": await service.ExpireCooldownAsync(Scope); break;
                case "start-probe": await service.StartProbeAsync(Scope); break;
                case "complete-probe": await service.CompleteProbeAsync(Scope, true); break;
                case "observation": await service.RecordProbeObservationAsync(Scope, true, "Observed a connection."); break;
                case "disable": await service.DisableManuallyAsync(Scope, "Disable requested."); break;
                case "enable": await service.EnableAsync(Scope, "Enable requested."); break;
                case "force": await service.ForceEnableAsync(Scope, "Force requested."); break;
                case "require-probe": await service.RequireProbeAsync(Scope); break;
                default: throw new InvalidOperationException("Unknown test operation.");
            }
        });

        Assert.Equal(1, states.UpsertCount); // Only the fixture's initial record.
        Assert.Empty(events.Events);
        Assert.Equal(initialState, (await states.GetAsync(Scope.ScopeType, Scope.ScopeId))!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewOnlyRead_DoesNotPersistAnElapsedCooldown(bool list)
    {
        var states = new InMemoryHealthStateRepository();
        var events = new InMemoryHealthEventRepository();
        var time = new HealthTestTimeProvider();
        await states.UpsertAsync(Record(HealthState.CoolingDown, time.GetUtcNow()));
        using var provider = CreateServices(states, events, time);
        var service = provider.GetRequiredService<IHealthCenterService>();

        var snapshot = list
            ? Assert.Single(await service.ListAsync())
            : await service.GetSnapshotAsync(Scope);

        Assert.Equal(HealthState.CoolingDown, snapshot.State);
        Assert.Equal(1, states.UpsertCount);
        Assert.Empty(events.Events);
    }

    private static HealthStateRecord Record(HealthState state, DateTimeOffset now) =>
        new("view-only-state", Scope.ScopeType, Scope.ScopeId, state, HealthErrorClass.NetworkOrTimeout,
            1, now.AddMinutes(-10), state == HealthState.CoolingDown ? now.AddMinutes(-1) : null,
            null, now.AddMinutes(-2));

    private static ServiceProvider CreateServices(InMemoryHealthStateRepository states,
        InMemoryHealthEventRepository events, TimeProvider time)
    {
        // Constructor activation mirrors production AddInfrastructure registration without creating
        // a native mutex or touching a real app-data directory. The service must honor the supplied
        // application-instance guard, not assume that a SQLite connection implies write permission.
        var services = new ServiceCollection();
        services.AddSingleton<IHealthStateRepository>(states);
        services.AddSingleton<IHealthEventRepository>(events);
        services.AddSingleton(time);
        services.AddSingleton<IApplicationInstanceGuard, ViewOnlyGuard>();
        services.AddSingleton<IHealthCenterService, HealthCenterService>();
        return services.BuildServiceProvider();
    }

    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "view-only-fixture";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new SecondaryInstanceReadOnlyException("View-only fixture.");
        public void Dispose() { }
    }
}
