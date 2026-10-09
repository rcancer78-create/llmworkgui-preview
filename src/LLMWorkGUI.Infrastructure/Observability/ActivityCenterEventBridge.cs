using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Infrastructure.Observability;

/// <summary>
/// Connects the product's own event sources to the Phase 11 Activity Center.
/// <para>
/// Until this bridge existed, not one shipped code path called
/// <see cref="IActivityCenterService.Append"/>: the journal existed, the screen existed, and the only
/// thing that ever produced activity events was a load driver. That is a real product defect rather than
/// a measurement artefact - an operator opening the Activity Center of a running application would have
/// seen an empty screen - so this bridge subscribes to the notification seams the product already has
/// and feeds them through the same ingestion boundary everything else uses.
/// </para>
/// <para>
/// Only services that already raise a notification are subscribed. Nothing here polls, samples or
/// synthesizes: each handler maps one real notification the product already raised, and each one shows up
/// in the journal as a <see cref="ActivityEventKind"/> the screen already knows how to render. A source
/// with no notification seam stays absent from <see cref="Sources"/> rather than being faked with a timer.
/// </para>
/// <para>
/// The bridge owns no threads and no timers. Every handler is a straight call into the ingestion boundary,
/// which is synchronous, redacts, counts and must never throw back into its raiser: an Activity Center
/// failure may not break the health machine or the Cursor turn it was reporting on.
/// </para>
/// </summary>
public sealed class ActivityCenterEventBridge : IDisposable
{
    private readonly IActivityCenterService _activity;
    private readonly TimeProvider _timeProvider;
    private readonly List<string> _sources = new();
    private readonly List<Action> _unsubscribe = new();
    private bool _isDisposed;
    private long _failedIngestions;
    public long FailedIngestions => Interlocked.Read(ref _failedIngestions);

    public ActivityCenterEventBridge(
        IActivityCenterService activity,
        IEnumerable<object?> sources,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(sources);

        _activity = activity;
        _timeProvider = timeProvider ?? TimeProvider.System;

        foreach (var source in sources)
        {
            switch (source)
            {
                case IHealthCenterService health:
                    health.TransitionRecorded += OnHealthTransition;
                    _unsubscribe.Add(() => health.TransitionRecorded -= OnHealthTransition);
                    _sources.Add("IHealthCenterService.TransitionRecorded");
                    break;

                case ICursorAcpSessionLifecycleService cursor:
                    cursor.TurnStateChanged += OnCursorTurnStateChanged;
                    _unsubscribe.Add(() => cursor.TurnStateChanged -= OnCursorTurnStateChanged);
                    cursor.PermissionRequested += OnCursorPermissionRequested;
                    _unsubscribe.Add(() => cursor.PermissionRequested -= OnCursorPermissionRequested);
                    _sources.Add("ICursorAcpSessionLifecycleService.TurnStateChanged");
                    _sources.Add("ICursorAcpSessionLifecycleService.PermissionRequested");
                    break;

                case ILongRunningExecutionService longRunning:
                    longRunning.ExecutionStalled += OnExecutionStalled;
                    _unsubscribe.Add(() => longRunning.ExecutionStalled -= OnExecutionStalled);
                    _sources.Add("ILongRunningExecutionService.ExecutionStalled");
                    break;

                case IQuotaRefreshScheduler quotas:
                    quotas.QuotaRefreshed += OnQuotaRefreshed;
                    _unsubscribe.Add(() => quotas.QuotaRefreshed -= OnQuotaRefreshed);
                    _sources.Add("IQuotaRefreshScheduler.QuotaRefreshed");
                    break;
            }
        }

        if (_sources.Count == 0)
        {
            return;
        }

        // A startup record, on the product's own journal, naming the seams that are wired. Without a
        // producer this bridge would be an empty class, and an empty class reads exactly like a bridge
        // that was never connected.
        Append(
            "activity-center:ingestion-wired",
            "Activity Center wired to the product event stream",
            $"Subscribed seams: {string.Join("; ", _sources)}",
            ActivityEventState.Completed,
            ActivityEventKind.System);
    }

    /// <summary>Notification seams this bridge is actually subscribed to, in report form.</summary>
    public IReadOnlyList<string> Sources => _sources;

    /// <summary>Health transitions observed since startup.</summary>
    public long ObservedHealthTransitions { get; private set; }

    /// <summary>Cursor ACP turn-state transitions observed since startup.</summary>
    public long ObservedCursorTurnTransitions { get; private set; }

    /// <summary>Stalled long-running executions observed since startup.</summary>
    public long ObservedStalledExecutions { get; private set; }

    /// <summary>Quota refreshes observed since startup.</summary>
    public long ObservedQuotaRefreshes { get; private set; }

    /// <summary>Notifications this bridge has handled in total.</summary>
    public long TotalObserved =>
        ObservedHealthTransitions
        + ObservedCursorTurnTransitions
        + ObservedStalledExecutions
        + ObservedQuotaRefreshes;

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        foreach (var unsubscribe in _unsubscribe)
        {
            unsubscribe();
        }

