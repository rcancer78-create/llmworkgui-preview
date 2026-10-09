using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests;

public sealed class SessionStateMachineTests
{
    private const string NativeSessionId = "ses_01HZX8Q4T2K9";

    [Fact]
    public void NewMachine_StartsInDraft()
    {
        var machine = new SessionStateMachine();

        Assert.Equal(SessionState.Draft, machine.State);
        Assert.Null(machine.NativeSessionId);
        Assert.Null(machine.ConfirmedBinding);
        Assert.Equal(CloseReason.None, machine.CloseReason);
        Assert.Equal(ReconciliationOutcome.None, machine.ReconciliationOutcome);
    }

    [Fact]
    public void Start_MovesDraftToStarting()
    {
        var machine = new SessionStateMachine();

        machine.Start();

        Assert.Equal(SessionState.Starting, machine.State);
    }

    [Fact]
    public void Start_FromStarting_IsRejected()
    {
        var machine = CreateStarting();

        var exception = Assert.Throws<InvalidStateTransitionException>(machine.Start);

        Assert.Equal(SessionState.Starting, exception.From);
        Assert.Equal(SessionState.Starting, exception.To);
    }

    [Fact]
    public void Start_FromClosed_IsRejected()
    {
        var machine = CreateInState(SessionState.Closed);

        Assert.Throws<InvalidStateTransitionException>(machine.Start);
    }

    [Fact]
    public void ConfirmNativeSession_MovesStartingToActiveAndRecordsEvidence()
    {
        var machine = CreateStarting();
        var binding = CreateBinding();

        machine.ConfirmNativeSession(NativeSessionId, binding);

        Assert.Equal(SessionState.Active, machine.State);
        Assert.Equal(NativeSessionId, machine.NativeSessionId);
        Assert.Equal(binding, machine.ConfirmedBinding);
    }

