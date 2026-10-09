using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Activity Monitor of a workflow run (ROADMAP Phase 10D). The role schema is built from the roles
/// declared by the active workflow version and from the transitions and observable run projections the
/// selected run actually produced; the order is only a layout, never a built-in mandatory process. Node
/// statuses cover exactly the five required states, parallel read-only turns are marked on every
/// actually working node, Codex and AGY appear only as observed star-cliproxy route evidence of the
/// assigned role, and every field the domain did not report is displayed as "Not reported" instead of
/// being invented. All data comes from <see cref="ObservableRunProjection"/>, <see cref="ActivityTimeline"/>,
/// <see cref="RoleTransferEvidence"/> and <see cref="WorkflowRun"/> — no external script, background HTTP
/// server or log parsing is involved.
/// </summary>
public sealed class WorkflowActivityMonitorViewModel : ObservableObject
{
    public const string NotReportedPlaceholder = ObservableRunProjection.NotReportedPlaceholder;

    public const string ParallelReadOnlyLabel = "parallel read-only";

    public const int DefaultHistoryWindowMinutes = 60;

    private readonly IWorkflowRunTimelineService _timelineService;
    private readonly RoleTransferEvidenceProjector _transferEvidenceProjector;
    private readonly TimeProvider _timeProvider;

    private WorkflowVersion? _activeVersion;
    private WorkflowRun? _run;
    private IReadOnlyList<ObservableRunProjection> _projections = Array.Empty<ObservableRunProjection>();
    private IReadOnlyList<RoleTransferEvidence> _transferEvidence = Array.Empty<RoleTransferEvidence>();
    private IReadOnlyList<string> _declaredRoles = Array.Empty<string>();
    private WorkflowActivityNodeViewModel? _selectedNode;
    private WorkflowActivityDrawerViewModel _drawer = WorkflowActivityDrawerViewModel.Closed;
    private int _historyWindowMinutes = DefaultHistoryWindowMinutes;
    private WorkflowMonitorPanelMode _activePanelMode = WorkflowMonitorPanelMode.Schema;

    public WorkflowActivityMonitorViewModel(
        IWorkflowRunTimelineService? timelineService = null,
        RoleTransferEvidenceProjector? transferEvidenceProjector = null,
        TimeProvider? timeProvider = null)
    {
        _timelineService = timelineService ?? new WorkflowRunTimelineService();
        _transferEvidenceProjector = transferEvidenceProjector ?? new RoleTransferEvidenceProjector();
        _timeProvider = timeProvider ?? TimeProvider.System;

        SelectPanelModeCommand = new RelayCommand(parameter => SelectPanelMode(ResolvePanelMode(parameter)));
    }

    /// <summary>
    /// The timeline service this monitor actually projects with. Exposed so the composition path can be
    /// proven to use the registered service instead of a privately constructed one.
    /// </summary>
    public IWorkflowRunTimelineService TimelineService => _timelineService;

    /// <summary>Role transfer evidence projector this monitor actually projects with.</summary>
    public RoleTransferEvidenceProjector TransferEvidenceProjector => _transferEvidenceProjector;


    public string Title => "Workflow Activity Monitor";

    public string Description =>
        "Role schema and observed transitions of the selected workflow run, with an evidence-backed details drawer.";

    public string SchemaNote =>
        "The schema is built from the roles declared by the active workflow version and from the transitions " +
        "and observable events the selected run actually produced. The order is only a layout, not a built-in " +
        "mandatory process (ROADMAP 10D).";

    public string RouteProvenanceNote =>
        "Codex and AGY are shown only as observed star-cliproxy route evidence of the assigned role, never as " +
        "fixed visual identities.";

    public string ConcurrencyNote =>
        "Parallel read-only turns are allowed and are marked only on actually working nodes whose observed "
        + "execution mode proves they do not take the writer lock.";


    public string MissingFieldPlaceholder => NotReportedPlaceholder;

    public ObservableCollection<WorkflowActivityNodeViewModel> Nodes { get; } = new();

    public ObservableCollection<WorkflowActivityHistoryItemViewModel> Activity { get; } = new();

    public bool HasRun => _run is not null;

    public bool HasNodes => Nodes.Count > 0;

    public bool HasActivity => Activity.Count > 0;

    public bool HasParallelReadOnlyActivity => Nodes.Any(node => node.IsParallelReadOnly);

    public string EmptyStateMessage => HasRun
        ? "The selected run declares no role node yet; no role schema is reported."
        : "No workflow run is selected: no role schema, transfer evidence or activity is reported.";

    public string RunSummaryDisplay => _run is null
        ? NotReportedPlaceholder
        : string.Join(
            " · ",
            _run.Id,
            _run.CurrentStageId,
            _run.CurrentRole,
            _run.State.ToString());

    public string RunStateDisplay => _run?.State.ToString() ?? NotReportedPlaceholder;

    public string CurrentStageDisplay => _run?.CurrentStageId ?? NotReportedPlaceholder;

