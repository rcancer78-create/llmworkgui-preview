using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Application.Repositories;

namespace LLMWorkGUI.Application.Health;

public sealed partial class HealthCenterService
{
    public async Task<HealthFailureOutcome> ReportProbeModelMismatchAsync(HealthProbeAttempt attempt, string modelId,
        string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (attempt.Scope.ScopeType != HealthScope.AccountScopeType)
            throw new ArgumentException("A model mismatch projection requires an account probe.", nameof(attempt));
        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var scope = HealthScope.ForModelRoute(attempt.Scope.ScopeId, modelId);
        var (machine, previous, observed) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);
        var auditId = "probe-model-mismatch:" + attempt.Id;
        var durable = await _eventRepository.FindByIdAsync(scope.ScopeType, scope.ScopeId,
            auditId, cancellationToken).ConfigureAwait(false);
        if (durable is not null)
        {
            var record = await _stateRepository.GetAsync(scope.ScopeType, scope.ScopeId, cancellationToken)
                .ConfigureAwait(false);
            return new HealthFailureOutcome
            {
                Snapshot = record is null ? CreateDefaultSnapshot(scope) : ToSnapshot(scope, record),
                CountedByBreaker = false,
                StateChanged = false,
                AutomaticRetryAllowed = false
            };
        }

        // The account owns this derivative observation. Validate its exact lease in the same
        // transition as the route audit/write; a newer account decision must not charge this result.
        var current = _activeProbeAttempts.TryGetValue(attempt.Scope, out var activeId) && activeId == attempt.Id;
        var now = _timeProvider.GetUtcNow();
        var counted = current && machine.RecordFailure(HealthErrorClass.ModelUnavailableOrMismatch, now);
        var changed = machine.State != previous;
        var snapshot = await PersistAsync(transition, scope, machine, HealthErrorClass.ModelUnavailableOrMismatch,
            current ? reason : "Late model mismatch recorded without changing the newer health state. " + reason,
            observed ? previous : null, changed, now, null, cancellationToken,
            forceAudit: true, updateState: current, auditId: auditId).ConfigureAwait(false);
        return new HealthFailureOutcome
        {
            Snapshot = snapshot,
            CountedByBreaker = counted,
            StateChanged = changed,
            AutomaticRetryAllowed = false
        };
    }

    public async Task<HealthProbeAttempt?> TryBeginProbeAttemptAsync(HealthScope scope, bool modelProbe,
        string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);
        var eligible = modelProbe
            ? machine.State is HealthState.ProbeRequired or HealthState.Recovering or HealthState.ForcedEnabled
            : machine.State == HealthState.ProbeRequired;
        if (!eligible || _activeProbeAttempts.ContainsKey(scope)) return null;

        if (machine.State != HealthState.Recovering) machine.StartProbe();
        await PersistAsync(transition, scope, machine, null, reason, wasObserved ? previousState : null,
            machine.State != previousState, _timeProvider.GetUtcNow(), null, cancellationToken, forceAudit: true)
            .ConfigureAwait(false);
        var attempt = new HealthProbeAttempt(Guid.NewGuid().ToString("N"), scope);
        _activeProbeAttempts.Add(scope, attempt.Id);
        return attempt;
    }

    public async Task<HealthProbeAttemptCompletion> CompleteProbeAttemptAsync(HealthProbeAttempt attempt,
        HealthProbeCompletionKind kind, string reason, string? evidenceRedactedJson = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == HealthProbeCompletionKind.AuthenticationFailed)
        {
            if (_transitionStore is IHealthAuthenticationFanoutStore)
            {
                var retained = await TryResumeAuthenticationProbeCompletionAsync(attempt, cancellationToken)
                    .ConfigureAwait(false);
                if (retained is not null) return retained;
                // The first completion commit is the durable multi-scope barrier, never a terminal
                // probe row followed by best-effort account propagation on an exhausted token.
                var auth = await ReportAuthenticationFailuresCoreAsync([attempt.Scope], reason, attempt,
                    evidenceRedactedJson, cancellationToken).ConfigureAwait(false);
                return new(auth.Outcomes[0].Snapshot, auth.ProbeWasCurrent, false);
            }
            // Compatibility for lightweight in-memory graphs; production DI always supplies the outbox.
            var completion = await CompleteProbeAttemptAsync(attempt, HealthProbeCompletionKind.Failed,
                reason, evidenceRedactedJson, cancellationToken).ConfigureAwait(false);
            var account = attempt.Scope.ScopeType == HealthScope.AccountScopeType ? attempt.Scope.ScopeId :
                attempt.Scope.TryGetModelRoute(out var modelAccount, out _) ? modelAccount : null;
            if (account is not null)
                await ReportFailureAsync(HealthScope.ForAccount(account), HealthErrorClass.AuthenticationOrRefresh,
                    reason, cancellationToken).ConfigureAwait(false);
            return completion;
        }
        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var (machine, previousState, wasObserved) = await RestoreAsync(attempt.Scope, cancellationToken).ConfigureAwait(false);
        var current = _activeProbeAttempts.TryGetValue(attempt.Scope, out var activeId) && activeId == attempt.Id;
        var completionId = "probe-completion:" + attempt.Id;
        var durable = await _eventRepository.FindByIdAsync(attempt.Scope.ScopeType, attempt.Scope.ScopeId,
            completionId, cancellationToken).ConfigureAwait(false);
        if (durable is not null)
        {
            // A store may commit and then lose its acknowledgement. Reconcile that exact observation
            // before attempting another state transition or append; a newer decision always wins.
            var record = await _stateRepository.GetAsync(attempt.Scope.ScopeType, attempt.Scope.ScopeId,
                cancellationToken).ConfigureAwait(false);
            var stillOwnsResult = current && record is not null && record.UpdatedAt == durable.OccurredAt &&
                record.State == durable.NewState;
            if (stillOwnsResult)
            {
                // The failed acknowledgement prevented PersistAsync from announcing this commit.
                // Retiring the matching local lease makes this repair announcement one-use.
                transition.Notifications.Add(new HealthTransitionEventArgs(attempt.Scope, durable.PreviousState,
                    durable.NewState, record!.ErrorClass, durable.Reason, durable.OccurredAt));
            }
            if (current) _activeProbeAttempts.Remove(attempt.Scope);
            return new(ToSnapshot(attempt.Scope, machine, record?.UpdatedAt ?? durable.OccurredAt),
                stillOwnsResult, stillOwnsResult && kind == HealthProbeCompletionKind.ModelSucceeded &&
                    durable.NewState == HealthState.Healthy);
        }
        if (current)
        {
            // PersistAsync retires ownership only after the state and audit are durable.
            // A failed commit must allow the same observed result to be saved again.
            if (kind == HealthProbeCompletionKind.ModelSucceeded) machine.ConfirmProbeSuccess();
            else if (kind == HealthProbeCompletionKind.Failed) machine.ConfirmProbeFailure();
        }

        var snapshot = await PersistAsync(transition, attempt.Scope, machine, null,
            current ? reason : "Late probe observation recorded without changing the newer health state. " + reason,
            wasObserved ? previousState : null, machine.State != previousState, _timeProvider.GetUtcNow(),
            evidenceRedactedJson, cancellationToken, forceAudit: true, updateState: current,
            auditId: completionId).ConfigureAwait(false);
        return new(snapshot, current, current && kind == HealthProbeCompletionKind.ModelSucceeded);
    }
}
