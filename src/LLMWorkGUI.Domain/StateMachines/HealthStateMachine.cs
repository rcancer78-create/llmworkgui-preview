using System.Collections.Frozen;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Domain.StateMachines;

public sealed class HealthStateMachine
{
    private readonly Dictionary<HealthErrorClass, Queue<DateTimeOffset>> _accountedFailures = new();

    public HealthStateMachine(HealthPolicy? policy = null)
    {
        Policy = policy ?? new HealthPolicy();

        if (Policy.FailureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Failure threshold must be at least 1.");
        }

        if (Policy.RollingWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Rolling window must be positive.");
        }

        if (Policy.CooldownDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Cooldown duration must be positive.");
        }
    }

    /// <summary>
    /// Rebuilds a machine from persisted observations. This is a rehydration, not a transition, so the
    /// transition table is deliberately not consulted: the stored state was already reached through a
    /// legal transition, and re-validating it here would reject a legitimately persisted scope.
    /// </summary>
    /// <param name="accountedFailureCount">
    /// Legacy aggregate used only when no exact failure history exists. Older persisted rows did
    /// not retain individual classes/timestamps, so that approximation cannot reconstruct them.
    /// </param>
    public static HealthStateMachine Restore(
        HealthState state,
        DateTimeOffset? coolingDownUntil,
        HealthErrorClass? accountedErrorClass,
        int accountedFailureCount,
        DateTimeOffset observedAt,
        HealthPolicy? policy = null,
        IReadOnlyList<HealthFailureObservation>? failureHistory = null)
    {
        if (accountedFailureCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(accountedFailureCount),
                accountedFailureCount,
                "The accounted failure count cannot be negative.");
        }

        if ((state == HealthState.CoolingDown) != coolingDownUntil.HasValue)
            throw new ArgumentException("Only CoolingDown requires and may retain a cooldown deadline.", nameof(coolingDownUntil));

        if (failureHistory is null && accountedFailureCount > 0 &&
            (accountedErrorClass is not { } legacyClass || !Enum.IsDefined(legacyClass) || !legacyClass.IsAccountedByBreaker()))
            throw new ArgumentException("A legacy nonzero failure aggregate requires an accounted error class.", nameof(accountedErrorClass));

        var machine = new HealthStateMachine(policy)
        {
            State = state,
            CoolingDownUntil = coolingDownUntil
        };

        if (failureHistory is not null)
        {
            if (failureHistory.Count != accountedFailureCount || failureHistory.Any(failure => failure is null))
                throw new ArgumentException("Exact failure history must agree with its aggregate count and contain no null entries.", nameof(failureHistory));
            foreach (var failure in failureHistory.OrderBy(failure => failure.OccurredAt))
            {
                if (!Enum.IsDefined(failure.ErrorClass) || !failure.ErrorClass.IsAccountedByBreaker())
                    throw new ArgumentException("Failure history contains an error class excluded from breaker accounting.", nameof(failureHistory));
                machine.GetFailures(failure.ErrorClass).Enqueue(failure.OccurredAt);
            }
        }
        else if (accountedErrorClass is { } errorClass && accountedFailureCount > 0)
        {
            var failures = machine.GetFailures(errorClass);

            for (var i = 0; i < accountedFailureCount; i++)
            {
                failures.Enqueue(observedAt);
            }
        }

