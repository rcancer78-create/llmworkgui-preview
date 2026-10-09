using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LLMWorkGUI.Application.Health;

public sealed partial class HealthCenterService
{
    public async Task<IReadOnlyList<HealthFailureOutcome>> ReportAuthenticationFailuresAsync(
        IReadOnlyList<HealthScope> scopes, string? reason = null, CancellationToken cancellationToken = default)
        => (await ReportAuthenticationFailuresCoreAsync(scopes, reason, null, null, cancellationToken).ConfigureAwait(false)).Outcomes;

    private async Task<(IReadOnlyList<HealthFailureOutcome> Outcomes, bool ProbeWasCurrent)> ReportAuthenticationFailuresCoreAsync(
        IReadOnlyList<HealthScope> scopes, string? reason, HealthProbeAttempt? probeAttempt, string? probeEvidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (_transitionStore is not IHealthAuthenticationFanoutStore store)
            throw new NotSupportedException("Durable authentication fan-out is not configured.");
        var requested = scopes.Distinct().ToArray();
        if (requested.Length == 0) return ([], false);
        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var currentProbe = probeAttempt is not null &&
            _activeProbeAttempts.TryGetValue(probeAttempt.Scope, out var activeId) && activeId == probeAttempt.Id;
        var targets = new Dictionary<HealthScope, string?>();
        foreach (var scope in requested)
        {
            ArgumentNullException.ThrowIfNull(scope);
            string? account = scope.ScopeType == HealthScope.AccountScopeType ? scope.ScopeId :
                scope.TryGetModelRoute(out var modelAccount, out _) ? modelAccount : null;
            if (scope.ScopeType == HealthScope.RouteScopeType && _routes is not null)
            {
                var assignment = await _routes.GetAssignmentAsync(scope.ScopeId, cancellationToken).ConfigureAwait(false);
                if (assignment is not null && !assignment.MissingIdentities.Contains(WorkflowRouteIdentity.Account))
                    account = assignment.Route.Binding.AccountId;
            }
            targets[scope] = account;
            if (account is not null) targets[HealthScope.ForAccount(account)] = account;
        }
        var accounts = targets.Values.Where(a => a is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var record in await _stateRepository.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var scope = new HealthScope(record.ScopeType, record.ScopeId);
            if (scope.TryGetModelRoute(out var account, out _) && accounts.Contains(account) && ToSnapshot(scope, record).IsRoutable)
                targets[scope] = account;
        }
        var now = _timeProvider.GetUtcNow();
        var protectedAccounts = Array.AsReadOnly(accounts.Select(account => account!).ToArray());
        var evidence = JsonSerializer.Serialize(new { authenticationObservedAt = now, probeAttemptId = probeAttempt?.Id,
            probeEvidence = probeEvidence is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(probeEvidence) });
        var projections = targets.Select(t => new HealthAuthenticationProjection(Guid.NewGuid().ToString("N"),
            t.Key, t.Value, reason ?? "An authentication failure blocks this scope and its account routes.", now)
            { ProtectedAccountIds = protectedAccounts, EvidenceRedactedJson = evidence }).ToArray();
        await store.EnqueueAsync(projections, cancellationToken).ConfigureAwait(false);
        // The durable enqueue is the first commit and the admission barrier. Caller cancellation must
        // not abandon the remaining accounts. A bounded attempt may leave visible, persisted work.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await ProjectAuthenticationAsync(transition, projections, budget.Token).ConfigureAwait(false);
        if (completed.Count != projections.Length)
            throw new InvalidOperationException("Authentication blocking is incomplete; pending scopes remain blocked and will be retried.");
        return (requested.Select(scope => completed[scope]).ToArray(), currentProbe);
    }

    public async Task<int> RetryAuthenticationFanoutAsync(CancellationToken cancellationToken = default)
    {
        if (_transitionStore is not IHealthAuthenticationFanoutStore store) return 0;
        if (!MayPersistHealth) return (await store.ListPendingAsync(cancellationToken).ConfigureAwait(false)).Count;
        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var pending = await store.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        await ProjectAuthenticationAsync(transition, pending, budget.Token).ConfigureAwait(false);
        using var readBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return (await store.ListPendingAsync(readBudget.Token).ConfigureAwait(false)).Count;
    }

