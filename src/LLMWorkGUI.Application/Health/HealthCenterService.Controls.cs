using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;

namespace LLMWorkGUI.Application.Health;

/// <summary>
/// Probe, manual-control and audit surface of <see cref="HealthCenterService"/>, plus the shared
/// persistence helpers used by every transition.
/// </summary>
public sealed partial class HealthCenterService
{
    public async Task<HealthSnapshot> StartProbeAsync(
        HealthScope scope,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);

        // StartProbe throws when the scope is not awaiting one; the caller must not be told a probe
        // started when it did not.
        machine.StartProbe();

        return await PersistAsync(
                transition,
                scope,
                machine,
                errorClass: null,
                reason ?? "A manual probe was started; the route stays out of routing until it passes.",
                wasObserved ? previousState : null,
                stateChanged: true,
                now,
                evidenceRedactedJson: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<HealthSnapshot> CompleteProbeAsync(
        HealthScope scope,
        bool succeeded,
        string? evidenceRedactedJson = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        if (succeeded)
            throw new InvalidOperationException("Verified recovery requires the current admitted pinned-model probe attempt.");
        var now = _timeProvider.GetUtcNow();
        var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);

        machine.ConfirmProbeFailure();

        var reason = "A probe failed, so the route was quarantined instead of returning to routing.";

        return await PersistAsync(
                transition,
                scope,
                machine,
                errorClass: null,
                reason,
                wasObserved ? previousState : null,
                stateChanged: machine.State != previousState,
                now,
                evidenceRedactedJson,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Records an observed probe result without letting a mere connection check claim a recovery. A
    /// failing observation quarantines the scope; a passing one is recorded as evidence and appends to
    /// the audit, but never promotes the scope to Healthy. Only a pinned model probe may verify a
    /// recovery (ТЗ §6.10), so a successful <c>GET /models</c> cannot fake one.
    /// </summary>
    public async Task<HealthSnapshot> RecordProbeObservationAsync(
        HealthScope scope,
        bool observedSuccess,
        string reason,
        string? evidenceRedactedJson = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);

        if (!observedSuccess)
        {
            machine.ConfirmProbeFailure();
        }

        var stateChanged = machine.State != previousState;

        return await PersistAsync(
                transition,
                scope,
                machine,
                errorClass: null,
                reason,
                wasObserved ? previousState : null,
                stateChanged,
                now,
                evidenceRedactedJson,
                cancellationToken,
                forceAudit: true)
            .ConfigureAwait(false);
    }

    public async Task<HealthSnapshot> DisableManuallyAsync(
        HealthScope scope,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);

        machine.Disable();

        return await PersistAsync(
                transition,
                scope,
                machine,
                errorClass: null,
                reason,
                wasObserved ? previousState : null,
                stateChanged: machine.State != previousState,
                now,
                evidenceRedactedJson: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<HealthSnapshot> EnableAsync(
        HealthScope scope,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);

        // Enable lands on ProbeRequired: re-enabling is not evidence that the route works.
        machine.Enable();

        return await PersistAsync(
                transition,
                scope,
                machine,
                errorClass: null,
                reason,
                wasObserved ? previousState : null,
                stateChanged: true,
                now,
                evidenceRedactedJson: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<HealthSnapshot> ForceEnableAsync(
        HealthScope scope,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);

        machine.ForceEnable();

        // The reason is prefixed so the audit itself states that no probe backed this transition.
        return await PersistAsync(
                transition,
                scope,
                machine,
                errorClass: null,
                $"Forced back into routing without a passing probe: {reason}",
                wasObserved ? previousState : null,
                stateChanged: true,
                now,
                evidenceRedactedJson: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<HealthSnapshot> RequireProbeAsync(
        HealthScope scope,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);

        // RequireProbe throws when the scope was never forced; the caller must not be told a probe is
        // required for a scope that does not owe one.
        machine.RequireProbe();

        return await PersistAsync(
                transition,
                scope,
                machine,
                errorClass: null,
                reason ?? "The forced route now requires a probe before its recovery may count as verified.",
                wasObserved ? previousState : null,
                stateChanged: true,
                now,
                evidenceRedactedJson: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HealthEventView>> GetAuditAsync(
        HealthScope scope,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var events = await _eventRepository
            .ListByScopeAsync(scope.ScopeType, scope.ScopeId, limit, cancellationToken)
            .ConfigureAwait(false);

        return events
            .Select(record => new HealthEventView(
                record.Id,
                new HealthScope(record.ScopeType, record.ScopeId),
                record.PreviousState,
                record.NewState,
                record.ErrorClass,
                record.Reason,
                record.OccurredAt))
            .ToList();
    }

    /// <summary>
    /// Rehydrates the state machine from the persisted observation. A scope with no record starts
    /// Healthy, which is the only honest default for the machine: nothing bad has been observed yet.
    /// <c>WasObserved</c> is reported separately so the audit can distinguish "previously Healthy" from
    /// "never observed" instead of inventing a history the scope never had.
    /// </summary>
    private async Task<(HealthStateMachine Machine, HealthState PreviousState, bool WasObserved)> RestoreAsync(
        HealthScope scope,
        CancellationToken cancellationToken)
    {
        var record = await _stateRepository
            .GetAsync(scope.ScopeType, scope.ScopeId, cancellationToken)
            .ConfigureAwait(false);

        var policy = ResolvePolicy(scope);

        if (record is null)
        {
            return (new HealthStateMachine(policy), HealthState.Healthy, false);
        }

        var machine = HealthStateMachine.Restore(
            record.State,
            record.CooldownUntil,
            record.ErrorClass,
            record.FailureCount,
            record.WindowStartedAt ?? record.UpdatedAt,
            policy,
            record.FailureHistory);

        return (machine, record.State, true);
    }

    /// <summary>
    /// Writes the observation and any audit in the production store's single commit. The repository
    /// fallback supports lightweight callers without a transactional store, such as in-memory tests.
    /// </summary>
    private async Task<HealthSnapshot> PersistAsync(
        TransitionLease transition,
        HealthScope scope,
        HealthStateMachine machine,
        HealthErrorClass? errorClass,
        string? reason,
        HealthState? previousState,
        bool stateChanged,
        DateTimeOffset now,
        string? evidenceRedactedJson,
        CancellationToken cancellationToken,
        bool forceAudit = false,
        bool updateState = true,
        string? authenticationProjectionId = null,
        string? auditId = null,
        bool retireProbeAttempt = true)
    {
        var accountedErrorClass = machine.AccountedErrorClass ?? errorClass;

        // The real timestamp of the oldest counted failure is persisted. A derived value such as
        // now - RollingWindow would put every restored failure exactly on the window edge, so the next
        // call would trim it and the breaker could never open across two calls.
        var record = new HealthStateRecord(
            $"{scope.ScopeType}:{scope.ScopeId}",
            scope.ScopeType,
            scope.ScopeId,
            machine.State,
            accountedErrorClass,
            machine.AccountedFailureCount,
            machine.AccountedWindowStartedAt,
            machine.CoolingDownUntil,
            evidenceRedactedJson,
            now)
        {
            FailureHistory = machine.AccountedFailureHistory
        };

        // A forced audit is used for an observed probe result that did not move the state (for example a
        // connection check that passed): the observation must still be visible in the append-only audit,
        // because "checked, but not yet verified" is itself evidence the operator needs.
        var audit = stateChanged || errorClass is not null || forceAudit
            ? new HealthEventRecord(auditId ?? Guid.NewGuid().ToString("N"), scope.ScopeType, scope.ScopeId,
                previousState, machine.State, errorClass, reason, evidenceRedactedJson, now)
            : null;

        if (_transitionStore is IHealthAuthenticationFanoutStore fanout && authenticationProjectionId is not null)
        {
            await fanout.SaveAndCompleteAsync(record, audit!, authenticationProjectionId, cancellationToken).ConfigureAwait(false);
        }
        else if (_transitionStore is IHealthAuthenticationFanoutStore pendingStore &&
            await pendingStore.HasPendingAsync(scope, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Authentication blocking is pending. Retry the health refresh before changing this scope.");
        }
        else if (_transitionStore is not null)
        {
            await _transitionStore.SaveAsync(updateState ? record : null, audit, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (updateState)
                await _stateRepository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            if (audit is not null)
                await _eventRepository.AppendAsync(audit, cancellationToken).ConfigureAwait(false);
        }
        if (updateState && retireProbeAttempt) _activeProbeAttempts.Remove(scope);

        if (audit is not null)
        {
            // Announced only after the audit row is durable, so a subscriber can never observe a
            // transition that the recovery audit does not already explain.
            transition.Notifications.Add(
                new HealthTransitionEventArgs(
                    scope,
                    previousState,
                    machine.State,
                    accountedErrorClass,
                    reason,
                    now));
        }

        return ToSnapshot(scope, machine, now);
    }

    private static HealthSnapshot CreateDefaultSnapshot(HealthScope scope) =>
        new()
        {
            Scope = scope,
            State = HealthState.Healthy,
            AccountedFailureCount = 0
        };

    private static HealthSnapshot ToSnapshot(HealthScope scope, HealthStateRecord record) =>
        new()
        {
            Scope = scope,
            State = record.State,
            ErrorClass = record.ErrorClass,
            AccountedFailureCount = record.FailureCount,
            CooldownUntil = record.CooldownUntil,
            UpdatedAt = record.UpdatedAt
        };

    private static HealthSnapshot ToSnapshot(
        HealthScope scope,
        HealthStateMachine machine,
        DateTimeOffset now) =>
        new()
        {
            Scope = scope,
            State = machine.State,
            ErrorClass = machine.AccountedErrorClass,
            AccountedFailureCount = machine.AccountedFailureCount,
            CooldownUntil = machine.CoolingDownUntil,
            UpdatedAt = now
        };
}
