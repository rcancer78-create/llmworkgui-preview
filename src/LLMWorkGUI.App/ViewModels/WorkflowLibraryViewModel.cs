using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Workflow library screen (ТЗ §7.2, ROADMAP Phase 8). It shows immutable packages and versions, renders
/// the tree/Markdown preview strictly from the selected version blob, and binds projects by moving the
/// active-version pointer only — the package, the version and the stored blob are never mutated
/// (ADR-0006 §1, §2, §3). Every command and method takes its inputs as parameters so the screen is fully
/// testable headless; this slice deliberately ships no file dialogs.
/// </summary>
public sealed class WorkflowLibraryViewModel : ScreenViewModel
{
    public const string UnavailableIndicator = "Not reported";

    /// <summary>
    /// The largest local file this screen will stream into a stage artifact, at most 20 MiB.
    ///
    /// The bytes are never read into memory: the run service streams them from the file handle straight
    /// into the content-addressed store, so this bound is what keeps an operator's chosen file from turning
    /// one button press into an unbounded copy.
    /// </summary>
    public const long MaxStageArtifactBytes = 20L * 1024L * 1024L;

    private readonly IWorkflowPackageRepository? _packageRepository;
    private readonly IWorkflowVersionRepository? _versionRepository;
    private readonly IWorkflowBindingRepository? _bindingRepository;
    private readonly IWorkflowBindingService? _bindingService;
    private readonly IWorkflowPreviewService? _previewService;
    private readonly IWorkflowImportService? _importService;
    private readonly IWorkflowExportService? _exportService;
    private readonly IProjectRepository? _projectRepository;
    private readonly IWorkflowActivationService? _activationService;
    private readonly IWorkflowRunTimelineService? _timelineService;
    private readonly IWorkflowStudioService? _studioService;
    private readonly IDocumentTemplateService? _documentTemplateService;
    private readonly IPreCoderGateValidator? _preCoderGateValidator;
    private readonly IWorkflowRunRepository? _runRepository;
    private readonly IWorkflowRunService? _runService;
    private readonly ISessionRepository? _sessionRepository;
    private readonly IExecutionRepository? _executionRepository;
    private readonly ICheckoutLockService? _checkoutLockService;
    private readonly IWorkflowArtifactBlobStore? _artifactBlobStore;
    private readonly IUserApprovalIdentity? _userApprovalIdentity;
    private readonly IWorkflowReviewRequestService? _reviewRequestService;
    private readonly TimeProvider _timeProvider;