        return machine;
    }

    public static IReadOnlySet<(HealthState From, HealthState To)> AllowedTransitions { get; } =
        new HashSet<(HealthState From, HealthState To)>
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
        }.ToFrozenSet();

    public HealthPolicy Policy { get; }

    public HealthState State { get; private set; } = HealthState.Healthy;

    public DateTimeOffset? CoolingDownUntil { get; private set; }

    /// <summary>
    /// Error class of the failures currently counted by the breaker, or <c>null</c> when none are
    /// counted. When several classes are tracked, the one closest to the threshold is reported,
    /// because that is the class which will trip the breaker next.
    /// </summary>
    public HealthErrorClass? AccountedErrorClass
    {
        get
        {
            HealthErrorClass? dominant = null;
            var dominantCount = 0;

            foreach (var (errorClass, failures) in _accountedFailures)
            {
                if (failures.Count > dominantCount)
                {
                    dominant = errorClass;
                    dominantCount = failures.Count;
                }
            }

            return dominant;
        }
    }

    public int AccountedFailureCount => _accountedFailures.Values.Sum(failures => failures.Count);

    /// <summary>Exact snapshot for persistence; aggregate count/class alone cannot restore the window.</summary>
    public IReadOnlyList<HealthFailureObservation> AccountedFailureHistory =>
        _accountedFailures.SelectMany(entry => entry.Value.Select(timestamp =>
            new HealthFailureObservation(entry.Key, timestamp))).OrderBy(failure => failure.OccurredAt).ToArray();

    /// <summary>
    /// Timestamp of the oldest failure currently counted by the breaker, or <c>null</c> when none are
    /// counted. This is the real start of the rolling window and must be persisted as such: storing a
    /// derived value such as <c>now - RollingWindow</c> would place every restored failure exactly on
    /// the window edge, so it would be trimmed the moment the clock advanced and the breaker could
    /// never open across two calls.
    /// </summary>
    public DateTimeOffset? AccountedWindowStartedAt
    {
        get
        {
            DateTimeOffset? oldest = null;

            foreach (var failures in _accountedFailures.Values)
            {
                if (failures.Count > 0 && (oldest is null || failures.Peek() < oldest))
                {
                    oldest = failures.Peek();
                }
            }

            return oldest;
        }
    }

    public bool CanTransitionTo(HealthState target) => AllowedTransitions.Contains((State, target));

    public bool RecordFailure(HealthErrorClass errorClass, DateTimeOffset now)
    {
        if (!errorClass.IsAccountedByBreaker())
        {
            return false;
        }

        TrimExpiredFailures(now);
        GetFailures(errorClass).Enqueue(now);

        switch (State)
        {
            case HealthState.Healthy:
            case HealthState.Degraded:
                if (HasReachedThreshold(errorClass))
                {
                    CoolingDownUntil = now + Policy.CooldownDuration;
                    State = HealthState.CoolingDown;
                }
                else if (State == HealthState.Healthy)
                {
                    State = HealthState.Degraded;
                }

                break;
            case HealthState.ForcedEnabled:
                // ТЗ §6.10: an auth failure blocks the account immediately, and that overrides a forced
                // route. A forced route must not stay routable while its credentials are rejected, so
                // reaching the threshold (immediately for AuthenticationOrRefresh) opens the breaker.
                if (HasReachedThreshold(errorClass))
                {
                    CoolingDownUntil = now + Policy.CooldownDuration;
                    State = HealthState.CoolingDown;
                }

                break;
            case HealthState.ProbeRequired:
            case HealthState.Recovering:
                State = HealthState.QuarantinedAuto;
                break;
        }

        return true;
    }

    public bool ExpireCooldown(DateTimeOffset now)
    {
        if (State != HealthState.CoolingDown || CoolingDownUntil is null || now < CoolingDownUntil)
        {
            return false;
        }

        CoolingDownUntil = null;
        State = HealthState.ProbeRequired;
        return true;
    }

    public void ClearCooldown()
    {
        if (State != HealthState.CoolingDown)
        {
            throw new InvalidStateTransitionException(
                nameof(HealthStateMachine),
                State,
                HealthState.ProbeRequired,
                "cooldown can only be cleared while the route is CoolingDown");
        }

        CoolingDownUntil = null;
        State = HealthState.ProbeRequired;
    }

    public void StartProbe()
    {
        EnsureTransition(HealthState.Recovering);
        State = HealthState.Recovering;
    }

    public void ConfirmProbeSuccess()
    {
        if (State is not (HealthState.Recovering or HealthState.ForcedEnabled))
        {
            throw new InvalidStateTransitionException(
                nameof(HealthStateMachine),
                State,
                HealthState.Healthy,
                "probe success is only valid from Recovering or ForcedEnabled");
        }

        _accountedFailures.Clear();
        CoolingDownUntil = null;
        State = HealthState.Healthy;
    }

    public void ConfirmProbeFailure()
    {
        if (State is not (HealthState.ProbeRequired or HealthState.Recovering))
        {
            throw new InvalidStateTransitionException(
                nameof(HealthStateMachine),
                State,
                HealthState.QuarantinedAuto,
                "probe failure is only valid from ProbeRequired or Recovering");
        }

        State = HealthState.QuarantinedAuto;
    }

    public void Disable()
    {
        CoolingDownUntil = null;
        State = HealthState.DisabledManual;
    }

    public void Enable()
    {
        if (State != HealthState.DisabledManual)
        {
            throw new InvalidStateTransitionException(
                nameof(HealthStateMachine),
                State,
                HealthState.ProbeRequired,
                "a route can only be enabled from DisabledManual");
        }

        State = HealthState.ProbeRequired;
    }

    public void ForceEnable()
    {
        EnsureTransition(HealthState.ForcedEnabled);
        CoolingDownUntil = null;
        State = HealthState.ForcedEnabled;
    }

    public void RequireProbe()
    {
        if (State != HealthState.ForcedEnabled)
        {
            throw new InvalidStateTransitionException(
                nameof(HealthStateMachine),
                State,
                HealthState.ProbeRequired,
                "a forced route can only require a probe while ForcedEnabled");
        }

        State = HealthState.ProbeRequired;
    }

    private Queue<DateTimeOffset> GetFailures(HealthErrorClass errorClass)
    {
        if (!_accountedFailures.TryGetValue(errorClass, out var failures))
        {
            failures = new Queue<DateTimeOffset>();
            _accountedFailures.Add(errorClass, failures);
        }

        return failures;
    }

    private void TrimExpiredFailures(DateTimeOffset now)
    {
        var windowStart = now - Policy.RollingWindow;
        foreach (var failures in _accountedFailures.Values)
        {
            while (failures.Count > 0 && failures.Peek() < windowStart)
            {
                failures.Dequeue();
            }
        }
    }

    /// <summary>
    /// True when the class has reached the identical-failure threshold, or when the policy blocks an
    /// authentication/model-mismatch failure on its very first observation (ТЗ §6.10).
    /// </summary>
    private bool HasReachedThreshold(HealthErrorClass errorClass) =>
        GetFailures(errorClass).Count >= Policy.FailureThreshold ||
        (Policy.BlocksImmediatelyOnAuthenticationOrModelMismatch &&
         errorClass is HealthErrorClass.AuthenticationOrRefresh or HealthErrorClass.ModelUnavailableOrMismatch);

    private void EnsureTransition(HealthState target)
    {
        if (!CanTransitionTo(target))
        {
            throw new InvalidStateTransitionException(
                nameof(HealthStateMachine),
                State,
                target,
                "transition is not present in the normative health transition table");
        }
    }
}
