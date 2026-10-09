using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class ExecutionStateMachineTests
{
    private const string NativeSessionId = "ses_01HZX8Q4T2K9";

    [Fact]
    public void NewMachine_StartsQueued()
    {
        var machine = new ExecutionStateMachine();

        Assert.Equal(ExecutionState.Queued, machine.State);
        Assert.Equal(ExecutionFailureReason.None, machine.FailureReason);
        Assert.Null(machine.NativeSessionId);
        Assert.Null(machine.ConfirmedBinding);
    }

    [Fact]
    public void HappyPath_ReachesSucceeded()
    {
        var machine = new ExecutionStateMachine();
        var binding = CreateBinding();

        machine.Start();
        machine.ConfirmSession(NativeSessionId, binding);
        machine.MarkRunning();
        machine.RequestApproval();
        machine.ResolveApproval();
        machine.Succeed();

        Assert.Equal(ExecutionState.Succeeded, machine.State);
        Assert.Equal(NativeSessionId, machine.NativeSessionId);
        Assert.Equal(binding, machine.ConfirmedBinding);
    }

    [Fact]
    public void Start_FromQueued_MovesToStarting()
    {
        var machine = new ExecutionStateMachine();

        machine.Start();

        Assert.Equal(ExecutionState.Starting, machine.State);
    }

    [Theory]
    [InlineData(ExecutionState.Starting)]
    [InlineData(ExecutionState.SessionConfirmed)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.Succeeded)]
    [InlineData(ExecutionState.Failed)]
    public void Start_IsRejectedOutsideQueued(ExecutionState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.Start);
    }

    [Fact]
    public void ConfirmSession_FromQueued_IsRejected()
    {
        var machine = new ExecutionStateMachine();

        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ConfirmSession(NativeSessionId, CreateBinding()));
        Assert.False(machine.CanTransitionTo(ExecutionState.SessionConfirmed));
    }

    [Fact]
    public void ConfirmSession_RecordsNativeSessionEvidence()
    {
        var machine = CreateStarting();
        var binding = CreateBinding();

        machine.ConfirmSession(NativeSessionId, binding);

        Assert.Equal(ExecutionState.SessionConfirmed, machine.State);
        Assert.Equal(NativeSessionId, machine.NativeSessionId);
        Assert.Equal(binding, machine.ConfirmedBinding);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void ConfirmSession_RequiresNativeSessionId(string? nativeSessionId)
    {
        var machine = CreateStarting();

        var exception = Assert.Throws<InvalidStateTransitionException>(
            () => machine.ConfirmSession(nativeSessionId!, CreateBinding()));

        Assert.Equal(ExecutionState.Starting, exception.From);
        Assert.Equal(ExecutionState.SessionConfirmed, exception.To);
        Assert.Equal(ExecutionState.Starting, machine.State);
    }

    [Fact]
    public void ConfirmSession_RequiresBinding()
    {
        var machine = CreateStarting();

        Assert.Throws<ArgumentNullException>(() => machine.ConfirmSession(NativeSessionId, null!));
        Assert.Equal(ExecutionState.Starting, machine.State);
    }

    [Fact]
    public void MarkRunning_FromSessionConfirmed_MovesToRunning()
    {
        var machine = CreateSessionConfirmed();

        machine.MarkRunning();

        Assert.Equal(ExecutionState.Running, machine.State);
    }

    [Fact]
    public void MarkRunning_FromStarting_IsRejected()
    {
        var machine = CreateStarting();

        Assert.Throws<InvalidStateTransitionException>(machine.MarkRunning);
    }

    [Fact]
    public void RequestApproval_And_ResolveApproval_RoundTrip()
    {
        var machine = CreateRunning();

        machine.RequestApproval();
        Assert.Equal(ExecutionState.WaitingApproval, machine.State);

        machine.ResolveApproval();
        Assert.Equal(ExecutionState.Running, machine.State);
    }

    [Fact]
    public void RequestApproval_FromSessionConfirmed_IsRejected()
    {
        var machine = CreateSessionConfirmed();

        Assert.Throws<InvalidStateTransitionException>(machine.RequestApproval);
    }

    [Fact]
    public void ResolveApproval_FromRunning_IsRejected()
    {
        var machine = CreateRunning();

        Assert.Throws<InvalidStateTransitionException>(machine.ResolveApproval);
    }

    [Theory]
    [InlineData(ExecutionState.Starting)]
    [InlineData(ExecutionState.SessionConfirmed)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.WaitingApproval)]
    public void RequestCancel_IsAllowedFromCancellableStates(ExecutionState state)
    {
        var machine = CreateInState(state);

        machine.RequestCancel();

        Assert.Equal(ExecutionState.Cancelling, machine.State);
    }

    [Theory]
    [InlineData(ExecutionState.Queued)]
    [InlineData(ExecutionState.Cancelling)]
    [InlineData(ExecutionState.Succeeded)]
    [InlineData(ExecutionState.Failed)]
    [InlineData(ExecutionState.TimedOut)]
    [InlineData(ExecutionState.Cancelled)]
    [InlineData(ExecutionState.Ambiguous)]
    [InlineData(ExecutionState.RouteMismatch)]
    public void RequestCancel_IsRejectedFromOtherStates(ExecutionState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.RequestCancel);
        Assert.Equal(state, machine.State);
    }

    [Fact]
    public void ConfirmCancelled_FromCancelling_MovesToCancelled()
    {
        var machine = CreateCancelling();

        machine.ConfirmCancelled();

        Assert.Equal(ExecutionState.Cancelled, machine.State);
    }

    [Theory]
    [InlineData(ExecutionState.Queued)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.WaitingApproval)]
    [InlineData(ExecutionState.Succeeded)]
    public void ConfirmCancelled_IsRejectedOutsideCancelling(ExecutionState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.ConfirmCancelled);
    }

    [Theory]
    [InlineData(ExecutionState.Queued)]
    [InlineData(ExecutionState.Starting)]
    [InlineData(ExecutionState.SessionConfirmed)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.WaitingApproval)]
    [InlineData(ExecutionState.Cancelling)]
    public void Succeed_IsAllowedFromAnyNonTerminalState(ExecutionState state)
    {
        var machine = CreateInState(state);

        machine.Succeed();

        Assert.Equal(ExecutionState.Succeeded, machine.State);
    }

    [Theory]
    [InlineData(ExecutionState.Queued)]
    [InlineData(ExecutionState.Starting)]
    [InlineData(ExecutionState.SessionConfirmed)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.WaitingApproval)]
    [InlineData(ExecutionState.Cancelling)]
    public void Fail_IsAllowedFromAnyNonTerminalState(ExecutionState state)
    {
        var machine = CreateInState(state);

        machine.Fail(ExecutionFailureReason.StartupFailure);

        Assert.Equal(ExecutionState.Failed, machine.State);
        Assert.Equal(ExecutionFailureReason.StartupFailure, machine.FailureReason);
    }

    [Fact]
    public void Fail_WithBufferOverflow_RecordsReason()
    {
        var machine = CreateRunning();

        machine.Fail(ExecutionFailureReason.BufferOverflow);

        Assert.Equal(ExecutionState.Failed, machine.State);
        Assert.Equal(ExecutionFailureReason.BufferOverflow, machine.FailureReason);
    }

    [Fact]
    public void Fail_WithNoneReason_IsRejected()
    {
        var machine = CreateRunning();

        var exception = Assert.Throws<InvalidStateTransitionException>(
            () => machine.Fail(ExecutionFailureReason.None));

        Assert.Equal(ExecutionState.Running, exception.From);
        Assert.Equal(ExecutionState.Failed, exception.To);
        Assert.Equal(ExecutionState.Running, machine.State);
        Assert.Equal(ExecutionFailureReason.None, machine.FailureReason);
    }

    [Theory]
    [InlineData(ExecutionState.Queued)]
    [InlineData(ExecutionState.Starting)]
    [InlineData(ExecutionState.SessionConfirmed)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.WaitingApproval)]
    [InlineData(ExecutionState.Cancelling)]
    public void Timeout_IsAllowedFromAnyNonTerminalState(ExecutionState state)
    {
        var machine = CreateInState(state);

        machine.Timeout();

        Assert.Equal(ExecutionState.TimedOut, machine.State);
    }

    [Theory]
    [InlineData(ExecutionState.Queued)]
    [InlineData(ExecutionState.Starting)]
    [InlineData(ExecutionState.SessionConfirmed)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.WaitingApproval)]
    [InlineData(ExecutionState.Cancelling)]
    public void MarkAmbiguous_IsAllowedFromAnyNonTerminalState(ExecutionState state)
    {
        var machine = CreateInState(state);

        machine.MarkAmbiguous();

        Assert.Equal(ExecutionState.Ambiguous, machine.State);
    }

    [Theory]
    [InlineData(ExecutionState.Queued)]
    [InlineData(ExecutionState.Starting)]
    [InlineData(ExecutionState.SessionConfirmed)]
    [InlineData(ExecutionState.Running)]
    [InlineData(ExecutionState.WaitingApproval)]
    [InlineData(ExecutionState.Cancelling)]
    public void MarkRouteMismatch_IsAllowedFromAnyNonTerminalState(ExecutionState state)
    {
        var machine = CreateInState(state);

        machine.MarkRouteMismatch();

        Assert.Equal(ExecutionState.RouteMismatch, machine.State);
    }

    [Theory]
    [InlineData(ExecutionState.Succeeded)]
    [InlineData(ExecutionState.Failed)]
    [InlineData(ExecutionState.TimedOut)]
    [InlineData(ExecutionState.Cancelled)]
    [InlineData(ExecutionState.Ambiguous)]
    [InlineData(ExecutionState.RouteMismatch)]
    public void TerminalStates_HaveNoOutgoingTransitions(ExecutionState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.Start);
        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ConfirmSession(NativeSessionId, CreateBinding()));
        Assert.Throws<InvalidStateTransitionException>(machine.MarkRunning);
        Assert.Throws<InvalidStateTransitionException>(machine.RequestApproval);
        Assert.Throws<InvalidStateTransitionException>(machine.ResolveApproval);
        Assert.Throws<InvalidStateTransitionException>(machine.RequestCancel);
        Assert.Throws<InvalidStateTransitionException>(machine.ConfirmCancelled);
        Assert.Throws<InvalidStateTransitionException>(machine.Succeed);
        Assert.Throws<InvalidStateTransitionException>(() => machine.Fail(ExecutionFailureReason.InternalError));
        Assert.Throws<InvalidStateTransitionException>(machine.Timeout);
        Assert.Throws<InvalidStateTransitionException>(machine.MarkAmbiguous);
        Assert.Throws<InvalidStateTransitionException>(machine.MarkRouteMismatch);

        foreach (var target in Enum.GetValues<ExecutionState>())
        {
            Assert.False(machine.CanTransitionTo(target));
        }
    }

    [Fact]
    public void CanTransitionTo_MatchesNormativeTransitionTable()
    {
        var allowed = CreateNormativeTransitionSet();

        foreach (var from in Enum.GetValues<ExecutionState>())
        {
            var machine = CreateInState(from);

            foreach (var to in Enum.GetValues<ExecutionState>())
            {
                Assert.Equal(allowed.Contains((from, to)), machine.CanTransitionTo(to));
            }
        }
    }

    [Fact]
    public void TerminalClassification_MatchesNormativeTable()
    {
        var expectedTerminal = new HashSet<ExecutionState>
        {
            ExecutionState.Succeeded,
            ExecutionState.Failed,
            ExecutionState.TimedOut,
            ExecutionState.Cancelled,
            ExecutionState.Ambiguous,
            ExecutionState.RouteMismatch
        };

        foreach (var state in Enum.GetValues<ExecutionState>())
        {
            Assert.Equal(expectedTerminal.Contains(state), ExecutionStateMachine.IsTerminal(state));
        }
    }

    [Fact]
    public void TerminalStates_HaveNoOutgoingTransitionsInTable()
    {
        foreach (var terminal in ExecutionStateMachine.TerminalStates)
        {
            Assert.DoesNotContain(
                ExecutionStateMachine.AllowedTransitions,
                transition => transition.From == terminal);
        }
    }

    private static SessionBinding CreateBinding() =>
        new(BackendType.OpenCode, "profile-1", "account-1", "model-1", "high", "fast", "agent");

    private static ExecutionStateMachine CreateStarting()
    {
        var machine = new ExecutionStateMachine();
        machine.Start();
        return machine;
    }

    private static ExecutionStateMachine CreateSessionConfirmed()
    {
        var machine = CreateStarting();
        machine.ConfirmSession(NativeSessionId, CreateBinding());
        return machine;
    }

    private static ExecutionStateMachine CreateRunning()
    {
        var machine = CreateSessionConfirmed();
        machine.MarkRunning();
        return machine;
    }

    private static ExecutionStateMachine CreateWaitingApproval()
    {
        var machine = CreateRunning();
        machine.RequestApproval();
        return machine;
    }

    private static ExecutionStateMachine CreateCancelling()
    {
        var machine = CreateRunning();
        machine.RequestCancel();
        return machine;
    }

    private static ExecutionStateMachine CreateInState(ExecutionState state)
    {
        switch (state)
        {
            case ExecutionState.Queued:
                return new ExecutionStateMachine();
            case ExecutionState.Starting:
                return CreateStarting();
            case ExecutionState.SessionConfirmed:
                return CreateSessionConfirmed();
            case ExecutionState.Running:
                return CreateRunning();
            case ExecutionState.WaitingApproval:
                return CreateWaitingApproval();
            case ExecutionState.Cancelling:
                return CreateCancelling();
            case ExecutionState.Cancelled:
                var cancelled = CreateCancelling();
                cancelled.ConfirmCancelled();
                return cancelled;
            case ExecutionState.Succeeded:
                var succeeded = CreateRunning();
                succeeded.Succeed();
                return succeeded;
            case ExecutionState.Failed:
                var failed = CreateRunning();
                failed.Fail(ExecutionFailureReason.InternalError);
                return failed;
            case ExecutionState.TimedOut:
                var timedOut = CreateRunning();
                timedOut.Timeout();
                return timedOut;
            case ExecutionState.Ambiguous:
                var ambiguous = CreateRunning();
                ambiguous.MarkAmbiguous();
                return ambiguous;
            case ExecutionState.RouteMismatch:
                var routeMismatch = CreateRunning();
                routeMismatch.MarkRouteMismatch();
                return routeMismatch;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported execution state.");
        }
    }

    private static HashSet<(ExecutionState From, ExecutionState To)> CreateNormativeTransitionSet()
    {
        var allowed = new HashSet<(ExecutionState From, ExecutionState To)>
        {
            (ExecutionState.Queued, ExecutionState.Starting),
            (ExecutionState.Starting, ExecutionState.SessionConfirmed),
            (ExecutionState.SessionConfirmed, ExecutionState.Running),
            (ExecutionState.Running, ExecutionState.WaitingApproval),
            (ExecutionState.WaitingApproval, ExecutionState.Running),
            (ExecutionState.Starting, ExecutionState.Cancelling),
            (ExecutionState.SessionConfirmed, ExecutionState.Cancelling),
            (ExecutionState.Running, ExecutionState.Cancelling),
            (ExecutionState.WaitingApproval, ExecutionState.Cancelling),
            (ExecutionState.Cancelling, ExecutionState.Cancelled)
        };

        foreach (var from in ExecutionStateMachine.NonTerminalStates)
        {
            foreach (var to in ExecutionStateMachine.TerminalStates)
            {
                if (to != ExecutionState.Cancelled)
                {
                    allowed.Add((from, to));
                }
            }
        }

        return allowed;
    }
}
