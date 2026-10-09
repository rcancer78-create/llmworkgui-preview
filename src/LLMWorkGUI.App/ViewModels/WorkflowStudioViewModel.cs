using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Workflow Studio (ROADMAP Phase 10E): the editable template schema and the actual run graph are shown
/// side by side, never merged. The panel creates/clones/versions templates, validates the graph through
/// <see cref="IWorkflowGraphValidator"/>, generates versioned document drafts with SHA-256 hashes, runs the
/// secret-scan preview, collects the separate reviewer verdicts and evaluates the pre-coder approval gate.
/// Every command has a public async counterpart so the panel is fully testable headless.
/// </summary>
public sealed class WorkflowStudioViewModel : ObservableObject
{
    public const string Title = "Workflow Studio";
    public const string TemplateSchemaHeader = "Редактируемая схема шаблона";
    public const string RunGraphHeader = "Фактический граф выполняемого run";
    public const string CoderTransitionId = WorkflowScheme.CodeAndUiStageId;
    public const string NoRunGraphMessage = "Активный запуск не загружен; показана только схема шаблона.";
    public const string NotReported = "Not reported";

    private readonly IWorkflowStudioService _studioService;
    private readonly IDocumentTemplateService _documentTemplateService;
    private readonly IPreCoderGateValidator _gateValidator;
    private readonly bool _usesComposedServices;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<(string DraftId, string ContentHash), (bool Ui, bool Visual)> _draftVisualEvidence = new();
    private readonly HashSet<WorkflowNodeEditorViewModel> _subscribedNodes = new();

    private WorkflowTemplateItemViewModel? _selectedTemplate;
    private WorkflowNodeEditorViewModel? _selectedNode;
    private DocumentTemplateOptionViewModel? _selectedDocumentTemplate;
    private WorkflowDocumentDraft? _currentDraft;
    private string _newTemplateId = "user-workflow";
    private string _newTemplateName = "User workflow";
    private int _newTemplateVersion = 2;
    private string _draftTitle = string.Empty;
    private string _draftContent = string.Empty;
    private string _previewContent = string.Empty;
    private string _previewSummary = string.Empty;
    private string _previewScanSummary = string.Empty;
    private bool _previewIsBlocked;
    private bool _hasPreview;
    private string _reviewerRole = "Reviewer";
    private string _reviewerRouteId = "route-opencode";
    private string _reviewerEvidence = "Reviewed the pinned document hash.";
    private WorkflowReviewVerdict _reviewerVerdict = WorkflowReviewVerdict.Approve;
    private string? _gateRequestedRouteId;
    private string? _gateObservedRouteId;
    private bool _gateRequiresUiArtifact;
    private bool _gateRequiresUserVisualAcceptance;
    private bool _uiArtifactPresent;
    private bool _visualAcceptancePresent;
    private string _gateSummary = string.Empty;
    private bool _hasGateResult;
    private bool _isCoderStartAllowed;
    private bool _gateRequiresEscalation;
    private string _gateEscalationDisplay = string.Empty;
    private string? _gateEscalationTargetNodeId;
    private bool _isTemplateGraphValid;
    private bool _hasTemplateValidation;
    private string _templateValidationMessage = string.Empty;
    private string _blocker = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;
    private int _nodeSequence;
    private WorkflowStudioPanelMode _activePanelMode = WorkflowStudioPanelMode.Overview;
    private string _roleMatrixSummary = string.Empty;

    public WorkflowStudioViewModel(
        IWorkflowStudioService? studioService = null,
        IDocumentTemplateService? documentTemplateService = null,
        IPreCoderGateValidator? gateValidator = null,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;

        // A directly constructed panel (headless rendering, no application composition) falls back to the
        // shipped in-process implementations so the view still renders. The production composition passes
        // the registered services, and <see cref="UsesComposedServices"/> then reports that fact instead of
        // letting a private instance hide a missing registration.
        _usesComposedServices = studioService is not null
            && documentTemplateService is not null
            && gateValidator is not null;

        _studioService = studioService ?? new WorkflowStudioService(timeProvider: _timeProvider);
        _documentTemplateService = documentTemplateService ?? new DocumentTemplateService(
            timeProvider: _timeProvider);
        _gateValidator = gateValidator ?? new PreCoderGateValidator();

        foreach (var template in _documentTemplateService.GetStandardTemplates())
        {
            DocumentTemplates.Add(new DocumentTemplateOptionViewModel(template));
        }

        RefreshTemplatesCommand = new RelayCommand(() => _ = RefreshTemplatesAsync(), () => !IsBusy);
        CloneTemplateCommand = new RelayCommand(() => _ = CloneTemplateAsync(), () => CanEditTemplate);
        CreateTemplateVersionCommand = new RelayCommand(
            () => _ = CreateTemplateVersionAsync(),
            () => CanEditTemplate);
        AddNodeCommand = new RelayCommand(AddTemplateNode, () => CanEditTemplate);
        RemoveNodeCommand = new RelayCommand(RemoveTemplateNode, () => SelectedNode is not null);
        ValidateTemplateCommand = new RelayCommand(() => _ = ValidateTemplateAsync(), () => CanEditTemplate);
        SaveTemplateCommand = new RelayCommand(() => _ = SaveTemplateAsync(), () => CanEditTemplate);
        GenerateDraftCommand = new RelayCommand(
            () => _ = RunSafelyAsync(GenerateDraftAsync),
            () => !IsBusy);
        UpdateDraftCommand = new RelayCommand(() => _ = UpdateDraftAsync(), () => HasDraft && !IsBusy);
        PreviewDraftCommand = new RelayCommand(() => _ = PreviewDraftAsync(), () => HasDraft && !HasUnsavedDraftChanges && !IsBusy);
        AddReviewVerdictCommand = new RelayCommand(
            () => _ = AddReviewVerdictAsync(),
            () => HasDraft && !HasUnsavedDraftChanges && !IsBusy);
        ApproveDocumentCommand = new RelayCommand(
            () => _ = ApproveDocumentAsync(),
            () => HasDraft && !HasUnsavedDraftChanges && !IsBusy);
        RefreshGateCommand = new RelayCommand(() => _ = RefreshGateAsync(), () => !IsBusy);
        RequestCoderStartCommand = new RelayCommand(
            () => _ = RequestCoderStartAsync(),
            () => HasGateResult && !IsBusy);
        SelectPanelModeCommand = new RelayCommand(parameter => SelectPanelMode(ResolvePanelMode(parameter)));

        TemplateNodes.CollectionChanged += OnTemplateNodesChanged;

        SelectedDocumentTemplate = DocumentTemplates.FirstOrDefault();
        _ = RefreshTemplatesAsync();
    }