    public string CurrentRoleDisplay => _run?.CurrentRole ?? NotReportedPlaceholder;

    public string RunSessionDisplay => _run?.SessionId ?? NotReportedPlaceholder;

    public string RunStartedAtDisplay => FormatTimestamp(_run?.StartedAtUtc);

    public string RunEndedAtDisplay => FormatTimestamp(_run?.EndedAtUtc);

    public bool IsRunTerminal => _run is not null && WorkflowRun.IsTerminalState(_run.State);

    public string ActiveVersionDisplay => _activeVersion is null
        ? NotReportedPlaceholder
        : string.Create(
            CultureInfo.InvariantCulture,
            $"v{_activeVersion.VersionNumber} ({_activeVersion.Id})");

    public string DeclaredRolesDisplay => _declaredRoles.Count == 0
        ? NotReportedPlaceholder
        : string.Join(", ", _declaredRoles);

    public string HistoryWindowDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"Last {HistoryWindowMinutes} min");

    public string ActivityWindowNote => string.Create(
        CultureInfo.InvariantCulture,
        $"Activity of the last {HistoryWindowMinutes} minutes.");

    /// <summary>Role transfer evidence the drawer is built from; exposed for tests and diagnostics.</summary>
    public IReadOnlyList<RoleTransferEvidence> TransferEvidence => _transferEvidence;

    public WorkflowActivityNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                Drawer = value is null
                    ? WorkflowActivityDrawerViewModel.Closed
                    : BuildDrawer(value);
                OnPropertyChanged(nameof(IsDrawerOpen));
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(IsDrawerPanelVisible));
        OnPropertyChanged(nameof(IsSchemaDrawerSplitterVisible));
        OnPropertyChanged(nameof(IsDrawerContentVisible));
                OnPropertyChanged(nameof(IsInspectorEmptyVisible));
            }
        }
    }

    public WorkflowActivityDrawerViewModel Drawer
    {
        get => _drawer;
        private set => SetProperty(ref _drawer, value);
    }

    public bool IsDrawerOpen => Drawer.IsOpen;

    public ICommand SelectPanelModeCommand { get; }

    /// <summary>
    /// The compact monitor surface currently shown. <see cref="WorkflowMonitorPanelMode.Schema"/> keeps the
    /// historical layout: the role schema stays visible and the details drawer opens next to it.
    /// </summary>
    public WorkflowMonitorPanelMode ActivePanelMode
    {
        get => _activePanelMode;
        private set
        {
            if (!SetProperty(ref _activePanelMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSchemaMode));
            OnPropertyChanged(nameof(IsActivityMode));
            OnPropertyChanged(nameof(IsInspectorMode));
            OnPropertyChanged(nameof(ActivePanelModeDisplay));
            OnPropertyChanged(nameof(IsDrawerPanelVisible));
            OnPropertyChanged(nameof(IsSchemaDrawerSplitterVisible));
            OnPropertyChanged(nameof(IsDrawerContentVisible));
            OnPropertyChanged(nameof(IsInspectorEmptyVisible));
        }
    }

    public bool IsSchemaMode => ActivePanelMode == WorkflowMonitorPanelMode.Schema;

    public bool IsActivityMode => ActivePanelMode == WorkflowMonitorPanelMode.Activity;

    public bool IsInspectorMode => ActivePanelMode == WorkflowMonitorPanelMode.Inspector;

    public string ActivePanelModeDisplay => ActivePanelMode switch
    {
        WorkflowMonitorPanelMode.Activity => "Поток активности",
        WorkflowMonitorPanelMode.Inspector => "Инспектор",
        _ => "Схема графа"
    };

    /// <summary>The drawer panel is visible in schema mode while open and always in inspector mode.</summary>
    public bool IsDrawerPanelVisible => Drawer.IsOpen || IsInspectorMode;

    /// <summary>The splitter only exists between the schema and an open drawer in schema mode.</summary>
    public bool IsSchemaDrawerSplitterVisible => IsSchemaMode && Drawer.IsOpen;

    /// <summary>The observed drawer content only exists while a node is selected.</summary>
    public bool IsDrawerContentVisible => Drawer.IsOpen;

    public bool IsInspectorEmptyVisible => IsInspectorMode && !Drawer.IsOpen;

    public bool HasSelectedNode => SelectedNode is not null;

    public string InspectorEmptyMessage =>
        "Ни один узел не выбран: инспектор не выдумывает данные. Выберите узел в режиме схемы.";

    public string ActivityModeNote =>
        "Поток активности показывает только наблюдаемые события выбранного run за заданное окно.";

    public string ActivityEmptyMessage => HasRun
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"No activity in the last {HistoryWindowMinutes} minutes.")
        : "No workflow run is selected: no activity is reported.";

    /// <summary>Switches the compact monitor surface without changing the observed run data.</summary>
    public void SelectPanelMode(WorkflowMonitorPanelMode mode)
    {
        ActivePanelMode = mode;

        if (mode == WorkflowMonitorPanelMode.Inspector && SelectedNode is null)
        {
            SelectedNode = Nodes.FirstOrDefault(node => node.IsCurrent) ?? Nodes.FirstOrDefault();
        }

        OnPropertyChanged(nameof(IsDrawerPanelVisible));
        OnPropertyChanged(nameof(IsSchemaDrawerSplitterVisible));
        OnPropertyChanged(nameof(IsDrawerContentVisible));
        OnPropertyChanged(nameof(IsInspectorEmptyVisible));
    }

    /// <summary>
    /// The history/activity window in minutes. Only activity actually observed within the window is
    /// listed; the value is guarded to at least one minute.
    /// </summary>
    public int HistoryWindowMinutes
    {
        get => _historyWindowMinutes;
        set
        {
            var guarded = Math.Max(1, value);

            if (SetProperty(ref _historyWindowMinutes, guarded))
            {
                OnPropertyChanged(nameof(HistoryWindowDisplay));
                OnPropertyChanged(nameof(ActivityWindowNote));
                RebuildActivity();
                RefreshDrawer();
            }
        }
    }

    /// <summary>
    /// Sets the active workflow version. Its declared roles become the leading part of the schema; when
    /// no run is loaded, no status is invented for them.
    /// </summary>
    public void LoadActiveVersion(WorkflowVersion? version)
    {
        _activeVersion = version;
        Rebuild();
    }

    /// <summary>
    /// Loads the selected run and its observable projections. Everything shown afterwards is derived
    /// from these two inputs plus the active version; nulls are reported as "Not reported".
    /// </summary>
    public void LoadRun(WorkflowRun? run, IReadOnlyList<ObservableRunProjection>? projections = null)
    {
        _run = run;
        _projections = projections is null ? Array.Empty<ObservableRunProjection>() : projections.ToArray();
        Rebuild();
    }

    public void ClearRun() => LoadRun(run: null, projections: null);

    public void Refresh() => Rebuild();

    private void Rebuild()
    {
        _declaredRoles = ParseDeclaredRoles(_activeVersion?.DeclaredRolesJson);
        _transferEvidence = _run is null
            ? Array.Empty<RoleTransferEvidence>()
            : _transferEvidenceProjector.Project(_run, _projections);

        var previousRole = _selectedNode?.RoleId;

        Nodes.Clear();

        if (_run is not null)
        {
            var roles = BuildRoleOrder(_run, _projections);

            for (var index = 0; index < roles.Count; index++)
            {
                Nodes.Add(BuildNode(
                    roles[index],
                    _run,
                    _projections,
                    isLast: index == roles.Count - 1));
            }
        }

        _selectedNode = null;
        Drawer = WorkflowActivityDrawerViewModel.Closed;

        OnPropertyChanged(nameof(SelectedNode));
        OnPropertyChanged(nameof(IsDrawerOpen));
        OnPropertyChanged(nameof(HasSelectedNode));
        OnPropertyChanged(nameof(IsDrawerPanelVisible));
                OnPropertyChanged(nameof(IsSchemaDrawerSplitterVisible));
        OnPropertyChanged(nameof(IsDrawerContentVisible));
        OnPropertyChanged(nameof(IsInspectorEmptyVisible));
        OnPropertyChanged(nameof(HasRun));
        OnPropertyChanged(nameof(HasNodes));
        OnPropertyChanged(nameof(HasParallelReadOnlyActivity));
        OnPropertyChanged(nameof(EmptyStateMessage));
        OnPropertyChanged(nameof(RunSummaryDisplay));
        OnPropertyChanged(nameof(RunStateDisplay));
        OnPropertyChanged(nameof(CurrentStageDisplay));
        OnPropertyChanged(nameof(CurrentRoleDisplay));
        OnPropertyChanged(nameof(RunSessionDisplay));
        OnPropertyChanged(nameof(RunStartedAtDisplay));
        OnPropertyChanged(nameof(RunEndedAtDisplay));
        OnPropertyChanged(nameof(IsRunTerminal));
        OnPropertyChanged(nameof(ActiveVersionDisplay));
        OnPropertyChanged(nameof(DeclaredRolesDisplay));

        RebuildActivity();

        if (previousRole is not null)
        {
            SelectedNode = Nodes.FirstOrDefault(node => RoleEquals(node.RoleId, previousRole));
        }
    }

    private void RebuildActivity()
    {
        Activity.Clear();

        foreach (var item in BuildActivityItems())
        {
            Activity.Add(item);
        }

        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(ActivityEmptyMessage));
        OnPropertyChanged(nameof(HistoryWindowDisplay));
        OnPropertyChanged(nameof(ActivityWindowNote));
    }

    private void RefreshDrawer()
    {
        if (_selectedNode is null)
        {
            return;
        }

        Drawer = BuildDrawer(_selectedNode);
        OnPropertyChanged(nameof(IsDrawerOpen));
    }

    private WorkflowActivityDrawerViewModel BuildDrawer(WorkflowActivityNodeViewModel node)
    {
        var evidence = _transferEvidence.FirstOrDefault(
            candidate => RoleEquals(candidate.ToRole, node.RoleId));

        return new WorkflowActivityDrawerViewModel(
            node,
            evidence,
            BuildActivityItems(),
            HistoryWindowMinutes);
    }

    private IReadOnlyList<WorkflowActivityHistoryItemViewModel> BuildActivityItems()
    {
        if (_run is null)
        {
            return Array.Empty<WorkflowActivityHistoryItemViewModel>();
        }

        var timeline = _timelineService.BuildTimeline(_run, _projections);
        var threshold = _timeProvider.GetUtcNow().AddMinutes(-HistoryWindowMinutes);

        return timeline.Items
            .Where(item => item.OccurredAtUtc >= threshold)
            .Select(item => new WorkflowActivityHistoryItemViewModel(item))
            .ToArray();
    }

    private IReadOnlyList<string> BuildRoleOrder(
        WorkflowRun run,
        IReadOnlyList<ObservableRunProjection> projections)
    {
        var roles = new List<string>();

        // The declared roles of the active version lead the schema; the observed roles follow. This is
        // a layout order only - it never turns the chain into a mandatory process.
        foreach (var declaredRole in _declaredRoles)
        {
            AddRole(roles, declaredRole);
        }

        foreach (var projection in projections
                     .OrderBy(projection => projection.LastActivityAtUtc)
                     .ThenBy(projection => projection.ExecutionId, StringComparer.Ordinal))
        {
            AddRole(roles, RoleTransferEvidenceProjector.ResolveRoleLabel(projection));
        }

        AddRole(roles, run.CurrentRole);

        foreach (var transition in run.Transitions)
        {
            foreach (var verdict in transition.ReviewerVerdicts)
            {
                AddRole(roles, verdict.ReviewerRole);
            }
        }

        return roles;
    }

    private static WorkflowActivityNodeViewModel BuildNode(
        string role,
        WorkflowRun run,
        IReadOnlyList<ObservableRunProjection> projections,
        bool isLast)
    {
        var matches = projections
            .Where(projection => RoleTransferEvidenceProjector.ProjectionMatchesRole(projection, role))
            .OrderBy(projection => projection.LastActivityAtUtc)
            .ThenBy(projection => projection.ExecutionId, StringComparer.Ordinal)
            .ToArray();

        var status = ResolveStatus(role, run, matches);

        var routeEvidence = matches
            .Select(projection => new WorkflowActivityRouteEvidenceViewModel(role, projection))
            .ToArray();

        return new WorkflowActivityNodeViewModel(
            role,
            status,
            isCurrent: RoleEquals(run.CurrentRole, role),
            isLast,
            activeTurnCount: matches.Count(projection => projection.IsActive),
            routeEvidence,
            lastActivityAtUtc: matches.Select(projection => (DateTimeOffset?)projection.LastActivityAtUtc).Max(),
            readOnlyActiveTurnCount: matches.Count(
                projection => projection.IsActive && projection.IsReadOnlyTurn));
    }

    /// <summary>
    /// Resolves the node status from the observed executions of the role. "Работает" (Running) and "Завис"
    /// (Stalled) require a matching active execution projection; the <see cref="WorkflowRunState"/> alone
    /// never marks a role as working, because a run state describes the workflow, not a turn.
    /// </summary>
    private static WorkflowActivityNodeStatus ResolveStatus(
        string role,
        WorkflowRun run,
        IReadOnlyList<ObservableRunProjection> matches)
    {
        var isCurrent = RoleEquals(run.CurrentRole, role);
        var activeProjection = matches
            .Where(projection => projection.IsActive)
            .OrderByDescending(projection => projection.LastActivityAtUtc)
            .FirstOrDefault();

        if (isCurrent && !WorkflowRun.IsTerminalState(run.State))
        {
            if (activeProjection is not null)
            {
                return MapExecutionState(activeProjection.State);
            }

            // No observed turn of this role exists, so the node is not working. A suspended run is
            // reported as stopped; a pending or running run without a turn waits.
            return run.State == WorkflowRunState.Suspended
                ? WorkflowActivityNodeStatus.Stopped
                : WorkflowActivityNodeStatus.Waiting;
        }

        if (isCurrent)
        {
            return run.State switch
            {
                WorkflowRunState.Completed => WorkflowActivityNodeStatus.Completed,
                _ => WorkflowActivityNodeStatus.Stopped
            };
        }

        var latestProjection = matches.LastOrDefault();

        return latestProjection is null
            ? WorkflowActivityNodeStatus.Waiting
            : MapExecutionState(latestProjection.State);
    }

    private static WorkflowActivityNodeStatus MapExecutionState(ExecutionState state) => state switch

    {
        ExecutionState.Succeeded => WorkflowActivityNodeStatus.Completed,
        ExecutionState.Failed or ExecutionState.TimedOut or ExecutionState.Cancelled or ExecutionState.Cancelling =>
            WorkflowActivityNodeStatus.Stopped,
        ExecutionState.Ambiguous or ExecutionState.RouteMismatch => WorkflowActivityNodeStatus.Stalled,
        ExecutionState.Queued or ExecutionState.WaitingApproval => WorkflowActivityNodeStatus.Waiting,
        ExecutionState.Starting or ExecutionState.SessionConfirmed or ExecutionState.Running =>
            WorkflowActivityNodeStatus.Running,
        _ => WorkflowActivityNodeStatus.Waiting
    };

    private static void AddRole(List<string> roles, string? role)
    {
        var candidate = role?.Trim();

        if (!string.IsNullOrEmpty(candidate)
            && !roles.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            roles.Add(candidate);
        }
    }

    private static bool RoleEquals(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("u", CultureInfo.InvariantCulture) ?? NotReportedPlaceholder;

    /// <summary>
    /// Reads the role names declared by the active workflow version itself. Malformed or absent JSON is
    /// reported as no declared roles instead of being guessed.
    /// </summary>
    private static IReadOnlyList<string> ParseDeclaredRoles(string? declaredRolesJson)
    {
        if (string.IsNullOrWhiteSpace(declaredRolesJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(declaredRolesJson);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var roles = new List<string>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    AddRole(roles, element.GetString());
                }
            }

            return roles;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static WorkflowMonitorPanelMode ResolvePanelMode(object? parameter) =>
        parameter switch
        {
            WorkflowMonitorPanelMode mode => mode,
            string text when Enum.TryParse<WorkflowMonitorPanelMode>(text, ignoreCase: true, out var parsed) =>
                parsed,
            _ => throw new ArgumentException(
                "Panel selection requires a WorkflowMonitorPanelMode value.",
                nameof(parameter))
        };
}

/// <summary>The compact surfaces of the consolidated Workflow Activity Monitor (ROADMAP Phase 11).</summary>
public enum WorkflowMonitorPanelMode
{
    Schema,
    Activity,
    Inspector
}

/// <summary>The five required node states of the Activity Monitor (ROADMAP Phase 10D).</summary>
public enum WorkflowActivityNodeStatus
{
    Running,
    Stalled,
    Stopped,
    Completed,
    Waiting
}

/// <summary>One role node of the schema. Its data is derived from the run and its projections only.</summary>
public sealed class WorkflowActivityNodeViewModel
{
    public WorkflowActivityNodeViewModel(
        string roleId,
        WorkflowActivityNodeStatus status,
        bool isCurrent,
        bool isLast,
        int activeTurnCount,
        IReadOnlyList<WorkflowActivityRouteEvidenceViewModel> routeEvidence,
        DateTimeOffset? lastActivityAtUtc,
        int readOnlyActiveTurnCount = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(routeEvidence);

        RoleId = roleId;
        Status = status;
        IsCurrent = isCurrent;
        IsLast = isLast;
        ActiveTurnCount = Math.Max(0, activeTurnCount);
        ReadOnlyActiveTurnCount = Math.Max(0, Math.Min(readOnlyActiveTurnCount, ActiveTurnCount));
        RouteEvidence = routeEvidence;
        LastActivityDisplay = lastActivityAtUtc?.ToString("u", CultureInfo.InvariantCulture)
            ?? WorkflowActivityMonitorViewModel.NotReportedPlaceholder;

        var observedRoutes = routeEvidence
            .Select(evidence => evidence.ObservedRouteDisplay)
            .Where(display => !string.Equals(
                display,
                WorkflowActivityMonitorViewModel.NotReportedPlaceholder,
                StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        RouteSummaryDisplay = observedRoutes.Length == 0
            ? WorkflowActivityMonitorViewModel.NotReportedPlaceholder
            : string.Join(" | ", observedRoutes);

        ObservedModels = routeEvidence
            .Select(evidence => evidence.ModelDisplay)
            .Where(display => !string.Equals(
                display,
                WorkflowActivityMonitorViewModel.NotReportedPlaceholder,
                StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        ObservedAccounts = routeEvidence
            .Select(evidence => evidence.AccountDisplay)
            .Where(display => !string.Equals(
                display,
                WorkflowActivityMonitorViewModel.NotReportedPlaceholder,
                StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        ModelSummaryDisplay = ObservedModels.Count == 0
            ? WorkflowActivityMonitorViewModel.NotReportedPlaceholder
            : string.Join(" | ", ObservedModels);

        AccountSummaryDisplay = ObservedAccounts.Count == 0
            ? WorkflowActivityMonitorViewModel.NotReportedPlaceholder
            : string.Join(" | ", ObservedAccounts);
    }

    public string RoleId { get; }


    public string DisplayName => RoleId;

    public WorkflowActivityNodeStatus Status { get; }

    public string StatusDisplay => Status switch
    {
        WorkflowActivityNodeStatus.Running => "Работает",
        WorkflowActivityNodeStatus.Stalled => "Завис",
        WorkflowActivityNodeStatus.Stopped => "Остановлен",
        WorkflowActivityNodeStatus.Completed => "Готово",
        _ => "Ждёт"
    };

    public string StatusDetail => Status.ToString();

    public bool IsCurrent { get; }

    public bool IsLast { get; }

    public string TransitionArrowDisplay => IsLast ? string.Empty : "→";

    public int ActiveTurnCount { get; }

    public bool HasActiveTurns => ActiveTurnCount > 0;

    /// <summary>
    /// Active turns of this role whose observed execution mode proved they do not take the writer lock.
    /// Writer turns are never counted here, so two concurrent writers are not reported as parallel
    /// read-only activity.
    /// </summary>
    public int ReadOnlyActiveTurnCount { get; }

    /// <summary>
    /// True only when at least two active same-role turns are proven read-only. Two active turns alone are
    /// not enough, and two writer turns never qualify.
    /// </summary>
    public bool IsParallelReadOnly => ReadOnlyActiveTurnCount >= 2;

    /// <summary>
    /// True only when at least one active same-role turn was actually observed for this node. A run state,
    /// a declared role or a terminal turn never makes a node look like it is working.
    /// </summary>
    public bool IsWorking =>
        HasActiveTurns
        && Status is WorkflowActivityNodeStatus.Running or WorkflowActivityNodeStatus.Stalled;

    public string ParallelDisplay => IsParallelReadOnly
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{ReadOnlyActiveTurnCount} {WorkflowActivityMonitorViewModel.ParallelReadOnlyLabel} turns")
        : string.Empty;

    public IReadOnlyList<WorkflowActivityRouteEvidenceViewModel> RouteEvidence { get; }

    public bool HasRouteEvidence => RouteEvidence.Count > 0;

    public string RouteSummaryDisplay { get; }

    /// <summary>Models actually observed on the session bindings of this role's turns.</summary>
    public IReadOnlyList<string> ObservedModels { get; }

    /// <summary>Accounts actually observed on the session bindings of this role's turns.</summary>
    public IReadOnlyList<string> ObservedAccounts { get; }

    /// <summary>"Not reported" until a session binding of this role actually supplied a model.</summary>
    public string ModelSummaryDisplay { get; }

    /// <summary>"Not reported" until a session binding of this role actually supplied an account.</summary>
    public string AccountSummaryDisplay { get; }

    public string ModelAccountDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"Model: {ModelSummaryDisplay} · Account: {AccountSummaryDisplay}");

    public string LastActivityDisplay { get; }
}


/// <summary>
/// Observed route evidence of one execution of a role. Codex and AGY are labelled as star-cliproxy
/// routes of the role only when the observed route id actually proves it; otherwise the raw route id or
/// "Not reported" is shown.
/// </summary>
public sealed class WorkflowActivityRouteEvidenceViewModel
{
    public WorkflowActivityRouteEvidenceViewModel(string roleId, ObservableRunProjection projection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(projection);

        RoleDisplay = roleId;
        ExecutionDisplay = projection.ExecutionId;
        StateDisplay = projection.State.ToString();
        RequestedRouteDisplay = DescribeRoute(projection.RequestedRouteId);
        ObservedRouteDisplay = DescribeRoute(projection.ObservedRouteId);
        NativeSessionDisplay = projection.NativeSessionId ?? WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        ModelDisplay = projection.ObservedModelIdDisplay;
        AccountDisplay = projection.ObservedAccountIdDisplay;
        ExecutionModeDisplay = projection.ObservedExecutionModeDisplay;
        IsReadOnlyTurn = projection.IsReadOnlyTurn;
        ReadOnlyTurnDisplay = projection.IsReadOnlyTurn
            ? "read-only turn (no writer lock)"
            : "writer turn (execution mode requires the writer lock)";
        EvidenceDisplay = projection.EvidenceSource switch
        {
            EvidenceSourceKind.SyntheticFixture => "Synthetic fixture",
            EvidenceSourceKind.NativeProtocolEvent => "Native protocol event",
            _ => WorkflowActivityMonitorViewModel.NotReportedPlaceholder
        };
        IsSynthetic = projection.IsSynthetic;
        SyntheticBadge = projection.IsSynthetic ? "SYNTHETIC" : string.Empty;
        IsActive = projection.IsActive;
        LastActivityDisplay = projection.LastActivityAtUtc.ToString("u", CultureInfo.InvariantCulture);
    }

    public string RoleDisplay { get; }

    public string ExecutionDisplay { get; }

    public string StateDisplay { get; }

    public string RequestedRouteDisplay { get; }

    public string ObservedRouteDisplay { get; }

    public string NativeSessionDisplay { get; }

    /// <summary>Model observed on the session binding, or "Not reported".</summary>
    public string ModelDisplay { get; }

    /// <summary>Account observed on the session binding, or "Not reported".</summary>
    public string AccountDisplay { get; }

    /// <summary>Execution mode observed on the session binding, or "Not reported".</summary>
    public string ExecutionModeDisplay { get; }

    /// <summary>True only when the writer-lock policy proved this turn is read-only.</summary>
    public bool IsReadOnlyTurn { get; }

    public string ReadOnlyTurnDisplay { get; }

    public string ModelAccountDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"Model: {ModelDisplay} · Account: {AccountDisplay} · Mode: {ExecutionModeDisplay}");


    public string EvidenceDisplay { get; }

    public bool IsSynthetic { get; }

    public string SyntheticBadge { get; }

    public bool IsActive { get; }

    public string LastActivityDisplay { get; }

    public string RouteEvidenceDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"Observed: {ObservedRouteDisplay} · Requested: {RequestedRouteDisplay}");

    /// <summary>
    /// Renders a route id as operator-readable evidence. The star-cliproxy gateway proves the Codex and
    /// AGY routes; anything else keeps its raw route id instead of being assigned a persona.
    /// </summary>
    public static string DescribeRoute(string? routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId))
        {
            return WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        }

        if (routeId.StartsWith("route-star-cliproxy-codex", StringComparison.OrdinalIgnoreCase))
        {
            return string.Create(CultureInfo.InvariantCulture, $"Codex via star-cliproxy ({routeId})");
        }

        if (routeId.StartsWith("route-star-cliproxy-agy", StringComparison.OrdinalIgnoreCase))
        {
            return string.Create(CultureInfo.InvariantCulture, $"AGY via star-cliproxy ({routeId})");
        }

        return routeId;
    }
}

/// <summary>One item of the observed activity history shown in the drawer.</summary>
public sealed class WorkflowActivityHistoryItemViewModel
{
    public WorkflowActivityHistoryItemViewModel(WorkflowRunTimelineItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        Sequence = item.Sequence;
        KindDisplay = item.Kind.ToString();
        OccurredAtDisplay = item.OccurredAtUtc.ToString("u", CultureInfo.InvariantCulture);
        StageDisplay = item.StageDisplay;
        RoleDisplay = item.RoleDisplay;
        Description = item.Description;
        RouteDisplay = item.ObservedRouteDisplay;
        NativeSessionDisplay = item.NativeSessionDisplay;
        EvidenceDisplay = item.EvidenceSource switch
        {
            EvidenceSourceKind.SyntheticFixture => "Synthetic fixture",
            EvidenceSourceKind.NativeProtocolEvent => "Native protocol event",
            _ => WorkflowActivityMonitorViewModel.NotReportedPlaceholder
        };
        IsSynthetic = item.EvidenceSource == EvidenceSourceKind.SyntheticFixture;
        SyntheticBadge = IsSynthetic ? "SYNTHETIC" : string.Empty;
        VerdictDisplay = item.ReviewerVerdicts.Count == 0
            ? WorkflowActivityMonitorViewModel.NotReportedPlaceholder
            : string.Join(
                "; ",
                item.ReviewerVerdicts.Select(verdict =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{verdict.ReviewerRole}: {verdict.Verdict} ({verdict.RouteId})")));
        ApprovalDisplay = item.UserApproval is null
            ? WorkflowActivityMonitorViewModel.NotReportedPlaceholder
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{item.UserApproval.ApprovedBy}: {item.UserApproval.Decision}");
    }

    public int Sequence { get; }

    public string SequenceDisplay => (Sequence + 1).ToString("00", CultureInfo.InvariantCulture);

    public string KindDisplay { get; }

    public string OccurredAtDisplay { get; }

    public string StageDisplay { get; }

    public string RoleDisplay { get; }

    public string Description { get; }

    public string RouteDisplay { get; }

    public string NativeSessionDisplay { get; }

    public string EvidenceDisplay { get; }

    public string VerdictDisplay { get; }

    public string ApprovalDisplay { get; }

    public bool IsSynthetic { get; }

    public string SyntheticBadge { get; }

    public string Summary => string.Join(
        " · ",
        SequenceDisplay,
        KindDisplay,
        OccurredAtDisplay,
        RoleDisplay,
        Description);
}

/// <summary>
/// The details drawer of the selected node. It shows the source role ("← от …"), the received/updated
/// timestamps, the full untruncated work text, the observed route evidence and the activity of the last
/// N minutes. Fields the domain did not report are shown as "Not reported".
/// </summary>
public sealed class WorkflowActivityDrawerViewModel
{
    public static WorkflowActivityDrawerViewModel Closed { get; } = new();

    private WorkflowActivityDrawerViewModel()
    {
        RoleDisplay = WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        StatusDisplay = WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        SourceRoleDisplay = string.Create(
            CultureInfo.InvariantCulture,
            $"← от {WorkflowActivityMonitorViewModel.NotReportedPlaceholder}");
        ReceivedAtDisplay = string.Create(
            CultureInfo.InvariantCulture,
            $"Получено: {WorkflowActivityMonitorViewModel.NotReportedPlaceholder}");
        UpdatedAtDisplay = string.Create(
            CultureInfo.InvariantCulture,
            $"Изменено: {WorkflowActivityMonitorViewModel.NotReportedPlaceholder}");
        WorkText = string.Empty;
        WorkTextDisplay = WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        WorkTextNotice = WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        EvidenceSourceDisplay = WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        ActiveTurnsDisplay = "No active turn reported";
        ParallelDisplay = string.Empty;
        ModelAccountDisplay = string.Create(
            CultureInfo.InvariantCulture,
            $"Model: {WorkflowActivityMonitorViewModel.NotReportedPlaceholder} · "
                + $"Account: {WorkflowActivityMonitorViewModel.NotReportedPlaceholder}");
        ParallelProofNote = WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        RouteEvidence = Array.Empty<WorkflowActivityRouteEvidenceViewModel>();

        Items = Array.Empty<WorkflowActivityHistoryItemViewModel>();
        ActivityWindowDisplay = WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        HistoryEmptyMessage = "No activity history is reported.";
        IsOpen = false;
    }

    public WorkflowActivityDrawerViewModel(
        WorkflowActivityNodeViewModel node,
        RoleTransferEvidence? evidence,
        IReadOnlyList<WorkflowActivityHistoryItemViewModel> activity,
        int historyWindowMinutes)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(activity);

        IsOpen = true;
        RoleDisplay = node.DisplayName;
        StatusDisplay = node.StatusDisplay;
        IsCurrent = node.IsCurrent;
        ActiveTurnsDisplay = node.ActiveTurnCount == 0
            ? "No active turn reported"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{node.ActiveTurnCount} active turn(s)");
        ParallelDisplay = node.ParallelDisplay;
        ModelAccountDisplay = node.ModelAccountDisplay;
        ParallelProofNote = node.ReadOnlyActiveTurnCount == 0
            ? "No active turn of this role is proven read-only, so no parallel read-only activity is reported."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{node.ReadOnlyActiveTurnCount} of {node.ActiveTurnCount} active turn(s) are proven read-only by the observed execution mode.");
        RouteEvidence = node.RouteEvidence;

        Items = activity;
        ActivityWindowDisplay = string.Create(
            CultureInfo.InvariantCulture,
            $"Last {historyWindowMinutes} min");
        HistoryEmptyMessage = string.Create(
            CultureInfo.InvariantCulture,
            $"No activity in the last {historyWindowMinutes} minutes.");

        SourceRoleDisplay = string.Create(
            CultureInfo.InvariantCulture,
            $"← от {evidence?.FromRoleDisplay ?? WorkflowActivityMonitorViewModel.NotReportedPlaceholder}");
        ReceivedAtDisplay = string.Create(
            CultureInfo.InvariantCulture,
            $"Получено: {evidence?.ReceivedAtDisplay ?? WorkflowActivityMonitorViewModel.NotReportedPlaceholder}");
        UpdatedAtDisplay = string.Create(
            CultureInfo.InvariantCulture,
            $"Изменено: {evidence?.UpdatedAtDisplay ?? WorkflowActivityMonitorViewModel.NotReportedPlaceholder}");
        WorkText = evidence?.WorkText ?? string.Empty;
        WorkTextDisplay = evidence?.WorkTextDisplay ?? WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        HasWorkText = evidence?.HasWorkText == true;
        WorkTextNotice = WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
        EvidenceSourceDisplay = evidence?.EvidenceSource ?? WorkflowActivityMonitorViewModel.NotReportedPlaceholder;
    }

    public bool IsOpen { get; }

    public string RoleDisplay { get; }

    public string StatusDisplay { get; }

    public bool IsCurrent { get; }

    public string SourceRoleDisplay { get; }

    public string ReceivedAtDisplay { get; }

    public string UpdatedAtDisplay { get; }

    /// <summary>The full work text of the selected role, never truncated by the view model.</summary>
    public string WorkText { get; }

    public string WorkTextDisplay { get; }

    public bool HasWorkText { get; }

    public string WorkTextNotice { get; }

    public string EvidenceSourceDisplay { get; }

    public string ActiveTurnsDisplay { get; }

    public string ParallelDisplay { get; }

    /// <summary>Model and account actually observed on the session bindings of this node's turns.</summary>
    public string ModelAccountDisplay { get; }

    /// <summary>Why the node is or is not marked as parallel read-only activity.</summary>
    public string ParallelProofNote { get; }


    public IReadOnlyList<WorkflowActivityRouteEvidenceViewModel> RouteEvidence { get; }

    public IReadOnlyList<WorkflowActivityHistoryItemViewModel> Items { get; }

    public bool HasRouteEvidence => RouteEvidence.Count > 0;

    public bool HasActivity => Items.Count > 0;

    public string ActivityWindowDisplay { get; }

    public string HistoryEmptyMessage { get; }
}