        _unsubscribe.Clear();
    }

    private void OnHealthTransition(object? sender, HealthTransitionEventArgs transition)
    {
        if (_isDisposed)
        {
            return;
        }

        ObservedHealthTransitions++;

        var detail = string.Join(
            " ",
            transition.PreviousState is { } previous ? $"was {previous};" : string.Empty,
            $"became {transition.NewState};",
            transition.ErrorClass is { } errorClass ? $"error class {errorClass};" : string.Empty,
            transition.Reason ?? string.Empty).Trim();

        Append(
            $"health:{transition.ScopeId}:{transition.OccurredAt.UtcTicks}:{transition.NewState}",
            $"Health {transition.ScopeId}: {transition.NewState}",
            detail,
            ActivityEvent.MapHealthState(transition.NewState),
            ActivityEventKind.Health,
            occurredAt: transition.OccurredAt);
    }

    private void OnCursorTurnStateChanged(object? sender, CursorAcpTurnStateChangedEventArgs change)
    {
        if (_isDisposed)
        {
            return;
        }

        ObservedCursorTurnTransitions++;

        // The turn state is a projection of the Cursor ACP execution, so this is an execution event with
        // the native session id as its provenance. The event id carries the state as well as the session,
        // so a turn that visits several states keeps every observation instead of collapsing onto one row.
        Append(
            $"cursor-acp:{change.SessionId}:{change.State}",
            $"Cursor ACP turn {change.State}",
            $"Session {change.SessionId} is in state {change.State}.",
            MapTurnState(change.State),
            ActivityEventKind.Execution,
            sessionId: change.SessionId,
            executionId: change.SessionId);
    }

    private void OnCursorPermissionRequested(object? sender, CursorAcpStreamEvent.PermissionRequest request)
    {
        if (_isDisposed)
        {
            return;
        }

        Append(
            $"cursor-acp-permission:{request.RequestId}",
            "Cursor ACP requested a permission",
            $"Request {request.RequestId}: {request.Description}",
            ActivityEventState.Warning,
            ActivityEventKind.UserAction,
            sessionId: request.SessionId,
            executionId: request.SessionId);
    }

    private void OnExecutionStalled(object? sender, LongRunningExecutionEventArgs stall)
    {
        if (_isDisposed)
        {
            return;
        }

        ObservedStalledExecutions++;

        Append(
            $"execution-stalled:{stall.Snapshot.ExecutionId}",
            "Long-running execution stalled",
            stall.Report.Summary,
            ActivityEventState.Warning,
            ActivityEventKind.System,
            executionId: stall.Snapshot.ExecutionId);
    }

    private void OnQuotaRefreshed(object? sender, QuotaRefreshedEventArgs refreshed)
    {
        if (_isDisposed)
        {
            return;
        }

        ObservedQuotaRefreshes++;

        Append(
            $"quota-refreshed:{refreshed.AccountId}:{_timeProvider.GetUtcNow().UtcTicks}",
            refreshed.IsSuccess
                ? $"Quota refreshed for account {refreshed.AccountId}"
                : $"Quota refresh failed for account {refreshed.AccountId}",
            refreshed.IsSuccess
                ? $"Provider profile {refreshed.ProviderProfileId} reported a fresh snapshot."
                : refreshed.Error ?? "No error detail was reported.",
            refreshed.IsSuccess ? ActivityEventState.Completed : ActivityEventState.Warning,
            ActivityEventKind.Health);
    }

    private void Append(
        string id,
        string title,
        string description,
        ActivityEventState state,
        ActivityEventKind kind,
        DateTimeOffset? occurredAt = null,
        string? sessionId = null,
        string? executionId = null)
    {
        try
        {
            _activity.Append(new ActivityEvent(
                id + ":" + Guid.NewGuid().ToString("N"),
                occurredAt ?? _timeProvider.GetUtcNow(),
                kind,
                ActivityRoleNames.System,
                state,
                ActivityEventSource.Native,
                title,
                description,
                sessionId,
                executionId));
        }
        catch (Exception)
        {
            // A failed sink cannot safely report its own failure through that same sink.
            // Keep an inspectable loss counter without throwing into the backend producer.
            Interlocked.Increment(ref _failedIngestions);
        }
    }


    private static ActivityEventState MapTurnState(CursorAcpTurnState state) => state switch
    {
        CursorAcpTurnState.Queued or CursorAcpTurnState.Starting or CursorAcpTurnState.SessionConfirmed
            or CursorAcpTurnState.Running => ActivityEventState.Running,
        CursorAcpTurnState.Succeeded => ActivityEventState.Completed,
        CursorAcpTurnState.Cancelled or CursorAcpTurnState.Cancelling => ActivityEventState.Cancelled,
        CursorAcpTurnState.WaitingApproval => ActivityEventState.Warning,
        _ => ActivityEventState.Failed
    };
}
