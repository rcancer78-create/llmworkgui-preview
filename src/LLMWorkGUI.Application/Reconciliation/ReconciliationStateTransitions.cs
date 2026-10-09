using LLMWorkGUI.Domain.StateMachines;
using ExecutionState = LLMWorkGUI.Domain.Enums.ExecutionState;
using SessionState = LLMWorkGUI.Domain.Enums.SessionState;

namespace LLMWorkGUI.Application.Reconciliation;

public static class ReconciliationStateTransitions
{
    public static SessionState ResolveSessionState(
        SessionState current,
        ReconciliationOutcome outcome,
        bool terminalTurnEvidence)
    {
        var target = outcome switch
        {
            ReconciliationOutcome.Reattached => ResolveReattachedSessionState(current, terminalTurnEvidence),
            ReconciliationOutcome.Orphaned => ToOrphaned(current),
            ReconciliationOutcome.Ambiguous => ToAmbiguous(current),
            ReconciliationOutcome.BackendMissing => ToOrphaned(current),
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "An actionable reconciliation outcome is required.")
        };

        EnsureSessionTransition(current, target);

        return target;
    }

    public static ExecutionState ResolveExecutionState(
        ExecutionState current,
        ReconciliationOutcome outcome,
        bool promptDeliveryExcluded)
    {
        if (ExecutionStateMachine.IsTerminal(current) && current != ExecutionState.Ambiguous)
        {
            return current;
        }

        var target = outcome switch
        {
            ReconciliationOutcome.Reattached => current,
            ReconciliationOutcome.Orphaned => promptDeliveryExcluded
                ? ExecutionState.Failed
                : ExecutionState.Ambiguous,
            ReconciliationOutcome.Ambiguous => ExecutionState.Ambiguous,
            ReconciliationOutcome.BackendMissing => promptDeliveryExcluded
                ? ExecutionState.Failed
                : ExecutionState.Ambiguous,
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "An actionable reconciliation outcome is required.")
        };

        EnsureExecutionTransition(current, target);

        return target;
    }

    private static SessionState ResolveReattachedSessionState(SessionState current, bool terminalTurnEvidence)
    {
        return current switch
        {
            SessionState.Starting or SessionState.Active or SessionState.Idle
                or SessionState.Ambiguous or SessionState.Orphaned =>
                terminalTurnEvidence ? SessionState.Idle : SessionState.Active,
            _ => current
        };
    }

    private static SessionState ToOrphaned(SessionState current)
    {
        return current switch
        {
            SessionState.Starting or SessionState.Active or SessionState.Idle => SessionState.Orphaned,
            _ => current
        };
    }

    private static SessionState ToAmbiguous(SessionState current)
    {
        return current switch
        {
            SessionState.Starting or SessionState.Active or SessionState.Idle => SessionState.Ambiguous,
            _ => current
        };
    }

    private static void EnsureSessionTransition(SessionState from, SessionState to)
    {
        if (from == to)
        {
            return;
        }

        if (SessionStateMachine.AllowedTransitions.Contains((from, to)))
        {
            return;
        }

        if (from == SessionState.Starting
            && to == SessionState.Idle
            && SessionStateMachine.AllowedTransitions.Contains((SessionState.Starting, SessionState.Active))
            && SessionStateMachine.AllowedTransitions.Contains((SessionState.Active, SessionState.Idle)))
        {
            return;
        }

        throw new InvalidStateTransitionException(
            nameof(ReconciliationStateTransitions),
            from,
            to,
            "the reconciliation transition is not present in the normative session transition table");
    }

    private static void EnsureExecutionTransition(ExecutionState from, ExecutionState to)
    {
        if (from == to)
        {
            return;
        }

        if (ExecutionStateMachine.AllowedTransitions.Contains((from, to)))
        {
            return;
        }

        throw new InvalidStateTransitionException(
            nameof(ReconciliationStateTransitions),
            from,
            to,
            "the reconciliation transition is not present in the normative execution transition table");
    }
}
