using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LLMWorkGUI.Application.Health;

/// <summary>
/// Normalized health and recovery service. Health decisions use the normative
/// <see cref="HealthStateMachine"/> rehydrated from the persisted observation, and
/// every resulting transition is appended to the recovery audit before the snapshot is returned.
/// </summary>
public sealed partial class HealthCenterService : IHealthCenterService
{
    private readonly IHealthStateRepository _stateRepository;
    private readonly IHealthEventRepository _eventRepository;
    private readonly TimeProvider _timeProvider;
    private readonly HealthPolicy _policy;
    private readonly IHealthPolicyProvider? _policyProvider;
    private readonly IApplicationInstanceGuard? _instanceGuard;
    private readonly ILogger<HealthCenterService> _logger;
    private readonly IHealthTransitionStore? _transitionStore;
    private readonly IRouteRepository? _routes;
    // One bounded gate for the singleton's short repository transitions. Provider/network work never
    // runs inside this gate, and cascades/notifications leave it before calling back into the service.
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    // Only currently admitted probes are retained. Terminal observations or intervening mutations
    // remove entries; arbitrary refused scopes never create a retained resource.
    private readonly Dictionary<HealthScope, string> _activeProbeAttempts = new();

    public HealthCenterService(
        IHealthStateRepository stateRepository,
        IHealthEventRepository eventRepository,
        TimeProvider? timeProvider = null,
        HealthPolicy? policy = null,
        IHealthPolicyProvider? policyProvider = null,
        IApplicationInstanceGuard? instanceGuard = null,
        ILogger<HealthCenterService>? logger = null,
        IHealthTransitionStore? transitionStore = null,
        IRouteRepository? routes = null)
    {
        ArgumentNullException.ThrowIfNull(stateRepository);
        ArgumentNullException.ThrowIfNull(eventRepository);

        _stateRepository = stateRepository;
        _eventRepository = eventRepository;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _policy = policy ?? new HealthPolicy();
        _policyProvider = policyProvider;
        _instanceGuard = instanceGuard;
        _logger = logger ?? NullLogger<HealthCenterService>.Instance;
        _transitionStore = transitionStore;
        _routes = routes;
    }

    /// <summary>
    /// Raised for every audited transition. Declared on the contract; raised from the single persistence
    /// choke point in <c>PersistAsync</c>, so every health command reaches subscribers through one place.
    /// </summary>
    public event EventHandler<HealthTransitionEventArgs>? TransitionRecorded;

    /// <summary>
    /// The policy that governs this scope. The per-scope provider wins when it is configured, so the
    /// breaker parameters can be tuned per provider without changing the machine itself.
    /// </summary>
    private HealthPolicy ResolvePolicy(HealthScope scope) =>
        _policyProvider?.GetPolicy(scope) ?? _policy;

    public async Task<HealthSnapshot> GetSnapshotAsync(
        HealthScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // State and pending must share a snapshot: completing a queued block between independent
        // reads must not combine the old Healthy state with the newly empty queue.
        var (record, pending) = _transitionStore is IHealthAuthenticationFanoutStore fanout
            ? await fanout.ReadSnapshotAsync(scope, cancellationToken).ConfigureAwait(false)
            : (await _stateRepository.GetAsync(scope.ScopeType, scope.ScopeId, cancellationToken).ConfigureAwait(false), false);

        if (pending)
            return (record is null ? CreateDefaultSnapshot(scope) : ToSnapshot(scope, record)) with
                { AuthenticationFanoutPending = true, ErrorClass = HealthErrorClass.AuthenticationOrRefresh };

        if (record is null)
        {
            return CreateDefaultSnapshot(scope);
        }

        // A cooldown that has elapsed is advanced on read, because nothing else drives the expiry:
        // without it a scope would stay CoolingDown forever and never reach the probe it owes.
        if (MayPersistHealth && HasElapsedCooldown(record))
        {
            return await ExpireCooldownAsync(scope, cancellationToken).ConfigureAwait(false);
        }

        return ToSnapshot(scope, record);
    }

    public async Task<IReadOnlyList<HealthSnapshot>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await _stateRepository.ListAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = new List<HealthSnapshot>(records.Count);

        if (_transitionStore is IHealthAuthenticationFanoutStore pendingStore)
        {
            var pending = await pendingStore.ListPendingAsync(cancellationToken).ConfigureAwait(false);
            var scopes = records.Select(r => new HealthScope(r.ScopeType, r.ScopeId))
                .Concat(pending.Select(p => p.Scope))
                .Concat(pending.SelectMany(p => p.ProtectedAccountIds).Select(HealthScope.ForAccount))
                .Concat(pending.Where(p => p.AccountId is not null).Select(p => HealthScope.ForAccount(p.AccountId!)))
                .Distinct();
            foreach (var scope in scopes)
                snapshots.Add(await GetSnapshotAsync(scope, cancellationToken).ConfigureAwait(false));
        }
        else
        {
            foreach (var record in records)
            {
                var scope = new HealthScope(record.ScopeType, record.ScopeId);
                snapshots.Add(MayPersistHealth && HasElapsedCooldown(record)
                    ? await ExpireCooldownAsync(scope, cancellationToken).ConfigureAwait(false)
                    : ToSnapshot(scope, record));
            }
        }

