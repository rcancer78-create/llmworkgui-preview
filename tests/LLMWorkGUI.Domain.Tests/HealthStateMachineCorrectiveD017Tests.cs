using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class HealthStateMachineCorrectiveD017Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CorrectiveD017_DifferentClassObservationRetiresExpiredAggregateAndPersistedHistory()
    {
        var machine = new HealthStateMachine(new HealthPolicy
        {
            FailureThreshold = 3,
            RollingWindow = TimeSpan.FromMinutes(15)
        });
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Now);
        machine.RecordFailure(HealthErrorClass.NetworkOrTimeout, Now.AddSeconds(1));
        var freshAt = Now.AddMinutes(16);
        machine.RecordFailure(HealthErrorClass.Provider4xx5xx, freshAt);

        Assert.Equal(HealthState.Degraded, machine.State);
        Assert.Equal(1, machine.AccountedFailureCount);
        Assert.Equal(HealthErrorClass.Provider4xx5xx, machine.AccountedErrorClass);
        Assert.Equal(freshAt, machine.AccountedWindowStartedAt);
        var remaining = Assert.Single(machine.AccountedFailureHistory);
        Assert.Equal(HealthErrorClass.Provider4xx5xx, remaining.ErrorClass);
        Assert.Equal(freshAt, remaining.OccurredAt);

        var restored = HealthStateMachine.Restore(machine.State, null, machine.AccountedErrorClass,
            machine.AccountedFailureCount, freshAt, machine.Policy, machine.AccountedFailureHistory);
        restored.RecordFailure(HealthErrorClass.NetworkOrTimeout, freshAt.AddSeconds(1));
        Assert.Equal(HealthState.Degraded, restored.State);
        Assert.Equal(2, restored.AccountedFailureCount);
    }

    [Fact]
    public void CorrectiveD017_LegacyAggregateWithoutClassCannotSilentlyForgiveObservedFailures()
    {
        Assert.Throws<ArgumentException>(() => HealthStateMachine.Restore(
            HealthState.Degraded, null, null, 2, Now));
    }
}