    public ObservableCollection<WorkflowTemplateItemViewModel> Templates { get; } = new();

    public ObservableCollection<WorkflowNodeEditorViewModel> TemplateNodes { get; } = new();

    public ObservableCollection<WorkflowRunNodeViewModel> RunNodes { get; } = new();

    /// <summary>Role matrix of the selected template: role, primary route, stages and required documents.</summary>
    public ObservableCollection<WorkflowRoleMatrixItemViewModel> RoleMatrix { get; } = new();

    public ObservableCollection<DocumentTemplateOptionViewModel> DocumentTemplates { get; } = new();

    public ObservableCollection<ReviewerVerdictItemViewModel> ReviewerVerdicts { get; } = new();

    public ObservableCollection<PreCoderGateCheckViewModel> GateChecks { get; } = new();

    public ICommand RefreshTemplatesCommand { get; }

    public ICommand CloneTemplateCommand { get; }

    public ICommand CreateTemplateVersionCommand { get; }

    public ICommand AddNodeCommand { get; }

    public ICommand RemoveNodeCommand { get; }

    public ICommand ValidateTemplateCommand { get; }

    public ICommand SaveTemplateCommand { get; }

    public ICommand GenerateDraftCommand { get; }

    public ICommand UpdateDraftCommand { get; }

    public ICommand PreviewDraftCommand { get; }

    public ICommand AddReviewVerdictCommand { get; }

    public ICommand ApproveDocumentCommand { get; }

    public ICommand RefreshGateCommand { get; }

    public ICommand RequestCoderStartCommand { get; }

    public ICommand SelectPanelModeCommand { get; }

    /// <summary>
    /// The compact studio surface currently shown. <see cref="WorkflowStudioPanelMode.Overview"/> keeps the
    /// complete template schema, actual run graph, documents and gate visible at once.
    /// </summary>
    public WorkflowStudioPanelMode ActivePanelMode
    {
        get => _activePanelMode;
        private set
        {
            if (!SetProperty(ref _activePanelMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsOverviewMode));
            OnPropertyChanged(nameof(IsTemplatesMode));
            OnPropertyChanged(nameof(IsRoleMatrixMode));
            OnPropertyChanged(nameof(IsDocumentsMode));
            OnPropertyChanged(nameof(IsPrimarySurfaceVisible));
            OnPropertyChanged(nameof(IsSecondarySurfaceVisible));
            OnPropertyChanged(nameof(ActivePanelModeDisplay));
        }
    }

    public bool IsOverviewMode => ActivePanelMode == WorkflowStudioPanelMode.Overview;

    public bool IsTemplatesMode => ActivePanelMode == WorkflowStudioPanelMode.Templates;

    public bool IsRoleMatrixMode => ActivePanelMode == WorkflowStudioPanelMode.RoleMatrix;

    public bool IsDocumentsMode => ActivePanelMode == WorkflowStudioPanelMode.Documents;

    /// <summary>The template/run surface is visible in the overview and in the templates-only mode.</summary>
    public bool IsPrimarySurfaceVisible => IsOverviewMode || IsTemplatesMode;

    /// <summary>The documents/gate surface is visible in the overview and in the documents-only mode.</summary>
    public bool IsSecondarySurfaceVisible => IsOverviewMode || IsDocumentsMode;

    public string ActivePanelModeDisplay => ActivePanelMode switch
    {
        WorkflowStudioPanelMode.Templates => "Редактирование шаблонов",
        WorkflowStudioPanelMode.RoleMatrix => "Матрица ролей",
        WorkflowStudioPanelMode.Documents => "Предпросмотр документов",
        _ => "Обзор студии"
    };

    public string RoleMatrixSummary
    {
        get => _roleMatrixSummary;
        private set => SetProperty(ref _roleMatrixSummary, value);
    }

    public string RoleMatrixNote =>
        "Матрица показывает роли, их основные route-привязки и этапы выбранного шаблона. "
        + "Она не меняет активный run и не переключает аккаунты автоматически.";

    public bool HasRoleMatrix => RoleMatrix.Count > 0;

    /// <summary>Selects one of the compact studio surfaces without touching the template or the run.</summary>
    public void SelectPanelMode(WorkflowStudioPanelMode mode)
    {
        ActivePanelMode = mode;
        StatusMessage = string.Create(CultureInfo.InvariantCulture, $"Режим студии: {ActivePanelModeDisplay}.");
    }

    public string TitleText => Title;

    public string DescriptionText =>
        "Создание и версионирование workflow-шаблонов, документы с hash-версиями, раздельные вердикты "
        + "ревьюеров и pre-coder approval gate.";

    public string TemplateSchemaHeaderText => TemplateSchemaHeader;

    public string RunGraphHeaderText => RunGraphHeader;

    public string StudioNote =>
        "Схема шаблона редактируется и сохраняется отдельно от фактического графа run: правки студии "
        + "никогда не меняют активный run и исходный импортированный ZIP.";

    public string GateNote =>
        "Кодер не стартует без единогласного Approve обязательных ревьюеров на текущий hash каждого "
        + "документа и отдельного утверждения пользователя.";

    public string ReviewerNote =>
        "Вердикты ревьюеров хранятся раздельно и пинятся к SHA-256 хэшу проверенного документа.";

    /// <summary>
    /// True when the panel is driven by the studio, document-template and gate services the application
    /// registered. A panel built without them still renders, but the screen states that the composed
    /// services are missing instead of silently reporting a private instance as if it were the product.
    /// </summary>
    public bool IsStudioAvailable => _usesComposedServices;

    /// <summary>The services this panel actually projects with; exposed as composition evidence.</summary>
    public IWorkflowStudioService StudioService => _studioService;

    public IDocumentTemplateService DocumentTemplateService => _documentTemplateService;

    public IPreCoderGateValidator GateValidator => _gateValidator;

    public TimeProvider TimeProvider => _timeProvider;

    public string UnavailableNotice =>
        "Службы Workflow Studio не настроены для этого окна, поэтому шаблоны, документы и предкодерный "
        + "гейт недоступны.";