    private async Task<Dictionary<HealthScope, HealthFailureOutcome>> ProjectAuthenticationAsync(
        TransitionLease transition,
        IReadOnlyList<HealthAuthenticationProjection> projections, CancellationToken cancellationToken)
    {
        var completed = new Dictionary<HealthScope, HealthFailureOutcome>();
        // Account barriers first, independent of backend/child repository failures.
        foreach (var projection in projections.OrderBy(p => p.Scope.ScopeType == HealthScope.AccountScopeType ? 0 : 1))
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                var now = _timeProvider.GetUtcNow();
                var (machine, previous, observed) = await RestoreAsync(projection.Scope, cancellationToken).ConfigureAwait(false);
                var counted = machine.RecordFailure(HealthErrorClass.AuthenticationOrRefresh, now);
                var changed = previous != machine.State;
                var snapshot = await PersistAsync(transition, projection.Scope, machine, HealthErrorClass.AuthenticationOrRefresh,
                    projection.Reason, observed ? previous : null, changed, now,
                    projection.EvidenceRedactedJson ?? JsonSerializer.Serialize(new { authenticationObservedAt = projection.ObservedAt }), cancellationToken,
                    authenticationProjectionId: projection.Id, auditId: projection.Id).ConfigureAwait(false);
                completed[projection.Scope] = new HealthFailureOutcome { Snapshot = snapshot, CountedByBreaker = counted,
                    StateChanged = changed, AutomaticRetryAllowed = false };
            }
            catch (Exception error)
            {
                var reconciled = await TryReconcileAuthenticationProjectionAsync(transition, projection).ConfigureAwait(false);
                if (reconciled is not null)
                {
                    completed[projection.Scope] = reconciled;
                    continue;
                }
                _logger.LogWarning("Authentication projection completion could not be confirmed for {ScopeType} ({ErrorType}).",
                    projection.Scope.ScopeType, error.GetType().Name);
            }
        }
        return completed;
    }

    private async Task<HealthFailureOutcome?> TryReconcileAuthenticationProjectionAsync(
        TransitionLease transition, HealthAuthenticationProjection projection)
    {
        if (_transitionStore is not IHealthAuthenticationFanoutStore store) return null;
        // Save may have committed before its acknowledgement faulted. The admitted projection's
        // audit ID, never matching reason text, identifies that one observation. Its owned bounded
        // read token remains usable even when the failed write token was cancelled.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var audit = await _eventRepository.FindByIdAsync(projection.Scope.ScopeType, projection.Scope.ScopeId,
                projection.Id, budget.Token).ConfigureAwait(false);
            if (audit is null || audit.ErrorClass != HealthErrorClass.AuthenticationOrRefresh) return null;
            var (record, pending) = await store.ReadSnapshotAsync(projection.Scope, budget.Token).ConfigureAwait(false);
            if (record is null) return null;
            var stillOwnsResult = record.UpdatedAt == audit.OccurredAt && record.State == audit.NewState;
            if (stillOwnsResult)
            {
                _activeProbeAttempts.Remove(projection.Scope);
                transition.Notifications.Add(new HealthTransitionEventArgs(projection.Scope, audit.PreviousState,
                    audit.NewState, record.ErrorClass, audit.Reason, audit.OccurredAt));
            }
            return new HealthFailureOutcome
            {
                Snapshot = ToSnapshot(projection.Scope, record) with { AuthenticationFanoutPending = pending },
                CountedByBreaker = true,
                StateChanged = audit.PreviousState != audit.NewState,
                AutomaticRetryAllowed = false
            };
        }
        catch (Exception error)
        {
            _logger.LogWarning("Authentication acknowledgement reconciliation failed for {ScopeType} ({ErrorType}).",
                projection.Scope.ScopeType, error.GetType().Name);
            return null;
        }
    }
}
