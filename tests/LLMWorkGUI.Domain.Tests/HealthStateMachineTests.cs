using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class HealthStateMachineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RollingWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(5);

    [Fact]
    public void NewMachine_IsHealthyWithNormativeDefaults()
    {
        var machine = new HealthStateMachine();

        Assert.Equal(HealthState.Healthy, machine.State);
        Assert.Equal(3, machine.Policy.FailureThreshold);
        Assert.Equal(RollingWindow, machine.Policy.RollingWindow);
        Assert.Equal(CooldownDuration, machine.Policy.CooldownDuration);
        Assert.Null(machine.CoolingDownUntil);
        Assert.Equal(0, machine.AccountedFailureCount);
    }

    [Fact]
    public void SingleAccountedFailure_MovesHealthyToDegraded()
    {
        var machine = new HealthStateMachine();

        var accounted = machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);

        Assert.True(accounted);
        Assert.Equal(HealthState.Degraded, machine.State);
        Assert.Equal(1, machine.AccountedFailureCount);
    }

    [Fact]
    public void TwoIdenticalFailures_StayDegraded()
    {
        var machine = new HealthStateMachine();

        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);

        Assert.Equal(HealthState.Degraded, machine.State);
    }

    [Fact]
    public void ThirdIdenticalFailureWithinWindow_EntersCoolingDown()
    {
        var machine = new HealthStateMachine();

        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start + TimeSpan.FromMinutes(1));
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start + TimeSpan.FromMinutes(2));

        Assert.Equal(HealthState.CoolingDown, machine.State);
        Assert.Equal(Start + TimeSpan.FromMinutes(2) + CooldownDuration, machine.CoolingDownUntil);
    }

    [Fact]
    public void FailuresOutsideRollingWindow_DoNotReachThreshold()
    {
        var machine = new HealthStateMachine();

        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start + RollingWindow + TimeSpan.FromMinutes(1));
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start + (2 * RollingWindow) + TimeSpan.FromMinutes(2));

        Assert.Equal(HealthState.Degraded, machine.State);
        Assert.Equal(1, machine.AccountedFailureCount);
    }

    [Fact]
    public void DifferentErrorClasses_DoNotCombineIntoThreshold()
    {
        var machine = new HealthStateMachine();

        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);
        machine.RecordFailure(HealthErrorClass.Provider4xx5xx, Start);
        machine.RecordFailure(HealthErrorClass.Provider4xx5xx, Start);

        Assert.Equal(HealthState.Degraded, machine.State);
    }

    [Theory]
    [InlineData(HealthErrorClass.UserCancellation)]
    [InlineData(HealthErrorClass.UnknownOrAmbiguousCompletion)]
    [InlineData(HealthErrorClass.UnexpectedInteractiveInputWait)]
    [InlineData(HealthErrorClass.UserApprovalDeny)]
    public void NonAccountedClasses_AreIgnored(HealthErrorClass errorClass)
    {
        var machine = new HealthStateMachine();

        var accounted = machine.RecordFailure(errorClass, Start);

        Assert.False(accounted);
        Assert.Equal(HealthState.Healthy, machine.State);
        Assert.Equal(0, machine.AccountedFailureCount);
    }

    [Fact]
    public void ExpireCooldown_BeforeDeadline_KeepsCoolingDown()
    {
        var machine = CreateInState(HealthState.CoolingDown);

        var changed = machine.ExpireCooldown(Start + CooldownDuration - TimeSpan.FromSeconds(1));

        Assert.False(changed);
        Assert.Equal(HealthState.CoolingDown, machine.State);
    }

    [Fact]
    public void ExpireCooldown_AtDeadline_MovesToProbeRequired()
    {
        var machine = CreateInState(HealthState.CoolingDown);

        var changed = machine.ExpireCooldown(Start + CooldownDuration);

        Assert.True(changed);
        Assert.Equal(HealthState.ProbeRequired, machine.State);
        Assert.Null(machine.CoolingDownUntil);
    }

    [Fact]
    public void ClearCooldown_MovesCoolingDownToProbeRequired()
    {
        var machine = CreateInState(HealthState.CoolingDown);

        machine.ClearCooldown();

        Assert.Equal(HealthState.ProbeRequired, machine.State);
        Assert.Null(machine.CoolingDownUntil);
    }

    [Theory]
    [InlineData(HealthState.Healthy)]
    [InlineData(HealthState.Degraded)]
    [InlineData(HealthState.ProbeRequired)]
    public void ClearCooldown_IsRejectedOutsideCoolingDown(HealthState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.ClearCooldown);
    }

    [Fact]
    public void DirectCoolingDownToHealthy_IsForbidden()
    {
        var machine = CreateInState(HealthState.CoolingDown);

        Assert.False(machine.CanTransitionTo(HealthState.Healthy));
        Assert.Throws<InvalidStateTransitionException>(machine.ConfirmProbeSuccess);
        Assert.Equal(HealthState.CoolingDown, machine.State);
    }

    [Theory]
    [InlineData(HealthState.ProbeRequired)]
    [InlineData(HealthState.QuarantinedAuto)]
    [InlineData(HealthState.DisabledManual)]
    public void HealthyIsUnreachableWithoutProbeFromNonRecoveringStates(HealthState state)
    {
        var machine = CreateInState(state);

        Assert.False(machine.CanTransitionTo(HealthState.Healthy));
    }

    [Fact]
    public void StartProbe_MovesProbeRequiredToRecovering()
    {
        var machine = CreateInState(HealthState.ProbeRequired);

        machine.StartProbe();

        Assert.Equal(HealthState.Recovering, machine.State);
    }

    [Theory]
    [InlineData(HealthState.Healthy)]
    [InlineData(HealthState.Degraded)]
    [InlineData(HealthState.CoolingDown)]
    [InlineData(HealthState.QuarantinedAuto)]
    public void StartProbe_IsRejectedOutsideProbeRequired(HealthState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.StartProbe);
    }

    [Fact]
    public void ProbeSuccess_MovesRecoveringToHealthyAndResetsHistory()
    {
        var machine = CreateInState(HealthState.Recovering);

        machine.ConfirmProbeSuccess();

        Assert.Equal(HealthState.Healthy, machine.State);
        Assert.Equal(0, machine.AccountedFailureCount);

        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);

        Assert.Equal(HealthState.Degraded, machine.State);
    }

    [Fact]
    public void ProbeSuccess_MovesForcedEnabledToHealthy()
    {
        var machine = CreateInState(HealthState.ForcedEnabled);

        machine.ConfirmProbeSuccess();

        Assert.Equal(HealthState.Healthy, machine.State);
    }

    [Theory]
    [InlineData(HealthState.Healthy)]
    [InlineData(HealthState.Degraded)]
    [InlineData(HealthState.CoolingDown)]
    [InlineData(HealthState.ProbeRequired)]
    [InlineData(HealthState.QuarantinedAuto)]
    [InlineData(HealthState.DisabledManual)]
    public void ProbeSuccess_IsRejectedOutsideRecoveringOrForcedEnabled(HealthState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.ConfirmProbeSuccess);
    }

    [Fact]
    public void ProbeFailure_MovesProbeRequiredToQuarantinedAuto()
    {
        var machine = CreateInState(HealthState.ProbeRequired);

        machine.ConfirmProbeFailure();

        Assert.Equal(HealthState.QuarantinedAuto, machine.State);
    }

    [Fact]
    public void ProbeFailure_MovesRecoveringToQuarantinedAuto()
    {
        var machine = CreateInState(HealthState.Recovering);

        machine.ConfirmProbeFailure();

        Assert.Equal(HealthState.QuarantinedAuto, machine.State);
    }

    [Theory]
    [InlineData(HealthState.Healthy)]
    [InlineData(HealthState.Degraded)]
    [InlineData(HealthState.CoolingDown)]
    [InlineData(HealthState.QuarantinedAuto)]
    [InlineData(HealthState.DisabledManual)]
    [InlineData(HealthState.ForcedEnabled)]
    public void ProbeFailure_IsRejectedOutsideProbeStates(HealthState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.ConfirmProbeFailure);
    }

    [Fact]
    public void RepeatedHomogeneousError_MovesProbeRequiredToQuarantinedAuto()
    {
        var machine = CreateInState(HealthState.ProbeRequired);

        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);

        Assert.Equal(HealthState.QuarantinedAuto, machine.State);
    }

    [Fact]
    public void RepeatedHomogeneousError_MovesRecoveringToQuarantinedAuto()
    {
        var machine = CreateInState(HealthState.Recovering);

        machine.RecordFailure(HealthErrorClass.AuthenticationOrRefresh, Start);

        Assert.Equal(HealthState.QuarantinedAuto, machine.State);
    }

    [Fact]
    public void AdditionalFailureWhileCoolingDown_DoesNotChangeState()
    {
        var machine = CreateInState(HealthState.CoolingDown);

        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);

        Assert.Equal(HealthState.CoolingDown, machine.State);
    }

    [Theory]
    [InlineData(HealthState.Healthy)]
    [InlineData(HealthState.Degraded)]
    [InlineData(HealthState.CoolingDown)]
    [InlineData(HealthState.ProbeRequired)]
    [InlineData(HealthState.Recovering)]
    [InlineData(HealthState.QuarantinedAuto)]
    [InlineData(HealthState.ForcedEnabled)]
    [InlineData(HealthState.DisabledManual)]
    public void Disable_IsAllowedFromAnyState(HealthState state)
    {
        var machine = CreateInState(state);

        machine.Disable();

        Assert.Equal(HealthState.DisabledManual, machine.State);
    }

    [Fact]
    public void Enable_MovesDisabledManualToProbeRequired()
    {
        var machine = CreateInState(HealthState.DisabledManual);

        machine.Enable();

        Assert.Equal(HealthState.ProbeRequired, machine.State);
    }

    [Theory]
    [InlineData(HealthState.Healthy)]
    [InlineData(HealthState.Degraded)]
    [InlineData(HealthState.CoolingDown)]
    [InlineData(HealthState.ProbeRequired)]
    [InlineData(HealthState.QuarantinedAuto)]
    [InlineData(HealthState.ForcedEnabled)]
    public void Enable_IsRejectedOutsideDisabledManual(HealthState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.Enable);
    }

    [Theory]
    [InlineData(HealthState.CoolingDown)]
    [InlineData(HealthState.ProbeRequired)]
    [InlineData(HealthState.QuarantinedAuto)]
    public void ForceEnable_IsAllowedFromExcludedStates(HealthState state)
    {
        var machine = CreateInState(state);

        machine.ForceEnable();

        Assert.Equal(HealthState.ForcedEnabled, machine.State);
        Assert.Null(machine.CoolingDownUntil);
    }

    [Theory]
    [InlineData(HealthState.Healthy)]
    [InlineData(HealthState.Degraded)]
    [InlineData(HealthState.Recovering)]
    [InlineData(HealthState.DisabledManual)]
    [InlineData(HealthState.ForcedEnabled)]
    public void ForceEnable_IsRejectedFromOtherStates(HealthState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.ForceEnable);
    }

    [Fact]
    public void RequireProbe_MovesForcedEnabledToProbeRequired()
    {
        var machine = CreateInState(HealthState.ForcedEnabled);

        machine.RequireProbe();

        Assert.Equal(HealthState.ProbeRequired, machine.State);
    }

    [Theory]
    [InlineData(HealthState.Healthy)]
    [InlineData(HealthState.ProbeRequired)]
    [InlineData(HealthState.DisabledManual)]
    public void RequireProbe_IsRejectedOutsideForcedEnabled(HealthState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.RequireProbe);
    }

    [Fact]
    public void ForcedEnabled_ProbeFailure_IsRejected()
    {
        var machine = CreateInState(HealthState.ForcedEnabled);

        Assert.Throws<InvalidStateTransitionException>(machine.ConfirmProbeFailure);
    }

    [Fact]
    public void AuthenticationFailure_MovesForcedEnabledToCoolingDown()
    {
        var machine = CreateInState(HealthState.ForcedEnabled);

        var accounted = machine.RecordFailure(HealthErrorClass.AuthenticationOrRefresh, Start);

        // ТЗ §6.10: an auth failure blocks the account immediately, and a forced route is no exception:
        // it must not keep sending requests while its credentials are rejected.
        Assert.True(accounted);
        Assert.Equal(HealthState.CoolingDown, machine.State);
        Assert.Equal(Start + CooldownDuration, machine.CoolingDownUntil);
    }

    [Fact]
    public void TransientFailureBelowThreshold_KeepsForcedEnabled()
    {
        // Restored without accounted failures: a freshly forced scope that never tripped the breaker.
        var machine = HealthStateMachine.Restore(
            HealthState.ForcedEnabled,
            coolingDownUntil: null,
            accountedErrorClass: null,
            accountedFailureCount: 0,
            observedAt: Start);

        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);

        // The operator accepted the risk for ordinary transient failures: only a class that trips the
        // breaker (immediately for AuthenticationOrRefresh) overrides the force.
        Assert.Equal(HealthState.ForcedEnabled, machine.State);
    }

    [Fact]
    public void StartProbe_MovesForcedEnabledToRecovering()
    {
        var machine = CreateInState(HealthState.ForcedEnabled);

        machine.StartProbe();

        // A pinned probe may verify a forced route, so its start step is a legal transition.
        Assert.Equal(HealthState.Recovering, machine.State);
    }

    [Fact]
    public void CanTransitionTo_MatchesNormativeTransitionTable()
    {
        var allowed = CreateNormativeTransitionSet();

        foreach (var from in Enum.GetValues<HealthState>())
        {
            var machine = CreateInState(from);

            foreach (var to in Enum.GetValues<HealthState>())
            {
                Assert.Equal(allowed.Contains((from, to)), machine.CanTransitionTo(to));
            }
        }
    }

    [Fact]
    public void CustomPolicy_UsesConfiguredThresholdAndCooldown()
    {
        var policy = new HealthPolicy
        {
            FailureThreshold = 2,
            RollingWindow = TimeSpan.FromMinutes(1),
            CooldownDuration = TimeSpan.FromSeconds(30)
        };
        var machine = new HealthStateMachine(policy);

        machine.RecordFailure(HealthErrorClass.QuotaOrRateLimit, Start);
        machine.RecordFailure(HealthErrorClass.QuotaOrRateLimit, Start + TimeSpan.FromSeconds(1));

        Assert.Equal(HealthState.CoolingDown, machine.State);
        Assert.Equal(Start + TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(30), machine.CoolingDownUntil);
    }

    [Theory]
    [InlineData(0, 15, 5)]
    [InlineData(3, 0, 5)]
    [InlineData(3, 15, 0)]
    public void Constructor_RejectsNonPositivePolicyValues(int threshold, int windowMinutes, int cooldownMinutes)
    {
        var policy = new HealthPolicy
        {
            FailureThreshold = threshold,
            RollingWindow = TimeSpan.FromMinutes(windowMinutes),
            CooldownDuration = TimeSpan.FromMinutes(cooldownMinutes)
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new HealthStateMachine(policy));
    }

    private static HealthStateMachine CreateInState(HealthState state)
    {
        switch (state)
        {
            case HealthState.Healthy:
                return new HealthStateMachine();
            case HealthState.Degraded:
                var degraded = new HealthStateMachine();
                degraded.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);
                return degraded;
            case HealthState.CoolingDown:
                var coolingDown = new HealthStateMachine();
                coolingDown.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);
                coolingDown.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);
                coolingDown.RecordFailure(HealthErrorClass.NetworkOrTimeout, Start);
                return coolingDown;
            case HealthState.ProbeRequired:
                var probeRequired = CreateInState(HealthState.CoolingDown);
                probeRequired.ExpireCooldown(Start + CooldownDuration);
                return probeRequired;
            case HealthState.Recovering:
                var recovering = CreateInState(HealthState.ProbeRequired);
                recovering.StartProbe();
                return recovering;
            case HealthState.QuarantinedAuto:
                var quarantined = CreateInState(HealthState.ProbeRequired);
                quarantined.ConfirmProbeFailure();
                return quarantined;
            case HealthState.DisabledManual:
                var disabled = new HealthStateMachine();
                disabled.Disable();
                return disabled;
            case HealthState.ForcedEnabled:
                var forced = CreateInState(HealthState.ProbeRequired);
                forced.ForceEnable();
                return forced;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported health state.");
        }
    }

    private static HashSet<(HealthState From, HealthState To)> CreateNormativeTransitionSet() =>
        new()
        {
            (HealthState.Healthy, HealthState.Degraded),
            (HealthState.Healthy, HealthState.CoolingDown),
            (HealthState.Degraded, HealthState.CoolingDown),
            (HealthState.CoolingDown, HealthState.ProbeRequired),
            (HealthState.ProbeRequired, HealthState.Recovering),
            (HealthState.Recovering, HealthState.Healthy),
            (HealthState.ProbeRequired, HealthState.QuarantinedAuto),
            (HealthState.Recovering, HealthState.QuarantinedAuto),
            (HealthState.Healthy, HealthState.DisabledManual),
            (HealthState.Degraded, HealthState.DisabledManual),
            (HealthState.CoolingDown, HealthState.DisabledManual),
            (HealthState.ProbeRequired, HealthState.DisabledManual),
            (HealthState.Recovering, HealthState.DisabledManual),
            (HealthState.QuarantinedAuto, HealthState.DisabledManual),
            (HealthState.ForcedEnabled, HealthState.DisabledManual),
            (HealthState.DisabledManual, HealthState.ProbeRequired),
            (HealthState.CoolingDown, HealthState.ForcedEnabled),
            (HealthState.ProbeRequired, HealthState.ForcedEnabled),
            (HealthState.QuarantinedAuto, HealthState.ForcedEnabled),
            (HealthState.ForcedEnabled, HealthState.ProbeRequired),
            (HealthState.ForcedEnabled, HealthState.CoolingDown),
            (HealthState.ForcedEnabled, HealthState.Recovering),
            (HealthState.ForcedEnabled, HealthState.Healthy)
        };
}
