using System.Collections.Frozen;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Domain.StateMachines;

public sealed class SessionStateMachine
{
    public static IReadOnlySet<(SessionState From, SessionState To)> AllowedTransitions { get; } =
        new HashSet<(SessionState From, SessionState To)>
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
        }.ToFrozenSet();

    public SessionState State { get; private set; } = SessionState.Draft;

    public string? NativeSessionId { get; private set; }

    public SessionBinding? ConfirmedBinding { get; private set; }

    public CloseReason CloseReason { get; private set; } = CloseReason.None;

    public ReconciliationOutcome ReconciliationOutcome { get; private set; } = ReconciliationOutcome.None;

    public static bool IsTerminal(SessionState state) => state == SessionState.Closed;

    public bool CanTransitionTo(SessionState target) => AllowedTransitions.Contains((State, target));

    public void Start()
    {
        EnsureTransition(SessionState.Starting);
        State = SessionState.Starting;
    }

    public void ConfirmNativeSession(string nativeSessionId, SessionBinding observedBinding)
    {
        if (State != SessionState.Starting)
        {
            throw new InvalidStateTransitionException(
                nameof(SessionStateMachine),
                State,
                SessionState.Active,
                "native session confirmation is only valid while the session is Starting");
        }

        if (string.IsNullOrWhiteSpace(nativeSessionId))
        {
            throw new InvalidStateTransitionException(
                nameof(SessionStateMachine),
                State,
                SessionState.Active,
                "native session id is required before a session becomes Active");
        }

        ArgumentNullException.ThrowIfNull(observedBinding);

        NativeSessionId = nativeSessionId;
        ConfirmedBinding = observedBinding;
        State = SessionState.Active;
    }

    public void MarkIdle()
    {
        EnsureTransition(SessionState.Idle);
        State = SessionState.Idle;
    }

    public void BeginExecution(SessionBinding binding)
    {
        if (State != SessionState.Idle)
        {
            throw new InvalidStateTransitionException(
                nameof(SessionStateMachine),
                State,
                SessionState.Active,
                "a new execution can only start while the session is Idle");
        }

        ArgumentNullException.ThrowIfNull(binding);

        if (ConfirmedBinding is null || !ConfirmedBinding.Equals(binding))
        {
            throw new InvalidStateTransitionException(
                nameof(SessionStateMachine),
                State,
                SessionState.Active,
                "a new execution must reuse the confirmed immutable session binding");
        }

        State = SessionState.Active;
    }

    public void MarkAmbiguous()
    {
        EnsureTransition(SessionState.Ambiguous);
        State = SessionState.Ambiguous;
    }

    public void MarkOrphaned()
    {
        EnsureTransition(SessionState.Orphaned);
        State = SessionState.Orphaned;
    }

    public void Close(CloseReason reason)
    {
        EnsureTransition(SessionState.Closed);

        if (reason == CloseReason.None)
        {
            throw new InvalidStateTransitionException(
                nameof(SessionStateMachine),
                State,
                SessionState.Closed,
                "an explicit close or reset reason is required");
        }

        CloseReason = reason;
        State = SessionState.Closed;
    }

    public void ApplyReconciliationOutcome(
        ReconciliationOutcome outcome,
        string? nativeSessionId,
        SessionBinding? observedBinding,
        bool terminalTurnEvidenceConfirmed)
    {
        switch (outcome)
        {
            case ReconciliationOutcome.Reattached:
                Reattach(nativeSessionId, observedBinding, terminalTurnEvidenceConfirmed);
                break;
            case ReconciliationOutcome.Orphaned:
                ApplyOrphanedOutcome();
                break;
            case ReconciliationOutcome.Ambiguous:
                ApplyAmbiguousOutcome();
                break;
            case ReconciliationOutcome.BackendMissing:
                ApplyBackendMissingOutcome();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "An actionable reconciliation outcome is required.");
        }

        ReconciliationOutcome = outcome;
    }

    private void Reattach(string? nativeSessionId, SessionBinding? observedBinding, bool terminalTurnEvidenceConfirmed)
    {
        if (State is not (SessionState.Ambiguous or SessionState.Orphaned))
        {
            throw new InvalidStateTransitionException(
                nameof(SessionStateMachine),
                State,
                SessionState.Active,
                "Reattached is allowed only from Ambiguous or Orphaned sessions");
        }

        if (string.IsNullOrWhiteSpace(nativeSessionId) || !string.Equals(NativeSessionId, nativeSessionId, StringComparison.Ordinal))
        {
            throw new InvalidStateTransitionException(
                nameof(SessionStateMachine),
                State,
                SessionState.Active,
                "Reattached requires the confirmed native session id to match exactly");
        }

        if (observedBinding is null || ConfirmedBinding is null || !ConfirmedBinding.Equals(observedBinding))
        {
            throw new InvalidStateTransitionException(
                nameof(SessionStateMachine),
                State,
                SessionState.Active,
                "Reattached requires the full immutable session binding to match exactly");
        }

        State = terminalTurnEvidenceConfirmed ? SessionState.Idle : SessionState.Active;
    }

    private void ApplyOrphanedOutcome()
    {
        switch (State)
        {
            case SessionState.Starting:
            case SessionState.Active:
            case SessionState.Idle:
                State = SessionState.Orphaned;
                break;
            case SessionState.Ambiguous:
            case SessionState.Orphaned:
                break;
            default:
                throw new InvalidStateTransitionException(
                    nameof(SessionStateMachine),
                    State,
                    SessionState.Orphaned,
                    "Orphaned reconciliation is not defined for this session state");
        }
    }

    private void ApplyAmbiguousOutcome()
    {
        switch (State)
        {
            case SessionState.Starting:
            case SessionState.Active:
            case SessionState.Idle:
            case SessionState.Ambiguous:
                State = SessionState.Ambiguous;
                break;
            default:
                throw new InvalidStateTransitionException(
                    nameof(SessionStateMachine),
                    State,
                    SessionState.Ambiguous,
                    "Ambiguous reconciliation is not defined for this session state");
        }
    }

    private void ApplyBackendMissingOutcome()
    {
        switch (State)
        {
            case SessionState.Starting:
            case SessionState.Active:
            case SessionState.Idle:
                State = SessionState.Orphaned;
                break;
            case SessionState.Ambiguous:
            case SessionState.Orphaned:
                break;
            default:
                throw new InvalidStateTransitionException(
                    nameof(SessionStateMachine),
                    State,
                    SessionState.Orphaned,
                    "BackendMissing reconciliation is not defined for this session state");
        }
    }

    private void EnsureTransition(SessionState target)
    {
        if (!CanTransitionTo(target))
        {
            throw new InvalidStateTransitionException(
                nameof(SessionStateMachine),
                State,
                target,
                "transition is not present in the normative session transition table");
        }
    }
}