    public WorkflowTemplateItemViewModel? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value))
            {
                OnPropertyChanged(nameof(SelectedTemplateDisplay));
                LoadTemplateIntoEditor(value);
                UpdateCommandStates();
            }
        }
    }

    public string SelectedTemplateDisplay =>
        SelectedTemplate is null
            ? NotReported
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{SelectedTemplate.DisplayName} · v{SelectedTemplate.Version}{(SelectedTemplate.IsBuiltIn ? " · built-in" : " · user")}");

    public WorkflowNodeEditorViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public string TemplateGraphSummary =>
        TemplateNodes.Count == 0
            ? "Схема шаблона пуста."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{TemplateNodes.Count} node(s), entry '{TemplateNodes[0].NodeId}'.");

    public string TemplateValidationMessage
    {
        get => _templateValidationMessage;
        private set
        {
            if (SetProperty(ref _templateValidationMessage, value))
            {
                OnPropertyChanged(nameof(HasTemplateValidation));
            }
        }
    }

    public bool HasTemplateValidation
    {
        get => _hasTemplateValidation;
        private set => SetProperty(ref _hasTemplateValidation, value);
    }

    public bool IsTemplateGraphValid
    {
        get => _isTemplateGraphValid;
        private set
        {
            if (SetProperty(ref _isTemplateGraphValid, value))
            {
                OnPropertyChanged(nameof(TemplateValidationStatusDisplay));
            }
        }
    }

    public string TemplateValidationStatusDisplay =>
        IsTemplateGraphValid ? "Схема валидна" : "Схема невалидна";

    public string NewTemplateId
    {
        get => _newTemplateId;
        set => SetProperty(ref _newTemplateId, value);
    }

    public string NewTemplateName
    {
        get => _newTemplateName;
        set => SetProperty(ref _newTemplateName, value);
    }

    public int NewTemplateVersion
    {
        get => _newTemplateVersion;
        set => SetProperty(ref _newTemplateVersion, value);
    }

    public bool HasRunGraph => RunNodes.Count > 0;

    public string RunGraphSummary { get; private set; } = NoRunGraphMessage;

    public void LoadRun(WorkflowRun? run, WorkflowScheme? scheme = null)
    {
        RunNodes.Clear();

        if (run is null)
        {
            RunGraphSummary = NoRunGraphMessage;
            OnPropertyChanged(nameof(HasRunGraph));
            OnPropertyChanged(nameof(RunGraphSummary));
            return;
        }

        var orderedStageIds = new List<string>();

        void Track(string stageId)
        {
            if (!orderedStageIds.Contains(stageId, StringComparer.Ordinal))
            {
                orderedStageIds.Add(stageId);
            }
        }

        foreach (var transition in run.Transitions)
        {
            Track(transition.FromStageId);
            Track(transition.ToStageId);
        }

        Track(run.CurrentStageId);

        foreach (var stageId in orderedStageIds)
        {
            var stage = scheme?.FindStage(stageId);

            RunNodes.Add(new WorkflowRunNodeViewModel(
                stageId,
                stage?.DisplayName ?? stageId,
                stage?.RequiredRole ?? NotReported,
                string.Equals(stageId, run.CurrentStageId, StringComparison.Ordinal)));
        }

        RunGraphSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"Run '{run.Id}': state {run.State}, current stage '{run.CurrentStageId}', "
                + $"{run.Transitions.Count} transition(s), {run.Verdicts.Count} verdict(s), "
                + $"{run.Approvals.Count} approval(s).");

        OnPropertyChanged(nameof(HasRunGraph));
        OnPropertyChanged(nameof(RunGraphSummary));
    }

    public DocumentTemplateOptionViewModel? SelectedDocumentTemplate
    {
        get => _selectedDocumentTemplate;
        set
        {
            if (SetProperty(ref _selectedDocumentTemplate, value))
            {
                OnPropertyChanged(nameof(SelectedDocumentDisplay));
                OnPropertyChanged(nameof(SelectedDocumentInstructions));
            }
        }
    }

    public string SelectedDocumentDisplay =>
        SelectedDocumentTemplate is null
            ? NotReported
            : SelectedDocumentTemplate.DisplayName;

    public string SelectedDocumentInstructions =>
        SelectedDocumentTemplate?.ModelInstructions ?? string.Empty;

    public string DraftTitle
    {
        get => _draftTitle;
        set => SetProperty(ref _draftTitle, value);
    }

    public string DraftContent
    {
        get => _draftContent;
        set
        {
            if (SetProperty(ref _draftContent, value))
            {
                OnPropertyChanged(nameof(HasUnsavedDraftChanges));
                ClearPreview();
                RetireGateResult();
                UpdateCommandStates();
            }
        }
    }

    public WorkflowDocumentDraft? CurrentDraft
    {
        get => _currentDraft;
        private set
        {
            if (SetProperty(ref _currentDraft, value))
            {
                OnPropertyChanged(nameof(HasDraft));
                OnPropertyChanged(nameof(DraftHashDisplay));
                OnPropertyChanged(nameof(DraftVersionDisplay));
                OnPropertyChanged(nameof(HasUnsavedDraftChanges));
                RetireGateResult();
                UpdateCommandStates();
            }
        }
    }

    public bool HasDraft => CurrentDraft is not null;

    public bool HasUnsavedDraftChanges => CurrentDraft is not null
        && !string.Equals(DraftContent, CurrentDraft.Content, StringComparison.Ordinal);

    public string DraftHashDisplay => CurrentDraft?.ContentHash ?? NotReported;

    public string DraftVersionDisplay =>
        CurrentDraft is null
            ? NotReported
            : string.Create(CultureInfo.InvariantCulture, $"v{CurrentDraft.Version}");

    public string CompletenessDisplay { get; private set; } = NotReported;

    public bool IsDocumentComplete { get; private set; }

    public string PreviewContent
    {
        get => _previewContent;
        private set => SetProperty(ref _previewContent, value);
    }

    public string PreviewSummary
    {
        get => _previewSummary;
        private set => SetProperty(ref _previewSummary, value);
    }

    public string PreviewScanSummary
    {
        get => _previewScanSummary;
        private set => SetProperty(ref _previewScanSummary, value);
    }

    public bool PreviewIsBlocked
    {
        get => _previewIsBlocked;
        private set => SetProperty(ref _previewIsBlocked, value);
    }

    public bool HasPreview
    {
        get => _hasPreview;
        private set => SetProperty(ref _hasPreview, value);
    }

    public string ReviewerRole
    {
        get => _reviewerRole;
        set => SetProperty(ref _reviewerRole, value);
    }

    public string ReviewerRouteId
    {
        get => _reviewerRouteId;
        set => SetProperty(ref _reviewerRouteId, value);
    }

    public string ReviewerEvidence
    {
        get => _reviewerEvidence;
        set => SetProperty(ref _reviewerEvidence, value);
    }

    public WorkflowReviewVerdict ReviewerVerdict
    {
        get => _reviewerVerdict;
        set => SetProperty(ref _reviewerVerdict, value);
    }

    public IReadOnlyList<WorkflowReviewVerdict> ReviewerVerdictOptions { get; } =
        new[]
        {
            WorkflowReviewVerdict.Approve,
            WorkflowReviewVerdict.Reject,
            WorkflowReviewVerdict.RequestChanges
        };

    public bool HasReviewerVerdicts => ReviewerVerdicts.Count > 0;

    public string? GateRequestedRouteId
    {
        get => _gateRequestedRouteId;
        set => SetProperty(ref _gateRequestedRouteId, value);
    }

    public string? GateObservedRouteId
    {
        get => _gateObservedRouteId;
        set => SetProperty(ref _gateObservedRouteId, value);
    }

    public bool GateRequiresUiArtifact
    {
        get => _gateRequiresUiArtifact;
        set => SetProperty(ref _gateRequiresUiArtifact, value);
    }

    public bool GateRequiresUserVisualAcceptance
    {
        get => _gateRequiresUserVisualAcceptance;
        set => SetProperty(ref _gateRequiresUserVisualAcceptance, value);
    }

    public bool UiArtifactPresent
    {
        get => _uiArtifactPresent;
        set
        {
            if (SetProperty(ref _uiArtifactPresent, value)) StoreDraftVisualEvidence();
        }
    }

    public bool VisualAcceptancePresent
    {
        get => _visualAcceptancePresent;
        set
        {
            if (SetProperty(ref _visualAcceptancePresent, value)) StoreDraftVisualEvidence();
        }
    }

    public string GateSummary
    {
        get => _gateSummary;
        private set => SetProperty(ref _gateSummary, value);
    }

    public bool HasGateResult
    {
        get => _hasGateResult;
        private set
        {
            if (SetProperty(ref _hasGateResult, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public bool IsCoderStartAllowed
    {
        get => _isCoderStartAllowed;
        private set
        {
            if (SetProperty(ref _isCoderStartAllowed, value))
            {
                OnPropertyChanged(nameof(CoderStartStatusDisplay));
            }
        }
    }

    public string CoderStartStatusDisplay =>
        IsCoderStartAllowed ? "Кодер может стартовать" : "Старт кодера заблокирован";

    /// <summary>The scheme node that resolves conflicting verdicts; empty routes to the user.</summary>
    public string? GateEscalationTargetNodeId
    {
        get => _gateEscalationTargetNodeId;
        set => SetProperty(ref _gateEscalationTargetNodeId, value);
    }

    public bool GateRequiresEscalation
    {
        get => _gateRequiresEscalation;
        private set => SetProperty(ref _gateRequiresEscalation, value);
    }

    public string GateEscalationDisplay
    {
        get => _gateEscalationDisplay;
        private set => SetProperty(ref _gateEscalationDisplay, value);
    }

    public string GateBlockedTransitionDisplay => CoderTransitionId;

    public int CoderStartCount { get; private set; }

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCommandStates();
            }
        }
    }

    public bool CanEditTemplate => !IsBusy && SelectedTemplate is not null;

    public async Task RefreshTemplatesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Blocker = string.Empty;

        try
        {
            await LoadTemplatesCoreAsync().ConfigureAwait(true);
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

    private async Task LoadTemplatesCoreAsync()
    {
        var previous = SelectedTemplate;
        var templates = await _studioService.ListTemplatesAsync().ConfigureAwait(true);

        Templates.Clear();

        foreach (var template in templates)
        {
            Templates.Add(new WorkflowTemplateItemViewModel(template));
        }

        SelectedTemplate = previous is null
            ? Templates.FirstOrDefault()
            : Templates.FirstOrDefault(template =>
                    string.Equals(template.TemplateId, previous.TemplateId, StringComparison.Ordinal)
                    && template.Version == previous.Version)
                ?? Templates.FirstOrDefault();
    }

    public async Task CloneTemplateAsync()
    {
        if (!CanEditTemplate)
        {
            return;
        }

        IsBusy = true;
        Blocker = string.Empty;
        StatusMessage = string.Empty;

        try
        {
            var clone = await _studioService
                .CloneTemplateAsync(
                    SelectedTemplate!.TemplateId,
                    NewTemplateId,
                    NewTemplateName,
                    SelectedTemplate.Version)
                .ConfigureAwait(true);

            StatusMessage =
                $"Клонирован шаблон '{clone.TemplateId}' v{clone.Version}; исходный шаблон не изменён.";
            await LoadTemplatesCoreAsync().ConfigureAwait(true);
            SelectedTemplate = Templates.FirstOrDefault(template =>
                string.Equals(template.TemplateId, clone.TemplateId, StringComparison.Ordinal)
                && template.Version == clone.Version);
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

    public async Task CreateTemplateVersionAsync()
    {
        if (!CanEditTemplate)
        {
            return;
        }

        IsBusy = true;
        Blocker = string.Empty;
        StatusMessage = string.Empty;

        try
        {
            var version = await _studioService
                .CreateTemplateVersionAsync(
                    SelectedTemplate!.TemplateId,
                    NewTemplateVersion,
                    SelectedTemplate.Version)
                .ConfigureAwait(true);

            StatusMessage =
                $"Создана версия v{version.Version} шаблона '{version.TemplateId}'; "
                + "предыдущая версия не изменена.";
            await LoadTemplatesCoreAsync().ConfigureAwait(true);
            SelectedTemplate = Templates.FirstOrDefault(template =>
                string.Equals(template.TemplateId, version.TemplateId, StringComparison.Ordinal)
                && template.Version == version.Version);
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

    public void AddTemplateNode()
    {
        _nodeSequence++;
        var node = new WorkflowNodeEditorViewModel(
            string.Create(CultureInfo.InvariantCulture, $"node-{_nodeSequence}"),
            WorkflowNodeKind.Prompt,
            string.Create(CultureInfo.InvariantCulture, $"Node {_nodeSequence}"),
            WorkflowScheme.CoordinatorRole);

        TemplateNodes.Add(node);
        SelectedNode = node;
        OnPropertyChanged(nameof(TemplateGraphSummary));
        HasTemplateValidation = false;
    }

    public void RemoveTemplateNode()
    {
        var node = SelectedNode;

        if (node is null)
        {
            return;
        }

        TemplateNodes.Remove(node);
        SelectedNode = TemplateNodes.FirstOrDefault();
        OnPropertyChanged(nameof(TemplateGraphSummary));
        HasTemplateValidation = false;
    }

    public async Task<WorkflowGraphValidationReport> ValidateTemplateAsync()
    {
        WorkflowGraphValidationReport report;

        try
        {
            report = _studioService.ValidateTemplateGraph(BuildTemplateGraph());
        }
        catch (Exception exception) when (exception is WorkflowValidationException or InvalidOperationException
            or ArgumentException)
        {
            report = WorkflowGraphValidationReport.Invalid(new[] { UiErrorMessage.Describe(exception) });
        }

        IsTemplateGraphValid = report.IsValid;
        HasTemplateValidation = true;
        TemplateValidationMessage = report.Summary;
        StatusMessage = report.Summary;

        await Task.CompletedTask.ConfigureAwait(true);

        return report;
    }

    public async Task SaveTemplateAsync()
    {
        if (!CanEditTemplate)
        {
            return;
        }

        IsBusy = true;
        Blocker = string.Empty;
        StatusMessage = string.Empty;

        try
        {
            var report = await ValidateTemplateAsync().ConfigureAwait(true);

            if (!report.IsValid)
            {
                Blocker = report.Summary;
                return;
            }

            var template = new WorkflowTemplateDefinition(
                NewTemplateId,
                NewTemplateVersion,
                NewTemplateName,
                "Пользовательский шаблон студии.",
                BuildTemplateGraph(),
                BuildRoleBindings(),
                SelectedTemplate!.RequiredDocumentTemplates,
                isBuiltIn: false,
                _timeProvider.GetUtcNow());

            await _studioService.SaveTemplateAsync(template).ConfigureAwait(true);
            StatusMessage = $"Сохранён шаблон '{template.TemplateId}' v{template.Version}.";
            await LoadTemplatesCoreAsync().ConfigureAwait(true);
            SelectedTemplate = Templates.FirstOrDefault(item =>
                string.Equals(item.TemplateId, template.TemplateId, StringComparison.Ordinal)
                && item.Version == template.Version);
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

    public async Task<WorkflowDocumentDraft> GenerateDraftAsync()
    {
        var option = SelectedDocumentTemplate
            ?? throw new WorkflowValidationException("Не выбран тип шаблона документа.");
        var title = string.IsNullOrWhiteSpace(DraftTitle)
            ? string.Create(CultureInfo.InvariantCulture, $"Черновик: {option.DisplayName}")
            : DraftTitle;

        IsBusy = true;
        Blocker = string.Empty;
        StatusMessage = string.Empty;

        try
        {
            var draft = await _documentTemplateService
                .GenerateDraftAsync(option.Kind, title)
                .ConfigureAwait(true);

            ApplyDraft(draft);
            StatusMessage = $"Сгенерирован черновик '{draft.Title}' ({draft.ContentHash[..19]}…).";

            return draft;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UpdateDraftAsync()
    {
        if (CurrentDraft is null)
        {
            return;
        }

        IsBusy = true;
        Blocker = string.Empty;

        try
        {
            var draft = await _documentTemplateService
                .UpdateDraftAsync(CurrentDraft.DraftId, DraftContent)
                .ConfigureAwait(true);

            ApplyDraft(draft);
            StatusMessage =
                $"Правка сохранена: v{draft.Version}, hash {draft.ContentHash[..19]}…; "
                + "прежние вердикты и утверждение сброшены.";
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

    public async Task PreviewDraftAsync()
    {
        if (!CanUsePersistedDraft())
        {
            return;
        }

        var currentDraft = CurrentDraft!;

        IsBusy = true;
        Blocker = string.Empty;

        try
        {
            var preview = await _documentTemplateService
                .GeneratePreSendPreviewAsync(currentDraft.DraftId)
                .ConfigureAwait(true);

            PreviewContent = preview.Content;
            PreviewSummary = preview.Summary;
            PreviewIsBlocked = preview.IsBlocked;
            HasPreview = true;
            PreviewScanSummary = preview.HasScanner
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Secret scan: {preview.ScanReport.Findings.Count} finding(s), "
                        + $"{preview.RedactedLines.Count} redacted line(s).")
                : "Secret scan недоступен: сканер не сконфигурирован, отправка заблокирована.";
            StatusMessage = preview.Summary;
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

    public async Task AddReviewVerdictAsync()
    {
        if (!CanUsePersistedDraft())
        {
            return;
        }

        var currentDraft = CurrentDraft!;

        IsBusy = true;
        Blocker = string.Empty;

        try
        {
            var verdict = new ReviewerVerdictRecord(
                ReviewerRole,
                ReviewerRouteId,
                currentDraft.ContentHash,
                ReviewerVerdict,
                ReviewerEvidence,
                _timeProvider.GetUtcNow());

            var draft = await _documentTemplateService
                .AddReviewVerdictAsync(currentDraft.DraftId, verdict)
                .ConfigureAwait(true);

            ApplyDraft(draft);
            StatusMessage = $"Вердикт {verdict.ReviewerRole}: {verdict.Verdict} привязан к hash.";
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

    public async Task ApproveDocumentAsync()
    {
        if (!CanUsePersistedDraft())
        {
            return;
        }

        var currentDraft = CurrentDraft!;

        IsBusy = true;
        Blocker = string.Empty;

        try
        {
            var approval = new UserApprovalEvidence(
                "approval-" + Guid.NewGuid().ToString("N"),
                "studio-user",
                WorkflowScheme.UserApprovalStageId,
                currentDraft.ContentHash,
                UserApprovalDecision.Approved,
                "Утверждено в Workflow Studio на текущий hash.",
                _timeProvider.GetUtcNow());

            var draft = await _documentTemplateService
                .ApproveDraftAsync(currentDraft.DraftId, approval)
                .ConfigureAwait(true);

            ApplyDraft(draft);
            StatusMessage = $"Пользователь утвердил {draft.Kind} на hash {draft.ContentHash[..19]}….";
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

    public Task<PreCoderGateValidationResult> RefreshGateAsync()
    {
        if (HasUnsavedDraftChanges)
        {
            var refused = PreCoderGateValidationResult.RefusedInput(CoderTransitionId,
                "Сохраните изменения документа перед проверкой pre-coder gate.");
            ApplyGateResult(refused);
            return Task.FromResult(refused);
        }

        var requiredKinds = SelectedTemplate?.RequiredDocumentTemplates
            ?? WorkflowStudioDocumentRules.RequiredDocumentKinds;
        var documents = _documentTemplateService
            .ListDrafts()
            .Select(draft => new PreCoderGateDocument(
                draft.Kind,
                draft.DraftId,
                draft.ContentHash,
                draft.Version,
                draft.ReviewerVerdicts,
                draft.UserApproval,
                GetDraftVisualEvidence(draft).Ui,
                GetDraftVisualEvidence(draft).Visual))
            .ToArray();

        var request = new PreCoderGateRequest(
            requiredKinds,
            _studioService.DocumentReviewerRoles,
            documents,
            CoderTransitionId,
            GateRequestedRouteId,
            GateObservedRouteId,
            GateRequiresUiArtifact,
            GateRequiresUserVisualAcceptance,
            GateEscalationTargetNodeId);

        var result = _gateValidator.Evaluate(request);

        ApplyGateResult(result);
        return Task.FromResult(result);
    }

    private void ApplyGateResult(PreCoderGateValidationResult result)
    {

        GateChecks.Clear();

        foreach (var check in result.Checks)
        {
            GateChecks.Add(new PreCoderGateCheckViewModel(check));
        }

        GateSummary = result.Summary;
        HasGateResult = true;
        IsCoderStartAllowed = result.IsAllowed;
        GateRequiresEscalation = result.RequiresEscalation;
        GateEscalationDisplay = result.RequiresEscalation
            ? $"Конфликт вердиктов направлен: {result.EscalationTargetDisplay}"
            : string.Empty;

    }

    public async Task RequestCoderStartAsync()
    {
        var result = await RefreshGateAsync().ConfigureAwait(true);

        if (!result.IsAllowed)
        {
            Blocker =
                $"Кодер не может стартовать: переход '{CoderTransitionId}' заблокирован. "
                + string.Join("; ", result.BlockingReasons);
            return;
        }

        CoderStartCount++;
        Blocker = string.Empty;
        StatusMessage = $"Pre-coder gate разрешил переход '{CoderTransitionId}'.";
    }

    public Task<WorkflowAccountContextSwitchResult> SwitchAccountContextAsync(
        WorkflowAccountContextSwitchRequest request,
        CancellationToken cancellationToken = default) =>
        _studioService.SwitchAccountContextAsync(request, cancellationToken);

    /// <summary>
    /// Requests the AGY profile switch. The helper takes no observed route and no native session id:
    /// both are backend observations, so a Studio caller cannot vouch for them. The result is a refusal
    /// unless a backend proved the switch.
    /// </summary>
    public Task<WorkflowAccountContextSwitchResult> SwitchAgyProfileAsync(
        string profileName,
        string requestedRouteId,
        string? previousNativeSessionId = null,
        WorkflowMirasimIsolationState? mirasimState = null) =>
        SwitchAccountContextAsync(new WorkflowAccountContextSwitchRequest(
            AccountContextKind.Agy,
            profileName,
            previousNativeSessionId,
            AgyProfileName: profileName,
            RequestedRouteId: requestedRouteId,
            MirasimState: mirasimState));

    /// <summary>
    /// Requests the Codex <c>CODEX_HOME</c> switch. Like the AGY helper it carries no observed route and
    /// no native session id, so a Studio caller cannot turn a request into evidence of a switch.
    /// </summary>
    public Task<WorkflowAccountContextSwitchResult> SwitchCodexHomeAsync(
        string accountId,
        string codexHomePath,
        string requestedRouteId,
        string? previousNativeSessionId = null,
        WorkflowMirasimIsolationState? mirasimState = null) =>
        SwitchAccountContextAsync(new WorkflowAccountContextSwitchRequest(
            AccountContextKind.Codex,
            accountId,
            previousNativeSessionId,
            CodexHomePath: codexHomePath,
            RequestedRouteId: requestedRouteId,
            MirasimState: mirasimState));

    private void ApplyDraft(WorkflowDocumentDraft draft)
    {
        CurrentDraft = draft;
        DraftTitle = draft.Title;
        DraftContent = draft.Content;
        var visual = GetDraftVisualEvidence(draft);
        SetProperty(ref _uiArtifactPresent, visual.Ui, nameof(UiArtifactPresent));
        SetProperty(ref _visualAcceptancePresent, visual.Visual, nameof(VisualAcceptancePresent));
        RefreshVerdicts(draft);
        RefreshCompleteness(draft);
        ClearPreview();
    }

    private void RefreshVerdicts(WorkflowDocumentDraft draft)
    {
        ReviewerVerdicts.Clear();

        foreach (var verdict in draft.ReviewerVerdicts.OrderBy(item => item.RecordedAtUtc))
        {
            ReviewerVerdicts.Add(new ReviewerVerdictItemViewModel(verdict));
        }

        OnPropertyChanged(nameof(HasReviewerVerdicts));
    }

    private void RefreshCompleteness(WorkflowDocumentDraft draft)
    {
        var evaluation = _documentTemplateService.EvaluateCompleteness(draft.DraftId);

        IsDocumentComplete = evaluation.AllRequiredSectionsPresent;
        CompletenessDisplay = evaluation.Summary;
        OnPropertyChanged(nameof(IsDocumentComplete));
        OnPropertyChanged(nameof(CompletenessDisplay));
    }

    private void ClearPreview()
    {
        PreviewContent = string.Empty;
        PreviewSummary = string.Empty;
        PreviewScanSummary = string.Empty;
        PreviewIsBlocked = false;
        HasPreview = false;
    }

    private bool CanUsePersistedDraft()
    {
        if (IsBusy || CurrentDraft is null) return false;
        if (!HasUnsavedDraftChanges) return true;
        Blocker = "Сохраните изменения документа перед предпросмотром, ревью или утверждением.";
        return false;
    }

    private (bool Ui, bool Visual) GetDraftVisualEvidence(WorkflowDocumentDraft draft) =>
        _draftVisualEvidence.TryGetValue((draft.DraftId, draft.ContentHash), out var evidence)
            ? evidence : (false, false);

    private void StoreDraftVisualEvidence()
    {
        // These are local planning acknowledgements for one saved hash, never native evidence.
        if (CurrentDraft is not null && !HasUnsavedDraftChanges)
            _draftVisualEvidence[(CurrentDraft.DraftId, CurrentDraft.ContentHash)] =
                (UiArtifactPresent, VisualAcceptancePresent);
        RetireGateResult();
    }

    private void RetireGateResult()
    {
        HasGateResult = false;
        IsCoderStartAllowed = false;
        GateChecks.Clear();
        GateSummary = string.Empty;
        GateRequiresEscalation = false;
        GateEscalationDisplay = string.Empty;
    }

    private void OnTemplateNodesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var node in _subscribedNodes.Where(node => !TemplateNodes.Contains(node)).ToArray())
        {
            node.PropertyChanged -= OnTemplateNodeChanged;
            _subscribedNodes.Remove(node);
        }
        foreach (var node in TemplateNodes)
            if (_subscribedNodes.Add(node)) node.PropertyChanged += OnTemplateNodeChanged;
        if (SelectedNode is not null && !TemplateNodes.Contains(SelectedNode)) SelectedNode = null;
        RetireTemplateValidation();
    }

    private void OnTemplateNodeChanged(object? sender, PropertyChangedEventArgs args) => RetireTemplateValidation();

    private void RetireTemplateValidation()
    {
        HasTemplateValidation = false;
        IsTemplateGraphValid = false;
        TemplateValidationMessage = string.Empty;
        OnPropertyChanged(nameof(TemplateGraphSummary));
    }

    private void LoadTemplateIntoEditor(WorkflowTemplateItemViewModel? template)
    {
        SelectedNode = null;
        RetireTemplateValidation();
        RetireGateResult();
        TemplateNodes.Clear();

        if (template is null)
        {
            RoleMatrix.Clear();
            RoleMatrixSummary = "Матрица ролей недоступна: шаблон не выбран.";
            OnPropertyChanged(nameof(HasRoleMatrix));
            return;
        }

        foreach (var node in template.Definition.Graph.Nodes)
        {
            TemplateNodes.Add(WorkflowNodeEditorViewModel.FromDefinition(node));
        }

        NewTemplateId = template.TemplateId + "-user";
        NewTemplateName = template.DisplayName + " (user)";
        NewTemplateVersion = template.Version + 1;
        HasTemplateValidation = false;
        RebuildRoleMatrix(template);
        OnPropertyChanged(nameof(TemplateGraphSummary));
    }

    private void RebuildRoleMatrix(WorkflowTemplateItemViewModel template)
    {
        RoleMatrix.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiredDocuments = string.Join(
            ", ",
            template.RequiredDocumentTemplates.Select(kind => kind.ToString()));

        foreach (var binding in template.Definition.RoleBindings)
        {
            if (!seen.Add(binding.RoleId))
            {
                continue;
            }

            var stages = template.Definition.Graph.Nodes
                .Where(node => string.Equals(node.RoleBinding, binding.RoleId, StringComparison.OrdinalIgnoreCase))
                .Select(node => node.DisplayName)
                .ToArray();

            RoleMatrix.Add(new WorkflowRoleMatrixItemViewModel(
                binding.RoleId,
                binding.PrimaryRouteId ?? NotReported,
                stages.Length == 0 ? NotReported : string.Join(", ", stages),
                requiredDocuments));
        }

        foreach (var node in template.Definition.Graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.RoleBinding) || !seen.Add(node.RoleBinding))
            {
                continue;
            }

            RoleMatrix.Add(new WorkflowRoleMatrixItemViewModel(
                node.RoleBinding,
                string.IsNullOrWhiteSpace(node.PrimaryRouteId) ? NotReported : node.PrimaryRouteId,
                node.DisplayName,
                requiredDocuments));
        }

        RoleMatrixSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"{RoleMatrix.Count} роль(ей) · шаблон '{template.DisplayName}' v{template.Version} · "
                + $"{template.RequiredDocumentCount} обязательных документ(ов).");
        OnPropertyChanged(nameof(HasRoleMatrix));
    }

    private WorkflowGraph BuildTemplateGraph()
    {
        var definitions = TemplateNodes.Select(node => node.ToDefinition()).ToArray();

        if (definitions.Length == 0)
        {
            throw new WorkflowValidationException("Схема шаблона не содержит узлов.");
        }

        return new WorkflowGraph(definitions[0].NodeId, definitions);
    }

    private IReadOnlyList<RoleBindingDefinition> BuildRoleBindings() =>
        TemplateNodes
            .Select(node => (node.RoleBinding, node.PrimaryRouteId))
            .Distinct()
            .Select(pair => new RoleBindingDefinition(
                pair.RoleBinding,
                string.IsNullOrWhiteSpace(pair.PrimaryRouteId) ? null : pair.PrimaryRouteId))
            .ToArray();

    private async Task RunSafelyAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = UiErrorMessage.Describe(exception);
        }
    }

    private static WorkflowStudioPanelMode ResolvePanelMode(object? parameter) =>
        parameter switch
        {
            WorkflowStudioPanelMode mode => mode,
            string text when Enum.TryParse<WorkflowStudioPanelMode>(text, ignoreCase: true, out var parsed) =>
                parsed,
            _ => throw new ArgumentException(
                "Panel selection requires a WorkflowStudioPanelMode value.",
                nameof(parameter))
        };

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(CanEditTemplate));
        OnPropertyChanged(nameof(HasDraft));
        RelayCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>One versioned template row of the studio list.</summary>
public sealed class WorkflowTemplateItemViewModel
{
    public WorkflowTemplateItemViewModel(WorkflowTemplateDefinition definition)
    {
        Definition = definition;
    }

    public WorkflowTemplateDefinition Definition { get; }

    public string TemplateId => Definition.TemplateId;

    public int Version => Definition.Version;

    public string DisplayName => Definition.DisplayName;

    public string Description => Definition.Description;

    public bool IsBuiltIn => Definition.IsBuiltIn;

    public string VersionDisplay => Definition.VersionDisplay;

    public string OriginDisplay => IsBuiltIn ? "built-in" : "user";

    public int NodeCount => Definition.Graph.Nodes.Count;

    public int RequiredDocumentCount => Definition.RequiredDocumentTemplates.Count;

    public IReadOnlyList<DocumentTemplateKind> RequiredDocumentTemplates =>
        Definition.RequiredDocumentTemplates;

    public string SummaryDisplay =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{DisplayName} · v{Version} · {OriginDisplay} · {NodeCount} node(s) · "
                + $"{RequiredDocumentCount} document template(s)");
}

/// <summary>A mutable editor row of the template graph.</summary>
public sealed class WorkflowNodeEditorViewModel : ObservableObject
{
    private string _nodeId;
    private WorkflowNodeKind _nodeKind;
    private string _displayName;
    private string _roleBinding;
    private string _primaryRouteId;
    private string _successTargetNodeId;
    private string _failureTargetNodeId;
    private int _retryBudget;
    private WorkflowNodeDefinition? _sourceDefinition;

    public WorkflowNodeEditorViewModel(
        string nodeId,
        WorkflowNodeKind nodeKind,
        string displayName,
        string roleBinding,
        string primaryRouteId = "",
        string successTargetNodeId = "",
        string failureTargetNodeId = "",
        int retryBudget = 0)
    {
        _nodeId = nodeId;
        _nodeKind = nodeKind;
        _displayName = displayName;
        _roleBinding = roleBinding;
        _primaryRouteId = primaryRouteId;
        _successTargetNodeId = successTargetNodeId;
        _failureTargetNodeId = failureTargetNodeId;
        _retryBudget = retryBudget;
    }

    public string NodeId
    {
        get => _nodeId;
        set => SetProperty(ref _nodeId, value);
    }

    public WorkflowNodeKind NodeKind
    {
        get => _nodeKind;
        set => SetProperty(ref _nodeKind, value);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string RoleBinding
    {
        get => _roleBinding;
        set => SetProperty(ref _roleBinding, value);
    }

    public string PrimaryRouteId
    {
        get => _primaryRouteId;
        set => SetProperty(ref _primaryRouteId, value);
    }

    public string SuccessTargetNodeId
    {
        get => _successTargetNodeId;
        set => SetProperty(ref _successTargetNodeId, value);
    }

    public string FailureTargetNodeId
    {
        get => _failureTargetNodeId;
        set => SetProperty(ref _failureTargetNodeId, value);
    }

    public int RetryBudget
    {
        get => _retryBudget;
        set => SetProperty(ref _retryBudget, value);
    }

    public WorkflowNodeDefinition ToDefinition() =>
        new(
            NodeId,
            NodeKind,
            DisplayName,
            RoleBinding,
            requiredCapabilities: _sourceDefinition?.RequiredCapabilities,
            primaryRouteId: NullIfBlank(PrimaryRouteId),
            fallbackRouteIds: _sourceDefinition?.FallbackRouteIds,
            timeout: _sourceDefinition?.Timeout,
            retryBudget: RetryBudget,
            successTargetNodeId: NullIfBlank(SuccessTargetNodeId),
            failureTargetNodeId: NullIfBlank(FailureTargetNodeId),
            conditionExpression: _sourceDefinition?.ConditionExpression,
            artifactContract: _sourceDefinition?.ArtifactContract,
            permissionIntent: _sourceDefinition?.PermissionIntent,
            gateMetadata: _sourceDefinition?.GateMetadata);

    public static WorkflowNodeEditorViewModel FromDefinition(WorkflowNodeDefinition node) =>
        new(
            node.NodeId,
            node.Kind,
            node.DisplayName,
            node.RoleBinding,
            node.PrimaryRouteId ?? string.Empty,
            node.SuccessTargetNodeId ?? string.Empty,
            node.FailureTargetNodeId ?? string.Empty,
            node.RetryBudget) { _sourceDefinition = node };

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>The compact surfaces of the consolidated Workflow Studio (ROADMAP Phase 11).</summary>
public enum WorkflowStudioPanelMode
{
    Overview,
    Templates,
    RoleMatrix,
    Documents
}

/// <summary>One role of the selected template as shown by the studio role matrix.</summary>
public sealed class WorkflowRoleMatrixItemViewModel
{
    public WorkflowRoleMatrixItemViewModel(
        string roleId,
        string routeDisplay,
        string stagesDisplay,
        string requiredDocumentsDisplay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);

        RoleDisplay = roleId;
        RouteDisplay = routeDisplay;
        StagesDisplay = stagesDisplay;
        RequiredDocumentsDisplay = requiredDocumentsDisplay;
    }

    public string RoleDisplay { get; }

    public string RouteDisplay { get; }

    public string StagesDisplay { get; }

    public string RequiredDocumentsDisplay { get; }

    public string SummaryDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"{RoleDisplay} → {RouteDisplay} · этапы: {StagesDisplay} · документы: {RequiredDocumentsDisplay}");
}

/// <summary>A read-only node of the actual run graph shown next to the editable schema.</summary>
public sealed class WorkflowRunNodeViewModel
{
    public WorkflowRunNodeViewModel(
        string stageId,
        string displayName,
        string role,
        bool isCurrent)
    {
        StageId = stageId;
        DisplayName = displayName;
        Role = role;
        IsCurrent = isCurrent;
    }

    public string StageId { get; }

    public string DisplayName { get; }

    public string Role { get; }

    public bool IsCurrent { get; }

    public string SummaryDisplay =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{DisplayName} · {Role}{(IsCurrent ? " · current" : string.Empty)}");
}

/// <summary>One of the seven document template kinds offered by the studio editor.</summary>
public sealed class DocumentTemplateOptionViewModel
{
    public DocumentTemplateOptionViewModel(DocumentTemplateDefinition definition)
    {
        Definition = definition;
    }

    public DocumentTemplateDefinition Definition { get; }

    public DocumentTemplateKind Kind => Definition.Kind;

    public string DisplayName => Definition.DisplayName;

    public string Description => Definition.Description;

    public string ModelInstructions => Definition.ModelInstructions;

    public string SectionsDisplay => string.Join(", ", Definition.RequiredSections);
}

/// <summary>A separate reviewer verdict row, pinned to its own document hash.</summary>
public sealed class ReviewerVerdictItemViewModel
{
    public ReviewerVerdictItemViewModel(ReviewerVerdictRecord verdict)
    {
        Verdict = verdict;
    }

    public ReviewerVerdictRecord Verdict { get; }

    public string ReviewerDisplay => Verdict.ReviewerRole;

    public string VerdictDisplay => Verdict.Verdict.ToString();

    public string RouteDisplay => Verdict.RouteId;

    public string HashDisplay => Verdict.DocumentHash.Length <= 19
        ? Verdict.DocumentHash
        : Verdict.DocumentHash[..19] + "…";

    public string RecordedAtDisplay => Verdict.RecordedAtUtc.ToString(
        "yyyy-MM-dd HH:mm:ss 'UTC'",
        CultureInfo.InvariantCulture);

    public string EvidenceDisplay => Verdict.EvidenceSummary;

    public string SummaryDisplay =>
        $"{ReviewerDisplay}: {VerdictDisplay} · {HashDisplay} · {RecordedAtDisplay}";
}

/// <summary>A separately displayed pre-coder gate check.</summary>
public sealed class PreCoderGateCheckViewModel
{
    public PreCoderGateCheckViewModel(PreCoderGateCheck check)
    {
        Check = check;
    }

    public PreCoderGateCheck Check { get; }

    public string CheckId => Check.CheckId;

    public string DisplayName => Check.DisplayName;

    public bool IsSatisfied => Check.IsSatisfied;

    public string Detail => Check.Detail;

    public string StatusDisplay => IsSatisfied ? "OK" : "Блок";

    public string SummaryDisplay => $"{DisplayName}: {StatusDisplay} — {Detail}";
}
