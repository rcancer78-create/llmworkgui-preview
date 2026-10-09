using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Health;

/// <summary>
/// Circuit-breaker behaviour of the Health Center (ROADMAP Phase 7 exit criteria): a repeated failure
/// must move the route to the expected state, a quarantined route must leave routing, and user
/// cancellation or an ambiguous completion must never charge the breaker.
/// </summary>
public sealed class HealthCenterBreakerTests
{
    private static readonly HealthScope Scope = HealthScope.ForRoute("route-1");

    private readonly InMemoryHealthStateRepository _states = new();
    private readonly InMemoryHealthEventRepository _events = new();
    private readonly HealthTestTimeProvider _time = new();

    [Fact]
    public async Task GetSnapshotAsync_WithNothingRecorded_ReportsHealthyAndRoutable()
    {
        var service = CreateService();

        var snapshot = await service.GetSnapshotAsync(Scope);

        Assert.Equal(HealthState.Healthy, snapshot.State);
        Assert.True(snapshot.IsRoutable);
        Assert.False(snapshot.RequiresProbe);
        Assert.Equal(0, snapshot.AccountedFailureCount);

        // Nothing was observed, so nothing may be audited.
        Assert.Empty(_events.Events);
    }

    [Fact]
    public async Task ReportFailureAsync_RepeatedFailure_TripsTheBreakerIntoCooldown()
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 3 });

        var first = await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        Assert.Equal(HealthState.Degraded, first.Snapshot.State);
        Assert.True(first.Snapshot.IsRoutable);

        var second = await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        Assert.Equal(HealthState.Degraded, second.Snapshot.State);

        var third = await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);

        // The threshold is reached, so the breaker opens and the route leaves routing.
        Assert.Equal(HealthState.CoolingDown, third.Snapshot.State);
        Assert.False(third.Snapshot.IsRoutable);
        Assert.NotNull(third.Snapshot.CooldownUntil);
        Assert.True(third.CountedByBreaker);
    }

    [Fact]
    public async Task ReportFailureAsync_AcrossSeparateCalls_StillTripsTheBreaker()
    {
        // Regression: the rolling-window start used to be persisted as now - RollingWindow, which put
        // every restored failure exactly on the window edge. The next call trimmed it, so the breaker
        // could never open once the clock moved between calls.
        var service = CreateService(new HealthPolicy
        {
            FailureThreshold = 2,
            RollingWindow = TimeSpan.FromMinutes(15)
        });

        await service.ReportFailureAsync(Scope, HealthErrorClass.Provider4xx5xx);

        _time.Advance(TimeSpan.FromMinutes(1));

        var second = await service.ReportFailureAsync(Scope, HealthErrorClass.Provider4xx5xx);

        Assert.Equal(HealthState.CoolingDown, second.Snapshot.State);
        Assert.False(second.Snapshot.IsRoutable);
        Assert.Equal(2, second.Snapshot.AccountedFailureCount);
    }

    [Fact]
    public async Task ReportFailureAsync_OutsideTheRollingWindow_DoesNotAccumulate()
    {
        var service = CreateService(new HealthPolicy
        {
            FailureThreshold = 2,
            RollingWindow = TimeSpan.FromMinutes(15)
        });

        await service.ReportFailureAsync(Scope, HealthErrorClass.Provider4xx5xx);

        // The first failure ages out, so the second one must not be treated as the second strike.
        _time.Advance(TimeSpan.FromMinutes(20));

        var second = await service.ReportFailureAsync(Scope, HealthErrorClass.Provider4xx5xx);

        Assert.Equal(HealthState.Degraded, second.Snapshot.State);
        Assert.True(second.Snapshot.IsRoutable);
        Assert.Equal(1, second.Snapshot.AccountedFailureCount);
    }

    [Theory]
    [InlineData(HealthErrorClass.UserCancellation)]
    [InlineData(HealthErrorClass.UnknownOrAmbiguousCompletion)]
    public async Task ReportFailureAsync_ExcludedClass_NeverMovesTheBreaker(HealthErrorClass errorClass)
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 2 });

        // Far more failures than the threshold: an excluded class must still not open the breaker.
        for (var i = 0; i < 10; i++)
        {
            var outcome = await service.ReportFailureAsync(Scope, errorClass);

            Assert.False(outcome.CountedByBreaker);
            Assert.False(outcome.StateChanged);
            Assert.Equal(HealthState.Healthy, outcome.Snapshot.State);
            Assert.Equal(0, outcome.Snapshot.AccountedFailureCount);
        }

        var snapshot = await service.GetSnapshotAsync(Scope);
        Assert.Equal(HealthState.Healthy, snapshot.State);
        Assert.True(snapshot.IsRoutable);
    }

    [Fact]
    public async Task ReportFailureAsync_ExcludedClass_IsStillAuditedForExplainability()
    {
        var service = CreateService();

        await service.ReportFailureAsync(Scope, HealthErrorClass.UserCancellation);

        // The operator must be able to see that the cancellation was observed and deliberately
        // not charged, instead of the event vanishing.
        var audit = Assert.Single(await service.GetAuditAsync(Scope));
        Assert.Equal(HealthErrorClass.UserCancellation, audit.ErrorClass);
        Assert.Equal(HealthState.Healthy, audit.NewState);
        Assert.Contains("not counted", audit.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportFailureAsync_AmbiguousCompletion_NeverAllowsAutomaticRetry()
    {
        var service = CreateService();

        var outcome = await service.ReportFailureAsync(
            Scope,
            HealthErrorClass.UnknownOrAmbiguousCompletion);

        // The turn may already have applied changes, so an automatic retry could duplicate work.
        Assert.True(outcome.IsAmbiguous);
        Assert.False(outcome.AutomaticRetryAllowed);
    }

    [Theory]
    [InlineData(HealthErrorClass.UserCancellation)]
    [InlineData(HealthErrorClass.UnexpectedInteractiveInputWait)]
    [InlineData(HealthErrorClass.UserApprovalDeny)]
    public async Task ReportFailureAsync_ExcludedOutcome_DoesNotRetry(HealthErrorClass errorClass)
    {
        var service = CreateService();

        var outcome = await service.ReportFailureAsync(Scope, errorClass);

        Assert.False(outcome.CountedByBreaker);
        Assert.False(outcome.AutomaticRetryAllowed);
    }

    [Fact]
    public async Task ReportFailureAsync_UnambiguousFailureOnARoutableScope_AllowsAutomaticRetry()
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 3 });

        var outcome = await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);

        Assert.False(outcome.IsAmbiguous);
        Assert.True(outcome.AutomaticRetryAllowed);
    }

    [Fact]
    public async Task ReportFailureAsync_AfterTheBreakerOpened_StopsAllowingRetry()
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 1 });

        var outcome = await service.ReportFailureAsync(Scope, HealthErrorClass.Provider4xx5xx);

        // A route that just left routing must not be retried immediately.
        Assert.Equal(HealthState.CoolingDown, outcome.Snapshot.State);
        Assert.False(outcome.AutomaticRetryAllowed);
    }

    [Fact]
    public async Task ReportFailureAsync_FailuresOutsideTheRollingWindow_DoNotAccumulate()
    {
        var service = CreateService(new HealthPolicy
        {
            FailureThreshold = 3,
            RollingWindow = TimeSpan.FromMinutes(10)
        });

        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(30));
        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(30));
        var outcome = await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);

        // Each failure aged out before the next one, so the breaker must not have opened.
        Assert.Equal(HealthState.Degraded, outcome.Snapshot.State);
        Assert.True(outcome.Snapshot.IsRoutable);
    }

    [Fact]
    public async Task ExpireCooldownAsync_MovesToProbeRequiredAndNotToHealthy()
    {
        var service = CreateService(new HealthPolicy
        {
            FailureThreshold = 1,
            CooldownDuration = TimeSpan.FromMinutes(5)
        });

        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(6));

        var snapshot = await service.ExpireCooldownAsync(Scope);

        // ROADMAP Phase 7: cooldown expiry means "probe required", never "healthy again".
        Assert.Equal(HealthState.ProbeRequired, snapshot.State);
        Assert.True(snapshot.RequiresProbe);
        Assert.False(snapshot.IsRoutable);
        Assert.Null(snapshot.CooldownUntil);
    }

    [Fact]
    public async Task ExpireCooldownAsync_BeforeTheCooldownElapsed_ChangesNothing()
    {
        var service = CreateService(new HealthPolicy
        {
            FailureThreshold = 1,
            CooldownDuration = TimeSpan.FromMinutes(5)
        });

        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        var auditCountBefore = _events.Events.Count;

        _time.Advance(TimeSpan.FromMinutes(1));
        var snapshot = await service.ExpireCooldownAsync(Scope);

        Assert.Equal(HealthState.CoolingDown, snapshot.State);

        // A no-op must not pollute the audit with a phantom transition.
        Assert.Equal(auditCountBefore, _events.Events.Count);
    }

    [Fact]
    public async Task GetSnapshotAsync_AfterTheCooldownElapsed_ExpiresItToProbeRequired()
    {
        var service = CreateService(new HealthPolicy
        {
            FailureThreshold = 1,
            CooldownDuration = TimeSpan.FromMinutes(5)
        });

        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(6));

        // Nothing else drives the expiry: reading the observation must advance it itself, otherwise a
        // cooling-down scope would stay stuck forever and never reach the probe it owes.
        var snapshot = await service.GetSnapshotAsync(Scope);

        Assert.Equal(HealthState.ProbeRequired, snapshot.State);
        Assert.False(snapshot.IsRoutable);
        Assert.Null(snapshot.CooldownUntil);

        // The automatic expiry is an audited transition, not a silent state rewrite.
        var audit = await service.GetAuditAsync(Scope);
        Assert.Contains(
            audit,
            entry => entry.PreviousState == HealthState.CoolingDown &&
                     entry.NewState == HealthState.ProbeRequired);
    }

    [Fact]
    public async Task ListAsync_AfterTheCooldownElapsed_ExpiresItToProbeRequired()
    {
        var service = CreateService(new HealthPolicy
        {
            FailureThreshold = 1,
            CooldownDuration = TimeSpan.FromMinutes(5)
        });

        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(6));

        var snapshot = Assert.Single(await service.ListAsync());

        Assert.Equal(Scope, snapshot.Scope);
        Assert.Equal(HealthState.ProbeRequired, snapshot.State);
        Assert.True(snapshot.RequiresProbe);
        Assert.False(snapshot.IsRoutable);
    }

    [Fact]
    public async Task GetSnapshotAsync_BeforeTheCooldownElapsed_KeepsCoolingDown()
    {
        var service = CreateService(new HealthPolicy
        {
            FailureThreshold = 1,
            CooldownDuration = TimeSpan.FromMinutes(5)
        });

        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(1));

        var snapshot = await service.GetSnapshotAsync(Scope);

        // A cooldown that has not elapsed must not be shortened by a read.
        Assert.Equal(HealthState.CoolingDown, snapshot.State);
        Assert.NotNull(snapshot.CooldownUntil);
    }

    [Fact]
    public async Task ReportSuccessAsync_OnAQuarantinedScope_DoesNotSilentlyRestoreRouting()
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 1 });

        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await service.ExpireCooldownAsync(Scope);
        await service.CompleteProbeAsync(Scope, succeeded: false);

        var snapshot = await service.ReportSuccessAsync(Scope);

        // Only a passing probe may restore routing; a stray success must not.
        Assert.Equal(HealthState.QuarantinedAuto, snapshot.State);
        Assert.False(snapshot.IsRoutable);
    }

    [Fact]
    public async Task ReportSuccessAsync_OnRecoveringScope_DoesNotPromoteToHealthy()
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 1 });

        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        _time.Advance(TimeSpan.FromMinutes(10));
        await service.ExpireCooldownAsync(Scope);
        await service.StartProbeAsync(Scope);

        var snapshot = await service.ReportSuccessAsync(Scope);

        // ТЗ §6.10: Recovering may return to Healthy only through a passing pinned probe, never merely
        // because a turn succeeded.
        Assert.Equal(HealthState.Recovering, snapshot.State);
        Assert.False(snapshot.IsRoutable);
    }

    [Fact]
    public async Task ReportSuccessAsync_OnForcedEnabledScope_DoesNotPromoteToHealthy()
    {
        var service = CreateService(new HealthPolicy { FailureThreshold = 1 });

        await service.ReportFailureAsync(Scope, HealthErrorClass.NetworkOrTimeout);
        await service.ForceEnableAsync(Scope, "The operator accepted the risk.");

        var snapshot = await service.ReportSuccessAsync(Scope);

        // ТЗ §6.10: ForcedEnabled stays visibly forced until a probe passes; an ordinary success must
        // not launder it into a verified Healthy state.
        Assert.Equal(HealthState.ForcedEnabled, snapshot.State);
        Assert.True(snapshot.IsForcedWithoutVerification);
    }

    private HealthCenterService CreateService(HealthPolicy? policy = null) =>
        new(_states, _events, _time, policy);
}
