using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

/// <summary>
/// Rehydration of <see cref="HealthStateMachine"/> from a persisted observation. A restored machine
/// must keep the distance to the breaker threshold it already had; starting from zero would silently
/// forgive failures that were actually recorded.
/// </summary>
public sealed class HealthStateMachineRestoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Restore_CoolingDownRequiresADeadline()
    {
        Assert.Throws<ArgumentException>(() => HealthStateMachine.Restore(
            HealthState.CoolingDown, null, HealthErrorClass.NetworkOrTimeout, 3, Now));
    }

    [Fact]
    public void Restore_NonCooldownStateCannotCarryACooldownDeadline()
    {
        Assert.Throws<ArgumentException>(() => HealthStateMachine.Restore(
            HealthState.Healthy, Now.AddMinutes(5), null, 0, Now));
    }

    [Fact]
    public void Restore_KeepsTheObservedStateAndCooldown()
    {
        var cooldownUntil = Now.AddMinutes(5);

        var machine = HealthStateMachine.Restore(
            HealthState.CoolingDown,
            cooldownUntil,
            HealthErrorClass.NetworkOrTimeout,
            accountedFailureCount: 3,
            observedAt: Now);

        Assert.Equal(HealthState.CoolingDown, machine.State);
        Assert.Equal(cooldownUntil, machine.CoolingDownUntil);
        Assert.Equal(3, machine.AccountedFailureCount);
        Assert.Equal(HealthErrorClass.NetworkOrTimeout, machine.AccountedErrorClass);
    }

    [Fact]
    public void Restore_WithoutFailures_ReportsNoAccountedErrorClass()
    {
        var machine = HealthStateMachine.Restore(
            HealthState.Healthy,
            coolingDownUntil: null,
            accountedErrorClass: null,
            accountedFailureCount: 0,
            observedAt: Now);

        Assert.Equal(HealthState.Healthy, machine.State);
        Assert.Equal(0, machine.AccountedFailureCount);
        Assert.Null(machine.AccountedErrorClass);
    }

    [Fact]
    public void Restore_PreservesProgressTowardsTheThreshold()
    {
        var policy = new HealthPolicy { FailureThreshold = 3 };

        var machine = HealthStateMachine.Restore(
            HealthState.Degraded,
            coolingDownUntil: null,
            HealthErrorClass.NetworkOrTimeout,
            accountedFailureCount: 2,
            observedAt: Now,
            policy);

        // Two failures were already recorded, so the very next one must open the breaker.
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Now);

        Assert.Equal(HealthState.CoolingDown, machine.State);
    }

    [Fact]
    public void Restore_DoesNotResurrectFailuresOlderThanTheWindow()
    {
        var policy = new HealthPolicy
        {
            FailureThreshold = 3,
            RollingWindow = TimeSpan.FromMinutes(10)
        };

        var machine = HealthStateMachine.Restore(
            HealthState.Degraded,
            coolingDownUntil: null,
            HealthErrorClass.NetworkOrTimeout,
            accountedFailureCount: 2,
            observedAt: Now,
            policy);

        // The restored failures are now far outside the rolling window, so they must be trimmed and
        // the breaker must not open on a single fresh failure.
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Now.AddHours(1));

        Assert.Equal(HealthState.Degraded, machine.State);
        Assert.Equal(1, machine.AccountedFailureCount);
    }

    [Fact]
    public void Restore_WithANegativeFailureCount_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HealthStateMachine.Restore(
            HealthState.Healthy,
            coolingDownUntil: null,
            accountedErrorClass: null,
            accountedFailureCount: -1,
            observedAt: Now));
    }

    [Fact]
    public void Restore_AllowsTheNormativeTransitionsOfTheRestoredState()
    {
        var machine = HealthStateMachine.Restore(
            HealthState.ProbeRequired,
            coolingDownUntil: null,
            accountedErrorClass: null,
            accountedFailureCount: 0,
            observedAt: Now);

        // A restored ProbeRequired scope must still be able to start its probe.
        machine.StartProbe();

        Assert.Equal(HealthState.Recovering, machine.State);
    }

    [Fact]
    public void AccountedErrorClass_ReportsTheClassClosestToTheThreshold()
    {
        var machine = new HealthStateMachine(new HealthPolicy { FailureThreshold = 5 });

        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Now);
        machine.RecordFailure(HealthErrorClass.Provider4xx5xx, Now);
        machine.RecordFailure(HealthErrorClass.Provider4xx5xx, Now);

        // Provider4xx5xx will trip the breaker first, so that is the class worth reporting.
        Assert.Equal(HealthErrorClass.Provider4xx5xx, machine.AccountedErrorClass);
    }

    [Fact]
    public void AccountedWindowStartedAt_ReportsTheOldestCountedFailure()
    {
        var machine = new HealthStateMachine(new HealthPolicy { FailureThreshold = 5 });

        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Now);
        machine.RecordFailure(HealthErrorClass.Provider4xx5xx, Now.AddMinutes(2));

        // This is the real start of the rolling window, which is what must be persisted: a derived
        // value would place restored failures on the window edge and defeat the breaker.
        Assert.Equal(Now, machine.AccountedWindowStartedAt);
    }

    [Fact]
    public void AccountedWindowStartedAt_WithoutCountedFailures_IsNull()
    {
        var machine = new HealthStateMachine();

        machine.RecordFailure(HealthErrorClass.UserCancellation, Now);

        Assert.Null(machine.AccountedWindowStartedAt);
    }

    [Fact]
    public void AccountedWindowStartedAt_SurvivesARestoreRoundTrip()
    {
        var policy = new HealthPolicy { FailureThreshold = 3, RollingWindow = TimeSpan.FromMinutes(15) };
        var original = new HealthStateMachine(policy);

        original.RecordFailure(HealthErrorClass.NetworkOrTimeout, Now);
        original.RecordFailure(HealthErrorClass.NetworkOrTimeout, Now.AddMinutes(1));

        var restored = HealthStateMachine.Restore(
            original.State,
            original.CoolingDownUntil,
            original.AccountedErrorClass,
            original.AccountedFailureCount,
            original.AccountedWindowStartedAt!.Value,
            policy);

        // A failure one minute later is still inside the window, so it must be the third strike.
        restored.RecordFailure(HealthErrorClass.NetworkOrTimeout, Now.AddMinutes(2));

        Assert.Equal(HealthState.CoolingDown, restored.State);
        Assert.Equal(3, restored.AccountedFailureCount);
    }

    [Fact]
    public void AccountedErrorClass_IgnoresClassesExcludedFromTheBreaker()
    {
        var machine = new HealthStateMachine();

        machine.RecordFailure(HealthErrorClass.UserCancellation, Now);
        machine.RecordFailure(HealthErrorClass.UnknownOrAmbiguousCompletion, Now);

        Assert.Null(machine.AccountedErrorClass);
        Assert.Equal(0, machine.AccountedFailureCount);
    }

    [Fact]
    public void Restore_ForcedEnabledWithAuthenticationFailure_IsBlockedImmediately()
    {
        var machine = HealthStateMachine.Restore(
            HealthState.ForcedEnabled,
            coolingDownUntil: null,
            accountedErrorClass: null,
            accountedFailureCount: 0,
            observedAt: Now);

        machine.RecordFailure(HealthErrorClass.AuthenticationOrRefresh, Now);

        // A restored forced scope must not stay routable when its credentials are rejected: the auth
        // failure blocks it on the very first observation (ТЗ §6.10).
        Assert.Equal(HealthState.CoolingDown, machine.State);
        Assert.Equal(Now.AddMinutes(5), machine.CoolingDownUntil);
    }
}