        return snapshots;
    }

    /// <summary>
    /// True when a persisted observation is cooling down with a deadline that has already passed. The
    /// transition itself stays in <see cref="ExpireCooldownAsync"/>, so the audit and the state are
    /// always written together.
    /// </summary>
    private bool HasElapsedCooldown(HealthStateRecord record) =>
        record.State == HealthState.CoolingDown &&
        record.CooldownUntil is { } until &&
        until <= _timeProvider.GetUtcNow();

    public Task<HealthFailureOutcome> ReportFailureAsync(
        HealthScope scope,
        HealthErrorClass errorClass,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        ReportFailureCoreAsync(scope, errorClass, reason, propagateToParentAccount: true, cancellationToken);

    private async Task<HealthFailureOutcome> ReportFailureCoreAsync(HealthScope scope,
        HealthErrorClass errorClass, string? reason, bool propagateToParentAccount,
        CancellationToken cancellationToken, string? auditId = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (errorClass == HealthErrorClass.AuthenticationOrRefresh && _transitionStore is IHealthAuthenticationFanoutStore)
            return (await ReportAuthenticationFailuresAsync([scope], reason, cancellationToken).ConfigureAwait(false))[0];

        // The account is the admission barrier for every sibling route. Commit it before
        // projecting the failure onto the route, so cancellation or a child audit failure cannot
        // leave the parent routable after a proven authentication failure.
        if (propagateToParentAccount && scope.ScopeType == HealthScope.RouteScopeType &&
            errorClass == HealthErrorClass.AuthenticationOrRefresh && _routes is not null)
        {
            // Resolve an actual persisted route; an opaque/composite-looking identifier is not an
            // account binding. A missing model/profile does not erase an existing account identity.
            var assignment = await _routes.GetAssignmentAsync(scope.ScopeId, cancellationToken).ConfigureAwait(false);
            if (assignment is not null && !assignment.MissingIdentities.Contains(WorkflowRouteIdentity.Account))
                await ReportFailureCoreAsync(HealthScope.ForAccount(assignment.Route.Binding.AccountId),
                    errorClass, "An authentication failure on a persisted route blocked its resolved account.",
                    propagateToParentAccount: false, cancellationToken).ConfigureAwait(false);
        }

        HealthSnapshot snapshot;
        bool counted;
        bool stateChanged;
        var isAmbiguous = errorClass == HealthErrorClass.UnknownOrAmbiguousCompletion;
        await using (var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = _timeProvider.GetUtcNow();
            var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);
            if (auditId is not null && await _eventRepository.FindByIdAsync(scope.ScopeType, scope.ScopeId,
                auditId, cancellationToken).ConfigureAwait(false) is not null)
            {
                var existing = await _stateRepository.GetAsync(scope.ScopeType, scope.ScopeId, cancellationToken)
                    .ConfigureAwait(false);
                return new HealthFailureOutcome
                {
                    Snapshot = existing is null ? CreateDefaultSnapshot(scope) : ToSnapshot(scope, existing),
                    CountedByBreaker = false,
                    StateChanged = false,
                    AutomaticRetryAllowed = false
                };
            }
            counted = machine.RecordFailure(errorClass, now);
            stateChanged = machine.State != previousState;

            // An excluded class still produces an audit entry when it changed nothing, so the operator
            // can see that the failure was deliberately not charged to the breaker.
            snapshot = await PersistAsync(
                    transition,
                    scope,
                    machine,
                    errorClass,
                    reason ?? BuildFailureReason(errorClass, counted),
                    wasObserved ? previousState : null,
                    stateChanged,
                    now,
                    evidenceRedactedJson: null,
                    cancellationToken, auditId: auditId)
                .ConfigureAwait(false);
        }

        await CascadeAuthenticationBlockToModelRoutesAsync(scope, errorClass, snapshot, cancellationToken)
            .ConfigureAwait(false);

        return new HealthFailureOutcome
        {
            Snapshot = snapshot,
            CountedByBreaker = counted,
            StateChanged = stateChanged,
            IsAmbiguous = isAmbiguous,

            // An ambiguous completion may already have applied changes, so retrying it automatically
            // could duplicate work. Only an unambiguous, still-routable scope may be retried.
            AutomaticRetryAllowed = counted && !isAmbiguous && snapshot.IsRoutable
        };
    }

    /// <summary>
    /// ТЗ §6.10: an authorization failure blocks every route of the account immediately. Reporting it on
    /// the account scope therefore also blocks each of the account's model-route scopes, because the
    /// router and the sticky-send gate read the route scope as well as the account scope. Routes that
    /// already left routing are left untouched: their own observation is already audited, and repeating
    /// the account failure on them would only duplicate that record.
    /// </summary>
    private async Task CascadeAuthenticationBlockToModelRoutesAsync(
        HealthScope scope,
        HealthErrorClass errorClass,
        HealthSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (scope.ScopeType != HealthScope.AccountScopeType ||
            errorClass != HealthErrorClass.AuthenticationOrRefresh ||
            snapshot.IsRoutable)
        {
            return;
        }

        var accountId = scope.ScopeId;
        var observed = await ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var route in observed)
        {
            if (!route.Scope.TryGetModelRoute(out var routeAccount, out _) ||
                !string.Equals(routeAccount, accountId, StringComparison.Ordinal) ||
                !route.IsRoutable)
            {
                continue;
            }

            await ReportFailureCoreAsync(
                    route.Scope,
                    HealthErrorClass.AuthenticationOrRefresh,
                    $"An authorization failure (401/403) was observed for account '{accountId}', so route " +
                    $"'{route.Scope.ScopeId}' was blocked (ТЗ §6.10).",
                    propagateToParentAccount: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<HealthSnapshot> ReportSuccessAsync(
        HealthScope scope,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);

        // A success is not a recovery (ТЗ §6.10): a Recovering or ForcedEnabled scope may only return to
        // Healthy through a passing pinned probe, so an ordinary successful turn must never promote it.
        // Only the current admitted pinned-model completion confirms a recovery.
        var stateChanged = machine.State != previousState;

        return await PersistAsync(
                transition,
                scope,
                machine,
                errorClass: null,
                reason ?? "A successful execution was observed.",
                wasObserved ? previousState : null,
                stateChanged,
                now,
                evidenceRedactedJson: null,
                cancellationToken, retireProbeAttempt: false)
            .ConfigureAwait(false);
    }

    public async Task<HealthSnapshot> ExpireCooldownAsync(
        HealthScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var transition = await BeginTransitionAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var (machine, previousState, wasObserved) = await RestoreAsync(scope, cancellationToken).ConfigureAwait(false);

        // ExpireCooldown moves to ProbeRequired, never straight to Healthy.
        var expired = machine.ExpireCooldown(now);

        if (!expired)
        {
            return ToSnapshot(scope, machine, now);
        }

        return await PersistAsync(
                transition,
                scope,
                machine,
                errorClass: null,
                "The cooldown elapsed, so the route now requires a probe before it can recover.",
                wasObserved ? previousState : null,
                stateChanged: true,
                now,
                evidenceRedactedJson: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildFailureReason(HealthErrorClass errorClass, bool counted) =>
        counted
            ? $"A {errorClass} failure was counted by the circuit breaker."
            : $"A {errorClass} outcome was observed and deliberately not counted by the circuit breaker.";

    private bool MayPersistHealth =>
        _instanceGuard is null || (_instanceGuard.IsPrimarySupervisor && !_instanceGuard.IsViewOnly);

    private async Task<TransitionLease> BeginTransitionAsync(CancellationToken cancellationToken)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _instanceGuard?.EnsureSupervisorPermitted();
            return new TransitionLease(this);
        }
        catch
        {
            _transitionGate.Release();
            throw;
        }
    }

    private sealed class TransitionLease(HealthCenterService owner) : IAsyncDisposable
    {
        private bool _disposed;
        public List<HealthTransitionEventArgs> Notifications { get; } = [];

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            owner._transitionGate.Release();
            // Subscribers may synchronously read or issue another health command. They must never
            // execute under the transition gate; the corresponding state/audit are already durable.
            foreach (var notification in Notifications)
                owner.PublishTransition(notification);
            return ValueTask.CompletedTask;
        }
    }

    private void PublishTransition(HealthTransitionEventArgs notification)
    {
        var handlers = TransitionRecorded;
        if (handlers is null) return;
        foreach (EventHandler<HealthTransitionEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, notification);
            }
            catch (Exception exception)
            {
                // This projection callback runs after both durable writes. It cannot roll them back,
                // prevent the authentication cascade, or turn a committed command into a retryable
                // failure. Log only the type, never an arbitrary subscriber's message/content.
                try
                {
                    _logger.LogWarning("A health transition subscriber failed ({ExceptionType}); the committed result was preserved.",
                        exception.GetType().Name);
                }
                catch (Exception)
                {
                    // A failing diagnostic sink must not invalidate the same committed operation.
                }
            }
        }
    }
}