    [Fact]
    public void ConfirmNativeSession_FromDraft_IsRejected()
    {
        var machine = new SessionStateMachine();

        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ConfirmNativeSession(NativeSessionId, CreateBinding()));
        Assert.False(machine.CanTransitionTo(SessionState.Active));
        Assert.Equal(SessionState.Draft, machine.State);
    }

    [Fact]
    public void ConfirmNativeSession_FromIdle_IsRejected()
    {
        var machine = CreateIdle();

        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ConfirmNativeSession(NativeSessionId, CreateBinding()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void ConfirmNativeSession_RequiresNativeSessionId(string? nativeSessionId)
    {
        var machine = CreateStarting();

        var exception = Assert.Throws<InvalidStateTransitionException>(
            () => machine.ConfirmNativeSession(nativeSessionId!, CreateBinding()));

        Assert.Equal(SessionState.Starting, exception.From);
        Assert.Equal(SessionState.Active, exception.To);
        Assert.Equal(SessionState.Starting, machine.State);
    }

    [Fact]
    public void ConfirmNativeSession_RequiresObservedBinding()
    {
        var machine = CreateStarting();

        Assert.Throws<ArgumentNullException>(() => machine.ConfirmNativeSession(NativeSessionId, null!));
        Assert.Equal(SessionState.Starting, machine.State);
    }

    [Fact]
    public void MarkIdle_MovesActiveToIdle()
    {
        var machine = CreateActive();

        machine.MarkIdle();

        Assert.Equal(SessionState.Idle, machine.State);
    }

    [Fact]
    public void MarkIdle_FromStarting_IsRejected()
    {
        var machine = CreateStarting();

        Assert.Throws<InvalidStateTransitionException>(machine.MarkIdle);
        Assert.False(machine.CanTransitionTo(SessionState.Idle));
    }

    [Fact]
    public void BeginExecution_MovesIdleToActiveOnIdenticalBinding()
    {
        var machine = CreateIdle();

        machine.BeginExecution(CreateBinding());

        Assert.Equal(SessionState.Active, machine.State);
        Assert.Equal(NativeSessionId, machine.NativeSessionId);
    }

    [Fact]
    public void BeginExecution_WithDifferentBinding_IsRejected()
    {
        var machine = CreateIdle();
        var changedBinding = new SessionBinding(
            BackendType.OpenCode,
            "profile-1",
            "account-1",
            "model-1",
            "high",
            "fast",
            "plan");

        var exception = Assert.Throws<InvalidStateTransitionException>(() => machine.BeginExecution(changedBinding));

        Assert.Equal(SessionState.Idle, exception.From);
        Assert.Equal(SessionState.Active, exception.To);
        Assert.Equal(SessionState.Idle, machine.State);
    }

    [Fact]
    public void BeginExecution_WithDifferentAccount_IsRejected()
    {
        var machine = CreateIdle();
        var changedBinding = new SessionBinding(
            BackendType.OpenCode,
            "profile-1",
            "account-2",
            "model-1",
            "high",
            "fast",
            "agent");

        Assert.Throws<InvalidStateTransitionException>(() => machine.BeginExecution(changedBinding));
    }

    [Fact]
    public void BeginExecution_RequiresBinding()
    {
        var machine = CreateIdle();

        Assert.Throws<ArgumentNullException>(() => machine.BeginExecution(null!));
        Assert.Equal(SessionState.Idle, machine.State);
    }

    [Fact]
    public void BeginExecution_FromActive_IsRejected()
    {
        var machine = CreateActive();

        Assert.Throws<InvalidStateTransitionException>(() => machine.BeginExecution(CreateBinding()));
    }

    [Fact]
    public void BeginExecution_FromStarting_IsRejected()
    {
        var machine = CreateStarting();

        Assert.Throws<InvalidStateTransitionException>(() => machine.BeginExecution(CreateBinding()));
    }

    [Theory]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Active)]
    [InlineData(SessionState.Idle)]
    public void MarkAmbiguous_IsAllowedFromNonTerminalLiveStates(SessionState state)
    {
        var machine = CreateInState(state);

        machine.MarkAmbiguous();

        Assert.Equal(SessionState.Ambiguous, machine.State);
    }

    [Theory]
    [InlineData(SessionState.Draft)]
    [InlineData(SessionState.Ambiguous)]
    [InlineData(SessionState.Orphaned)]
    [InlineData(SessionState.Closed)]
    public void MarkAmbiguous_IsRejectedFromOtherStates(SessionState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.MarkAmbiguous);
        Assert.Equal(state, machine.State);
    }

    [Theory]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Active)]
    [InlineData(SessionState.Idle)]
    public void MarkOrphaned_IsAllowedFromNonTerminalLiveStates(SessionState state)
    {
        var machine = CreateInState(state);

        machine.MarkOrphaned();

        Assert.Equal(SessionState.Orphaned, machine.State);
    }

    [Theory]
    [InlineData(SessionState.Draft)]
    [InlineData(SessionState.Ambiguous)]
    [InlineData(SessionState.Orphaned)]
    [InlineData(SessionState.Closed)]
    public void MarkOrphaned_IsRejectedFromOtherStates(SessionState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(machine.MarkOrphaned);
        Assert.Equal(state, machine.State);
    }

    [Theory]
    [InlineData(SessionState.Draft)]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Active)]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Ambiguous)]
    [InlineData(SessionState.Orphaned)]
    public void Close_IsAllowedFromAnyNonTerminalState(SessionState state)
    {
        var machine = CreateInState(state);

        machine.Close(CloseReason.UserClose);

        Assert.Equal(SessionState.Closed, machine.State);
        Assert.Equal(CloseReason.UserClose, machine.CloseReason);
    }

    [Fact]
    public void Close_RecordsResetReason()
    {
        var machine = CreateIdle();

        machine.Close(CloseReason.UserReset);

        Assert.Equal(SessionState.Closed, machine.State);
        Assert.Equal(CloseReason.UserReset, machine.CloseReason);
    }

    [Fact]
    public void Close_RequiresExplicitReason()
    {
        var machine = CreateActive();

        var exception = Assert.Throws<InvalidStateTransitionException>(() => machine.Close(CloseReason.None));

        Assert.Equal(SessionState.Active, exception.From);
        Assert.Equal(SessionState.Closed, exception.To);
        Assert.Equal(SessionState.Active, machine.State);
    }

    [Fact]
    public void Close_FromClosed_IsRejected()
    {
        var machine = CreateInState(SessionState.Closed);

        Assert.Throws<InvalidStateTransitionException>(() => machine.Close(CloseReason.UserClose));
    }

    [Fact]
    public void Closed_StateHasNoOutgoingTransitions()
    {
        var machine = CreateInState(SessionState.Closed);

        Assert.Throws<InvalidStateTransitionException>(machine.Start);
        Assert.Throws<InvalidStateTransitionException>(() => machine.ConfirmNativeSession(NativeSessionId, CreateBinding()));
        Assert.Throws<InvalidStateTransitionException>(machine.MarkIdle);
        Assert.Throws<InvalidStateTransitionException>(() => machine.BeginExecution(CreateBinding()));
        Assert.Throws<InvalidStateTransitionException>(machine.MarkAmbiguous);
        Assert.Throws<InvalidStateTransitionException>(machine.MarkOrphaned);
        Assert.Throws<InvalidStateTransitionException>(() => machine.Close(CloseReason.UserClose));
        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ApplyReconciliationOutcome(ReconciliationOutcome.Reattached, NativeSessionId, CreateBinding(), false));

        foreach (var target in Enum.GetValues<SessionState>())
        {
            Assert.False(machine.CanTransitionTo(target));
        }
    }

    [Fact]
    public void CanTransitionTo_MatchesNormativeTransitionTable()
    {
        var allowed = CreateNormativeTransitionSet();

        foreach (var from in Enum.GetValues<SessionState>())
        {
            var machine = CreateInState(from);

            foreach (var to in Enum.GetValues<SessionState>())
            {
                Assert.Equal(allowed.Contains((from, to)), machine.CanTransitionTo(to));
            }
        }
    }

    [Fact]
    public void AllowedTransitions_ContainNoTransitionsFromTerminalState()
    {
        Assert.DoesNotContain(SessionStateMachine.AllowedTransitions, transition => transition.From == SessionState.Closed);
    }

    [Fact]
    public void ApplyReconciliationOutcome_Reattached_MovesAmbiguousToActive()
    {
        var machine = CreateInState(SessionState.Ambiguous);

        machine.ApplyReconciliationOutcome(ReconciliationOutcome.Reattached, NativeSessionId, CreateBinding(), false);

        Assert.Equal(SessionState.Active, machine.State);
        Assert.Equal(ReconciliationOutcome.Reattached, machine.ReconciliationOutcome);
    }

    [Fact]
    public void ApplyReconciliationOutcome_Reattached_MovesOrphanedToActive()
    {
        var machine = CreateInState(SessionState.Orphaned);

        machine.ApplyReconciliationOutcome(ReconciliationOutcome.Reattached, NativeSessionId, CreateBinding(), false);

        Assert.Equal(SessionState.Active, machine.State);
        Assert.Equal(ReconciliationOutcome.Reattached, machine.ReconciliationOutcome);
    }

    [Fact]
    public void ApplyReconciliationOutcome_ReattachedWithTerminalEvidence_MovesAmbiguousToIdle()
    {
        var machine = CreateInState(SessionState.Ambiguous);

        machine.ApplyReconciliationOutcome(ReconciliationOutcome.Reattached, NativeSessionId, CreateBinding(), true);

        Assert.Equal(SessionState.Idle, machine.State);
        Assert.Equal(ReconciliationOutcome.Reattached, machine.ReconciliationOutcome);
    }

    [Fact]
    public void ApplyReconciliationOutcome_ReattachedWithTerminalEvidence_MovesOrphanedToIdle()
    {
        var machine = CreateInState(SessionState.Orphaned);

        machine.ApplyReconciliationOutcome(ReconciliationOutcome.Reattached, NativeSessionId, CreateBinding(), true);

        Assert.Equal(SessionState.Idle, machine.State);
    }

    [Fact]
    public void ApplyReconciliationOutcome_ReattachedWithMismatchedBinding_IsRejected()
    {
        var machine = CreateInState(SessionState.Ambiguous);
        var changedBinding = new SessionBinding(
            BackendType.OpenCode,
            "profile-1",
            "account-1",
            "model-2",
            "high",
            "fast",
            "agent");

        var exception = Assert.Throws<InvalidStateTransitionException>(
            () => machine.ApplyReconciliationOutcome(ReconciliationOutcome.Reattached, NativeSessionId, changedBinding, false));

        Assert.Equal(SessionState.Ambiguous, machine.State);
        Assert.Equal(ReconciliationOutcome.None, machine.ReconciliationOutcome);
        Assert.Contains("binding", exception.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyReconciliationOutcome_ReattachedWithMismatchedNativeSessionId_IsRejected()
    {
        var machine = CreateInState(SessionState.Ambiguous);

        var exception = Assert.Throws<InvalidStateTransitionException>(
            () => machine.ApplyReconciliationOutcome(ReconciliationOutcome.Reattached, "ses_other", CreateBinding(), false));

        Assert.Equal(SessionState.Ambiguous, machine.State);
        Assert.Contains("native session id", exception.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyReconciliationOutcome_ReattachedWithoutRecordedNativeSessionId_IsRejected()
    {
        var machine = CreateStarting();
        machine.MarkAmbiguous();

        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ApplyReconciliationOutcome(ReconciliationOutcome.Reattached, NativeSessionId, CreateBinding(), false));
    }

    [Fact]
    public void ApplyReconciliationOutcome_ReattachedWithNullBinding_IsRejected()
    {
        var machine = CreateInState(SessionState.Ambiguous);

        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ApplyReconciliationOutcome(ReconciliationOutcome.Reattached, NativeSessionId, null, false));
    }

    [Theory]
    [InlineData(SessionState.Draft)]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Active)]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Closed)]
    public void ApplyReconciliationOutcome_Reattached_IsRejectedOutsideAmbiguousOrOrphaned(SessionState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ApplyReconciliationOutcome(ReconciliationOutcome.Reattached, NativeSessionId, CreateBinding(), false));
    }

    [Theory]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Active)]
    [InlineData(SessionState.Idle)]
    public void ApplyReconciliationOutcome_Orphaned_MovesLiveStatesToOrphaned(SessionState state)
    {
        var machine = CreateInState(state);

        machine.ApplyReconciliationOutcome(ReconciliationOutcome.Orphaned, null, null, false);

        Assert.Equal(SessionState.Orphaned, machine.State);
        Assert.Equal(ReconciliationOutcome.Orphaned, machine.ReconciliationOutcome);
    }

    [Theory]
    [InlineData(SessionState.Ambiguous)]
    [InlineData(SessionState.Orphaned)]
    public void ApplyReconciliationOutcome_Orphaned_PreservesRecoveryStates(SessionState state)
    {
        var machine = CreateInState(state);

        machine.ApplyReconciliationOutcome(ReconciliationOutcome.Orphaned, null, null, false);

        Assert.Equal(state, machine.State);
        Assert.Equal(ReconciliationOutcome.Orphaned, machine.ReconciliationOutcome);
    }

    [Theory]
    [InlineData(SessionState.Draft)]
    [InlineData(SessionState.Closed)]
    public void ApplyReconciliationOutcome_Orphaned_IsRejectedFromOtherStates(SessionState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ApplyReconciliationOutcome(ReconciliationOutcome.Orphaned, null, null, false));
    }

    [Theory]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Active)]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Ambiguous)]
    public void ApplyReconciliationOutcome_Ambiguous_MovesOrPreservesAmbiguous(SessionState state)
    {
        var machine = CreateInState(state);

        machine.ApplyReconciliationOutcome(ReconciliationOutcome.Ambiguous, null, null, false);

        Assert.Equal(SessionState.Ambiguous, machine.State);
        Assert.Equal(ReconciliationOutcome.Ambiguous, machine.ReconciliationOutcome);
    }

    [Theory]
    [InlineData(SessionState.Draft)]
    [InlineData(SessionState.Orphaned)]
    [InlineData(SessionState.Closed)]
    public void ApplyReconciliationOutcome_Ambiguous_IsRejectedFromOtherStates(SessionState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ApplyReconciliationOutcome(ReconciliationOutcome.Ambiguous, null, null, false));
    }

    [Theory]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Active)]
    [InlineData(SessionState.Idle)]
    public void ApplyReconciliationOutcome_BackendMissing_MovesLiveStatesToOrphaned(SessionState state)
    {
        var machine = CreateInState(state);

        machine.ApplyReconciliationOutcome(ReconciliationOutcome.BackendMissing, null, null, false);

        Assert.Equal(SessionState.Orphaned, machine.State);
        Assert.Equal(ReconciliationOutcome.BackendMissing, machine.ReconciliationOutcome);
    }

    [Theory]
    [InlineData(SessionState.Ambiguous)]
    [InlineData(SessionState.Orphaned)]
    public void ApplyReconciliationOutcome_BackendMissing_PreservesRecoveryStates(SessionState state)
    {
        var machine = CreateInState(state);

        machine.ApplyReconciliationOutcome(ReconciliationOutcome.BackendMissing, null, null, false);

        Assert.Equal(state, machine.State);
        Assert.Equal(ReconciliationOutcome.BackendMissing, machine.ReconciliationOutcome);
    }

    [Theory]
    [InlineData(SessionState.Draft)]
    [InlineData(SessionState.Closed)]
    public void ApplyReconciliationOutcome_BackendMissing_IsRejectedFromOtherStates(SessionState state)
    {
        var machine = CreateInState(state);

        Assert.Throws<InvalidStateTransitionException>(
            () => machine.ApplyReconciliationOutcome(ReconciliationOutcome.BackendMissing, null, null, false));
    }

    [Fact]
    public void ApplyReconciliationOutcome_None_IsRejected()
    {
        var machine = CreateInState(SessionState.Ambiguous);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => machine.ApplyReconciliationOutcome(ReconciliationOutcome.None, null, null, false));
    }

    private static SessionBinding CreateBinding() =>
        new(BackendType.OpenCode, "profile-1", "account-1", "model-1", "high", "fast", "agent");

    private static SessionStateMachine CreateStarting()
    {
        var machine = new SessionStateMachine();
        machine.Start();
        return machine;
    }

    private static SessionStateMachine CreateActive()
    {
        var machine = CreateStarting();
        machine.ConfirmNativeSession(NativeSessionId, CreateBinding());
        return machine;
    }

    private static SessionStateMachine CreateIdle()
    {
        var machine = CreateActive();
        machine.MarkIdle();
        return machine;
    }

    private static SessionStateMachine CreateInState(SessionState state)
    {
        switch (state)
        {
            case SessionState.Draft:
                return new SessionStateMachine();
            case SessionState.Starting:
                return CreateStarting();
            case SessionState.Active:
                return CreateActive();
            case SessionState.Idle:
                return CreateIdle();
            case SessionState.Ambiguous:
                var ambiguous = CreateActive();
                ambiguous.MarkAmbiguous();
                return ambiguous;
            case SessionState.Orphaned:
                var orphaned = CreateActive();
                orphaned.MarkOrphaned();
                return orphaned;
            case SessionState.Closed:
                var closed = CreateIdle();
                closed.Close(CloseReason.UserClose);
                return closed;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported session state.");
        }
    }

    private static HashSet<(SessionState From, SessionState To)> CreateNormativeTransitionSet() =>
        new()
        {
            (SessionState.Draft, SessionState.Starting),
            (SessionState.Starting, SessionState.Active),
            (SessionState.Active, SessionState.Idle),
            (SessionState.Idle, SessionState.Active),
            (SessionState.Starting, SessionState.Ambiguous),
            (SessionState.Active, SessionState.Ambiguous),
            (SessionState.Idle, SessionState.Ambiguous),
            (SessionState.Starting, SessionState.Orphaned),
            (SessionState.Active, SessionState.Orphaned),
            (SessionState.Idle, SessionState.Orphaned),
            (SessionState.Draft, SessionState.Closed),
            (SessionState.Starting, SessionState.Closed),
            (SessionState.Active, SessionState.Closed),
            (SessionState.Idle, SessionState.Closed),
            (SessionState.Ambiguous, SessionState.Closed),
            (SessionState.Orphaned, SessionState.Closed),
            (SessionState.Ambiguous, SessionState.Active),
            (SessionState.Ambiguous, SessionState.Idle),
            (SessionState.Orphaned, SessionState.Active),
            (SessionState.Orphaned, SessionState.Idle)
        };
}