    private WorkflowPackageItemViewModel? _selectedPackage;
    private WorkflowVersionItemViewModel? _selectedVersion;
    private WorkflowTreeNodeViewModel? _selectedNode;
    private WorkflowBindingViewModel? _selectedBinding;
    private Project? _selectedProject;
    private WorkflowBinding? _activeBinding;
    private WorkflowRun? _observedRun;
    private IReadOnlyList<ObservableRunProjection> _observedProjections = Array.Empty<ObservableRunProjection>();
    private string _documentationContent = string.Empty;
    private string? _documentationPath;
    private bool _isDocumentationTruncated;
    private string _selectedFileContent = string.Empty;
    private bool _isSelectedFileBinary;
    private bool _isSelectedFileTruncated;
    private string _blocker = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;
    private string? _routePolicyId;
    private string _quickImportPath = string.Empty;
    private string _quickExportDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LLMWorkGUI",
        "WorkflowExports");
    private string _lastQuickExportPath = string.Empty;
    private string _runCommandNotice = string.Empty;
    private string _stageArtifactPath = string.Empty;
    private string _stageArtifactExecutionId = string.Empty;
    private DataClassification _stageArtifactClassification = DataClassification.PrivateSource;
    private string _userApprovalComment = string.Empty;
    private ApprovableStageArtifact? _approvableArtifact;
    private ApprovableStageArtifact? _reviewableArtifact;
    private WorkflowReviewGateStatus _reviewGateStatus = WorkflowReviewGateStatus.NoObservedRun;
    private WorkflowReviewRequestResult? _assignedReviewResult;
    private int _versionsLoadToken;
    private int _previewLoadToken;
    private int _filePreviewLoadToken;
    private int _runObservationToken;
    private bool _runObservationInFlight;
    private int _bindingsLoadToken;
    private string? _observedRunSelectionKey;
    private PendingActivation? _pendingActivation;

    public WorkflowLibraryViewModel(
        IWorkflowPackageRepository? packageRepository = null,
        IWorkflowVersionRepository? versionRepository = null,
        IWorkflowBindingRepository? bindingRepository = null,
        IWorkflowBindingService? bindingService = null,
        IWorkflowPreviewService? previewService = null,
        IWorkflowImportService? importService = null,
        IWorkflowExportService? exportService = null,
        IProjectRepository? projectRepository = null,
        IWorkflowActivationService? activationService = null,
        IWorkflowAdaptationService? adaptationService = null,
        ISanitizedCatalogProvider? catalogProvider = null,
        IWorkflowRunTimelineService? timelineService = null,
        IWorkflowStudioService? studioService = null,
        IDocumentTemplateService? documentTemplateService = null,
        IPreCoderGateValidator? preCoderGateValidator = null,
        IWorkflowRunRepository? runRepository = null,
        IWorkflowRunService? runService = null,
        ISessionRepository? sessionRepository = null,
        IExecutionRepository? executionRepository = null,
        ICheckoutLockService? checkoutLockService = null,
        RoleTransferEvidenceProjector? transferEvidenceProjector = null,
        TimeProvider? timeProvider = null,
        IWorkflowArtifactBlobStore? artifactBlobStore = null,
        IUserApprovalIdentity? userApprovalIdentity = null,
        IWorkflowReviewRequestService? reviewRequestService = null,
        IWorkflowMaterialPolicyStore? materialPolicies = null)
        : base(
            ScreenId.Workflows,
            "Процессы",
            "Ctrl+8",
            "Импорт процессов, версии, предпросмотр, привязка и адаптация (ТЗ §7.2).")
    {
        _packageRepository = packageRepository;
        _versionRepository = versionRepository;
        _bindingRepository = bindingRepository;
        _bindingService = bindingService;
        _previewService = previewService;
        _importService = importService;
        _exportService = exportService;
        _projectRepository = projectRepository;
        _activationService = activationService;

        // The composed workflow services are forwarded to the embedded panels unchanged. The library never
        // substitutes a privately constructed studio, document, gate or timeline service for them, so the
        // shipped console projects the same services the application registered.
        _timelineService = timelineService;
        _studioService = studioService;
        _documentTemplateService = documentTemplateService;
        _preCoderGateValidator = preCoderGateValidator;
        _runRepository = runRepository;

        // Phase 10G: the same composed run service that production registers. A window without one - a
        // headless UI host, for example - simply cannot start or advance a run; no private service and no
        // in-memory substitute is ever constructed to fill the gap.
        _runService = runService;
        _sessionRepository = sessionRepository;
        _executionRepository = executionRepository;
        _checkoutLockService = checkoutLockService;

        // The product user approval is decided against bytes, not against a row that claims to have them.
        // The screen therefore reads the very same two collaborators the run service reads: the blob store
        // that re-hashes a committed artifact, and the identity source that stamps the approver. A window
        // without them is unoffered the action rather than deciding against a row.
        _artifactBlobStore = artifactBlobStore;
        _userApprovalIdentity = userApprovalIdentity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reviewRequestService = reviewRequestService;

        AdaptationDialog = new WorkflowAdaptationViewModel(
            adaptationService,
            activationService,
            catalogProvider,
            materialPolicies);
        AdaptationDialog.OnLibraryChangedAsync = RefreshAsync;

        // The Activity Monitor (ROADMAP 10D) is embedded as a child panel of this screen. It consumes
        // only the observable run projection pipeline; without a selected run it reports the empty state
        // instead of inventing a schema.
        ActivityMonitor = new WorkflowActivityMonitorViewModel(
            timelineService,
            transferEvidenceProjector,
            timeProvider);

        // The Workflow Studio (ROADMAP 10E) is embedded as a child panel of this screen. It edits
        // versioned template copies and document drafts; the selected run graph stays read-only.
        Studio = new WorkflowStudioViewModel(
            studioService,
            documentTemplateService,
            preCoderGateValidator,
            timeProvider);

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
        BindToProjectCommand = new RelayCommand(() => _ = BindToProjectAsync(), () => CanBindToProject);
        SetActiveVersionCommand = new RelayCommand(() => _ = SetActiveVersionAsync(), () => CanSetActiveVersion);
        UnbindCommand = new RelayCommand(() => _ = UnbindAsync(), () => CanUnbind);
        AdaptWorkflowCommand = new RelayCommand(() => _ = AdaptWorkflowAsync(), () => CanAdaptWorkflow);
        RollbackCommand = new RelayCommand(() => _ = RollbackAsync(), () => CanRollback);
        ConfirmActivationCommand = new RelayCommand(
            () => _ = ConfirmActivationAsync(),
            () => CanConfirmActivation);
        DismissActivationBlockersCommand = new RelayCommand(
            DismissActivationBlockers,
            () => HasActivationBlockers);
        QuickImportCommand = new RelayCommand(() => _ = QuickImportAsync(), () => CanQuickImport);
        QuickExportCommand = new RelayCommand(() => _ = QuickExportAsync(), () => CanQuickExport);
        StartAssignedRunCommand = new RelayCommand(() => _ = StartAssignedRunAsync(), () => CanStartAssignedRun);
        AdvanceObservedRunCommand = new RelayCommand(
            () => _ = AdvanceObservedRunAsync(),
            () => CanAdvanceObservedRun);
        AttachStageArtifactCommand = new RelayCommand(
            () => _ = AttachStageArtifactAsync(),
            () => CanAttachStageArtifact);
        ApproveObservedArtifactCommand = new RelayCommand(
            () => _ = RecordObservedUserApprovalAsync(UserApprovalDecision.Approved),
            () => CanApproveObservedArtifact);
        RejectObservedArtifactCommand = new RelayCommand(
            () => _ = RecordObservedUserApprovalAsync(UserApprovalDecision.Rejected),
            () => CanRejectObservedArtifact);
        RequestAssignedReviewCommand = new RelayCommand(
            () => _ = RequestAssignedReviewAsync(),
            () => CanRequestAssignedReview);
    }

    /// <summary>True only when the package and version repositories are wired into this window.</summary>
    public bool IsLibraryAvailable => _packageRepository is not null && _versionRepository is not null;

    /// <summary>True only when the immutable tree/Markdown preview service is wired in.</summary>
    public bool IsPreviewAvailable => _previewService is not null;

    /// <summary>True only when project-to-package binding can actually be written.</summary>
    public bool IsBindingAvailable => _bindingRepository is not null && _bindingService is not null;

    public bool IsImportAvailable => _importService is not null;

    public bool IsExportAvailable => _exportService is not null;

    public bool IsProjectSelectionAvailable => _projectRepository is not null;

    /// <summary>True only when the model-assisted adaptation service is wired into this window.</summary>
    public bool IsAdaptationAvailable => AdaptationDialog.IsAdaptationAvailable;

    /// <summary>True only when the activation and rollback engine is wired into this window.</summary>
    public bool IsActivationAvailable => _activationService is not null;

    /// <summary>Adaptation dialog view model rendered as an in-window modal overlay.</summary>
    public WorkflowAdaptationViewModel AdaptationDialog { get; }

    /// <summary>
    /// Activity Monitor panel (ROADMAP Phase 10D) embedded in the workflow screen. The selected version
    /// defines the declared roles; a run is loaded through <see cref="WorkflowActivityMonitorViewModel.LoadRun"/>.
    /// </summary>
    public WorkflowActivityMonitorViewModel ActivityMonitor { get; }

    /// <summary>
    /// Workflow Studio panel (ROADMAP Phase 10E) embedded in the workflow screen: editable template
    /// schema, versioned document drafts, separate reviewer verdicts and the pre-coder approval gate.
    /// </summary>
    public WorkflowStudioViewModel Studio { get; }

    /// <summary>True only when the studio can edit and save template versions.</summary>
    public bool IsStudioAvailable => Studio.IsStudioAvailable;

    /// <summary>
    /// True only when the run, session and execution repositories are all wired into this window, which
    /// is the minimum required to observe a real run and its projections. Without them nothing is queried
    /// and every run field stays "Not reported".
    /// </summary>
    public bool IsRunObservationAvailable =>
        _runRepository is not null
        && _sessionRepository is not null
        && _executionRepository is not null;

    /// <summary>The workflow run actually observed for the selected project and version, or null.</summary>
    public WorkflowRun? ObservedRun => _observedRun;

    public bool HasObservedRun => _observedRun is not null;

    /// <summary>
    /// The observable run projections actually built from the stored executions of the observed run's
    /// sessions. A timeline alone never produces projections, so this stays empty without executions.
    /// </summary>
    public IReadOnlyList<ObservableRunProjection> ObservedProjections => _observedProjections;

    public string ObservedRunDisplay => _observedRun is null
        ? UnavailableIndicator
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{_observedRun.Id} · {_observedRun.CurrentStageId} · {_observedRun.CurrentRole} · {_observedRun.State}");

    public string ObservedProjectionsDisplay => _observedProjections.Count == 0
        ? UnavailableIndicator
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{_observedProjections.Count} observed turn(s).");

    /// <summary>
    /// The assigned template version the observed run is pinned to, as a separate identity from the source
    /// workflow version the run is matched on. A legacy run carries no template at all, and that is
    /// reported as "Not reported" rather than being read off the run's source version.
    /// </summary>
    public string ObservedRunTemplateDisplay => _observedRun is { IsTemplateBacked: true } run
        ? string.Create(CultureInfo.InvariantCulture, $"{run.TemplateId}@{run.TemplateVersion}")
        : UnavailableIndicator;

    /// <summary>
    /// The failure route the observed run's own pinned scheme declares for the stage it currently sits on,
    /// and the fact that this build cannot take it.
    /// <para>
    /// The edge is part of the run's plan, so it is shown rather than hidden: a stage of the shipped
    /// standard chain that sends a rejected review back to the code stage says so here. What is stated with
    /// it is equally important - the edge is preserved and never followed, so nothing on this screen offers
    /// to move the run along it. A stage that declares no failure target says that instead of borrowing
    /// another stage's route, and a run this screen does not observe reports "Not reported" rather than
    /// describing a stage it cannot see.
    /// </para>
    /// </summary>
    public string ObservedRunFailureRouteDisplay
    {
        get
        {
            var run = _observedRun;

            if (run is null)
            {
                return UnavailableIndicator;
            }

            if (!TryResolvePinnedFailureStageId(run, out var failureStageId, out var blocker))
            {
                return blocker;
            }

            if (failureStageId is null)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{run.Id} · стадия '{run.CurrentStageId}' не объявляет маршрут отказа.");
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{run.Id} · стадия '{run.CurrentStageId}' объявляет маршрут отказа '{failureStageId}', который сохранён в закреплённой схеме, но этой сборкой не исполняется.");
        }
    }

    /// <summary>
    /// Explains why no run is reported. It never guesses a stage, route, model, account or session: an
    /// absent run, a run of another workflow version or a run without observed sessions is stated as
    /// "Not reported" instead of being filled in.
    /// </summary>
    public string ObservedRunNotice => _observedRun is null
        ? IsRunObservationAvailable
            ? "Активный запуск выбранной версии не наблюдается: состояние, роль, маршрут, модель, аккаунт и сессия — Not reported."
            : "Службы наблюдения запусков не настроены для этого окна, поэтому состояние, роль, маршрут, модель, аккаунт и сессия — Not reported."
        : "Показаны только наблюдаемые данные активного запуска выбранной версии.";

    public bool HasObservedRunNotice => !string.IsNullOrWhiteSpace(ObservedRunNotice);

    /// <summary>
    /// True only when the composed workflow run service is wired into this window. Without it the product
    /// start/advance commands stay unoffered instead of running a run through a private substitute.
    /// </summary>
    public bool IsRunExecutionAvailable => _runService is not null;

    /// <summary>The registered run service this screen executes real runs through; exposed as evidence.</summary>
    public IWorkflowRunService? RunService => _runService;

    /// <summary>
    /// The run this window is about to act on, and the single source of every run-identity question asked
    /// by the commands. It is the run the console currently observes for the selected project, never a
    /// locally constructed one.
    /// </summary>
    public WorkflowRun? CommandableRun => _observedRun;

    /// <summary>
    /// Offered when a project, a package and a version are selected, the run service is composed, nothing
    /// else is running and no non-terminal run is observed for that selection.
    ///
    /// The action re-reads the persisted active binding when invoked. An inactive or unbound selection is
    /// offered a named refusal on click; selecting a version by hand never changes the persisted binding.
    ///
    /// Two further clauses keep the action from promising what the screen has not established yet:
    /// <list type="bullet">
    /// <item>The observation for <em>this</em> selection has to have finished. While a project or version
    /// change is still being observed the screen is holding the previous selection's run, and offering
    /// Start on that basis would be a claim about a run it no longer observes.</item>
    /// <item>The run that blocks the start has to belong to the current selection.</item>
    /// </list>
    /// A non-terminal observed run of the selected version is the one condition the screen can settle
    /// without reading a row at click time, so it takes the action away instead of offering a button that
    /// only refuses when it is pressed. The service keeps its own atomic duplicate-run refusal for
    /// everything this screen cannot know - an active run of a version the console is not showing, or one
    /// created between the last observation and the click - and that refusal keeps its own name.
    /// </summary>
    public bool CanStartAssignedRun =>
        IsRunExecutionAvailable &&
        IsRunObservationAvailable &&
        !IsBusy &&
        SelectedProject is not null &&
        SelectedPackage is not null &&
        SelectedVersion is not null &&
        !IsRunObservationPending &&
        !HasNonTerminalObservedRun;

    /// <summary>
    /// Whether the observation the start action depends on has not settled for the current project and
    /// version yet.
    /// <para>
    /// It is true while an observation is in flight and while none has been made for the current selection at
    /// all, which is what makes the action fail closed from the synchronous moment a selection setter returns -
    /// not one await later, when the query comes back. Without a selection there is nothing to observe and
    /// nothing to wait for, so the other clauses of <see cref="CanStartAssignedRun"/> already take the action
    /// away without a reason on screen.
    /// </para>
    /// </summary>
    private bool IsRunObservationPending =>
        SelectedProject is not null
        && SelectedVersion is not null
        && (_runObservationInFlight
            || !string.Equals(_observedRunSelectionKey, CurrentRunObservationSelectionKey, StringComparison.Ordinal));

    /// <summary>
    /// Whether the observed run is still open <em>for the current selection</em>. This is the duplicate-start
    /// condition the screen can see for itself; it is deliberately not a query of "any" active run, because
    /// an active run of another workflow version is invisible here and stays the service's refusal to report.
    /// </summary>
    private bool HasNonTerminalObservedRun =>
        _observedRun is { IsTerminal: false } run && BelongsToCurrentSelection(run);

    /// <summary>
    /// The one concise reason the start action is not offered, or an empty string when it is. It names the
    /// run that is in the way, so a disabled button is never a mute one, and it stays empty for a terminal
    /// run - a finished run is a normal reason to start the next one, not a blocker.
    /// <para>
    /// While the observation for the current selection is still in flight the reason is the wait itself. A
    /// silent disabled button there would read as "no active run" and invite a click the service then
    /// refuses, so the screen says what it is waiting for instead.
    /// </para>
    /// </summary>
    public string StartAssignedRunUnavailableReason
    {
        get
        {
            if (_observedRun is { IsTerminal: false } run && BelongsToCurrentSelection(run))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Запуск недоступен: у проекта уже есть активный запуск '{run.Id}' (стадия {run.CurrentStageId}, {run.State}). "
                        + $"Завершите или отмените его, чтобы начать следующий.");
            }

            return IsRunObservationPending
                ? "Запуск недоступен: наблюдение активного запуска выбранной версии ещё выполняется."
                : string.Empty;
        }
    }

    public bool HasStartAssignedRunUnavailableReason =>
        !string.IsNullOrWhiteSpace(StartAssignedRunUnavailableReason);

    /// <summary>
    /// Offered when a real observed run exists for the selected project, it is pinnable to a template
    /// version, and the stage it currently sits on still has a next stage of its own pinned scheme.
    ///
    /// The last clause is what the task requires and what the screen has to show: a run whose pinned
    /// current stage has no <c>NextStageId</c> - including a <see cref="WorkflowRunState.Running"/> run
    /// sitting on the TerminalOutcome stage - has nowhere left to go, so the button is unoffered instead
    /// of looking runnable and only refusing when it is clicked. The remaining conditions - a mismatched
    /// selection, a moved binding - can only be known from rows read at command time and are still
    /// reported there as named blockers.
    /// </summary>
    public bool CanAdvanceObservedRun =>
        IsRunExecutionAvailable &&
        IsRunObservationAvailable &&
        !IsBusy &&
        CanAdvanceInspectedObservedRun;

    /// <summary>
    /// Whether an observed run could be acted on at all. A run that exists but cannot move past its pinned
    /// current stage still gets its named reason reported through this flag instead of a silent no-op, so
    /// a direct invocation fails closed with an explanation rather than without one.
    /// </summary>
    private bool CanInspectObservedRunForAdvance =>
        IsRunExecutionAvailable && IsRunObservationAvailable && !IsBusy && HasObservedRun;

    /// <summary>
    /// Whether the observed run can actually take another step: it is pinned to a template version and its
    /// current stage names a successor. The same resolution the command uses at click time answers it
    /// here, so the button never promises a transition the pinned scheme cannot make.
    /// </summary>
    private bool CanAdvanceInspectedObservedRun =>
        HasObservedRun && TryResolvePinnedNextStageId(_observedRun!, out _, out _);

    /// <summary>
    /// Offered when the same composed run service is wired in, a real observed run exists for the selected
    /// project and version, nothing else is running, that run is still active and its own pinned scheme
    /// requires an artifact at the stage it currently sits on.
    ///
    /// The requirement is never taken from the Studio's edited graph, a route label, the stage display name
    /// or anything the operator typed, so a run whose pinned current stage declares no artifact is
    /// unoffered rather than looking attachable and refusing on click. A terminal run is unoffered for the
    /// same reason: it stores nothing any more.
    /// </summary>
    public bool CanAttachStageArtifact =>
        IsRunExecutionAvailable &&
        IsRunObservationAvailable &&
        !IsBusy &&
        SelectedProject is not null &&
        SelectedPackage is not null &&
        SelectedVersion is not null &&
        HasObservedRun &&
        !_observedRun!.IsTerminal &&
        TryResolvePinnedStageArtifact(_observedRun!, out _, out _, out _);

    /// <summary>
    /// Whether an observed run could take an artifact at all. A run that exists but whose pinned current
    /// stage requires none still gets that named reason reported through this flag instead of a silent
    /// no-op, so a direct invocation fails closed with an explanation rather than without one.
    /// </summary>
    private bool CanInspectObservedRunForArtifact =>
        IsRunExecutionAvailable && IsRunObservationAvailable && !IsBusy && HasObservedRun;

    /// <summary>
    /// What the run commands are about to do, in the operator's own terms, including the two independent
    /// identities a run carries. It never promises a run that the pinned assignment cannot produce.
    /// </summary>
    public string RunCommandNotice
    {
        get
        {
            if (!IsRunExecutionAvailable)
            {
                return "Служба выполнения запусков не настроена для этого окна: запуск и переход недоступны.";
            }

            if (!string.IsNullOrWhiteSpace(_runCommandNotice))
            {
                return _runCommandNotice;
            }

            var project = SelectedProject;
            var version = SelectedVersion;

            if (project is null || version is null)
            {
                return "Выберите проект и активную версию процесса: запуск создаётся только для активной привязки.";
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"Исходная версия {version.VersionDisplay} · версия шаблона определяется назначением проекта.");
        }

        private set
        {
            if (SetProperty(ref _runCommandNotice, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasRunCommandNotice));
            }
        }
    }

    public bool HasRunCommandNotice => !string.IsNullOrWhiteSpace(RunCommandNotice);

    /// <summary>
    /// The state of the run the commands act on, reported from the observed run only. A run that is not
    /// observed is stated as "Not reported" instead of being described from the editor state.
    /// </summary>
    public string RunCommandStateDisplay
    {
        get
        {
            var run = _observedRun;

            if (run is null)
            {
                return UnavailableIndicator;
            }

            var failureRoute = TryResolvePinnedFailureStageId(run, out var failureStageId, out _)
                ? failureStageId
                : null;

            return failureRoute is null
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{run.Id} · stage {run.CurrentStageId} · {run.State} · шаблон {ObservedRunTemplateDisplay}")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{run.Id} · stage {run.CurrentStageId} · {run.State} · шаблон {ObservedRunTemplateDisplay} "
                        + $"(маршрут отказа {failureRoute} сохранён, но не исполняется)");
        }
    }

    /// <summary>
    /// The observed pinned run's current stage and the exact artifact kind that stage requires in the run's
    /// own pinned execution scheme, stated before any file has been chosen.
    ///
    /// When the scheme declares an artifact for that stage, the stored hash of the current record is named
    /// too, so a replacement is visible as a replacement rather than as a second identical attach. When it
    /// declares none, the reason is reported instead of a requirement that was never there.
    /// </summary>
    public string StageArtifactRequirementDisplay
    {
        get
        {
            var run = _observedRun;

            if (run is null)
            {
                return UnavailableIndicator;
            }

            if (!TryResolvePinnedStageArtifact(run, out var stageId, out var kind, out var blocker))
            {
                return blocker;
            }

            var current = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, stageId!, kind!);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{run.Id} · стадия '{stageId}' · требуется артефакт '{kind}' · "
                    + $"записан: {current?.HashSha256 ?? "нет"}");
        }
    }

    /// <summary>
    /// What the action does to the operator's file and to the run's evidence, stated permanently next to
    /// the controls: the bytes of the chosen local file are copied into the application's content-addressed
    /// store under their own hash and the original file is left untouched, and attaching again makes the
    /// new bytes the current artifact so that verdicts and approvals recorded against the previous hash
    /// stop authorizing the transition.
    /// </summary>
    public string StageArtifactNotice =>
        $"Прикрепление копирует байты выбранного локального файла в адресное хранилище приложения "
            + $"(предел {MaxStageArtifactBytes / (1024 * 1024)} МиБ); исходный файл не изменяется. "
            + "Повторное прикрепление делает новые байты текущим артефактом, и прежние вердикты и одобрения "
            + "по старому хешу перестают авторизовать переход.";

    /// <summary>
    /// True only when this window can decide a product user approval at all: the composed run service, the
    /// blob store that re-hashes a committed artifact and the identity source that names the approver all
    /// have to be wired in. A reduced window is unoffered the action instead of deciding it against a row.
    /// </summary>
    public bool IsUserApprovalAvailable =>
        IsRunExecutionAvailable && _artifactBlobStore is not null && _userApprovalIdentity is not null;

    /// <summary>
    /// What the decision is about, read entirely from stored rows: the observed run, the stage its own
    /// pinned scheme requires a user approval on, the exact artifact kind that stage requires, the hash of
    /// the newest stored artifact of that run, stage and kind, whether those bytes were re-hashed for this
    /// observation, and the decision already recorded for that stage.
    ///
    /// The hash is never an input: it is read from the run's own artifacts, and the operator cannot type or
    /// alter it. When anything of that cannot be established the reason is stated instead of a value.
    /// </summary>
    public string UserApprovalRequirementDisplay
    {
        get
        {
            var run = _observedRun;

            if (run is null)
            {
                return UnavailableIndicator;
            }

            if (!TryResolvePinnedStageApproval(run, out var stageId, out var kind, out var blocker))
            {
                return blocker;
            }

            var current = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, stageId!, kind!);
            var verified = _approvableArtifact is { BytesVerified: true }
                && string.Equals(_approvableArtifact.HashSha256, current?.HashSha256, StringComparison.Ordinal);
            var decision = LatestStageDecision(run, stageId!);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{run.Id} · стадия '{stageId}' · требуется артефакт '{kind}' · "
                    + $"записанный хеш: {current?.HashSha256 ?? "нет"} · "
                    + $"байты хранилища: {(verified ? "проверены" : "не проверены")} · "
                    + $"решение по стадии: {decision ?? "нет"}");
        }
    }

    /// <summary>
    /// The identity a decision made on this screen would be recorded under, and the permanent statement of
    /// what that name is: the unsigned local Windows logon of this window. It is not a provider, not a
    /// backend account and not a signature, and the run service resolves it again itself instead of
    /// believing the name that arrived with the decision.
    /// </summary>
    public string UserApprovalApproverDisplay =>
        TryResolveApproverIdentity(out var identity)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Решение запишется от: '{identity}' — локальный вход Windows, без подписи.")
            : UserApprovalIdentityBlocker;

    /// <summary>
    /// The permanent contract of the two decision buttons: neither is a default, both need a deliberate
    /// comment, the hash is the stored one and never typed, a rejection ends the run, and replacing the
    /// artifact afterwards leaves the recorded decision authorizing nothing.
    /// </summary>
    public string UserApprovalNotice =>
        "Одобрение и отклонение — два отдельных действия: ни одно не выбрано по умолчанию, и оба требуют свой "
            + "комментарий. Хеш берётся из сохранённого артефакта стадии и не вводится вручную. Отклонение "
            + "немедленно завершает запуск терминальным исходом «отклонено»: после него ни переходов, ни новых "
            + "решений нет. Замена артефакта оставляет решение в истории, но переход больше не авторизует.";

    /// <summary>
    /// The operator's own words for the decision. Nothing is recorded without it, in either direction, so
    /// an empty comment leaves both buttons unoffered rather than recording an unexplained decision.
    /// </summary>
    public string UserApprovalComment
    {
        get => _userApprovalComment;
        set
        {
            if (SetProperty(ref _userApprovalComment, value ?? string.Empty))
            {
                UpdateCommandStates();
            }
        }
    }

    /// <summary>
    /// Whether the two decision buttons accept input. They are frozen for the whole busy window, because
    /// the command captures the comment before its first await.
    /// </summary>
    public bool IsUserApprovalInputEditable => !IsBusy;

    /// <summary>
    /// The approvable current artifact of the observed run, or null while the observation has not
    /// established one. It is derived, never typed, and it carries whether the committed bytes behind the
    /// newest stored artifact were re-hashed when the run was last observed.
    /// </summary>
    public string? CurrentApprovableArtifactHash => _approvableArtifact?.HashSha256;

    /// <summary>The registered blob store this screen re-hashes committed artifacts with; exposed as evidence.</summary>
    public IWorkflowArtifactBlobStore? ArtifactBlobStore => _artifactBlobStore;

    /// <summary>The registered identity source this screen names the approver with; exposed as evidence.</summary>
    public IUserApprovalIdentity? UserApprovalIdentity => _userApprovalIdentity;

    /// <summary>True when the current artifact of the observed run exists and is unoffered here.</summary>
    public bool HasApprovableArtifact => _approvableArtifact is not null;

    /// <summary>
    /// Offered when a real observed run sits on a stage its own pinned scheme marks as requiring an
    /// explicit user approval, that stage declares an artifact kind, a stored artifact of exactly that run,
    /// stage and kind exists, its committed bytes were re-hashed for this observation, an approver identity
    /// is available, the selection is complete and nothing else is running.
    ///
    /// <see cref="UserApprovalComment"/> is deliberately not part of this: the artifact target is what may
    /// be decided, and the deliberate part is the comment, which is checked by the two decision predicates
    /// below. A terminal run, a run that was replaced, a stage whose bytes cannot be verified and a stage
    /// that requires no approval are all unoffered rather than looking decidable and refusing on click.
    /// </summary>
    public bool CanDecideObservedUserApproval =>
        IsUserApprovalAvailable &&
        IsRunObservationAvailable &&
        !IsBusy &&
        SelectedProject is not null &&
        SelectedPackage is not null &&
        SelectedVersion is not null &&
        HasObservedRun &&
        !_observedRun!.IsTerminal &&
        _approvableArtifact is { BytesVerified: true } &&
        TryResolvePinnedStageApproval(_observedRun!, out _, out _, out _) &&
        TryResolveApproverIdentity(out _);

    /// <summary>
    /// The deliberate part of a decision: the target must be decidable and the operator must have said
    /// something about it. Both buttons share this predicate on purpose - there is one decision to make
    /// about one artifact, and the two buttons are the two directions of it, not a setting with a default.
    /// </summary>
    private bool CanRecordUserApproval =>
        CanDecideObservedUserApproval && !string.IsNullOrWhiteSpace(UserApprovalComment);

    /// <summary>The deliberate positive decision, offered only together with a comment of its own.</summary>
    public bool CanApproveObservedArtifact => CanRecordUserApproval;

    /// <summary>The deliberate negative decision, offered only together with a comment of its own.</summary>
    public bool CanRejectObservedArtifact => CanRecordUserApproval;

    /// <summary>
    /// Records an explicit product user approval of the observed run's current pinned stage artifact, and
    /// then re-observes the stored run.
    ///
    /// This is the product decision and not the Studio's document approval: it is about the run's own
    /// stored artifact, pinned to the hash the run service computes from the committed bytes, and it is
    /// never derived from a draft or from an in-memory projection.
    /// </summary>
    public async Task RecordObservedUserApprovalAsync(UserApprovalDecision decision)
    {
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "The user decision is not declared.");
        }

        if (CanRecordUserApproval)
        {
            await ExecuteRunCommandAsync(() => RecordObservedUserApprovalCoreAsync(decision)).ConfigureAwait(true);
            return;
        }

        // The buttons are unoffered by exactly the availability flag, so a click can never reach this
        // branch. A direct invocation still has to fail closed, and it fails closed out loud.
        if (CanInspectObservedRunForApproval)
        {
            ReportUserApprovalRefusal();
        }
    }

    /// <summary>Approves the observed run's current stage artifact with the recorded comment.</summary>
    public Task ApproveObservedArtifactAsync() =>
        RecordObservedUserApprovalAsync(UserApprovalDecision.Approved);

    /// <summary>Rejects the observed run's current stage artifact with the recorded comment.</summary>
    public Task RejectObservedArtifactAsync() =>
        RecordObservedUserApprovalAsync(UserApprovalDecision.Rejected);

    /// <summary>
    /// True only when this window can request an assigned model review at all: the composed review-request
    /// service, the run service, the run repository and the blob store that re-hashes a committed artifact
    /// all have to be wired in. A reduced window is unoffered the action instead of resolving an assignment
    /// against a row it cannot verify.
    /// </summary>
    public bool IsAssignedReviewAvailable =>
        _reviewRequestService is not null
        && IsRunExecutionAvailable
        && IsRunObservationAvailable
        && _artifactBlobStore is not null;

    /// <summary>The registered review-request service; exposed as evidence.</summary>
    public IWorkflowReviewRequestService? ReviewRequestService => _reviewRequestService;

    /// <summary>
    /// What the request would be about, read entirely from stored rows: the observed run, the stage its own
    /// pinned scheme requires reviewer roles on, the exact artifact kind that stage requires, the hash of
    /// the newest stored artifact of that run, stage and kind, and whether those bytes were re-hashed for
    /// this observation.
    ///
    /// Nothing here is an input. There is no role picker, no route picker and no hash field, because every
    /// one of those is exactly what the request has to resolve for itself from the run's pinned template.
    /// </summary>
    public string AssignedReviewRequirementDisplay
    {
        get
        {
            var run = _observedRun;

            if (run is null)
            {
                return UnavailableIndicator;
            }

            if (!IsAssignedReviewAvailable)
            {
                return AssignedReviewUnavailableNotice;
            }

            if (run.IsTerminal)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Run '{run.Id}' в терминальном состоянии {run.State}: назначенное ревью не запрашивается.");
            }

            if (!TryResolvePinnedReviewStage(run, out var stageId, out var kind, out var roles, out var blocker))
            {
                return blocker;
            }

            var current = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, stageId!, kind!);
            var verified = _reviewableArtifact is { BytesVerified: true }
                && string.Equals(_reviewableArtifact.HashSha256, current?.HashSha256, StringComparison.Ordinal);

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{run.Id} · стадия '{stageId}' · роли: {string.Join(", ", roles!)} · требуется артефакт '{kind}' · хеш: {current?.HashSha256 ?? "нет"} · байты хранилища: {(verified ? "проверены" : "не проверены")}");
        }
    }

    /// <summary>
    /// What the assigned review is, and what it is not.
    /// <para>
    /// The action asks the run's pinned template which model is assigned to each required reviewer role and
    /// whether a real backend can be observed to have served that route. It never lets the operator choose a
    /// role, a route, a document or an outcome: a click here cannot create a reviewer verdict, and the panel
    /// says so next to the button that requests one.
    /// </para>
    /// <para>
    /// The notice also names the one thing that decides the outcome today. A channel is registered, it is
    /// the read-only star-cliproxy reviewer, and it supports no route on this host - so the panel has to say
    /// which identity is missing rather than leaving an operator to guess whether a component is broken.
    /// </para>
    /// </summary>
    public string AssignedReviewNotice =>
        "Действие запрашивает назначенное ревью у модели, привязанной к роли в закреплённом шаблоне самого запуска: "
            + "роль, маршрут, модель и артефакт выбираются не вручную и не из текста оператора. Маршрут должен "
            + "быть реально сохранённой строкой маршрутов, у которой доступны профиль провайдера, аккаунт и "
            + "модель, — произвольный идентификатор вроде 'route-opencode' отклоняется и никогда не создаётся. "
            + "Перед отправкой байты артефакта перечитываются из адресного хранилища с ограничением размера. "
            + "Исполнение и фактически наблюдаемый маршрут сохраняются только после реальной отправки; "
            + "запрос, отменённый или не дошедший до модели, остаётся неудовлетворённым и не повторяется. "
            + "Канал ревью прокси в режиме «только чтение» в этой сборке не поддерживает ни один маршрут, и "
            + "причина называется прямо: у прокси нет собственного идентификатора провайдера среди профилей "
            + "провайдеров, у аккаунта нет стабильного идентификатора, отличного от пути Codex и метки "
            + "профиля, имя модели прокси не сопоставлено с сохранённым идентификатором модели провайдера, "
            + "а размерности режима маршрута не входят в наблюдаемый ключ — значит два сохранённых "
            + "маршрута могут дать одну пару «провайдер, аккаунт, модель», и такая пара не идентифицирует "
            + "ни один из них. Пока этой идентичности нет, запрос отклоняется поимённо до отправки: ни "
            + "сессии, ни исполнения, ни наблюдаемого маршрута не записывается. Точное имя канала и сам "
            + "отказ службы называются построчно рядом с этим текстом, как только запрос выполнялся. "
            + "Вердикт это действие не создаёт: вердикт пишется только из сохранённого исполнения ревьюера.";

    /// <summary>Why the action is not offered at all when the window cannot resolve an assignment.</summary>
    public string AssignedReviewUnavailableNotice =>
        "Служба запроса назначенного ревью не настроена для этого окна, поэтому назначение роли, маршрута и "
            + "модели не разрешается и запрос не отправляется.";

    /// <summary>
    /// The outcome of the last request made on this screen, or null when none applies to the run being
    /// observed.
    /// <para>
    /// A request that really was dispatched stays on screen while the same run is observed, including across
    /// a refresh, because the fact that it happened is a fact. It does not survive the screen moving to a
    /// different run or to no run at all: an outcome names the run, stage, roles and hash it was about, and
    /// showing it beside another run's controls would read as that run's answer. The run id is part of the
    /// status line as well, so a retained result can never be read as belonging to a different run.
    /// </para>
    /// </summary>
    public WorkflowReviewRequestResult? AssignedReviewResult => _assignedReviewResult;

    public bool HasAssignedReviewResult => _assignedReviewResult is not null;

    /// <summary>
    /// The compact status of the last request: refused, or dispatched with the number of roles that reached a
    /// real backend and how many of those reported the assigned route back.
    /// </summary>
    public string AssignedReviewStatusDisplay => _assignedReviewResult is { } result
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"запуск {result.RunId} · {result.StageId ?? UnavailableIndicator} · {result.StateDisplay}")
        : "Запрос назначенного ревью ещё не выполнялся.";

    /// <summary>
    /// The whole answer of the last request, including the named reason for every refused role. The observed
    /// route of a dispatched role is shown as what a backend reported, and stays empty when nothing reported
    /// one - it is never filled in from the requested route.
    /// </summary>
    public string AssignedReviewDetailDisplay
    {
        get
        {
            var result = _assignedReviewResult;

            if (result is null)
            {
                return UnavailableIndicator;
            }

            if (result.Roles.Count == 0)
            {
                return result.Detail;
            }

            var lines = result.Roles
                .Select(role => role.Refusal is { } refusal
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"{role.Role}: отказ — {refusal}")
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{role.Role}: исполнение {role.ExecutionId} в состоянии {role.ExecutionState}, наблюдаемый маршрут: {role.ObservedRouteId ?? UnavailableIndicator}, только чтение: {role.ReadOnly}"))
                .ToArray();

            return string.Join(" ", lines);
        }
    }

    /// <summary>
    /// Offered when this window can resolve an assigned review, a real observed run exists for the selected
    /// project and version, nothing else is running, the run is still active, and its own pinned current
    /// stage declares reviewer roles and an artifact kind that is stored and whose committed bytes were
    /// re-hashed for this observation.
    ///
    /// Whether a route behind those roles is a real persisted route and whether a backend can be observed on
    /// it cannot be known from rows this screen holds, so it is reported as a named refusal at click time
    /// rather than silently disabling a button that would have worked.
    /// </summary>
    public bool CanRequestAssignedReview =>
        IsAssignedReviewAvailable
        && !IsBusy
        && SelectedProject is not null
        && SelectedPackage is not null
        && SelectedVersion is not null
        && HasObservedRun
        && !_observedRun!.IsTerminal
        && _reviewableArtifact is { BytesVerified: true }
        && TryResolvePinnedReviewStage(_observedRun!, out _, out _, out _, out _);

    /// <summary>Whether an observed run could be asked for a review at all, so a refusal can be named.</summary>
    private bool CanInspectObservedRunForReview =>
        IsAssignedReviewAvailable && IsRunObservationAvailable && !IsBusy && HasObservedRun;

    /// <summary>
    /// The stage, artifact kind and required reviewer roles a request would be resolved from, or the reason
    /// the observed run admits none.
    ///
    /// All of it comes from <see cref="WorkflowRun.TemplateSchemeSnapshotJson"/> - the run's own pinned
    /// scheme - and from nowhere else: not from the Studio's edited graph, not from a route label, not from a
    /// stage display name, not from the process-wide standard scheme and not from anything the operator
    /// typed. Unlike the two product decisions above, this reads the stage's reviewer roles rather than its
    /// approval flag, because the question here is who has to review the artifact, not whether a person has
    /// to sign it.
    /// </summary>
    private static bool TryResolvePinnedReviewStage(
        WorkflowRun run,
        out string? stageId,
        out string? kind,
        out IReadOnlyList<string>? roles,
        out string blocker)
    {
        stageId = null;
        kind = null;
        roles = null;

        if (!run.IsTemplateBacked || run.TemplateSchemeSnapshotJson is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Run '{run.Id}' не закреплён за версией шаблона, поэтому роли ревьюеров для него неприменимы. Назначенное ревью запрашивается только у run, начатого на назначенной версии шаблона.");
            return false;
        }

        WorkflowSchemeSnapshot snapshot;

        try
        {
            snapshot = WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson, run.Id);
        }
        catch (Exception exception) when (exception is WorkflowValidationException or ArgumentException)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Закреплённая схема run '{run.Id}' нечитаема, поэтому назначенное ревью не запрашивается: {UiErrorMessage.Describe(exception)}");
            return false;
        }

        var currentStage = snapshot.Scheme.FindStage(run.CurrentStageId);

        if (currentStage is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{run.CurrentStageId}' отсутствует в закреплённой схеме run '{run.Id}', поэтому назначенное ревью не запрашивается.");
            return false;
        }

        if (currentStage.RequiredReviewerRoles.Count == 0)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{currentStage.StageId}' run '{run.Id}' не требует ролей ревьюеров в закреплённой схеме, поэтому ревью запрашивать не у кого.");
            return false;
        }

        if (currentStage.ArtifactRequirement is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{currentStage.StageId}' run '{run.Id}' требует ролей ревьюеров, но не объявляет вид артефакта, поэтому отправлять нечего.");
            return false;
        }

        stageId = currentStage.StageId;
        kind = currentStage.ArtifactRequirement;
        roles = currentStage.RequiredReviewerRoles.ToArray();
        blocker = string.Empty;
        return true;
    }

    /// <summary>
    /// Requests the assigned model review of the observed run's current stage artifact.
    /// <para>
    /// The screen contributes no authority of its own. The run, its stage, the required roles, the route
    /// behind each role, the model and the exact verified bytes are all resolved by the review-request
    /// service from the run's own pinned identity; this method re-confirms the target after the awaits and
    /// then reports what was actually persisted. A refusal is reported in full and never leaves a pending
    /// reviewer turn behind, because a request that never reached a dispatch boundary persists nothing.
    /// </para>
    /// </summary>
    public async Task RequestAssignedReviewAsync()
    {
        if (CanRequestAssignedReview)
        {
            await ExecuteRunCommandAsync(RequestAssignedReviewCoreAsync).ConfigureAwait(true);
            return;
        }

        // The button is unoffered by exactly the availability flag, so a click can never reach this branch.
        // A direct invocation still fails closed, and it fails closed out loud.
        if (CanInspectObservedRunForReview)
        {
            ReportAssignedReviewRefusal();
        }
    }

    private void ReportAssignedReviewRefusal()
    {
        var run = _observedRun;

        if (run is null)
        {
            return;
        }

        if (run.IsTerminal)
        {
            ReportBlocker(TerminalRunReviewBlocker(run.Id, run.State.ToString()));
            return;
        }

        if (!TryResolvePinnedReviewStage(run, out var stageId, out var kind, out _, out var blocker))
        {
            ReportBlocker(blocker);
            return;
        }

        if (_reviewableArtifact is null)
        {
            ReportBlocker(ReviewArtifactRequiredBlocker(run.Id, stageId!));
            return;
        }

        if (!_reviewableArtifact.BytesVerified)
        {
            ReportBlocker(ReviewArtifactUnverifiedBlocker(
                run.Id,
                _reviewableArtifact.ArtifactId,
                stageId!,
                kind!));
            return;
        }

        ReportBlocker(ReviewRequestUnavailableBlocker);
    }

    private async Task RequestAssignedReviewCoreAsync()
    {
        var run = _observedRun!;
        var projectId = SelectedProject!.Id;
        var packageId = SelectedPackage!.Id;
        var versionId = SelectedVersion!.Id;
        var projectDisplay = SelectedProject.DisplayName;
        var versionDisplay = SelectedVersion.VersionDisplay;
        var runId = run.Id;

        if (!TryResolvePinnedReviewStage(run, out var capturedStageId, out var capturedKind, out _, out var blocker))
        {
            ReportBlocker(blocker);
            return;
        }

        var captured = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, capturedStageId!, capturedKind!);

        if (captured is null)
        {
            ReportBlocker(ReviewArtifactRequiredBlocker(runId, capturedStageId!));
            return;
        }

        var hash = captured.HashSha256;

        try
        {
            if (!await ConfirmsReviewTargetAsync(projectId, packageId, versionId, runId, capturedStageId!, capturedKind!, hash)
                    .ConfigureAwait(true))
            {
                return;
            }

            var result = await _reviewRequestService!
                .RequestAssignedReviewAsync(runId)
                .ConfigureAwait(true);

            // A new run can replace the old one without changing any picker. Publish the result only
            // beside its actual captured/current run; the run-qualified outcome below still reports
            // everything that happened even when the screen now observes another target.
            if (SelectionIsCurrent(projectId, packageId, versionId)
                && string.Equals(result.RunId, runId, StringComparison.Ordinal)
                && string.Equals(_observedRun?.Id, result.RunId, StringComparison.Ordinal))
            {
                _assignedReviewResult = result;

                OnPropertyChanged(nameof(AssignedReviewResult));
                OnPropertyChanged(nameof(HasAssignedReviewResult));
                OnPropertyChanged(nameof(AssignedReviewStatusDisplay));
                OnPropertyChanged(nameof(AssignedReviewDetailDisplay));
            }

            ReportAssignedReviewOutcome(result, runId, projectId, projectDisplay, packageId, versionId, versionDisplay, capturedStageId!, capturedKind!, hash);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportBlocker(ReviewRequestFailureBlocker(exception));
        }
    }

    /// <summary>
    /// Reports what was actually persisted.
    ///
    /// A refusal is reported in full, because a refusal is the honest answer and the operator has to see
    /// which of the four things was missing: a route that is not a persisted row, an account, profile or
    /// model that is absent, a channel that cannot report an observed route, or verified bytes that could
    /// not be opened. A dispatch is reported with the execution state and the observed route as persisted,
    /// and states plainly that no verdict was created.
    /// </summary>
    private void ReportAssignedReviewOutcome(
        WorkflowReviewRequestResult result,
        string runId,
        string projectId,
        string projectDisplay,
        string packageId,
        string versionId,
        string versionDisplay,
        string stageId,
        string kind,
        string hash)
    {
        var selectionNote = SelectionDifferenceNote(projectId, packageId, versionId);

        RunCommandNotice = string.Create(
            CultureInfo.InvariantCulture,
            $"Назначенное ревью run '{runId}' проекта '{projectId}' ({projectDisplay}), версии '{versionId}' "
                + $"({versionDisplay}), стадия '{stageId}', артефакт '{kind}', хеш {hash}: {result.Detail}"
                + $"{selectionNote}");

        StatusMessage = string.Create(
            CultureInfo.InvariantCulture,
            $"Run '{runId}': {result.StateDisplay}. Вердикт этим действием не создаётся.");
    }

    /// <summary>
    /// Whether the run, the stage it is on, the artifact kind its pinned scheme requires, the hash of its
    /// current stored artifact and the three selected identities are still the ones captured before the
    /// first await.
    ///
    /// This is the same re-read the two product decisions perform, and for the same reason: a run that has
    /// been replaced by a newer one, a stage that has moved and an artifact that has been written since the
    /// click would all otherwise be reviewed under an identity the operator never saw.
    /// </summary>
    private async Task<bool> ConfirmsReviewTargetAsync(
        string projectId,
        string packageId,
        string versionId,
        string runId,
        string stageId,
        string kind,
        string hash)
    {
        if (!SelectionIsCurrent(projectId, packageId, versionId))
        {
            ReportBlocker(RunSelectionChangedBlocker(projectId, versionId, "Ревью не отправлено."));
            return false;
        }

        var activeVersionId = await ResolveActiveVersionIdAsync(projectId, packageId).ConfigureAwait(true);

        if (!SelectionIsCurrent(projectId, packageId, versionId))
        {
            ReportBlocker(RunSelectionChangedBlocker(projectId, versionId, "Ревью не отправлено."));
            return false;
        }

        if (activeVersionId is null)
        {
            ReportBlocker(NoBindingBlocker(projectId, packageId, versionId, "Ревью не отправлено."));
            return false;
        }

        if (!string.Equals(activeVersionId, versionId, StringComparison.Ordinal))
        {
            ReportBlocker(InactiveVersionBlocker(versionId, activeVersionId, "Ревью не отправлено."));
            return false;
        }

        var activeRun = await _runRepository!.GetActiveByProjectIdAsync(projectId).ConfigureAwait(true);
        var storedRun = await _runRepository!.GetByIdAsync(runId).ConfigureAwait(true);

        if (storedRun is null
            || activeRun is null
            || !string.Equals(activeRun.Id, runId, StringComparison.Ordinal))
        {
            ReportBlocker(ReplacedObservedRunReviewBlocker(runId, activeRun?.Id));
            return false;
        }

        if (!string.Equals(storedRun.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(storedRun.WorkflowVersionId, versionId, StringComparison.Ordinal))
        {
            ReportBlocker(StaleObservedRunReviewBlocker(runId, versionId));
            return false;
        }

        if (storedRun.IsTerminal)
        {
            ReportBlocker(TerminalRunReviewBlocker(runId, storedRun.State.ToString()));
            return false;
        }

        if (!string.Equals(storedRun.CurrentStageId, stageId, StringComparison.Ordinal))
        {
            ReportBlocker(StaleReviewStageBlocker(runId, stageId, storedRun.CurrentStageId));
            return false;
        }

        if (!TryResolvePinnedReviewStage(storedRun, out var storedStageId, out var storedKind, out _, out var storedBlocker))
        {
            ReportBlocker(storedBlocker);
            return false;
        }

        if (!string.Equals(storedStageId, stageId, StringComparison.Ordinal)
            || !string.Equals(storedKind, kind, StringComparison.Ordinal))
        {
            ReportBlocker(StaleReviewKindBlocker(runId, stageId, kind, storedKind));
            return false;
        }

        var current = WorkflowArtifactEvidence.SelectCurrent(storedRun.Artifacts, runId, stageId, kind);

        if (current is null)
        {
            ReportBlocker(ReviewArtifactRequiredBlocker(runId, stageId));
            return false;
        }

        if (!string.Equals(current.HashSha256, hash, StringComparison.Ordinal))
        {
            ReportBlocker(ChangedReviewArtifactBlocker(runId, stageId, hash, current.HashSha256));
            return false;
        }

        return true;
    }

    /// <summary>
    /// The read-only reviewer-gate status of the observed run's current stage, recomputed on every
    /// observation: which roles that stage requires, and what the persisted verdicts say about each of them
    /// on the current verified artifact hash.
    ///
    /// This panel only reads. It has no verdict-writing control, never calls
    /// <see cref="IWorkflowRunService.RecordReviewerVerdictAsync"/>, never invokes the Studio's draft form and
    /// never writes a verdict on the operator's behalf: a human click must not be able to create
    /// model-verdict evidence, and a verdict is written only from a persisted reviewer execution whose
    /// observed route matches the route that was assigned. The roles, the stage and the required artifact kind
    /// come from the run's own pinned scheme snapshot; the verdicts come from the run's own stored rows,
    /// matched to the exact role and hash.
    /// </summary>
    public WorkflowReviewGateStatus ReviewGateStatus => _reviewGateStatus;

    /// <summary>One entry per required reviewer role of the observed run's current stage.</summary>
    public IReadOnlyList<WorkflowReviewGateRoleStatus> ReviewGateRoles => _reviewGateStatus.Roles;

    /// <summary>True when the current stage declares reviewer roles and at least one row has to be shown.</summary>
    public bool HasReviewGateRoles => _reviewGateStatus.Roles.Count > 0;

    /// <summary>
    /// The compact status: the run, its current stage, the artifact kind that stage's pinned definition
    /// requires, the hash the verdicts were matched against, whether those bytes were re-hashed for this
    /// observation, and how many required roles currently hold an <c>Approve</c> on exactly that hash.
    ///
    /// The hash is read, never typed, and the count is a fact about verdict rows on this stage - it is not a
    /// claim that the phase is complete. The transition is still decided separately by the run service.
    /// </summary>
    public string ReviewGateRequirementDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"{_reviewGateStatus.StageId} · вид артефакта: {_reviewGateStatus.RequiredArtifactKind ?? "не объявлен"} · "
            + $"хеш вердиктов: {_reviewGateStatus.CurrentArtifactHash ?? "нет"} · "
            + $"байты хранилища: {(_reviewGateStatus.ArtifactBytesVerified ? "проверены" : "не проверены")} · "
            + $"Одобрено: {_reviewGateStatus.ApprovedRoleCount}/{_reviewGateStatus.RequiredRoleCount}");

    /// <summary>
    /// The named refusal or unknown state. It is stated rather than left blank whenever the roles, the
    /// current stage or the artifact requirement could not be established, so an unevaluable gate is never
    /// shown as an empty one.
    /// </summary>
    public string ReviewGateStateDisplay => _reviewGateStatus.Explanation;

    /// <summary>
    /// The permanent contract of this read-only panel: it creates no verdict, the route inside a verdict row
    /// is a stored label rather than an observed model execution, a verdict on a replaced artifact no longer
    /// counts, and a full set of approvals here still does not decide the transition.
    /// </summary>
    public string ReviewGateNotice =>
        "Панель только читает сохранённые вердикты: сама она ничьёго вердикта не создаёт, запись не вызывает и "
            + "черновик редактора схемы не использует. Роль и маршрут берутся из строки вердикта как из записи: "
            + "наблюдения исполнения ревьюера в хранилище пока нет, поэтому панель не утверждает, какая модель "
            + "дала вердикт. Вердикт по заменённому артефакту не засчитывается, а полный набор одобрений не "
            + "означает, что переход состоится — его решает служба запусков.";

    /// <summary>The registered workflow services this screen forwards to its panels; exposed as evidence.</summary>
    public IWorkflowRunTimelineService? TimelineService => _timelineService;

    public IWorkflowStudioService? StudioService => _studioService;

    public IDocumentTemplateService? DocumentTemplateService => _documentTemplateService;

    public IPreCoderGateValidator? PreCoderGateValidator => _preCoderGateValidator;

    public IWorkflowRunRepository? RunRepository => _runRepository;

    public ISessionRepository? SessionRepository => _sessionRepository;

    public IExecutionRepository? ExecutionRepository => _executionRepository;

    public ICheckoutLockService? CheckoutLockService => _checkoutLockService;

    public string LibraryUnavailableNotice =>
        "Библиотека процессов не настроена для этого окна, поэтому пакеты, версии и привязки недоступны.";

    public string PreviewUnavailableNotice =>
        "Служба предпросмотра процессов не настроена для этого окна, поэтому дерево и Markdown-предпросмотр недоступны.";

    public string EmptyStateMessage =>
        IsLibraryAvailable ? "Процессы пока не импортированы." : LibraryUnavailableNotice;

    /// <summary>
    /// Permanent statement of the immutability contract, so the screen never looks like it can edit the
    /// imported original.
    /// </summary>
    public string LibraryNote =>
        "Пакеты, версии и BLOB-объекты неизменяемы: привязка проекта лишь перемещает указатель активной " +
        "версии, а версия никогда не перезаписывается при изменении привязки (ADR-0006).";

    public string DocumentationTruncationNotice =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Предпросмотр основной документации усечён на лимите в {IWorkflowPreviewService.MaxPreviewSizeBytes / 1024} КиБ.");

    public ObservableCollection<WorkflowPackageItemViewModel> Packages { get; } = new();

    public ObservableCollection<WorkflowVersionItemViewModel> Versions { get; } = new();

    public ObservableCollection<WorkflowTreeNodeViewModel> TreeNodes { get; } = new();

    public ObservableCollection<WorkflowBindingViewModel> Bindings { get; } = new();

    public ObservableCollection<Project> Projects { get; } = new();

    /// <summary>
    /// Fresh blockers of the last refused activation. The pointer only moves after every row has been
    /// acknowledged individually and <see cref="ConfirmActivationCommand"/> runs.
    /// </summary>
    public ObservableCollection<ActivationBlockerItemViewModel> ActivationBlockers { get; } = new();

    public bool HasActivationBlockers => ActivationBlockers.Count > 0;

    /// <summary>True only when a blocked action is pending and every blocker row is acknowledged.</summary>
    public bool CanConfirmActivation =>
        !IsBusy &&
        _pendingActivation is not null &&
        SelectionIsCurrent(_pendingActivation.Project.Id, _pendingActivation.Package.Id, _pendingActivation.Version.Id) &&
        ActivationBlockers.Count > 0 &&
        ActivationBlockers.All(blocker => blocker.IsAcknowledged);

    public bool HasPackages => Packages.Count > 0;

    public bool HasVersions => Versions.Count > 0;

    public bool HasTreeNodes => TreeNodes.Count > 0;

    public bool HasBindings => Bindings.Count > 0;

    public bool HasProjects => Projects.Count > 0;

    public ICommand RefreshCommand { get; }

    public ICommand BindToProjectCommand { get; }

    public ICommand SetActiveVersionCommand { get; }

    public ICommand UnbindCommand { get; }

    public ICommand AdaptWorkflowCommand { get; }

    public ICommand RollbackCommand { get; }

    /// <summary>Repeats the refused action with the per-blocker decisions the operator recorded.</summary>
    public ICommand ConfirmActivationCommand { get; }

    /// <summary>Abandons the refused action without acknowledging anything.</summary>
    public ICommand DismissActivationBlockersCommand { get; }

    /// <summary>One-click import of the ZIP package at <see cref="QuickImportPath"/>.</summary>
    public ICommand QuickImportCommand { get; }

    /// <summary>One-click byte-identical export of <see cref="SelectedVersion"/>.</summary>
    public ICommand QuickExportCommand { get; }

    /// <summary>
    /// Starts a real run of the selected project through the composed
    /// <see cref="IWorkflowRunService.StartRunAsync"/>, pinned to the template version the project is
    /// assigned to. This is the product run path; the Studio's coder-gate demonstration is a separate
    /// document check and never starts one.
    /// </summary>
    public ICommand StartAssignedRunCommand { get; }

    /// <summary>
    /// Advances the observed run once through <see cref="IWorkflowRunService.AdvanceStageAsync"/> against
    /// the execution scheme the run was pinned to. It never completes, fails or cancels a run: a stage
    /// result is not a workflow outcome.
    /// </summary>
    public ICommand AdvanceObservedRunCommand { get; }

    /// <summary>
    /// Copies the bytes of the local file at <see cref="StageArtifactPath"/> into the content-addressed
    /// store as the artifact the observed run's own pinned scheme requires at its current stage, and then
    /// re-observes the stored run.
    ///
    /// This is the only product action that supplies artifact bytes. There is no hash input, no blob-store
    /// write and no kind input anywhere on this screen: the run, its stage and the exact artifact kind are
    /// read from the run's pinned scheme snapshot, and the run service is the only writer.
    /// </summary>
    public ICommand AttachStageArtifactCommand { get; }

    /// <summary>
    /// Records a deliberate product approval of the observed run's current pinned stage artifact, against
    /// the hash of the stored artifact whose bytes were verified, and then re-observes the stored run.
    /// </summary>
    public ICommand ApproveObservedArtifactCommand { get; }

    /// <summary>
    /// Records a deliberate product rejection of the same artifact. A rejection is a terminal transition
    /// for the run, so the notice after it says so rather than leaving the operator to infer it.
    /// </summary>
    public ICommand RejectObservedArtifactCommand { get; }

    /// <summary>
    /// Requests the assigned model review of the observed run's current stage artifact. The action resolves
    /// the roles, the routes, the model and the verified bytes itself and offers no input that could stand in
    /// for any of them; it never records a verdict, and a request it cannot dispatch truthfully is refused by
    /// name without persisting anything.
    /// </summary>
    public ICommand RequestAssignedReviewCommand { get; }

    /// <summary>The classifications the operator may record an artifact under, in declared order.</summary>
    public IReadOnlyList<DataClassification> StageArtifactClassifications { get; } = new[]
    {
        DataClassification.PublicSource,
        DataClassification.PrivateSource,
        DataClassification.Restricted
    };

    /// <summary>
    /// The operator's name for an artifact classification. Every control and every notice that prints one
    /// goes through here, so the shipped screen never shows the stored enum name as prose; an undeclared
    /// value is named as such rather than falling back to printing the identifier.
    /// </summary>
    public static string DescribeClassification(DataClassification classification) => classification switch
    {
        DataClassification.PublicSource => "открытый источник",
        DataClassification.PrivateSource => "частный источник",
        DataClassification.Restricted => "ограниченный доступ",
        _ => "не определена"
    };

    /// <summary>
    /// Existing execution selected for collection. The service validates its persisted ownership and
    /// chronology; this operator input does not prove model authorship of the file.
    /// </summary>
    public string StageArtifactExecutionId
    {
        get => _stageArtifactExecutionId;
        set => SetProperty(ref _stageArtifactExecutionId, value ?? string.Empty);
    }

    public bool StageArtifactRequiresExecution
    {
        get
        {
            if (_observedRun is not { IsTemplateBacked: true } run) return false;
            try
            {
                return WorkflowGraphSnapshot.Deserialize(run.TemplateGraphSnapshotJson!, run.Id)
                    .FindNode(run.CurrentStageId)?.Kind == WorkflowNodeKind.ArtifactCollection;
            }
            catch (Exception) { return false; } // The existing stage validation exposes unreadable run evidence.
        }
    }

    /// <summary>Local file path; neither the path nor user input is trusted as content identity.</summary>
    public string StageArtifactPath
    {
        get => _stageArtifactPath;
        set
        {
            if (SetProperty(ref _stageArtifactPath, value ?? string.Empty))
            {
                UpdateCommandStates();
            }
        }
    }

    /// <summary>
    /// The classification recorded with the artifact. It defaults to
    /// <see cref="DataClassification.PrivateSource"/> because an operator-supplied local file is private
    /// by default and an unstated classification must not be the most permissive one.
    /// </summary>
    public DataClassification StageArtifactClassification
    {
        get => _stageArtifactClassification;
        set
        {
            if (SetProperty(ref _stageArtifactClassification, value))
            {
                UpdateCommandStates();
            }
        }
    }

    /// <summary>
    /// Wraps the bounded read window the operator's file is opened into, or nothing in the shipped screen.
    ///
    /// This is a decorator and deliberately not a source: the fault class and the bytes still come from
    /// <see cref="StageArtifactFileInput"/>, so a decorated window can make one step of handling the
    /// operator's file fault - the close of its handle, for instance - but it can never supply an
    /// artifact, a kind or a hash of its own. It exists so the post-commit half of the attach path can be
    /// exercised at all, and the shipped screen leaves it unset.
    /// </summary>
    public Func<Stream, Stream>? StageArtifactContentDecorator { get; set; }

    /// <summary>Local ZIP path used by the one-click import. No file dialog is required.</summary>
    public string QuickImportPath
    {
        get => _quickImportPath;
        set
        {
            if (SetProperty(ref _quickImportPath, value ?? string.Empty))
            {
                UpdateCommandStates();
            }
        }
    }

    /// <summary>Local directory the one-click export writes to. Defaults to the app-data exports folder.</summary>
    public string QuickExportDirectory
    {
        get => _quickExportDirectory;
        set
        {
            if (SetProperty(ref _quickExportDirectory, value ?? string.Empty))
            {
                UpdateCommandStates();
            }
        }
    }

    /// <summary>The target path of the last successful one-click export; empty until one succeeds.</summary>
    public string LastQuickExportPath
    {
        get => _lastQuickExportPath;
        private set
        {
            if (SetProperty(ref _lastQuickExportPath, value))
            {
                OnPropertyChanged(nameof(HasQuickExportPath));
            }
        }
    }

    public bool HasQuickExportPath => !string.IsNullOrWhiteSpace(LastQuickExportPath);

    public bool CanQuickImport =>
        IsImportAvailable &&
        !IsBusy &&
        !string.IsNullOrWhiteSpace(QuickImportPath);

    public bool CanQuickExport =>
        IsExportAvailable &&
        !IsBusy &&
        SelectedVersion is not null &&
        !string.IsNullOrWhiteSpace(QuickExportDirectory);

    public string QuickActionsNote =>
        "Быстрый импорт и экспорт выполняются одним нажатием без модальных диалогов: импорт не меняет "
        + "исходный архив, экспорт побайтово повторяет импортированный оригинал.";

    public WorkflowPackageItemViewModel? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (SetProperty(ref _selectedPackage, value))
            {
                ClearActivationBlockers();
                UpdateCommandStates();
                _ = LoadVersionsAsync();
            }
        }
    }

    public WorkflowVersionItemViewModel? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                ClearActivationBlockers();
                OnPropertyChanged(nameof(HasSelectedVersion));
                InvalidateRunObservationForSelectionChange();
                UpdateCommandStates();
                _ = LoadPreviewAsync();
                _ = ObserveActiveRunAsync();
            }
        }
    }

    public WorkflowTreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                OnPropertyChanged(nameof(HasSelectedNode));
                _ = LoadFilePreviewAsync();
            }
        }
    }

    public WorkflowBindingViewModel? SelectedBinding
    {
        get => _selectedBinding;
        set
        {
            if (SetProperty(ref _selectedBinding, value))
            {
                UpdateCommandStates();
            }
        }
    }

    /// <summary>Project whose binding decides which version is shown as active.</summary>
    public Project? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                ClearActivationBlockers();
                OnPropertyChanged(nameof(SelectedProjectDisplay));
                ++_versionsLoadToken;
                ++_bindingsLoadToken;
                _activeBinding = null;
                OnPropertyChanged(nameof(HasCurrentBinding));
                InvalidateRunObservationForSelectionChange();
                UpdateCommandStates();
                _ = ReloadForProjectAsync();
            }
        }
    }

    public string SelectedProjectDisplay =>
        SelectedProject?.DisplayName ?? "Проект не выбран";

    /// <summary>Binding of the selected project to the selected package, if one exists.</summary>
    public bool HasCurrentBinding => _activeBinding is not null;

    public bool HasSelectedVersion => SelectedVersion is not null;

    public bool HasSelectedNode => SelectedNode is not null;

    public bool HasDocumentation => !string.IsNullOrEmpty(DocumentationContent);

    public bool HasSelectedFileContent => !string.IsNullOrEmpty(SelectedFileContent);

    public string? DocumentationPath
    {
        get => _documentationPath;
        private set
        {
            if (SetProperty(ref _documentationPath, value))
            {
                OnPropertyChanged(nameof(DocumentationPathDisplay));
            }
        }
    }

    public string DocumentationPathDisplay => DocumentationPath ?? "Основная документация не найдена";

    public string DocumentationContent
    {
        get => _documentationContent;
        private set
        {
            if (SetProperty(ref _documentationContent, value))
            {
                OnPropertyChanged(nameof(HasDocumentation));
            }
        }
    }

    /// <summary>Truncation of the primary documentation preview at the 512 KiB limit.</summary>
    public bool IsDocumentationTruncated
    {
        get => _isDocumentationTruncated;
        private set => SetProperty(ref _isDocumentationTruncated, value);
    }

    public string SelectedFileContent
    {
        get => _selectedFileContent;
        private set
        {
            if (SetProperty(ref _selectedFileContent, value))
            {
                OnPropertyChanged(nameof(HasSelectedFileContent));
            }
        }
    }

    /// <summary>True when the selected file is binary and therefore has no text preview.</summary>
    public bool IsSelectedFileBinary
    {
        get => _isSelectedFileBinary;
        private set
        {
            if (SetProperty(ref _isSelectedFileBinary, value))
            {
                OnPropertyChanged(nameof(SelectedFileNotice));
                OnPropertyChanged(nameof(HasSelectedFileNotice));
            }
        }
    }

    public bool IsSelectedFileTruncated
    {
        get => _isSelectedFileTruncated;
        private set
        {
            if (SetProperty(ref _isSelectedFileTruncated, value))
            {
                OnPropertyChanged(nameof(SelectedFileNotice));
                OnPropertyChanged(nameof(HasSelectedFileNotice));
            }
        }
    }

    public string SelectedFileNotice => IsSelectedFileBinary
        ? "Выбранный файл является бинарным; текстовый предпросмотр не отображается."
        : IsSelectedFileTruncated
            ? "Предпросмотр выбранного файла был усечён на 512 КиБ."
            : string.Empty;

    public bool HasSelectedFileNotice => IsSelectedFileBinary || IsSelectedFileTruncated;

    /// <summary>Optional route policy recorded with a binding.</summary>
    public string? RoutePolicyId
    {
        get => _routePolicyId;
        set => SetProperty(ref _routePolicyId, value);
    }

    /// <summary>Package name used by the testable import entry points and the shipped import box.</summary>
    public string ImportPackageName { get; set; } = "Imported workflow";

    public string Blocker
    {
        get => _blocker;
        private set
        {
            if (SetProperty(ref _blocker, value))
            {
                OnPropertyChanged(nameof(HasBlocker));
            }
        }
    }

    public bool HasBlocker => !string.IsNullOrWhiteSpace(Blocker);

    /// <summary>Summary of the last completed action. Empty until one succeeds.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>
    /// Whether the project, package and version pickers accept operator input.
    ///
    /// The product run path captures those three identities before its first await, so the pickers are
    /// frozen for the whole busy window and a click can no longer switch the selection under a command
    /// that is already committing its side effect. A selection changed programmatically is not blocked by
    /// this: it is still reported truthfully, naming what was persisted and what differs on screen now.
    /// </summary>
    public bool IsRunSelectionEditable => !IsBusy;

    /// <summary>
    /// Whether the local path and the classification accept operator input.
    ///
    /// The command captures both before its first await, so they are frozen for the whole busy window: a
    /// click must not be able to swap the file or the classification under a command that is already
    /// committing its side effect. A value changed programmatically is not blocked by this, and it is
    /// reported truthfully afterwards rather than denied.
    /// </summary>
    public bool IsStageArtifactInputEditable => !IsBusy;

    /// <summary>
    /// The named reason an approver identity cannot be established, shown where the decision buttons are.
    /// There is no fallback name: a decision nobody can be named for is not recorded.
    /// </summary>
    private static string UserApprovalIdentityBlocker =>
        "Локальный вход Windows этого окна недоступен, поэтому решение пользователя не записывается.";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                // The freeze flag is bound to the shipped pickers, so a silent recomputation would leave
                // the real controls clickable for the whole busy window while only the commands notice.
                OnPropertyChanged(nameof(IsRunSelectionEditable));
                OnPropertyChanged(nameof(IsStageArtifactInputEditable));
                OnPropertyChanged(nameof(IsUserApprovalInputEditable));
                UpdateCommandStates();
            }
        }
    }

    /// <summary>
    /// Binding moves the active pointer, so it needs both the binding service and the activation gate.
    /// Without the gate the command stays unoffered instead of bypassing validation.
    /// </summary>
    public bool CanBindToProject =>
        IsBindingAvailable &&
        IsActivationAvailable &&
        !IsBusy &&
        SelectedProject is not null &&
        SelectedPackage is not null &&
        SelectedVersion is not null;

    public bool CanSetActiveVersion =>
        IsBindingAvailable &&
        IsActivationAvailable &&
        !IsBusy &&
        HasCurrentBinding &&
        SelectedVersion is not null &&
        !SelectedVersion.IsActiveInCurrentProject;

    public bool CanUnbind => IsBindingAvailable && !IsBusy && SelectedBinding is not null;

    /// <summary>Offered when a version and a project are selected and adaptation is configured.</summary>
    public bool CanAdaptWorkflow =>
        IsAdaptationAvailable &&
        !IsBusy &&
        SelectedProject is not null &&
        SelectedPackage is not null &&
        SelectedVersion is not null;

    /// <summary>Offered for any selected version that is not already active in the selected project.</summary>
    public bool CanRollback =>
        IsActivationAvailable &&
        CanSetActiveVersion &&
        SelectedVersion is not null &&
        !SelectedVersion.IsActiveInCurrentProject;

    /// <summary>
    /// Loads projects, packages and the bindings of the selected project. The previous package selection
    /// is kept when it still exists, and its versions and preview are reloaded.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_packageRepository is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Blocker = string.Empty;

        try
        {
            await LoadProjectsAsync().ConfigureAwait(true);

            var packages = await _packageRepository.ListAsync().ConfigureAwait(true);
            var previousPackageId = SelectedPackage?.Id;

            Packages.Clear();

            foreach (var package in packages)
            {
                Packages.Add(new WorkflowPackageItemViewModel(package));
            }

            OnPropertyChanged(nameof(HasPackages));

            SelectedPackage = Packages.FirstOrDefault(package => package.Id == previousPackageId)
                ?? Packages.FirstOrDefault();

            await LoadBindingsAsync().ConfigureAwait(true);
            await LoadVersionsAsync().ConfigureAwait(true);
            await ObserveActiveRunAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = UiErrorMessage.Describe(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Loads the versions of <see cref="SelectedPackage"/> and marks the one that is active in
    /// <see cref="SelectedProject"/>. The active flag comes from the binding row, never from the version.
    /// The selection follows the active version of the project, because the console observes a run only
    /// for the selected version; a project without a binding keeps the previous selection.
    /// </summary>
    public async Task LoadVersionsAsync()
    {
        var token = ++_versionsLoadToken;
        var package = SelectedPackage;
        var project = SelectedProject;

        // The version items are recreated below, so the current selection has to be remembered by id:
        // only a project with a binding decides the selection by the active version.
        var previousVersionId = _selectedVersion?.Id;

        Versions.Clear();
        OnPropertyChanged(nameof(HasVersions));

        _activeBinding = null;
        OnPropertyChanged(nameof(HasCurrentBinding));

        SelectedVersion = null;
        UpdateCommandStates();

        if (_versionRepository is null || package is null)
        {
            return;
        }

        try
        {
            var versions = await _versionRepository.ListByPackageIdAsync(package.Id).ConfigureAwait(true);
    
            WorkflowBinding? activeBinding = null;
    
            if (_bindingRepository is not null && project is not null)
            {
                activeBinding = await _bindingRepository
                    .GetByProjectAndPackageAsync(project.Id, package.Id)
                    .ConfigureAwait(true);
            }
    
            if (token != _versionsLoadToken || !ReferenceEquals(package, SelectedPackage)
                || !ReferenceEquals(project, SelectedProject))
            {
                return;
            }
    
            Versions.Clear();
    
            foreach (var version in versions)
            {
                Versions.Add(new WorkflowVersionItemViewModel(version)
                {
                    IsActiveInCurrentProject = activeBinding is not null
                        && string.Equals(activeBinding.ActiveVersionId, version.Id, StringComparison.Ordinal)
                });
            }
    
            _activeBinding = activeBinding;
            OnPropertyChanged(nameof(HasCurrentBinding));
            OnPropertyChanged(nameof(HasVersions));
    
            // The repository lists the oldest version first, so blindly taking the first entry would leave a
            // live run of the active v2 unobserved on every refresh. The version the binding marks active wins;
            // without an active version the previous selection survives, and only a project with no prior
            // selection falls back to the first version. A deliberate selection of another version afterwards
            // stays untouched and keeps the version mismatch fail-closed.
            SelectedVersion = Versions.FirstOrDefault(version => version.IsActiveInCurrentProject)
                ?? Versions.FirstOrDefault(version => version.Id == previousVersionId)
                ?? Versions.FirstOrDefault();
            UpdateCommandStates();
        }
        catch (Exception exception)
        {
            if (token == _versionsLoadToken && ReferenceEquals(package, SelectedPackage)
                && ReferenceEquals(project, SelectedProject))
            {
                Blocker = $"Не удалось загрузить версии сценария ({exception.GetType().Name}).";
            }
        }
    }

    /// <summary>
    /// Loads the tree and primary Markdown of the selected version strictly by
    /// <see cref="WorkflowVersion.BlobId"/>, then groups the flat nodes back into a tree.
    /// </summary>
    public async Task LoadPreviewAsync()
    {
        var token = ++_previewLoadToken;
        var version = SelectedVersion;

        ActivityMonitor.LoadActiveVersion(version?.Version);

        ClearPreview();

        if (_previewService is null || version is null)
        {
            return;
        }

        try
        {
            var tree = await _previewService
                .GetTreePreviewAsync(version.Version.BlobId)
                .ConfigureAwait(true);

            if (token != _previewLoadToken || !ReferenceEquals(version, SelectedVersion))
            {
                return;
            }

            DocumentationPath = tree.PrimaryDocumentationPath;
            DocumentationContent = tree.PrimaryDocumentationContent ?? string.Empty;
            IsDocumentationTruncated = tree.IsPrimaryDocumentationTruncated;

            foreach (var node in BuildTreeNodes(tree.Nodes))
            {
                TreeNodes.Add(node);
            }

            OnPropertyChanged(nameof(HasTreeNodes));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (token == _previewLoadToken && ReferenceEquals(version, SelectedVersion))
                Blocker = UiErrorMessage.Describe(exception);
        }
    }

    /// <summary>
    /// Loads the preview of <see cref="SelectedNode"/> by the selected version blob. Directories have no
    /// file preview; binary files are reported as binary instead of being decoded.
    /// </summary>
    public async Task LoadFilePreviewAsync()
    {
        var token = ++_filePreviewLoadToken;
        var node = SelectedNode;
        var version = SelectedVersion;

        SelectedFileContent = string.Empty;
        IsSelectedFileBinary = false;
        IsSelectedFileTruncated = false;
        OnPropertyChanged(nameof(SelectedFileNotice));

        if (_previewService is null || node is null || version is null || node.IsDirectory)
        {
            return;
        }

        try
        {
            var preview = await _previewService
                .GetFilePreviewAsync(version.Version.BlobId, node.Path)
                .ConfigureAwait(true);

            if (token != _filePreviewLoadToken || !ReferenceEquals(node, SelectedNode)
                || !ReferenceEquals(version, SelectedVersion))
            {
                return;
            }

            SelectedFileContent = preview.Content ?? string.Empty;
            IsSelectedFileBinary = preview.IsBinary;
            IsSelectedFileTruncated = preview.IsTruncated;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (token == _filePreviewLoadToken && ReferenceEquals(node, SelectedNode)
                && ReferenceEquals(version, SelectedVersion))
                Blocker = UiErrorMessage.Describe(exception);
        }
    }

    /// <summary>
    /// Binds <see cref="SelectedPackage"/> to <see cref="SelectedProject"/> at
    /// <see cref="SelectedVersion"/>. The command goes through
    /// <see cref="IWorkflowActivationService.ActivateVersionAsync"/> and therefore refuses a candidate
    /// with unacknowledged blockers; a repeated bind keeps the original binding id and creation time.
    /// </summary>
    public async Task BindToProjectAsync()
    {
        if (!CanBindToProject)
        {
            return;
        }

        var project = SelectedProject!;
        var package = SelectedPackage!;
        var version = SelectedVersion!;

        await ExecuteActivationAsync(
                PendingActivationKind.Bind,
                project,
                package,
                version,
                routePolicyId: NormalizeRoutePolicyId(),
                isNewBinding: _activeBinding is null,
                acknowledgedBlockerIssues: Array.Empty<AdaptationValidationIssue>(),
                isRetry: false)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Moves the active-version pointer of an existing binding through the explicit activation gate, so
    /// an unacknowledged blocker leaves the pointer untouched. Only the binding row changes; the
    /// version's ActivatedAtUtc, the package and the blob stay untouched (ADR-0006 §1.7).
    /// </summary>
    public async Task SetActiveVersionAsync()
    {
        if (!CanSetActiveVersion)
        {
            return;
        }

        var project = SelectedProject!;
        var package = SelectedPackage!;
        var version = SelectedVersion!;

        await ExecuteActivationAsync(
                PendingActivationKind.SetActiveVersion,
                project,
                package,
                version,
                routePolicyId: null,
                isNewBinding: false,
                acknowledgedBlockerIssues: Array.Empty<AdaptationValidationIssue>(),
                isRetry: false)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the adaptation dialog for the selected version, package and project. The dialog performs
    /// the pre-send preview and the candidate session; nothing is sent or saved until an explicit
    /// command inside the dialog (ТЗ §6.14).
    /// </summary>
    public async Task AdaptWorkflowAsync()
    {
        if (!CanAdaptWorkflow)
        {
            return;
        }

        await AdaptationDialog
            .OpenForVersionAsync(SelectedVersion!, SelectedPackage!, SelectedProject!)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Rolls the active-version pointer back to the selected version through the activation service,
    /// which re-validates the target with the same fail-closed gate. Only the binding row changes; the
    /// version blob stays byte-identical (ADR-0006 §1.7).
    /// </summary>
    public async Task RollbackAsync()
    {
        if (!CanRollback)
        {
            return;
        }

        var project = SelectedProject!;
        var package = SelectedPackage!;
        var version = SelectedVersion!;

        await ExecuteActivationAsync(
                PendingActivationKind.Rollback,
                project,
                package,
                version,
                routePolicyId: null,
                isNewBinding: false,
                acknowledgedBlockerIssues: Array.Empty<AdaptationValidationIssue>(),
                isRetry: false)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Repeats the refused action after the operator acknowledged every blocker issue. Nothing runs while
    /// any issue is left undecided, so blockers are never acknowledged silently.
    /// </summary>
    public async Task ConfirmActivationAsync()
    {
        var pending = _pendingActivation;

        if (!CanConfirmActivation || pending is null)
        {
            return;
        }

        var acknowledged = ActivationBlockers
            .Select(blocker => blocker.Issue)
            .ToArray();

        await ExecuteActivationAsync(
                pending.Kind,
                pending.Project,
                pending.Package,
                pending.Version,
                pending.RoutePolicyId,
                pending.IsNewBinding,
                acknowledged,
                isRetry: true)
            .ConfigureAwait(true);
    }

    /// <summary>Drops the refused action and its blocker rows without moving the pointer.</summary>
    public void DismissActivationBlockers()
    {
        ClearActivationBlockers();
        Blocker = string.Empty;
    }

    private async Task ExecuteActivationAsync(
        PendingActivationKind kind,
        Project project,
        WorkflowPackageItemViewModel package,
        WorkflowVersionItemViewModel version,
        string? routePolicyId,
        bool isNewBinding,
        IReadOnlyList<AdaptationValidationIssue> acknowledgedBlockerIssues,
        bool isRetry)
    {
        if (isRetry && _pendingActivation is null)
        {
            return;
        }

        IsBusy = true;
        Blocker = string.Empty;
        StatusMessage = string.Empty;

        if (!isRetry)
        {
            ClearActivationBlockers();
        }

        var shouldReload = true;

        try
        {
            if (kind == PendingActivationKind.Rollback)
            {
                var rollback = await _activationService!
                    .RollbackToVersionAsync(
                        project.Id,
                        package.Id,
                        version.Id,
                        acknowledgeBlockers: false,
                        acknowledgedBlockerKinds: null,
                        acknowledgedBlockerIssues,
                        CancellationToken.None)
                    .ConfigureAwait(true);

                if (rollback.IsSuccess)
                {
                    ClearActivationBlockers();
                    StatusMessage =
                        $"Активная версия возвращена к {version.VersionDisplay} для проекта " +
                        $"'{project.DisplayName}'. Исходный BLOB остаётся байт-в-байт идентичным.";
                }
                else
                {
                    HandleActivationFailure(
                        kind,
                        project,
                        package,
                        version,
                        routePolicyId,
                        isNewBinding,
                        rollback.IsBlocked,
                        rollback.Validation,
                        rollback.ErrorMessage,
                        acknowledgedBlockerIssues);

                    shouldReload = !rollback.IsBlocked;
                }
            }
            else
            {
                var activation = await _activationService!
                    .ActivateVersionAsync(
                        new WorkflowActivationRequest(
                            project.Id,
                            package.Id,
                            version.Id,
                            acknowledgeBlockers: false,
                            routePolicyId,
                            acknowledgedBlockerKinds: null,
                            acknowledgedBlockerIssues,
                            preserveExistingRoutePolicy: kind == PendingActivationKind.SetActiveVersion),
                        CancellationToken.None)
                    .ConfigureAwait(true);

                if (activation.IsSuccess)
                {
                    ClearActivationBlockers();

                    StatusMessage = kind == PendingActivationKind.Bind
                        ? (isNewBinding
                            ? $"Пакет '{package.Name}' привязан к проекту '{project.DisplayName}' на версии {version.VersionDisplay}."
                            : $"Привязка пакета '{package.Name}' к проекту '{project.DisplayName}' обновлена на версии {version.VersionDisplay}.")
                        : $"Активирована версия {version.VersionDisplay} пакета '{package.Name}' для проекта '{project.DisplayName}'.";
                }
                else
                {
                    HandleActivationFailure(
                        kind,
                        project,
                        package,
                        version,
                        routePolicyId,
                        isNewBinding,
                        activation.IsBlocked,
                        activation.Validation,
                        activation.ErrorMessage,
                        acknowledgedBlockerIssues);

                    shouldReload = !activation.IsBlocked;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ClearActivationBlockers();
            Blocker = UiErrorMessage.Describe(exception);
        }
        finally
        {
            IsBusy = false;
        }

        if (shouldReload)
        {
            await LoadBindingsAsync().ConfigureAwait(true);
            await LoadVersionsAsync().ConfigureAwait(true);
        }
    }

    private void HandleActivationFailure(
        PendingActivationKind kind,
        Project project,
        WorkflowPackageItemViewModel package,
        WorkflowVersionItemViewModel version,
        string? routePolicyId,
        bool isNewBinding,
        bool isBlocked,
        WorkflowActivationValidationResult? validation,
        string? errorMessage,
        IReadOnlyList<AdaptationValidationIssue> acknowledgedBlockerIssues)
    {
        Blocker = errorMessage ?? "Операция активации отклонена.";

        if (!isBlocked || validation is null || !validation.HasBlockers)
        {
            ClearActivationBlockers();
            return;
        }

        _pendingActivation = new PendingActivation(
            kind,
            project,
            package,
            version,
            routePolicyId,
            isNewBinding);

        ActivationBlockers.Clear();

        // Decisions survive only when the revalidation returned exactly the issues that were confirmed.
        // A new, removed or changed issue invalidates them, so no stale checkmark is reused by kind.
        var retainDecisions = validation.IsFullyAcknowledgedBy(acknowledgedBlockerIssues);

        foreach (var issue in validation.Issues)
        {
            ActivationBlockers.Add(new ActivationBlockerItemViewModel(
                issue,
                isAcknowledged: retainDecisions,
                onChanged: UpdateCommandStates));
        }

        OnPropertyChanged(nameof(HasActivationBlockers));
        UpdateCommandStates();
    }

    private void ClearActivationBlockers()
    {
        _pendingActivation = null;
        ActivationBlockers.Clear();
        OnPropertyChanged(nameof(HasActivationBlockers));
        UpdateCommandStates();
    }

    /// <summary>Removes the selected binding. Packages and versions are not touched.</summary>
    public async Task UnbindAsync()
    {
        if (!CanUnbind)
        {
            return;
        }

        var binding = SelectedBinding!;

        IsBusy = true;
        Blocker = string.Empty;
        StatusMessage = string.Empty;
        ClearActivationBlockers();

        try
        {
            var removed = await _bindingService!
                .UnbindWorkflowAsync(binding.Binding.ProjectId, binding.Binding.WorkflowPackageId)
                .ConfigureAwait(true);

            StatusMessage = removed
                ? $"Привязка '{binding.WorkflowPackageId}' к проекту '{binding.ProjectDisplay}' удалена."
                : $"Привязка '{binding.WorkflowPackageId}' к проекту '{binding.ProjectDisplay}' уже была удалена.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = UiErrorMessage.Describe(exception);
        }
        finally
        {
            IsBusy = false;
        }

        await LoadBindingsAsync().ConfigureAwait(true);
        await LoadVersionsAsync().ConfigureAwait(true);
    }

    /// <summary>Imports a ZIP archive through the import service. No file dialog is involved.</summary>
    public async Task<WorkflowImportResult?> ImportZipAsync(Stream stream, string packageName)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (_importService is null)
        {
            Blocker = "Служба импорта процессов не настроена для этого окна.";
            return null;
        }

        WorkflowImportResult result;

        IsBusy = true;
        Blocker = string.Empty;
        StatusMessage = string.Empty;

        try
        {
            result = await _importService
                .ImportZipAsync(stream, packageName)
                .ConfigureAwait(true);

            StatusMessage = result.IsDuplicate
                ? $"'{packageName}' уже существует для импортированного оригинала; используется повторно."
                : $"Импортирован пакет '{packageName}' как неизменяемый пакет.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = UiErrorMessage.Describe(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
        SelectedPackage = Packages.FirstOrDefault(package => package.Id == result.Package.Id)
            ?? SelectedPackage;

        return result;
    }

    /// <summary>Imports a ZIP archive from a path through the import service.</summary>
    public async Task<WorkflowImportResult?> ImportFromZipPathAsync(string zipPath, string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);

        if (_importService is null)
        {
            Blocker = "Служба импорта процессов не настроена для этого окна.";
            return null;
        }

        await using var stream = File.OpenRead(zipPath);

        return await ImportZipAsync(stream, packageName).ConfigureAwait(true);
    }

    /// <summary>Exports a version byte-identical to the imported original through the export service.</summary>
    public Task ExportVersionAsync(string versionId, string targetZipPath) =>
        ExportVersionCoreAsync(versionId, targetZipPath, createOnly: false);

    private async Task ExportVersionCoreAsync(string versionId, string targetZipPath, bool createOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetZipPath);

        if (_exportService is null)
        {
            Blocker = "Служба экспорта процессов не настроена для этого окна.";
            return;
        }

        IsBusy = true;
        Blocker = string.Empty;
        StatusMessage = string.Empty;

        try
        {
            if (createOnly) await _exportService.ExportToNewFileAsync(versionId, targetZipPath).ConfigureAwait(true);
            else await _exportService.ExportToFileAsync(versionId, targetZipPath).ConfigureAwait(true);

            StatusMessage = $"Версия '{versionId}' экспортирована в '{targetZipPath}'.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = UiErrorMessage.Describe(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One-click import of <see cref="QuickImportPath"/>. The imported original stays immutable; a
    /// repeated import of the same bytes reuses the existing package.
    /// </summary>
    public async Task<WorkflowImportResult?> QuickImportAsync()
    {
        if (!CanQuickImport)
        {
            return null;
        }

        try
        {
            var result = await ImportFromZipPathAsync(QuickImportPath, ImportPackageName)
                .ConfigureAwait(true);
            StatusMessage = result is null
                ? StatusMessage
                : $"Быстрый импорт: '{result.Package.Name}' ({(result.IsDuplicate ? "дубликат переиспользован" : "новый пакет")}).";
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = UiErrorMessage.Describe(exception);
            return null;
        }
    }

    /// <summary>
    /// One-click byte-identical export of the selected version into <see cref="QuickExportDirectory"/>.
    /// The target file name is derived from the package and version; nothing is overwritten silently.
    /// </summary>
    public async Task<string?> QuickExportAsync()
    {
        if (!CanQuickExport)
        {
            return null;
        }

        var version = SelectedVersion!;
        var package = SelectedPackage;
        var fileName = BuildQuickExportFileName(package?.Name, version);
        var targetPath = Path.Combine(QuickExportDirectory, fileName);

        try
        {
            Directory.CreateDirectory(QuickExportDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            Blocker = $"Каталог экспорта недоступен: {UiErrorMessage.Describe(exception)}";
            return null;
        }

        if (File.Exists(targetPath))
        {
            Blocker =
                $"Файл '{targetPath}' уже существует; быстрый экспорт не перезаписывает его. "
                + "Укажите другой каталог или удалите файл вручную.";
            return null;
        }

        await ExportVersionCoreAsync(version.Id, targetPath, createOnly: true).ConfigureAwait(true);

        if (HasBlocker)
        {
            return null;
        }

        LastQuickExportPath = targetPath;
        StatusMessage = $"Быстрый экспорт: версия '{version.VersionDisplay}' → '{targetPath}'.";
        return targetPath;
    }

    private static string BuildQuickExportFileName(string? packageName, WorkflowVersionItemViewModel version)
    {
        var baseName = string.IsNullOrWhiteSpace(packageName) ? "workflow" : packageName!;
        var sanitized = new string(baseName
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)
            .ToArray());

        return $"{sanitized}-v{version.Version.VersionNumber}.zip";
    }

    /// <summary>
    /// Starts a real, persisted run for the selected project through the composed run service, pinned to
    /// the template version the project is assigned to, and then re-observes the stored run.
    ///
    /// The identities are read from the database at command time, not from the screen's cached binding flag
    /// and not from the Studio's edited graph: the binding row is re-read and its active version must still
    /// be the selected one, and a run of another workflow version that the console simply is not showing
    /// still blocks the start. A refused start never claims success - the blocker is reported, the run
    /// service's own template blockers keep their name, and the panels keep whatever they actually observe.
    ///
    /// The command also never denies what already happened: once the run service has returned, the run
    /// exists, so a selection that changed during the await is reported as a difference from what was
    /// persisted instead of being turned back into "не создан".
    /// </summary>
    public async Task StartAssignedRunAsync()
    {
        if (CanStartAssignedRun)
        {
            await ExecuteRunCommandAsync(StartAssignedRunCoreAsync).ConfigureAwait(true);
            return;
        }

        // The button is unoffered by exactly this flag, so a click can never reach this branch. A direct
        // invocation still has to fail closed, and it fails closed out loud: an observed non-terminal run
        // gets the same named refusal the repository re-read inside the command would report, so the
        // authoritative duplicate-run refusal outlives the enablement that now hides it from the operator.
        if (CanInspectSelectedProjectForStart)
        {
            ReportObservedRunStartRefusal();
        }
    }

    /// <summary>
    /// Whether a start could be attempted at all, ignoring whether the console can already see a run that
    /// blocks it. It is the direct-invocation guard of the command, so a refusal is still named instead of
    /// becoming a silent no-op.
    /// </summary>
    private bool CanInspectSelectedProjectForStart =>
        IsRunExecutionAvailable &&
        IsRunObservationAvailable &&
        !IsBusy &&
        SelectedProject is not null &&
        SelectedPackage is not null &&
        SelectedVersion is not null;

    /// <summary>
    /// Names why a start is refused for a selection the screen can already see. Only the duplicate-run
    /// condition is reported here: every other reason for a start not being offered is an absence of a
    /// selection, which has its own named notice already, and inventing a blocker for it would be noise.
    /// </summary>
    private void ReportObservedRunStartRefusal()
    {
        if (_observedRun is { IsTerminal: false } run)
        {
            ReportBlocker(ActiveRunConflictBlocker(run));
        }
    }

    /// <summary>
    /// Runs one run command with the busy flag held across the whole operation, including the observation
    /// that follows it, so the buttons cannot be offered again while the panels are still stale.
    /// </summary>
    private async Task ExecuteRunCommandAsync(Func<Task> core)
    {
        IsBusy = true;
        Blocker = string.Empty;
        StatusMessage = string.Empty;
        RunCommandNotice = string.Empty;

        try
        {
            await core().ConfigureAwait(true);

            // The panels are refreshed from the stored run for the success path and for the refusal path
            // alike: what they show is always what the repository holds, never a locally built object.
            await ObserveActiveRunAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartAssignedRunCoreAsync()
    {
        // The selection is captured by id before the first await and rechecked afterwards, so a project,
        // package or version the operator switched away from mid-command cannot start a run of its own.
        // The display names are captured with the ids, because the success message must name what was
        // actually acted on even when the selection has moved on by the time the run service returns.
        var projectId = SelectedProject!.Id;
        var packageId = SelectedPackage!.Id;
        var versionId = SelectedVersion!.Id;
        var projectDisplay = SelectedProject.DisplayName;
        var versionDisplay = SelectedVersion.VersionDisplay;

        try
        {
            if (!SelectionIsCurrent(projectId, packageId, versionId))
            {
                ReportBlocker(RunSelectionChangedBlocker(projectId, versionId));
                return;
            }

            var activeVersionId = await ResolveActiveVersionIdAsync(projectId, packageId).ConfigureAwait(true);

            if (!SelectionIsCurrent(projectId, packageId, versionId))
            {
                ReportBlocker(RunSelectionChangedBlocker(projectId, versionId));
                return;
            }

            if (activeVersionId is null)
            {
                ReportBlocker(NoBindingBlocker(projectId, packageId, versionId));
                return;
            }

            if (!string.Equals(activeVersionId, versionId, StringComparison.Ordinal))
            {
                ReportBlocker(InactiveVersionBlocker(versionId, activeVersionId));
                return;
            }

            // The conflict check is the project's own active run, not the one the console happens to
            // display: a run of another workflow version is invisible here and would still be a second
            // run for the same project.
            var activeRun = await _runRepository!
                .GetActiveByProjectIdAsync(projectId)
                .ConfigureAwait(true);

            if (!SelectionIsCurrent(projectId, packageId, versionId))
            {
                ReportBlocker(RunSelectionChangedBlocker(projectId, versionId));
                return;
            }

            if (activeRun is not null && !activeRun.IsTerminal)
            {
                ReportBlocker(ActiveRunConflictBlocker(activeRun));
                return;
            }

            var started = await _runService!
                .StartRunAsync(projectId, packageId, versionId)
                .ConfigureAwait(true);

            // From here on the side effect is already committed: the run service wrote the run row before
            // it returned. A selection that moved while the service was awaited is therefore a display
            // mismatch and never a refused create, so RunSelectionChangedBlocker is deliberately not used
            // here - claiming "не создан" over a persisted run would be the one untrue thing this screen
            // must never say.
            var startedRunId = started.Id;
            var selectionNote = SelectionDifferenceNote(projectId, packageId, versionId);

            // The returned aggregate is never handed to the panels: only its identity is read for the
            // message, and the panels themselves are refreshed from the stored run, so what they show is
            // what the database actually holds.
            RunCommandNotice = string.Create(
                CultureInfo.InvariantCulture,
                $"Run '{startedRunId}' создан для проекта '{projectId}' ({projectDisplay}) и версии '{versionId}' ({versionDisplay}) и закреплён за назначенной версией шаблона.{selectionNote}");
            StatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"Запущен run '{startedRunId}' проекта '{projectDisplay}' на версии {versionDisplay}.{selectionNote}");
        }
        catch (WorkflowTemplateExecutionBlockedException exception)
        {
            // The refusal keeps its own named blocker; a generic failure message would hide which
            // assignment or graph property made the run impossible.
            Blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Запуск заблокирован [{exception.Blocker}]: {UiErrorMessage.Describe(exception)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = UiErrorMessage.Describe(exception);
        }
    }

    /// <summary>
    /// Advances the observed run exactly one stage through the composed run service and then re-observes
    /// the stored run.
    ///
    /// The run acted on is the one the console observes for the selected project, its id is re-checked
    /// against the re-read binding, and the transition target is read from the execution scheme the run
    /// was pinned to. A run sitting on the last stage therefore has no transition offered at all
    /// (<see cref="CanAdvanceObservedRun"/>) and stops here with a named reason even though its state is
    /// still Running: reaching TerminalOutcome is a stage, not a workflow outcome, and only an explicit
    /// terminal command - which this screen does not offer - may set one.
    ///
    /// As with the start, a selection that changed after the transition was stored is a display mismatch
    /// and is reported as one; the transition itself is never denied.
    /// </summary>
    public async Task AdvanceObservedRunAsync()
    {
        if (CanAdvanceObservedRun)
        {
            await ExecuteRunCommandAsync(AdvanceObservedRunCoreAsync).ConfigureAwait(true);
            return;
        }

        // The button is unoffered by exactly this flag, so a click can never reach this branch. A direct
        // invocation still has to fail closed, and it fails closed out loud: an observed run that cannot
        // move past its pinned current stage gets that named reason here instead of a silent no-op.
        if (CanInspectObservedRunForAdvance)
        {
            ReportObservedRunAdvanceRefusal();
        }
    }

    /// <summary>
    /// Names why an observed run cannot take another step. It is the click-time guard of the command,
    /// shared by the disabled button's enablement and by a direct invocation, so both surfaces always
    /// state the same reason and neither of them invents a completion.
    /// </summary>
    private void ReportObservedRunAdvanceRefusal()
    {
        var run = _observedRun;

        if (run is null)
        {
            return;
        }

        if (run.IsTerminal)
        {
            ReportBlocker(TerminalRunBlocker(run.Id, run.State.ToString()));
            return;
        }

        if (!TryResolvePinnedNextStageId(run, out _, out var schemeBlocker))
        {
            ReportBlocker(schemeBlocker);
        }
    }

    private async Task AdvanceObservedRunCoreAsync()
    {
        var run = _observedRun!;
        var projectId = SelectedProject!.Id;
        var packageId = SelectedPackage!.Id;
        var versionId = SelectedVersion!.Id;
        var runId = run.Id;
        var projectDisplay = SelectedProject.DisplayName;
        var versionDisplay = SelectedVersion.VersionDisplay;
        var advancedFromStageId = run.CurrentStageId;

        try
        {
            if (!SelectionIsCurrent(projectId, packageId, versionId))
            {
                ReportBlocker(RunSelectionChangedBlocker(projectId, versionId, "Переход не выполнен."));
                return;
            }

            var activeVersionId = await ResolveActiveVersionIdAsync(projectId, packageId).ConfigureAwait(true);

            if (!SelectionIsCurrent(projectId, packageId, versionId))
            {
                ReportBlocker(RunSelectionChangedBlocker(projectId, versionId, "Переход не выполнен."));
                return;
            }

            if (activeVersionId is null)
            {
                ReportBlocker(NoBindingBlocker(projectId, packageId, versionId, "Переход не выполнен."));
                return;
            }

            if (!string.Equals(activeVersionId, versionId, StringComparison.Ordinal))
            {
                ReportBlocker(InactiveVersionBlocker(versionId, activeVersionId, "Переход не выполнен."));
                return;
            }

            if (!string.Equals(run.ProjectId, projectId, StringComparison.Ordinal)
                || !string.Equals(run.WorkflowVersionId, versionId, StringComparison.Ordinal))
            {
                ReportBlocker(StaleObservedRunBlocker(runId, versionId));
                return;
            }

            if (run.IsTerminal)
            {
                ReportBlocker(TerminalRunBlocker(runId, run.State.ToString()));
                return;
            }

            if (!TryResolvePinnedNextStageId(run, out _, out var schemeBlocker))
            {
                ReportBlocker(schemeBlocker);
                return;
            }

            var advanced = await _runService!
                .AdvanceStageAsync(
                    runId,
                    $"Оператор выполнил переход в '{advancedFromStageId}' на экране «Процессы».")
                .ConfigureAwait(true);

            // The transition is already stored, so a selection that moved during the service await is a
            // display mismatch and not a refused advance: the persisted run, its stage and the identities
            // it was advanced under are reported, and RunSelectionChangedBlocker stays unused past the
            // side effect.
            var selectionNote = SelectionDifferenceNote(projectId, packageId, versionId);
            var advancedToStageId = advanced.CurrentStageId;

            // The returned aggregate is never handed to the panels: only its identity is read for the
            // message, and the panels themselves are refreshed from the stored run, so what they show is
            // what the database actually holds.
            RunCommandNotice = string.Create(
                CultureInfo.InvariantCulture,
                $"Переход выполнен: run '{runId}' проекта '{projectId}' ({projectDisplay}) и версии '{versionId}' ({versionDisplay}) переведён с '{advancedFromStageId}' на '{advancedToStageId}' по закреплённой схеме шаблона.{selectionNote}");
            StatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"Run '{runId}' переведён с '{advancedFromStageId}' на '{advancedToStageId}'.{selectionNote}");
        }
        catch (WorkflowTemplateExecutionBlockedException exception)
        {
            Blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Переход заблокирован [{exception.Blocker}]: {UiErrorMessage.Describe(exception)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = "Переход стадии не выполнен. " + UiErrorMessage.Describe(exception);
            if (_reviewGateStatus.Availability == WorkflowReviewGateAvailability.Evaluated
                && !_reviewGateStatus.IsEveryRequiredRoleApproved)
            {
                Blocker += $" Проверка вердиктов ревью (verdict): одобрено {_reviewGateStatus.ApprovedRoleCount}/{_reviewGateStatus.RequiredRoleCount}. "
                    + "Для каждой обязательной роли требуется Approve по текущему хешу; причины показаны в панели ревью.";
            }
        }
    }

    /// <summary>
    /// The transition target of the run's own pinned execution scheme, or the reason the run cannot move.
    ///
    /// A legacy run carries no pinned scheme, so its next stage would have to be guessed from the
    /// process-wide standard scheme; the command refuses instead, because this screen may only advance a
    /// run along the template version it was actually started against.
    /// </summary>
    private static bool TryResolvePinnedNextStageId(
        WorkflowRun run,
        out string? nextStageId,
        out string blocker)
    {
        nextStageId = null;

        if (!run.IsTemplateBacked || run.TemplateSchemeSnapshotJson is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Run '{run.Id}' не закреплён за версией шаблона, поэтому следующая стадия для него неизвестна. Эта команда продвигает только run, начатый на назначенной версии шаблона.");
            return false;
        }

        WorkflowSchemeSnapshot snapshot;

        try
        {
            snapshot = WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson, run.Id);
        }
        catch (Exception exception) when (exception is WorkflowValidationException or ArgumentException)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Закреплённая схема run '{run.Id}' нечитаема, поэтому переход запрещён: {UiErrorMessage.Describe(exception)}");
            return false;
        }

        var currentStage = snapshot.Scheme.FindStage(run.CurrentStageId);

        if (currentStage is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{run.CurrentStageId}' отсутствует в закреплённой схеме run '{run.Id}', поэтому переход запрещён.");
            return false;
        }

        if (currentStage.NextStageId is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{currentStage.StageId}' run '{run.Id}' является последней в закреплённой схеме. Завершение run выполняется отдельной терминальной командой, а не переходом.");
            return false;
        }

        nextStageId = currentStage.NextStageId;
        blocker = string.Empty;
        return true;
    }

    /// <summary>
    /// The failure target the run's own pinned execution scheme declares for the stage it currently sits on,
    /// or the reason the run's plan cannot be read at all.
    ///
    /// A null <paramref name="failureStageId"/> with a true result is the ordinary case: the stage declares
    /// no failure route. It is read only from the run's own snapshot, exactly like the next-stage
    /// resolution, so a stage a run does not declare never contributes a route to this screen.
    /// </summary>
    private static bool TryResolvePinnedFailureStageId(
        WorkflowRun run,
        out string? failureStageId,
        out string blocker)
    {
        failureStageId = null;

        if (!run.IsTemplateBacked || run.TemplateSchemeSnapshotJson is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Run '{run.Id}' не закреплён за версией шаблона, поэтому маршрут отказа для него неизвестен.");
            return false;
        }

        WorkflowSchemeSnapshot snapshot;

        try
        {
            snapshot = WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson, run.Id);
        }
        catch (Exception exception) when (exception is WorkflowValidationException or ArgumentException)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Закреплённая схема run '{run.Id}' нечитаема, поэтому маршрут отказа неизвестен: {UiErrorMessage.Describe(exception)}");
            return false;
        }

        var currentStage = snapshot.Scheme.FindStage(run.CurrentStageId);

        if (currentStage is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{run.CurrentStageId}' отсутствует в закреплённой схеме run '{run.Id}'.");
            return false;
        }

        failureStageId = currentStage.FailureStageId;
        blocker = string.Empty;
        return true;
    }

    /// <summary>
    /// The exact artifact kind the run's own pinned execution scheme requires at the stage it currently
    /// sits on, or the reason this run can take no artifact at all.
    ///
    /// The requirement is read only from <see cref="WorkflowRun.TemplateSchemeSnapshotJson"/>. The Studio's
    /// edited graph, the route labels, the stage display name, the chosen file name and the process-wide
    /// standard scheme are all deliberately not consulted, and neither is any kind the operator could have
    /// typed: the kind the run service will be asked for is the kind its own pinned stage declares.
    /// </summary>
    private static bool TryResolvePinnedStageArtifact(
        WorkflowRun run,
        out string? stageId,
        out string? kind,
        out string blocker)
    {
        stageId = null;
        kind = null;

        if (!run.IsTemplateBacked || run.TemplateSchemeSnapshotJson is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Run '{run.Id}' не закреплён за версией шаблона, поэтому требуемый им артефакт неизвестен. Эта команда прикрепляет артефакт только к run, начатому на назначенной версии шаблона.");
            return false;
        }

        WorkflowSchemeSnapshot snapshot;

        try
        {
            snapshot = WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson, run.Id);
        }
        catch (Exception exception) when (exception is WorkflowValidationException or ArgumentException)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Закреплённая схема run '{run.Id}' нечитаема, поэтому требуемый артефакт неизвестен: {UiErrorMessage.Describe(exception)}");
            return false;
        }

        var currentStage = snapshot.Scheme.FindStage(run.CurrentStageId);

        if (currentStage is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{run.CurrentStageId}' отсутствует в закреплённой схеме run '{run.Id}', поэтому артефакт не прикреплён.");
            return false;
        }

        if (currentStage.ArtifactRequirement is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{currentStage.StageId}' run '{run.Id}' не объявляет артефакт в закреплённой схеме, поэтому прикреплять к ней нечего.");
            return false;
        }

        stageId = currentStage.StageId;
        kind = currentStage.ArtifactRequirement;
        blocker = string.Empty;
        return true;
    }

    /// <summary>
    /// Copies the bytes of the operator's local file into the run as the artifact its own pinned scheme
    /// requires right now, and then re-observes the stored run.
    ///
    /// Everything the bytes are recorded against is captured before the first await - the run, the stage it
    /// is on, the exact artifact kind its pinned snapshot requires, the selected project, package and
    /// version, the local path and the classification. Nothing below re-targets them from a later run read,
    /// an editable field, the Studio's graph, the file name or the process-wide scheme, so the file that was
    /// on screen when the operator pressed the button is the file that gets recorded.
    ///
    /// The run itself is confirmed twice against fresh rows - once before the file is opened and once
    /// immediately before the run service is called - because opening a file is the one step that can take
    /// arbitrarily long, and a run that moved, went terminal or was replaced by a newer active one during
    /// that window would otherwise receive bytes under a stale stage. A refusal never claims success, and
    /// no path, file name, file content or IO message ever reaches the operator: a local file fault is
    /// reported as its class alone.
    ///
    /// As with start and advance, the command never denies what already happened. Once the run service has
    /// returned, the artifact is stored, so a selection that changed during the await is reported as a
    /// difference from what was persisted - measured against the captured package and version, not against
    /// the live selection - instead of being turned back into a refusal. Closing the operator's file is the
    /// only step that outlives the commit, so it is the only one allowed to fail without saying anything:
    /// a handle that will not close is not a statement about the artifact.
    /// </summary>
    public async Task AttachStageArtifactAsync()
    {
        if (CanAttachStageArtifact)
        {
            await ExecuteRunCommandAsync(AttachStageArtifactCoreAsync).ConfigureAwait(true);
            return;
        }

        // The button is unoffered by exactly the availability flag, so a click can never reach this branch.
        // A direct invocation still has to fail closed, and it fails closed out loud: an observed run that
        // can take no artifact gets that named reason here instead of a silent no-op.
        if (CanInspectObservedRunForArtifact)
        {
            ReportStageArtifactRefusal();
        }
    }

    private void ReportStageArtifactRefusal()
    {
        var run = _observedRun;

        if (run is null)
        {
            return;
        }

        if (run.IsTerminal)
        {
            ReportBlocker(TerminalRunArtifactBlocker(run.Id, run.State.ToString()));
            return;
        }

        if (!TryResolvePinnedStageArtifact(run, out _, out _, out var schemeBlocker))
        {
            ReportBlocker(schemeBlocker);
        }
    }

    private async Task AttachStageArtifactCoreAsync()
    {
        var run = _observedRun!;
        var projectId = SelectedProject!.Id;
        var packageId = SelectedPackage!.Id;
        var versionId = SelectedVersion!.Id;
        var projectDisplay = SelectedProject.DisplayName;
        var versionDisplay = SelectedVersion.VersionDisplay;

        // The capture happens here, before anything is awaited, and it is the only place the target of the
        // bytes is decided from. The display names come along with the ids because the success message has
        // to name what was actually acted on even if the selection has moved on by the time the run
        // service returns. The package id is carried all the way into the message for the same reason:
        // a difference is only honest if it is measured against what was acted on and not against
        // whatever happens to be selected when the service comes back.
        var runId = run.Id;

        if (!TryResolvePinnedStageArtifact(run, out var capturedStageId, out var capturedKind, out var schemeBlocker))
        {
            ReportBlocker(schemeBlocker);
            return;
        }

        var stageId = capturedStageId!;
        var kind = capturedKind!;
        var path = StageArtifactPath;
        var classification = StageArtifactClassification;
        var artifactExecutionId = StageArtifactRequiresExecution ? StageArtifactExecutionId : null;

        Stream? content = null;
        var committed = false;

        try
        {
            if (!await ConfirmsAttachedRunTargetAsync(projectId, packageId, versionId, runId, stageId, kind)
                .ConfigureAwait(true))
            {
                return;
            }

            var fault = StageArtifactFileInput.TryOpen(path, MaxStageArtifactBytes, out content);

            if (fault != StageArtifactFileFault.None || content is null)
            {
                ReportBlocker(StageArtifactFileBlocker(fault));
                return;
            }

            content = StageArtifactContentDecorator is null
                ? content
                : StageArtifactContentDecorator(content);

            // The file is open but nothing has been written yet, so the run is confirmed once more
            // immediately before the call: an active run that was replaced, a stage that moved, a run
            // that went terminal and a binding that stopped pointing at the selected version are all
            // refused here, while a file that merely exists is not a reason to record anything.
            if (!await ConfirmsAttachedRunTargetAsync(projectId, packageId, versionId, runId, stageId, kind)
                .ConfigureAwait(true))
            {
                return;
            }

            committed = await RecordCapturedStageArtifactAsync(
                runId,
                stageId,
                kind,
                content,
                classification,
                artifactExecutionId,
                projectId,
                packageId,
                versionId,
                projectDisplay,
                versionDisplay).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Use the artifact-specific safe reason here; arbitrary exception messages can name
            // private files or contain credentials and are never suitable for display.
            //
            // The one thing it may not do is deny an artifact the run service has already committed, so
            // this catch is scoped to the half of the command that happens before the commit. Past the
            // commit the screen keeps the hash it was given: a fault after that point says nothing that
            // would improve on it, and the panels are refreshed from the stored run either way.
            if (!committed)
            {
                ReportBlocker(StageArtifactFailureBlocker(exception));
            }
        }
        finally
        {
            // Closing the window is the one step that always happens after the last possible commit, which
            // is exactly why it sits outside every catch above. A handle that refuses to close is a fact
            // about the operator's file and not a statement about the artifact, and the message of the
            // IO failure is the one text that could carry that file's path into a durable record, so
            // nothing from it reaches the screen and the committed notice is left standing.
            await CloseStageArtifactContentAsync(content).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Releases the bounded window, and swallows a fault from doing so.
    ///
    /// This runs once the run service has returned, so by now the artifact is committed whatever the handle
    /// does, and a refusal here would deny something that really happened. Disposal is attempted once and
    /// nothing is reported from it; the caller keeps the notice the service earned.
    /// </summary>
    private static async Task CloseStageArtifactContentAsync(Stream? content)
    {
        if (content is null)
        {
            return;
        }

        try
        {
            await content.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Deliberately silent: the commit is already stored, and the only text this could add is the
            // operator's own path. The file itself is not kept open by a failed close.
        }
    }

    /// <summary>
    /// Hands the captured bytes to the composed run service and reports what was actually committed.
    ///
    /// The identities passed in are the ones captured before the first await, and they are the ones the
    /// call and the message are built from - the package included, so that the difference note measures the
    /// live selection against what was really acted on rather than against itself. The returned aggregate
    /// is read for its artifact hash and is never handed to the panels, which are refreshed from the
    /// repository instead.
    ///
    /// Returns whether the run service handed back a committed artifact. The caller uses that to keep the
    /// two halves of the command apart: only a failure before the commit may claim that nothing was
    /// recorded.
    /// </summary>
    private async Task<bool> RecordCapturedStageArtifactAsync(
        string runId,
        string stageId,
        string kind,
        Stream content,
        DataClassification classification,
        string? artifactExecutionId,
        string projectId,
        string packageId,
        string versionId,
        string projectDisplay,
        string versionDisplay)
    {
        WorkflowRun recorded;
        WorkflowArtifactEvidence? committedArtifact = null;

        try
        {
            if (artifactExecutionId is null)
                recorded = await _runService!.RecordStageArtifactAsync(runId, stageId, kind, content, classification).ConfigureAwait(true);
            else
            {
                var collected = await _runService!.RecordExecutionArtifactAsync(runId, stageId, kind, artifactExecutionId, content, classification).ConfigureAwait(true);
                recorded = collected.Run;
                committedArtifact = collected.Artifact;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The service stores the bytes before the evidence row, so a failure anywhere in it leaves no
            // artifact behind and the screen must not describe one as attached.
            ReportBlocker(StageArtifactFailureBlocker(exception));
            return false;
        }

        // Past this point the artifact is committed: a selection that moved while the service was awaited
        // is a display mismatch, never a refused attach.
        var selectionNote = SelectionDifferenceNote(projectId, packageId, versionId);
        var current = committedArtifact ?? WorkflowArtifactEvidence.SelectCurrent(recorded.Artifacts, runId, stageId, kind);

        if (current is null)
        {
            // The service returned a run that does not carry the artifact it just committed. The screen
            // cannot name a hash it was not given and must not invent one, and it must not call a
            // committed artifact refused either: it says the one thing it actually knows.
            Blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Служба run '{runId}' вернула результат без записи об артефакте вида '{kind}' для стадии '{stageId}', поэтому хеш не подтверждён.");
            StatusMessage = string.Empty;
            RunCommandNotice = string.Empty;
            return true;
        }

        var hash = current.HashSha256;
        if (current.ExecutionId is { } artifactExecution)
            selectionNote += $" Связано с выполнением '{artifactExecution}'; авторство модели этим не подтверждается.";

        RunCommandNotice = string.Create(
            CultureInfo.InvariantCulture,
            $"Артефакт прикреплён: запуск '{runId}' проекта '{projectId}' ({projectDisplay}) и версии '{versionId}' ({versionDisplay}) принял байты вида '{kind}' для стадии '{stageId}'; сохранённый хеш {hash}, классификация: {DescribeClassification(classification)}.{selectionNote}");
        StatusMessage = string.Create(
            CultureInfo.InvariantCulture,
            $"Run '{runId}': артефакт '{kind}' для стадии '{stageId}' записан, хеш {hash}.{selectionNote}");

        return true;
    }

    /// <summary>
    /// Whether the run, the stage it is on, the artifact kind its pinned scheme requires and the three
    /// selected identities are still the ones captured before the first await.
    ///
    /// Both the project's active run and the captured run itself are re-read. The active run matters
    /// because a run that has been replaced by a newer one is invisible to this console - the observed run
    /// stays the old one on screen - and would still be the row the bytes would land on. The captured run
    /// matters because its stored current stage, its terminal state and its own pinned requirement are the
    /// row the service is about to check, and they are read here rather than trusted from memory.
    ///
    /// Every refusal is reported here, so the caller only has to return.
    /// </summary>
    private async Task<bool> ConfirmsAttachedRunTargetAsync(
        string projectId,
        string packageId,
        string versionId,
        string runId,
        string stageId,
        string kind)
    {
        if (!SelectionIsCurrent(projectId, packageId, versionId))
        {
            ReportBlocker(RunSelectionChangedBlocker(projectId, versionId, "Артефакт не прикреплён."));
            return false;
        }

        var activeVersionId = await ResolveActiveVersionIdAsync(projectId, packageId).ConfigureAwait(true);

        if (!SelectionIsCurrent(projectId, packageId, versionId))
        {
            ReportBlocker(RunSelectionChangedBlocker(projectId, versionId, "Артефакт не прикреплён."));
            return false;
        }

        if (activeVersionId is null)
        {
            ReportBlocker(NoBindingBlocker(projectId, packageId, versionId, "Артефакт не прикреплён."));
            return false;
        }

        if (!string.Equals(activeVersionId, versionId, StringComparison.Ordinal))
        {
            ReportBlocker(InactiveVersionBlocker(versionId, activeVersionId, "Артефакт не прикреплён."));
            return false;
        }

        var activeRun = await _runRepository!.GetActiveByProjectIdAsync(projectId).ConfigureAwait(true);
        var storedRun = await _runRepository!.GetByIdAsync(runId).ConfigureAwait(true);

        if (!SelectionIsCurrent(projectId, packageId, versionId))
        {
            ReportBlocker(RunSelectionChangedBlocker(projectId, versionId, "Артефакт не прикреплён."));
            return false;
        }

        if (storedRun is null)
        {
            ReportBlocker(ReplacedObservedRunBlocker(runId, activeRun?.Id));
            return false;
        }

        if (activeRun is null || !string.Equals(activeRun.Id, runId, StringComparison.Ordinal))
        {
            ReportBlocker(ReplacedObservedRunBlocker(runId, activeRun?.Id));
            return false;
        }

        if (!string.Equals(storedRun.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(storedRun.WorkflowVersionId, versionId, StringComparison.Ordinal))
        {
            ReportBlocker(StaleObservedRunBlocker(runId, versionId));
            return false;
        }

        if (storedRun.IsTerminal)
        {
            ReportBlocker(TerminalRunArtifactBlocker(runId, storedRun.State.ToString()));
            return false;
        }

        if (!string.Equals(storedRun.CurrentStageId, stageId, StringComparison.Ordinal))
        {
            ReportBlocker(StaleArtifactStageBlocker(runId, stageId, storedRun.CurrentStageId));
            return false;
        }

        if (!TryResolvePinnedStageArtifact(storedRun, out var storedStageId, out var storedKind, out var schemeBlocker))
        {
            ReportBlocker(schemeBlocker);
            return false;
        }

        if (!string.Equals(storedStageId, stageId, StringComparison.Ordinal)
            || !string.Equals(storedKind, kind, StringComparison.Ordinal))
        {
            ReportBlocker(StaleArtifactKindBlocker(runId, stageId, kind, storedKind));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether an observed run could be decided about at all. A run that exists but cannot be approved still
    /// gets that named reason reported through this flag instead of a silent no-op, so a direct invocation
    /// fails closed with an explanation rather than without one.
    /// </summary>
    private bool CanInspectObservedRunForApproval =>
        IsUserApprovalAvailable && IsRunObservationAvailable && !IsBusy && HasObservedRun;

    /// <summary>
    /// The stage and the artifact kind a product user approval could be recorded for, or the reason the
    /// observed run admits none.
    ///
    /// Both come from <see cref="WorkflowRun.TemplateSchemeSnapshotJson"/> - the run's own pinned scheme -
    /// and from nowhere else: not from the Studio's edited graph, not from a route label, not from a stage
    /// display name, not from the process-wide standard scheme and not from anything the operator typed. A
    /// stage that requires no explicit user approval and a stage that declares no artifact kind are both
    /// refused by name, because there is nothing to decide about in either case.
    /// </summary>
    private static bool TryResolvePinnedStageApproval(
        WorkflowRun run,
        out string? stageId,
        out string? kind,
        out string blocker)
    {
        stageId = null;
        kind = null;

        if (!run.IsTemplateBacked || run.TemplateSchemeSnapshotJson is null)
        {
            blocker = $"Run '{run.Id}' не закреплён за версией шаблона, поэтому решение пользователя к нему неприменимо. "
                + "Это действие доступно только run, начатому на назначенной версии шаблона.";
            return false;
        }

        WorkflowSchemeSnapshot snapshot;

        try
        {
            snapshot = WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson, run.Id);
        }
        catch (Exception exception) when (exception is WorkflowValidationException or ArgumentException)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Закреплённая схема run '{run.Id}' нечитаема, поэтому решение пользователя неприменимо: {UiErrorMessage.Describe(exception)}");
            return false;
        }

        var currentStage = snapshot.Scheme.FindStage(run.CurrentStageId);

        if (currentStage is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{run.CurrentStageId}' отсутствует в закреплённой схеме run '{run.Id}', поэтому решение пользователя не записывается.");
            return false;
        }

        if (!currentStage.RequiresUserApproval)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{currentStage.StageId}' run '{run.Id}' не требует явного решения пользователя в закреплённой схеме, поэтому оно не записывается.");
            return false;
        }

        if (currentStage.ArtifactRequirement is null)
        {
            blocker = string.Create(
                CultureInfo.InvariantCulture,
                $"Стадия '{currentStage.StageId}' run '{run.Id}' не объявляет вид артефакта, поэтому решать нечего.");
            return false;
        }

        stageId = currentStage.StageId;
        kind = currentStage.ArtifactRequirement;
        blocker = string.Empty;
        return true;
    }

    /// <summary>
    /// The approver identity this window would record, resolved from the very source the run service
    /// stamps with. An unavailable identity is never replaced by a name: the caller fails closed instead.
    /// </summary>
    private bool TryResolveApproverIdentity(out string? identity)
    {
        identity = _userApprovalIdentity?.GetCurrentApproverIdentity();

        return !string.IsNullOrWhiteSpace(identity);
    }

    /// <summary>
    /// The decision already recorded for a stage, described from the stored evidence: its decision, who
    /// recorded it and when. The run's own approval history is the only source; nothing is reconstructed
    /// from a draft, a projection or a notice.
    /// </summary>
    private static string? LatestStageDecision(WorkflowRun run, string stageId) =>
        run.Approvals
            .Where(approval => string.Equals(approval.StageId, stageId, StringComparison.Ordinal))
            .OrderByDescending(approval => approval.DecidedAtUtc)
            .Select(approval => string.Create(
                CultureInfo.InvariantCulture,
                $"{approval.Decision} от '{approval.ApprovedBy}' ({approval.DecidedAtUtc:yyyy-MM-dd HH:mm} UTC), комментарий: {approval.Comment}"))
            .FirstOrDefault();

    private void ReportUserApprovalRefusal()
    {
        var run = _observedRun;

        if (run is null)
        {
            return;
        }

        if (run.IsTerminal)
        {
            ReportBlocker(TerminalRunApprovalBlocker(run.Id, run.State.ToString()));
            return;
        }

        if (!TryResolvePinnedStageApproval(run, out _, out _, out var schemeBlocker))
        {
            ReportBlocker(schemeBlocker);
            return;
        }

        if (!TryResolveApproverIdentity(out _))
        {
            ReportBlocker(UserApprovalIdentityBlocker);
            return;
        }

        if (_approvableArtifact is null)
        {
            ReportBlocker(ApprovalArtifactRequiredBlocker(run.Id, run.CurrentStageId));
            return;
        }

        if (!_approvableArtifact.BytesVerified)
        {
            ReportBlocker(ApprovalArtifactUnverifiedBlocker(
                run.Id,
                _approvableArtifact.ArtifactId,
                _approvableArtifact.StageId,
                _approvableArtifact.Kind));
            return;
        }

        ReportBlocker(UserApprovalCommentRequiredBlocker);
    }

    private async Task RecordObservedUserApprovalCoreAsync(UserApprovalDecision decision)
    {
        var run = _observedRun!;
        var projectId = SelectedProject!.Id;
        var packageId = SelectedPackage!.Id;
        var versionId = SelectedVersion!.Id;
        var projectDisplay = SelectedProject.DisplayName;
        var versionDisplay = SelectedVersion.VersionDisplay;
        var runId = run.Id;

        // Everything the decision is about is captured here, before the first await, and it is the only
        // place the target is decided from. The displayed names come along with the ids because the
        // success message has to name what was actually acted on even when the selection has moved on by
        // the time the run service returns.
        if (!TryResolvePinnedStageApproval(run, out var capturedStageId, out var capturedKind, out var schemeBlocker))
        {
            ReportBlocker(schemeBlocker);
            return;
        }

        var stageId = capturedStageId!;
        var kind = capturedKind!;
        var captured = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, stageId, kind);

        if (captured is null)
        {
            ReportBlocker(ApprovalArtifactRequiredBlocker(runId, stageId));
            return;
        }

        var hash = captured.HashSha256;
        var comment = UserApprovalComment;

        if (string.IsNullOrWhiteSpace(comment))
        {
            ReportBlocker(UserApprovalCommentRequiredBlocker);
            return;
        }

        if (!TryResolveApproverIdentity(out var approver))
        {
            ReportBlocker(UserApprovalIdentityBlocker);
            return;
        }

        // A unique id, minted here so a repeated decision is a second record and never an overwrite of the
        // first one. It is persisted with the decision and reported back from the stored run.
        var approvalId = Guid.NewGuid().ToString("N");

        try
        {
            if (!await ConfirmsApprovalTargetAsync(projectId, packageId, versionId, runId, stageId, kind, hash)
                .ConfigureAwait(true))
            {
                return;
            }

            // The row is not the evidence: the bytes are. The committed artifact is re-hashed here,
            // immediately before the decision is stored, and the target is confirmed once more after that
            // await, so a run that moved, a stage that changed, an artifact that was replaced and a blob
            // that was deleted or rewritten are all refused here rather than decided against.
            if (!await VerifyApprovableArtifactBytesAsync(captured.BlobId).ConfigureAwait(true))
            {
                ReportBlocker(ApprovalArtifactUnverifiedBlocker(runId, captured.ArtifactId, stageId, kind));
                return;
            }

            if (!await ConfirmsApprovalTargetAsync(projectId, packageId, versionId, runId, stageId, kind, hash)
                .ConfigureAwait(true))
            {
                return;
            }

            // The approver named here is the same local Windows logon the run service resolves for itself;
            // the service replaces it with its own resolution before saving, so a name that arrived from
            // anywhere else could not be what gets stored.
            var evidence = new UserApprovalEvidence(
                approvalId,
                approver!,
                stageId,
                hash,
                decision,
                comment,
                _timeProvider.GetUtcNow());

            var recorded = await _runService!
                .RecordUserApprovalAsync(runId, evidence)
                .ConfigureAwait(true);

            ReportRecordedUserApproval(recorded, runId, projectId, packageId, projectDisplay, versionId, versionDisplay, stageId, kind, approvalId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportBlocker(UserApprovalFailureBlocker(exception));
        }
    }

    /// <summary>
    /// Reports what the run service actually committed.
    ///
    /// Past the service call the decision is stored, so a selection that moved while it was awaited is a
    /// display mismatch and never a refused decision. The approver named in the report is the one the
    /// stored evidence carries - the identity the service stamped - and not the one this screen resolved,
    /// so the notice states the persisted truth even if the two ever differed. A rejection states its
    /// terminal effect, because a run that has just been rejected accepts nothing further and the observed
    /// run disappears from this console in the refresh that follows.
    /// </summary>
    private void ReportRecordedUserApproval(
        WorkflowRun recorded,
        string runId,
        string projectId,
        string packageId,
        string projectDisplay,
        string versionId,
        string versionDisplay,
        string stageId,
        string kind,
        string approvalId)
    {
        var selectionNote = SelectionDifferenceNote(projectId, packageId, versionId);
        var persisted = recorded.Approvals
            .LastOrDefault(approval => string.Equals(approval.ApprovalId, approvalId, StringComparison.Ordinal));

        if (persisted is null)
        {
            // The service returned a run that does not carry the decision it just stored. The screen cannot
            // name an approver it was not given and must not invent one, and it must not call a committed
            // decision refused either: it says the one thing it actually knows.
            Blocker = $"Служба run '{runId}' вернула результат без записи решения '{approvalId}', поэтому личность "
                + "записавшего и сам факт записи не подтверждены.";
            StatusMessage = string.Empty;
            RunCommandNotice = string.Empty;
            return;
        }

        var isRejection = persisted.Decision == UserApprovalDecision.Rejected;
        var terminalNote = isRejection
            ? $" Решение отклонено: run '{runId}' переведён в терминальное состояние {recorded.State} "
                + $"с исходом {recorded.TerminalOutcome}; переходы и новые решения по нему больше недоступны."
            : string.Empty;

        // The reported hash is the one the stored evidence carries, not the one this screen captured: after
        // the service has returned, only the stored record is evidence of what was decided.
        var storedHash = persisted.ArtifactHash;

        RunCommandNotice = string.Create(
            CultureInfo.InvariantCulture,
            $"Решение записано: run '{runId}' проекта '{projectId}' ({projectDisplay}) и версии '{versionId}' "
                + $"({versionDisplay}), стадия '{stageId}', артефакт '{kind}', хеш {storedHash} — {persisted.Decision} "
                + $"от '{persisted.ApprovedBy}', комментарий: {persisted.Comment}.{terminalNote}{selectionNote}");
        StatusMessage = string.Create(
            CultureInfo.InvariantCulture,
            $"Run '{runId}': решение {persisted.Decision} по стадии '{stageId}' и хешу {storedHash} записано "
                + $"от '{persisted.ApprovedBy}'.{terminalNote}{selectionNote}");
    }

    /// <summary>
    /// Whether the run, the stage it is on, the artifact kind its pinned scheme requires, the hash of its
    /// current stored artifact and the three selected identities are still the ones captured before the
    /// first await.
    ///
    /// The project's active run and the captured run are both re-read, for the same reason the attach path
    /// re-reads them: a run that has been replaced by a newer one stays on screen here and would still be
    /// the row the decision would land on. The stored current stage, the terminal state, the pinned
    /// requirement and the current artifact hash are read from rows rather than remembered, so a stage
    /// that moved and an artifact that was replaced are both refused by name.
    /// </summary>
    private async Task<bool> ConfirmsApprovalTargetAsync(
        string projectId,
        string packageId,
        string versionId,
        string runId,
        string stageId,
        string kind,
        string hash)
    {
        if (!SelectionIsCurrent(projectId, packageId, versionId))
        {
            ReportBlocker(RunSelectionChangedBlocker(projectId, versionId, "Решение пользователя не записано."));
            return false;
        }

        var activeVersionId = await ResolveActiveVersionIdAsync(projectId, packageId).ConfigureAwait(true);

        if (!SelectionIsCurrent(projectId, packageId, versionId))
        {
            ReportBlocker(RunSelectionChangedBlocker(projectId, versionId, "Решение пользователя не записано."));
            return false;
        }

        if (activeVersionId is null)
        {
            ReportBlocker(NoBindingBlocker(projectId, packageId, versionId, "Решение пользователя не записано."));
            return false;
        }

        if (!string.Equals(activeVersionId, versionId, StringComparison.Ordinal))
        {
            ReportBlocker(InactiveVersionBlocker(versionId, activeVersionId, "Решение пользователя не записано."));
            return false;
        }

        var activeRun = await _runRepository!.GetActiveByProjectIdAsync(projectId).ConfigureAwait(true);
        var storedRun = await _runRepository!.GetByIdAsync(runId).ConfigureAwait(true);

        if (!SelectionIsCurrent(projectId, packageId, versionId))
        {
            ReportBlocker(RunSelectionChangedBlocker(projectId, versionId, "Решение пользователя не записано."));
            return false;
        }

        if (storedRun is null)
        {
            ReportBlocker(ReplacedObservedRunApprovalBlocker(runId, activeRun?.Id));
            return false;
        }

        if (activeRun is null || !string.Equals(activeRun.Id, runId, StringComparison.Ordinal))
        {
            ReportBlocker(ReplacedObservedRunApprovalBlocker(runId, activeRun?.Id));
            return false;
        }

        if (!string.Equals(storedRun.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(storedRun.WorkflowVersionId, versionId, StringComparison.Ordinal))
        {
            ReportBlocker(StaleObservedRunApprovalBlocker(runId, versionId));
            return false;
        }

        if (storedRun.IsTerminal)
        {
            ReportBlocker(TerminalRunApprovalBlocker(runId, storedRun.State.ToString()));
            return false;
        }

        if (!string.Equals(storedRun.CurrentStageId, stageId, StringComparison.Ordinal))
        {
            ReportBlocker(StaleApprovalStageBlocker(runId, stageId, storedRun.CurrentStageId));
            return false;
        }

        if (!TryResolvePinnedStageApproval(storedRun, out var storedStageId, out var storedKind, out var storedBlocker))
        {
            ReportBlocker(storedBlocker);
            return false;
        }

        if (!string.Equals(storedStageId, stageId, StringComparison.Ordinal)
            || !string.Equals(storedKind, kind, StringComparison.Ordinal))
        {
            ReportBlocker(StaleApprovalKindBlocker(runId, stageId, kind, storedKind));
            return false;
        }

        var current = WorkflowArtifactEvidence.SelectCurrent(storedRun.Artifacts, runId, stageId, kind);

        if (current is null)
        {
            ReportBlocker(ApprovalArtifactRequiredBlocker(runId, stageId));
            return false;
        }

        if (!string.Equals(current.HashSha256, hash, StringComparison.Ordinal))
        {
            ReportBlocker(ChangedApprovalArtifactBlocker(runId, stageId, hash, current.HashSha256));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Re-hashes the committed bytes behind a stored artifact, answering "verified" or "not verified" and
    /// nothing else. A missing file, altered bytes and an unreadable store are one answer here: no verified
    /// content, so no decision. The store's own fault is deliberately not turned into a message, because a
    /// fault from the file layer is exactly the text that could carry a local path into a durable record.
    /// </summary>
    private async Task<bool> VerifyApprovableArtifactBytesAsync(string blobId)
    {
        if (_artifactBlobStore is null)
        {
            ReportBlocker(ApprovalBlobStoreUnavailableBlocker);
            return false;
        }

        try
        {
            return await _artifactBlobStore.VerifyAsync(blobId).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<string?> ResolveActiveVersionIdAsync(string projectId, string packageId)
    {
        var binding = await _bindingRepository!
            .GetByProjectAndPackageAsync(projectId, packageId)
            .ConfigureAwait(true);

        return binding?.ActiveVersionId;
    }

    /// <summary>
    /// True while the screen still selects the very identities the command captured before its first
    /// await. Any change in between makes the command's premise stale, and a stale command blocks instead
    /// of acting on a selection the operator never made.
    /// </summary>
    private bool SelectionIsCurrent(string projectId, string packageId, string versionId) =>
        string.Equals(SelectedProject?.Id, projectId, StringComparison.Ordinal)
        && string.Equals(SelectedPackage?.Id, packageId, StringComparison.Ordinal)
        && string.Equals(SelectedVersion?.Id, versionId, StringComparison.Ordinal);

    private void ReportBlocker(string message)
    {
        Blocker = message;
        StatusMessage = string.Empty;
        RunCommandNotice = message;
    }

    /// <summary>
    /// The sentence a committed run command appends when the screen selection moved while the run service
    /// was awaited, and nothing at all when it did not.
    ///
    /// It is used only after the side effect has been persisted. Before that point a stale selection is a
    /// refused command and keeps <see cref="RunSelectionChangedBlocker"/>; afterwards the run exists, so
    /// the only honest thing left to say is which run was persisted and how the current selection differs
    /// from it. The observation that follows may still follow the current selection and clear its panels -
    /// this notice survives that, because the action it describes really did happen.
    /// </summary>
    private string SelectionDifferenceNote(string projectId, string packageId, string versionId) =>
        SelectionIsCurrent(projectId, packageId, versionId)
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $" Выбор на экране изменился во время операции (проект '{SelectedProject?.Id ?? UnavailableIndicator}', "
                    + $"версия '{SelectedVersion?.Id ?? UnavailableIndicator}'): run сохранён для проекта '{projectId}' "
                    + $"и версии '{versionId}', а панели наблюдения следуют за текущим выбором.");

    /// <summary>
    /// The pre-side-effect refusal: nothing has been written yet, so a selection that moved away from the
    /// captured identities stops the command before it acts on a project the operator never picked.
    /// </summary>
    private static string RunSelectionChangedBlocker(string projectId, string versionId, string? refusedOperation = null) =>
        refusedOperation is null
            ? $"Выбор изменился во время операции: run проекта '{projectId}' на версии '{versionId}' не создан и не продвинут."
            : $"Выбор изменился во время операции для проекта '{projectId}' на версии '{versionId}'. {refusedOperation}";

    private static string NoBindingBlocker(string projectId, string packageId, string versionId, string? refusedOperation = null) =>
        refusedOperation is null
            ? $"Проект '{projectId}' не привязан к пакету '{packageId}', поэтому версия '{versionId}' не является активной и run не создан."
            : $"Проект '{projectId}' не привязан к пакету '{packageId}', поэтому версия '{versionId}' не является активной. {refusedOperation}";

    private static string InactiveVersionBlocker(string versionId, string activeVersionId, string? refusedOperation = null) =>
        $"Выбранная версия '{versionId}' не является активной для проекта: активна '{activeVersionId}'. {refusedOperation ?? "Run не создан."}";

    private static string ActiveRunConflictBlocker(WorkflowRun activeRun) =>
        $"Для проекта уже есть активный запуск '{activeRun.Id}' (стадия {activeRun.CurrentStageId}, {activeRun.State}). "
            + "Новый run не создан: сначала завершите или отмените существующий.";

    private static string StaleObservedRunBlocker(string runId, string versionId) =>
        $"Наблюдаемый run '{runId}' не принадлежит выбранной активной версии '{versionId}', поэтому переход не выполнен.";

    private static string TerminalRunBlocker(string runId, string state) =>
        $"Run '{runId}' уже в терминальном состоянии {state} и не продвигается.";

    private static string TerminalRunArtifactBlocker(string runId, string state) =>
        $"Run '{runId}' уже в терминальном состоянии {state} и больше ничего не принимает.";

    private static string ReplacedObservedRunBlocker(string runId, string? activeRunId) =>
        activeRunId is null
            ? $"Наблюдаемый run '{runId}' больше не является активным run проекта, поэтому артефакт к нему не прикреплён."
            : $"Активный run проекта теперь '{activeRunId}', а не наблюдаемый '{runId}', поэтому артефакт не прикреплён.";

    private static string StaleArtifactStageBlocker(string runId, string capturedStageId, string currentStageId) =>
        $"Run '{runId}' уже находится на стадии '{currentStageId}', поэтому артефакт для стадии '{capturedStageId}' не прикреплён.";

    private static string StaleArtifactKindBlocker(
        string runId,
        string capturedStageId,
        string capturedKind,
        string? currentKind) =>
        $"Закреплённая схема run '{runId}' требует для стадии '{capturedStageId}' артефакт вида "
            + $"'{currentKind ?? UnavailableIndicator}', а не '{capturedKind}', поэтому артефакт не прикреплён.";

    /// <summary>
    /// The named refusal for a local file that cannot supply bytes, stated as its class alone.
    ///
    /// The path, the file name, the file content and the message of the IO exception that produced the
    /// fault are all deliberately absent. A local path is the operator's own directory layout and a
    /// <c>Blocker</c>, a status line, a notice, a log and persisted evidence all read as durable records, so
    /// none of them may carry it. This is also why the one-click export's own path-echoing messages are
    /// not followed here.
    /// </summary>
    private static string StageArtifactFileBlocker(StageArtifactFileFault fault) => fault switch
    {
        StageArtifactFileFault.Directory =>
            "Указанный локальный путь ведёт к каталогу, а не к файлу артефакта. Артефакт не прикреплён.",
        StageArtifactFileFault.Unreadable =>
            "Файл артефакта недоступен для чтения. Артефакт не прикреплён.",
        StageArtifactFileFault.Oversized =>
            $"Файл артефакта превышает предел {MaxStageArtifactBytes / (1024 * 1024)} МиБ. Артефакт не прикреплён.",
        _ =>
            "Файл артефакта по указанному локальному пути не найден. Артефакт не прикреплён."
    };

    /// <summary>
    /// The named refusal for a failure that left nothing recorded.
    ///
    /// The bound violation and the missing/unreadable/directory classes are the ones the operator can act
    /// on and are named as such. A <see cref="WorkflowValidationException"/> is the run service's own
    /// named refusal about the run, its stage or the required kind, and it names no file, so it is passed
    /// through. Everything else is stated as a failure to record and nothing more: an unrecognised
    /// exception message is exactly the text that could carry a local path into a durable record.
    /// </summary>
    private static string StageArtifactFailureBlocker(Exception exception) => exception switch
    {
        StageArtifactFileTooLargeException => StageArtifactFileBlocker(StageArtifactFileFault.Oversized),
        FileNotFoundException or DirectoryNotFoundException =>
            StageArtifactFileBlocker(StageArtifactFileFault.Missing),
        IOException => StageArtifactFileBlocker(StageArtifactFileFault.Unreadable),
        UnauthorizedAccessException => StageArtifactFileBlocker(StageArtifactFileFault.Unreadable),
        WorkflowValidationException =>
            $"Прикрепление артефакта отклонено службой run: {UiErrorMessage.Describe(exception)}",
        _ => "Прикрепить артефакт не удалось: команда прервана до записи, артефакт не создан."
    };

    private const string ApprovalBlobStoreUnavailableBlocker =
        "Адресное хранилище артефактов не настроено для этого окна, поэтому решение пользователя не записывается.";

    /// <summary>
    /// The refusal for a run that has no stored artifact a decision could be about. The hash of a decision
    /// is the hash of committed content, so a stage with no artifact is unoffered and refused by name
    /// rather than decided against a hash somebody could have typed.
    /// </summary>
    private static string ApprovalArtifactRequiredBlocker(string runId, string stageId) =>
        $"У run '{runId}' на стадии '{stageId}' нет сохранённого артефакта этой стадии, поэтому решение пользователя не записывается.";

    /// <summary>
    /// The refusal for stored bytes that are missing, altered or unreadable. The row exists and says it is
    /// current, and the blob store says its content is not there any more; the decision is refused because
    /// only verified content can be approved.
    /// </summary>
    private static string ApprovalArtifactUnverifiedBlocker(
        string runId,
        string artifactId,
        string stageId,
        string kind) =>
        $"Сохранённый артефакт '{artifactId}' вида '{kind}' стадии '{stageId}' run '{runId}' больше не "
            + "проверяется по своим байтам, поэтому решение пользователя не записывается.";

    /// <summary>
    /// The refusal for a stage whose newest stored artifact is not the one the decision was made against.
    /// A replacement after the operator read the hash leaves the older decision authorizing nothing, which
    /// is exactly why it is refused instead of quietly re-pointed at the new bytes.
    /// </summary>
    private static string ChangedApprovalArtifactBlocker(
        string runId,
        string stageId,
        string capturedHash,
        string currentHash) =>
        $"Текущий артефакт стадии '{stageId}' run '{runId}' теперь имеет хеш {currentHash}, а решение принято "
            + $"по хешу {capturedHash}. Повторите решение для актуального артефакта.";

    private static string StaleApprovalStageBlocker(string runId, string capturedStageId, string currentStageId) =>
        $"Run '{runId}' уже находится на стадии '{currentStageId}', поэтому решение по стадии '{capturedStageId}' не записано.";

    private static string StaleApprovalKindBlocker(
        string runId,
        string capturedStageId,
        string capturedKind,
        string? currentKind) =>
        $"Закреплённая схема run '{runId}' требует для стадии '{capturedStageId}' артефакт вида "
            + $"'{currentKind ?? UnavailableIndicator}', а не '{capturedKind}', поэтому решение не записано.";

    private static string StaleObservedRunApprovalBlocker(string runId, string versionId) =>
        $"Наблюдаемый run '{runId}' не принадлежит выбранной активной версии '{versionId}', поэтому решение пользователя не записано.";

    private static string TerminalRunApprovalBlocker(string runId, string state) =>
        $"Run '{runId}' уже в терминальном состоянии {state} и больше не принимает решений пользователя.";

    private static string ReplacedObservedRunApprovalBlocker(string runId, string? activeRunId) =>
        activeRunId is null
            ? $"Наблюдаемый run '{runId}' больше не является активным run проекта, поэтому решение к нему не записано."
            : $"Активный run проекта теперь '{activeRunId}', а не наблюдаемый '{runId}', поэтому решение не записано.";

    private static string UserApprovalCommentRequiredBlocker =>
        "Решение пользователя требует собственный комментарий: ни одобрение, ни отклонение не записываются без него.";

    /// <summary>
    /// The refusal for a run that has no stored artifact a reviewer could be shown. A review is about stored
    /// content, so a stage with no artifact is unoffered and refused by name rather than dispatched against
    /// bytes somebody could have typed.
    /// </summary>
    private static string ReviewArtifactRequiredBlocker(string runId, string stageId) =>
        $"У run '{runId}' на стадии '{stageId}' нет сохранённого артефакта этой стадии, поэтому назначенное ревью не запрашивается.";

    /// <summary>
    /// The refusal for stored bytes that are missing, altered or unreadable. The row says it is current and
    /// the blob store says the content is not there any more; a reviewer is never shown bytes that cannot be
    /// re-hashed, and no execution is recorded for a turn that never had content.
    /// </summary>
    private static string ReviewArtifactUnverifiedBlocker(
        string runId,
        string artifactId,
        string stageId,
        string kind) =>
        $"Сохранённый артефакт '{artifactId}' вида '{kind}' стадии '{stageId}' run '{runId}' больше не проверяется "
            + "по своим байтам, поэтому назначенное ревью не запрашивается.";

    /// <summary>
    /// The refusal for a stage whose newest stored artifact is not the one the request was resolved against.
    /// Replacing the artifact after the operator read the hash leaves the older request describing a document
    /// that is no longer the one under review, which is why it is refused instead of quietly re-pointed.
    /// </summary>
    private static string ChangedReviewArtifactBlocker(
        string runId,
        string stageId,
        string capturedHash,
        string currentHash) =>
        $"Текущий артефакт стадии '{stageId}' run '{runId}' теперь имеет хеш {currentHash}, а запрос ревью принят "
            + $"по хешу {capturedHash}. Повторите запрос для актуального артефакта.";

    private static string StaleReviewStageBlocker(string runId, string capturedStageId, string currentStageId) =>
        $"Run '{runId}' уже находится на стадии '{currentStageId}', поэтому запрос ревью по стадии '{capturedStageId}' не отправлен.";

    private static string StaleReviewKindBlocker(
        string runId,
        string capturedStageId,
        string capturedKind,
        string? currentKind) =>
        $"Закреплённая схема run '{runId}' требует для стадии '{capturedStageId}' артефакт вида "
            + $"'{currentKind ?? UnavailableIndicator}', а не '{capturedKind}', поэтому запрос ревью не отправлен.";

    private static string StaleObservedRunReviewBlocker(string runId, string versionId) =>
        $"Наблюдаемый run '{runId}' не принадлежит выбранной активной версии '{versionId}', поэтому запрос ревью не отправлен.";

    private static string TerminalRunReviewBlocker(string runId, string state) =>
        $"Run '{runId}' уже в терминальном состоянии {state} и больше не запрашивает ревьюеров.";

    private static string ReplacedObservedRunReviewBlocker(string runId, string? activeRunId) =>
        activeRunId is null
            ? $"Наблюдаемый run '{runId}' больше не является активным run проекта, поэтому запрос ревью к нему не отправлен."
            : $"Активный run проекта теперь '{activeRunId}', а не наблюдаемый '{runId}', поэтому запрос ревью не отправлен.";

    /// <summary>
    /// The refusal for a window that cannot resolve an assignment at all. It names no role, no route and no
    /// document, because nothing about them was established.
    /// </summary>
    private const string ReviewRequestUnavailableBlocker =
        "Запрос назначенного ревью недоступен для этого окна: назначение роли, маршрута и модели не разрешено, "
            + "ничего не отправлено и ничего не записано.";

    /// <summary>
    /// The refusal for a failure that left nothing recorded.
    ///
    /// A <see cref="WorkflowValidationException"/> is the review service's own named refusal about the run,
    /// its stage, its route or its artifact, and it names no content and no local path, so it is passed
    /// through. Everything else is stated as a failure to dispatch and nothing more: an unrecognised
    /// exception message is exactly the text that could carry artifact bytes, a local path or a secret into a
    /// durable record.
    /// </summary>
    private static string ReviewRequestFailureBlocker(Exception exception) => exception switch
    {
        WorkflowValidationException =>
            $"Назначенное ревью отклонено службой run: {UiErrorMessage.Describe(exception)}",
        _ => "Запросить назначенное ревью не удалось: команда прервана до отправки, исполнение не создано и вердикт не записан."
    };

    /// <summary>
    /// The refusal for a failure that left no decision recorded.
    ///
    /// A <see cref="WorkflowValidationException"/> is the run service's own named refusal about the run, its
    /// stage, the required kind or the current hash, and it names no content, so it is passed through.
    /// Everything else is stated as a failure to record and nothing more: an unrecognised exception message
    /// is exactly the text that could carry artifact bytes, a local path or a secret into a durable record.
    /// </summary>
    private static string UserApprovalFailureBlocker(Exception exception) => exception switch
    {
        WorkflowValidationException =>
            $"Решение пользователя отклонено службой run: {UiErrorMessage.Describe(exception)}",
        _ => "Записать решение пользователя не удалось: команда прервана до записи, решение не сохранено."
    };

    private async Task ReloadForProjectAsync()
    {
        await LoadBindingsAsync().ConfigureAwait(true);
        await LoadVersionsAsync().ConfigureAwait(true);
        await ObserveActiveRunAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Observes the active run of the selected project without starting, advancing or re-running anything.
    ///
    /// Only the active run of the selected project is read, and only for the selected workflow version: a
    /// missing run or a run pinned to another version clears the observed Studio/Monitor state and reports
    /// "Not reported" instead of showing a foreign run. For a matching run the observable projections are
    /// built from the sessions of that run (<c>WorkflowRunId == run.Id</c> or <c>Id == run.SessionId</c>)
    /// and their stored executions, so the monitor shows real turns, models, accounts and read-only proof
    /// rather than a run record alone. The workflow timeline deliberately does not supply projections.
    ///
    /// Only project/version selection and the library refresh tokens trigger this query; there is no
    /// polling. A stale query is discarded by the load token, which keeps the panels and their project
    /// isolation consistent with the current selection.
    /// </summary>
    public async Task ObserveActiveRunAsync(CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        var version = SelectedVersion;
        var selectionKey = BuildRunObservationSelectionKey(project, version);
        var token = BeginRunObservation(selectionKey);

        if (!IsRunObservationAvailable || project is null || version is null)
        {
            ApplyObservedRun(run: null, projections: null, token, selectionKey);
            await ApplyApprovableArtifactAsync(run: null, token, cancellationToken).ConfigureAwait(true);
            await ApplyReviewGateStatusAsync(run: null, token, cancellationToken).ConfigureAwait(true);
            SettleRunObservation(token, selectionKey);
            return;
        }

        try
        {
            var run = await _runRepository!
                .GetActiveByProjectIdAsync(project.Id, cancellationToken)
                .ConfigureAwait(true);

            if (token != _runObservationToken)
            {
                return;
            }

            if (run is null
                || !string.Equals(run.ProjectId, project.Id, StringComparison.Ordinal)
                || !string.Equals(run.WorkflowVersionId, version.Id, StringComparison.Ordinal))
            {
                ApplyObservedRun(run: null, projections: null, token, selectionKey);
                await ApplyApprovableArtifactAsync(run: null, token, cancellationToken).ConfigureAwait(true);
                SettleRunObservation(token, selectionKey);
                return;
            }

            var sessions = await _sessionRepository!
                .ListByProjectAsync(project.Id, cancellationToken)
                .ConfigureAwait(true);

            if (token != _runObservationToken)
            {
                return;
            }

            var runSessions = sessions
                .Where(session => BelongsToRun(run, session))
                .ToArray();

            var projections = new List<ObservableRunProjection>(runSessions.Length);

            foreach (var session in runSessions)
            {
                var executions = await _executionRepository!
                    .ListBySessionAsync(session.Id, cancellationToken)
                    .ConfigureAwait(true);

                if (token != _runObservationToken)
                {
                    return;
                }

                foreach (var execution in executions)
                {
                    if (!string.Equals(execution.SessionId, session.Id, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    projections.Add(ObservableRunProjection.FromExecution(
                        execution,
                        session,
                        WorkflowRoleParser.Parse(session.Role),
                        session.Role,
                        EvidenceSourceKind.NotReported,
                        lastActivityAtUtc: null,
                        _checkoutLockService));
                }
            }

            ApplyObservedRun(run, projections, token, selectionKey);
            await ApplyApprovableArtifactAsync(run, token, cancellationToken).ConfigureAwait(true);
            await ApplyReviewGateStatusAsync(run, token, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (token != _runObservationToken)
            {
                return;
            }

            ApplyObservedRun(run: null, projections: null, token, selectionKey);
            await ApplyApprovableArtifactAsync(run: null, token, cancellationToken).ConfigureAwait(true);
            await ApplyReviewGateStatusAsync(run: null, token, cancellationToken).ConfigureAwait(true);
            Blocker = $"Не удалось прочитать состояние запуска ({exception.GetType().Name}).";
        }
        finally
        {
            // A cancelled or abandoned read must not leave the start action pending for ever: the observation
            // settles here whether it committed, found nothing or gave up, and it settles as "nothing observed"
            // so the action stays offered on the authority of the repository re-read rather than on a flag
            // that never clears. Only the newest observation may settle; a superseded one is already stale.
            SettleRunObservation(token, selectionKey);
        }
    }

    /// <summary>
    /// Starts a fresh observation for one selection and marks it as in flight.
    /// <para>
    /// The in-flight mark is what makes the start action fail closed: until the query that answers "is there
    /// already an active run of this project?" comes back, the screen holds a run that belongs to the previous
    /// selection, and neither offering nor withholding Start on that basis would be a statement about the
    /// project the operator is looking at.
    /// </para>
    /// </summary>
    private int BeginRunObservation(string? selectionKey)
    {
        _runObservationInFlight = true;
        _observedRunSelectionKey = null;
        var token = ++_runObservationToken;
        UpdateCommandStates();
        return token;
    }

    /// <summary>
    /// Drops the previous selection's observation synchronously, before the new one has even started.
    ///
    /// <para>
    /// The reload that follows a project change and the query that follows a version change are both
    /// asynchronous, so a setter that only cleared the observed run would leave the previous project's run
    /// on screen - and Start enabled for it - for as long as those awaits take. Advancing the token here
    /// makes every query started before this setter discard its result, and the two flags make
    /// <see cref="CanStartAssignedRun"/> false from the very statement the operator made.
    /// </para>
    /// </summary>
    private void InvalidateRunObservationForSelectionChange()
    {
        _runObservationInFlight = true;
        _observedRunSelectionKey = null;
        _ = ++_runObservationToken;
        ApplyObservedRun(null, null, _runObservationToken, null, settle: false);
        // With no run these methods have no storage await; withdraw the old artifact authority now.
        _ = ApplyApprovableArtifactAsync(null, _runObservationToken, CancellationToken.None);
        _ = ApplyReviewGateStatusAsync(null, _runObservationToken, CancellationToken.None);
    }

    /// <summary>
    /// Records that the observation of <paramref name="token"/> finished for
    /// <paramref name="selectionKey"/>, so the enablement it guards is decided against that selection and no
    /// other. A superseded observation cannot settle: the selection has already moved past it.
    /// </summary>
    private void SettleRunObservation(int token, string? selectionKey)
    {
        if (token != _runObservationToken)
        {
            return;
        }

        if (!_runObservationInFlight
            && string.Equals(_observedRunSelectionKey, selectionKey, StringComparison.Ordinal))
        {
            return;
        }

        _runObservationInFlight = false;
        _observedRunSelectionKey = selectionKey;
        UpdateCommandStates();
    }

    /// <summary>The project and version the observation on screen was made for, or null when there is none.</summary>
    private string? CurrentRunObservationSelectionKey =>
        BuildRunObservationSelectionKey(SelectedProject, SelectedVersion);

    private static string? BuildRunObservationSelectionKey(
        Project? project,
        WorkflowVersionItemViewModel? version) =>
        project is null || version is null
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{project.Id}{version.Id}");

    /// <summary>
    /// Whether an observed run belongs to the selection this screen is currently showing. A run of another
    /// project or another workflow version is invisible to this screen's own duplicate check and stays the
    /// service's refusal to report.
    /// </summary>
    private bool BelongsToCurrentSelection(WorkflowRun run) =>
        SelectedProject is { } project
        && SelectedVersion is { } version
        && string.Equals(run.ProjectId, project.Id, StringComparison.Ordinal)
        && string.Equals(run.WorkflowVersionId, version.Id, StringComparison.Ordinal);

    /// <summary>
    /// Re-derives, for the run just observed, the one stored artifact a product user approval could be
    /// recorded against - and re-hashes its committed bytes while it is here.
    ///
    /// This is why the two decision buttons are unoffered when the bytes are gone: an enablement fact has to
    /// be a fact about verified content, not about a row that claims to hold it. The verification is
    /// therefore repeated immediately before the decision is stored, and again inside the run service, so
    /// the state captured here is an offer and never the guarantee.
    ///
    /// Anything that cannot be established - no run, no approval stage, no artifact, an unavailable store -
    /// leaves the screen with no approvable artifact at all instead of a fallback one.
    /// </summary>
    private async Task ApplyApprovableArtifactAsync(
        WorkflowRun? run,
        int token,
        CancellationToken cancellationToken)
    {
        ApprovableStageArtifact? approvable = null;

        if (run is not null
            && _artifactBlobStore is not null
            && TryResolvePinnedStageApproval(run, out var stageId, out var kind, out _))
        {
            var current = WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, run.Id, stageId!, kind!);

            if (current is not null)
            {
                var verified = await _artifactBlobStore
                    .VerifyAsync(current.BlobId, cancellationToken)
                    .ConfigureAwait(true);

                approvable = new ApprovableStageArtifact(
                    run.Id,
                    stageId!,
                    kind!,
                    current.ArtifactId,
                    current.HashSha256,
                    verified);
            }
        }

        if (token != _runObservationToken)
        {
            return;
        }

        _approvableArtifact = approvable;

        OnPropertyChanged(nameof(CurrentApprovableArtifactHash));
        OnPropertyChanged(nameof(HasApprovableArtifact));
        OnPropertyChanged(nameof(UserApprovalRequirementDisplay));
        UpdateCommandStates();
    }

    /// <summary>
    /// Re-derives the read-only reviewer-gate status of the run just observed, re-hashing the committed
    /// bytes of the artifact the current stage's pinned scheme declares.
    ///
    /// The verification is what keeps the panel honest. A role is only ever shown as approved when the
    /// artifact its verdict is pinned to is one whose bytes still hash to the recorded digest, so a missing
    /// or altered blob turns the whole gate into a named refusal instead of a set of satisfied roles. The
    /// newest artifact row is the one resolved, and a newer row that does not verify is never quietly
    /// replaced by an older one that did.
    ///
    /// The projection itself is pure; only this re-hash is asynchronous, so the observation token is
    /// re-checked after the await and again before the commit. A selection that moved on while the store was
    /// being read never overwrites the newer run's status.
    /// </summary>
    private async Task ApplyReviewGateStatusAsync(
        WorkflowRun? run,
        int token,
        CancellationToken cancellationToken)
    {
        bool? verified = null;
        ApprovableStageArtifact? reviewable = null;

        if (run is not null && _artifactBlobStore is not null)
        {
            // The probe is pure and touches no storage: it only resolves which stage, which roles and which
            // current artifact hash the run's own rows describe, so the blob store is consulted exactly when
            // there is something to verify and never for a stage that declares no gate.
            var probe = WorkflowReviewGateStatusProjector.Project(run, currentArtifactVerified: null);

            if (probe.Roles.Count > 0 && probe.CurrentArtifactHash is not null)
            {
                verified = await VerifyGateArtifactAsync(probe.CurrentArtifactHash, cancellationToken)
                    .ConfigureAwait(true);

                // The very same verified read is what the "request assigned review" action offers against, so
                // the action and the read-only panel can never disagree about which bytes exist. A stage that
                // requires reviewers and a stage that requires a user approval are different stages, and each
                // one gets its own artifact rather than the other's.
                var current = WorkflowArtifactEvidence.SelectCurrent(
                    run.Artifacts,
                    run.Id,
                    probe.StageId,
                    probe.RequiredArtifactKind!);

                if (current is not null)
                {
                    reviewable = new ApprovableStageArtifact(
                        run.Id,
                        current.StageId,
                        current.Kind,
                        current.ArtifactId,
                        current.HashSha256,
                        verified is true);
                }
            }
        }

        if (token != _runObservationToken)
        {
            return;
        }

        _reviewableArtifact = reviewable;
        _reviewGateStatus = WorkflowReviewGateStatusProjector.Project(run, verified);

        OnPropertyChanged(nameof(AssignedReviewRequirementDisplay));
        OnPropertyChanged(nameof(ReviewGateStatus));
        OnPropertyChanged(nameof(ReviewGateRoles));
        OnPropertyChanged(nameof(HasReviewGateRoles));
        OnPropertyChanged(nameof(ReviewGateRequirementDisplay));
        OnPropertyChanged(nameof(ReviewGateStateDisplay));
        UpdateCommandStates();
    }

    /// <summary>
    /// Drops the reviewer-gate projection back to the named "no observed run" state, synchronously and without
    /// touching storage.
    ///
    /// This is the other half of clearing the observed run, and it is deliberately not an asynchronous
    /// derivation: the invalidation has to land in the same step that clears the run, so the panel can never be
    /// observed - by a binding refresh or by an operator - showing a run that is already gone together with the
    /// approvals, the role chips and the artifact hash that belonged to it.
    ///
    /// It is idempotent, because a projection that is already the "no observed run" state needs no change
    /// notification: an observation that clears a run it never had, and a second invalidation of the same
    /// run, both leave the panel exactly as it already is.
    /// </summary>
    private void InvalidateReviewGateStatus()
    {
        if (ReferenceEquals(_reviewGateStatus, WorkflowReviewGateStatus.NoObservedRun))
        {
            return;
        }

        _reviewGateStatus = WorkflowReviewGateStatus.NoObservedRun;

        OnPropertyChanged(nameof(ReviewGateStatus));
        OnPropertyChanged(nameof(ReviewGateRoles));
        OnPropertyChanged(nameof(HasReviewGateRoles));
        OnPropertyChanged(nameof(ReviewGateRequirementDisplay));
        OnPropertyChanged(nameof(ReviewGateStateDisplay));
    }

    /// <summary>
    /// Re-hashes the committed bytes behind the artifact the gate was resolved against. The digest being
    /// verified is the very one the verdicts are matched to, so a row that claims one hash cannot be
    /// authorized by bytes of another. Any failure to establish them is "not verified" rather than an
    /// error, so a damaged store degrades the panel to a refusal instead of throwing.
    /// </summary>
    private async Task<bool?> VerifyGateArtifactAsync(string hashSha256, CancellationToken cancellationToken)
    {
        try
        {
            return await _artifactBlobStore!
                .VerifyAsync(hashSha256, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// A session belongs to the run when it declares the run id or when the run points at it as its own
    /// session. Anything else is a session of another run and is never projected onto this one.
    /// </summary>
    private static bool BelongsToRun(WorkflowRun run, Session session) =>
        string.Equals(session.WorkflowRunId, run.Id, StringComparison.Ordinal)
        || (run.SessionId is not null && string.Equals(session.Id, run.SessionId, StringComparison.Ordinal));

    private void ApplyObservedRun(
        WorkflowRun? run,
        IReadOnlyList<ObservableRunProjection>? projections,
        int token,
        string? selectionKey,
        bool settle = true)
    {
        if (token != _runObservationToken)
        {
            return;
        }

        _observedRun = run;
        _observedProjections = projections is null
            ? Array.Empty<ObservableRunProjection>()
            : projections.ToArray();

        // The outcome of a review request is a statement about one run: which stage of which run was asked,
        // which roles refused and which reached a backend. It therefore stops being this screen's status the
        // moment the observed run is a different one - or is gone - because next to run B's button and run B's
        // stage, run A's "dispatched" or "refused" reads as an answer about B. The clear is synchronous and
        // happens here, in the same step that adopts the new run, so no window exists in which the panel can
        // show a stale outcome; and a refresh of the same run keeps it, because the fact that the request
        // really happened is still a fact about the run it happened on.
        ClearAssignedReviewResultForOtherRun(run);

        // The observed run feeds both embedded panels, so a cleared run clears the Studio run graph too.
        ActivityMonitor.LoadRun(_observedRun, _observedProjections);
        Studio.LoadRun(_observedRun);

        // The reviewer gate is a statement about one observed run's current stage, so clearing that run has
        // to invalidate the gate in the very same step. It is done here rather than at the call sites so that
        // every clearing path - a version switch, an active query that no longer matches, an observation
        // without collaborators, a read that failed - cannot leave the previous run's `Approve N/N`, its role
        // chips and its verified hash on a panel that no longer observes that run.
        if (_observedRun is null)
        {
            InvalidateReviewGateStatus();
        }

        OnPropertyChanged(nameof(ObservedRun));
        OnPropertyChanged(nameof(HasObservedRun));
        OnPropertyChanged(nameof(ObservedProjections));
        OnPropertyChanged(nameof(ObservedRunDisplay));
        OnPropertyChanged(nameof(ObservedRunTemplateDisplay));
        OnPropertyChanged(nameof(ObservedProjectionsDisplay));
        OnPropertyChanged(nameof(ObservedRunNotice));
        OnPropertyChanged(nameof(HasObservedRunNotice));
        OnPropertyChanged(nameof(CommandableRun));
        OnPropertyChanged(nameof(RunCommandStateDisplay));

        // The run commands follow the observed run, so its arrival, change and clearing have to reach
        // their enablement and their notice rather than leaving the previous run's state on screen.
        UpdateCommandStates();

        // The run that is now on screen is the answer for exactly one selection, and the start action may
        // only be decided against that answer. Settling here - in the same step that adopts the run - is
        // what closes the window between "the query returned" and "the button is enabled again".
        if (settle) SettleRunObservation(token, selectionKey);
    }

    /// <summary>
    /// Drops the last review-request outcome as soon as the screen is no longer observing the run it was
    /// about, and says so out loud through the four properties that display it.
    /// <para>
    /// A refusal is not decoration: "run A · node-a · refused: route is not a persisted route" next to run
    /// B is a claim about B that happens to name A, and an operator who requests a review for B and reads
    /// A's outcome has been told the wrong thing by the screen. Clearing is therefore decided by the run id
    /// and not by whether a refresh happened, so every path that changes what is observed - a selection
    /// change, a newer run replacing the old one, an observation that finds no run, a read that failed -
    /// withdraws the outcome, and only a re-observation of the same run keeps it.
    /// </para>
    /// </summary>
    private void ClearAssignedReviewResultForOtherRun(WorkflowRun? run)
    {
        var result = _assignedReviewResult;

        if (result is null || string.Equals(run?.Id, result.RunId, StringComparison.Ordinal))
        {
            return;
        }

        _assignedReviewResult = null;

        OnPropertyChanged(nameof(AssignedReviewResult));
        OnPropertyChanged(nameof(HasAssignedReviewResult));
        OnPropertyChanged(nameof(AssignedReviewStatusDisplay));
        OnPropertyChanged(nameof(AssignedReviewDetailDisplay));
    }

    private async Task LoadProjectsAsync()
    {
        if (_projectRepository is null)
        {
            return;
        }

        var projects = await _projectRepository.ListAsync().ConfigureAwait(true);
        var previousProjectId = SelectedProject?.Id;

        Projects.Clear();

        foreach (var project in projects)
        {
            Projects.Add(project);
        }

        OnPropertyChanged(nameof(HasProjects));

        SelectedProject = Projects.FirstOrDefault(project => project.Id == previousProjectId)
            ?? Projects.FirstOrDefault();
    }

    private async Task LoadBindingsAsync()
    {
        var token = ++_bindingsLoadToken;
        var project = SelectedProject;
        Bindings.Clear();
        OnPropertyChanged(nameof(HasBindings));

        if (_bindingRepository is null || project is null)
        {
            SelectedBinding = null;
            return;
        }

        var bindings = await _bindingRepository
            .ListByProjectIdAsync(project.Id)
            .ConfigureAwait(true);

        if (token != _bindingsLoadToken || !ReferenceEquals(project, SelectedProject)) return;
        var projectDisplayName = project.DisplayName;

        foreach (var binding in bindings)
        {
            Bindings.Add(new WorkflowBindingViewModel(binding, projectDisplayName));
        }

        OnPropertyChanged(nameof(HasBindings));

        SelectedBinding = SelectedPackage is null
            ? Bindings.FirstOrDefault()
            : Bindings.FirstOrDefault(binding => binding.WorkflowPackageId == SelectedPackage.Id)
                ?? Bindings.FirstOrDefault();
    }

    private void ClearPreview()
    {
        DocumentationPath = null;
        DocumentationContent = string.Empty;
        IsDocumentationTruncated = false;

        TreeNodes.Clear();
        OnPropertyChanged(nameof(HasTreeNodes));

        SelectedNode = null;

        SelectedFileContent = string.Empty;
        IsSelectedFileBinary = false;
        IsSelectedFileTruncated = false;
        OnPropertyChanged(nameof(SelectedFileNotice));
    }

    private string? NormalizeRoutePolicyId() =>
        string.IsNullOrWhiteSpace(RoutePolicyId) ? null : RoutePolicyId;

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(CanBindToProject));
        OnPropertyChanged(nameof(CanSetActiveVersion));
        OnPropertyChanged(nameof(CanUnbind));
        OnPropertyChanged(nameof(CanAdaptWorkflow));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(CanConfirmActivation));
        OnPropertyChanged(nameof(CanQuickImport));
        OnPropertyChanged(nameof(CanQuickExport));
        OnPropertyChanged(nameof(CanStartAssignedRun));
        OnPropertyChanged(nameof(StartAssignedRunUnavailableReason));
        OnPropertyChanged(nameof(HasStartAssignedRunUnavailableReason));
        OnPropertyChanged(nameof(CanAdvanceObservedRun));
        OnPropertyChanged(nameof(CanAttachStageArtifact));
        OnPropertyChanged(nameof(StageArtifactRequirementDisplay));
        OnPropertyChanged(nameof(StageArtifactRequiresExecution));
        OnPropertyChanged(nameof(StageArtifactNotice));
        OnPropertyChanged(nameof(CanApproveObservedArtifact));
        OnPropertyChanged(nameof(CanRejectObservedArtifact));
        OnPropertyChanged(nameof(CanDecideObservedUserApproval));
        OnPropertyChanged(nameof(UserApprovalRequirementDisplay));
        OnPropertyChanged(nameof(UserApprovalApproverDisplay));
        OnPropertyChanged(nameof(CanRequestAssignedReview));
        OnPropertyChanged(nameof(AssignedReviewRequirementDisplay));
        OnPropertyChanged(nameof(AssignedReviewResult));
        OnPropertyChanged(nameof(HasAssignedReviewResult));
        OnPropertyChanged(nameof(AssignedReviewStatusDisplay));
        OnPropertyChanged(nameof(AssignedReviewDetailDisplay));
        OnPropertyChanged(nameof(ReviewGateStatus));
        OnPropertyChanged(nameof(ReviewGateRoles));
        OnPropertyChanged(nameof(HasReviewGateRoles));
        OnPropertyChanged(nameof(ReviewGateRequirementDisplay));
        OnPropertyChanged(nameof(ReviewGateStateDisplay));
        OnPropertyChanged(nameof(RunCommandNotice));
        OnPropertyChanged(nameof(HasRunCommandNotice));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private enum PendingActivationKind
    {
        Bind,
        SetActiveVersion,
        Rollback
    }

    /// <summary>The refused action kept until every blocker decision is recorded or the operator cancels.</summary>
    private sealed record PendingActivation(
        PendingActivationKind Kind,
        Project Project,
        WorkflowPackageItemViewModel Package,
        WorkflowVersionItemViewModel Version,
        string? RoutePolicyId,
        bool IsNewBinding);

    /// <summary>
    /// The stored artifact a product user approval could be recorded against, as of the last observation:
    /// which run, which stage, which required kind, which artifact row, which hash - and whether the
    /// committed bytes behind that row were re-hashed when the run was read. Nothing here is operator input.
    /// </summary>
    private sealed record ApprovableStageArtifact(
        string RunId,
        string StageId,
        string Kind,
        string ArtifactId,
        string HashSha256,
        bool BytesVerified);

    private static IReadOnlyList<WorkflowTreeNodeViewModel> BuildTreeNodes(
        IReadOnlyList<WorkflowTreeNode> nodes)
    {
        var directoryPaths = new HashSet<string>(
            nodes.Where(node => node.IsDirectory).Select(node => node.Path),
            StringComparer.Ordinal);

        var childrenByParent = new Dictionary<string, List<WorkflowTreeNode>>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var parentPath = ResolveParentPath(node.Path, directoryPaths);

            if (!childrenByParent.TryGetValue(parentPath, out var children))
            {
                children = new List<WorkflowTreeNode>();
                childrenByParent[parentPath] = children;
            }

            children.Add(node);
        }

        var flattened = new List<WorkflowTreeNodeViewModel>(nodes.Count);

        AppendChildren(flattened, childrenByParent, string.Empty, depth: 0);

        return flattened;
    }

    /// <summary>
    /// Finds the closest directory node that actually exists in the tree, so a file without an explicit
    /// directory entry is attached to the root instead of being dropped.
    /// </summary>
    private static string ResolveParentPath(string path, HashSet<string> directoryPaths)
    {
        var parentPath = GetParentPath(path);

        while (parentPath.Length > 0 && !directoryPaths.Contains(parentPath))
        {
            parentPath = GetParentPath(parentPath);
        }

        return parentPath;
    }

    private static void AppendChildren(
        List<WorkflowTreeNodeViewModel> flattened,
        Dictionary<string, List<WorkflowTreeNode>> childrenByParent,
        string parentPath,
        int depth)
    {
        if (!childrenByParent.TryGetValue(parentPath, out var children))
        {
            return;
        }

        foreach (var child in children.OrderBy(node => node.Path, StringComparer.Ordinal))
        {
            flattened.Add(new WorkflowTreeNodeViewModel(child, depth));

            if (child.IsDirectory)
            {
                AppendChildren(flattened, childrenByParent, child.Path, depth + 1);
            }
        }
    }

    private static string GetParentPath(string path)
    {
        var separatorIndex = path.LastIndexOf('/');

        return separatorIndex <= 0 ? string.Empty : path[..separatorIndex];
    }
}
