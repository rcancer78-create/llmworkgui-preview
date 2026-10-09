using System.Collections.Frozen;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Domain.StateMachines;

public sealed class ExecutionStateMachine
{
    public static IReadOnlyList<ExecutionState> NonTerminalStates { get; } = Array.AsReadOnly(new[]
    {
        ExecutionState.Queued,
        ExecutionState.Starting,
        ExecutionState.SessionConfirmed,
        ExecutionState.Running,
        ExecutionState.WaitingApproval,
        ExecutionState.Cancelling
    });

    public static IReadOnlyList<ExecutionState> TerminalStates { get; } = Array.AsReadOnly(new[]
    {
        ExecutionState.Succeeded,
        ExecutionState.Failed,
        ExecutionState.TimedOut,
        ExecutionState.Cancelled,
        ExecutionState.Ambiguous,
        ExecutionState.RouteMismatch
    });

    public static IReadOnlySet<(ExecutionState From, ExecutionState To)> AllowedTransitions { get; } =
        BuildAllowedTransitions().ToFrozenSet();

    public ExecutionState State { get; private set; } = ExecutionState.Queued;

    public ExecutionFailureReason FailureReason { get; private set; } = ExecutionFailureReason.None;

    public string? NativeSessionId { get; private set; }

    public SessionBinding? ConfirmedBinding { get; private set; }

    public static bool IsTerminal(ExecutionState state) => TerminalStates.Contains(state);

    public bool CanTransitionTo(ExecutionState target) => AllowedTransitions.Contains((State, target));

    public void Start()
    {
        EnsureTransition(ExecutionState.Starting);
        State = ExecutionState.Starting;
    }

    public void ConfirmSession(string nativeSessionId, SessionBinding binding)
    {
        EnsureTransition(ExecutionState.SessionConfirmed);

        if (string.IsNullOrWhiteSpace(nativeSessionId))
        {
            throw new InvalidStateTransitionException(
                nameof(ExecutionStateMachine),
                State,
                ExecutionState.SessionConfirmed,
                "native session id is required before a session is confirmed");
        }

        ArgumentNullException.ThrowIfNull(binding);

        NativeSessionId = nativeSessionId;
        ConfirmedBinding = binding;
        State = ExecutionState.SessionConfirmed;
    }

    public void MarkRunning()
    {
        EnsureTransition(ExecutionState.Running);
        State = ExecutionState.Running;
    }

    public void RequestApproval()
    {
        EnsureTransition(ExecutionState.WaitingApproval);
        State = ExecutionState.WaitingApproval;
    }

    public void ResolveApproval()
    {
        EnsureTransition(ExecutionState.Running);
        State = ExecutionState.Running;
    }

    public void RequestCancel()
    {
        EnsureTransition(ExecutionState.Cancelling);
        State = ExecutionState.Cancelling;
    }

    public void ConfirmCancelled()
    {
        EnsureTransition(ExecutionState.Cancelled);
        State = ExecutionState.Cancelled;
    }

    public void Succeed()
    {
        EnsureTransition(ExecutionState.Succeeded);
        State = ExecutionState.Succeeded;
    }

    public void Fail(ExecutionFailureReason reason)
    {
        EnsureTransition(ExecutionState.Failed);

        if (reason == ExecutionFailureReason.None)
        {
            throw new InvalidStateTransitionException(
                nameof(ExecutionStateMachine),
                State,
                ExecutionState.Failed,
                "an explicit failure reason is required");
        }

        FailureReason = reason;
        State = ExecutionState.Failed;
    }

    public void Timeout()
    {
        EnsureTransition(ExecutionState.TimedOut);
        State = ExecutionState.TimedOut;
    }

    public void MarkAmbiguous()
    {
        EnsureTransition(ExecutionState.Ambiguous);
        State = ExecutionState.Ambiguous;
    }

    public void MarkRouteMismatch()
    {
        EnsureTransition(ExecutionState.RouteMismatch);
        State = ExecutionState.RouteMismatch;
    }

    private static HashSet<(ExecutionState From, ExecutionState To)> BuildAllowedTransitions()
    {
        var transitions = new HashSet<(ExecutionState From, ExecutionState To)>
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

        foreach (var from in NonTerminalStates)
        {
            foreach (var to in TerminalStates)
            {
                if (to != ExecutionState.Cancelled)
                {
                    transitions.Add((from, to));
                }
            }
        }

        return transitions;
    }

    private void EnsureTransition(ExecutionState target)
    {
        if (!CanTransitionTo(target))
        {
            throw new InvalidStateTransitionException(
                nameof(ExecutionStateMachine),
                State,
                target,
                "transition is not present in the normative execution transition table");
        }
    }
}
