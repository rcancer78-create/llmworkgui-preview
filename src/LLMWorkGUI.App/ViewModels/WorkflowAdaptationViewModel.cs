using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Headless-testable adaptation dialog of ТЗ §6.14. It has exactly two states: the pre-send preview
/// (route, goal, semantic scope, scanned files with exclusions) and the candidate session (unified
/// diff, semantic role mappings, warnings/blockers, follow-up prompt and the four standard actions).
/// No model call, candidate save, activation, rollback or discard ever happens in the background:
/// every command is an explicit operator action and the source blob plus active binding stay untouched
/// until <c>Принять и активировать</c> runs through <see cref="IWorkflowActivationService"/>.
/// </summary>
public sealed class WorkflowAdaptationViewModel : ObservableObject
{
    public const string AcknowledgeBlockersRequiredMessage =
        "Невозможно активировать: у кандидата есть блокировки. Подтвердите каждую показанную блокировку отдельно.";

    public const string UnverifiableSemanticScopeMessage =
        "Невозможно активировать: часть пакета не удалось проверить (файл не читается или превышен лимит анализа). "
        + "Этот пробел нельзя подтвердить — перезапустите адаптацию так, чтобы сравнение охватывало весь пакет.";

    private readonly IWorkflowAdaptationService? _adaptationService;
    private readonly IWorkflowActivationService? _activationService;
    private readonly ISanitizedCatalogProvider? _catalogProvider;
    private readonly IWorkflowMaterialPolicyStore? _materialPolicies;
    private WorkflowMaterialPolicy? _materialPolicy;
    private DataClassification _materialClassification = DataClassification.Restricted;

    private bool _isVisible;
    private bool _isSessionActive;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private string _errorMessage = string.Empty;
    private string _promptPreview = string.Empty;
    private WorkflowVersionItemViewModel? _sourceVersion;
    private WorkflowPackageItemViewModel? _package;
    private Project? _project;
    private DiscoveredModelDetails? _selectedRoute;
    private AdaptationGoal _selectedGoal = AdaptationGoal.Balanced;
    private bool _allowExpandedSemanticScope;
    private string _costQuotaEstimateDisplay = AdaptationPreSendPreview.UnknownQuotaValue;
    private string? _activeSessionId;
    private WorkflowFileDiffViewModel? _selectedDiffFile;
    private string _selectedDiffContent = string.Empty;
    private string _diffSummary = string.Empty;
    private string _adaptationRationale = string.Empty;
    private bool _hasBlockers;
    private string _blockersSummary = string.Empty;
    private string _followUpPrompt = string.Empty;
    private string? _candidateVersionId;
    private int? _candidateVersionNumber;
    private bool _sessionSaved;
    private bool _cleanupPending;
    private PreviewSelection? _acceptedPreview;
    private int _previewToken;
    private int _dialogGeneration;
    private bool _applyingPreview;
    private readonly HashSet<AdaptationFileItemViewModel> _subscribedPreviewFiles = new();
    private sealed record PreviewSelection(string? VersionId, string? PackageId, string? ProjectId,
        string? RouteId, AdaptationGoal Goal, bool ExpandedScope, string ExcludedPaths);
    private const string CleanupPendingMessage = "Версия сохранена, но временные файлы пока не удалены. Повторите очистку, закрыв сессию.";

    public WorkflowAdaptationViewModel(
        IWorkflowAdaptationService? adaptationService = null,
        IWorkflowActivationService? activationService = null,
        ISanitizedCatalogProvider? catalogProvider = null,
        IWorkflowMaterialPolicyStore? materialPolicies = null)
    {
        _adaptationService = adaptationService;
        _activationService = activationService;
        _catalogProvider = catalogProvider;
        _materialPolicies = materialPolicies;
        SaveMaterialClassificationCommand = new RelayCommand(() => _ = SaveMaterialClassificationAsync(),
            () => !IsBusy && !IsSessionActive && SourceVersion is not null && _materialPolicies is not null);

        AvailableGoals = new ObservableCollection<AdaptationGoal>(Enum.GetValues<AdaptationGoal>());

        StartAdaptationCommand = new RelayCommand(
            () => _ = StartAdaptationAsync(),
            () => CanStartAdaptation);
        RefreshPreSendPreviewCommand = new RelayCommand(() => _ = RefreshPreSendPreviewAsync(),
            () => !IsBusy && !IsSessionActive && IsAdaptationAvailable && SourceVersion is not null && SelectedRoute is not null);
        CancelCommand = new RelayCommand(CloseDialog);
        SubmitFollowUpCommand = new RelayCommand(
            () => _ = SubmitFollowUpAsync(),
            () => CanSubmitFollowUp);
        AcceptAndActivateCommand = new RelayCommand(
            () => _ = AcceptAndActivateAsync(),
            () => CanAcceptAndActivate);
        SaveCandidateCommand = new RelayCommand(
            () => _ = SaveCandidateAsync(),
            () => CanSaveCandidate);
        DiscardCommand = new RelayCommand(
            () => _ = DiscardAsync(),
            () => CanDiscard);
        PreSendFiles.CollectionChanged += OnPreviewFilesChanged;
    }

    /// <summary>Notifies the parent workflow library that versions or bindings changed.</summary>
    public Func<Task>? OnLibraryChangedAsync { get; set; }

    public IReadOnlyList<DataClassification> MaterialClassifications { get; } = Enum.GetValues<DataClassification>();
    public bool CanEditMaterialClassification => !IsBusy && !IsSessionActive && SourceVersion is not null && _materialPolicies is not null;
    public DataClassification MaterialClassification
    {
        get => _materialClassification;
        set { if (!IsBusy && !IsSessionActive) SetProperty(ref _materialClassification,value); }
    }
    public string MaterialClassificationStatus => _materialPolicy is { IsDeclared:true }
        ? $"Сохранённый класс версии: {WorkflowLibraryViewModel.DescribeClassification(_materialPolicy.Classification)}. Restricted не передаётся в OpenCode."
        : "Класс исходной версии не сохранён. Подтвердите классификацию материалов перед отправкой.";
    public ICommand SaveMaterialClassificationCommand { get; }
    public async Task SaveMaterialClassificationAsync()
    {
        if (IsBusy || IsSessionActive || SourceVersion is null || _materialPolicies is null) return;
        IsBusy = true;
        try
        {
            _materialPolicy = await _materialPolicies.DeclareAsync(SourceVersion.Id,SourceVersion.Version.BlobId,
                _materialPolicy?.Revision ?? 0,MaterialClassification).ConfigureAwait(true);
            OnPropertyChanged(nameof(MaterialClassificationStatus)); ErrorMessage = string.Empty;
        }
        catch (Exception exception) { ErrorMessage = UiErrorMessage.Describe(exception); }
        finally { IsBusy = false; }
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    /// <summary>False until the candidate exists; true once a session produced a diff.</summary>
    public bool IsSessionActive
    {
        get => _isSessionActive;
        private set
        {
            if (SetProperty(ref _isSessionActive, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsAdaptationAvailable => _adaptationService is not null && _catalogProvider is not null;

    public bool IsActivationAvailable => _activationService is not null;

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

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    // ---- Pre-send preview state -------------------------------------------------------------

    public WorkflowVersionItemViewModel? SourceVersion
    {
        get => _sourceVersion;
        private set => SetProperty(ref _sourceVersion, value);
    }

    public WorkflowPackageItemViewModel? Package
    {
        get => _package;
        private set => SetProperty(ref _package, value);
    }

    public Project? Project
    {
        get => _project;
        private set => SetProperty(ref _project, value);
    }

    public ObservableCollection<DiscoveredModelDetails> AvailableRoutes { get; } = new();

    public DiscoveredModelDetails? SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            if (IsBusy || IsSessionActive) return;
            SetSelectedRouteCore(value);
        }
    }

    private void SetSelectedRouteCore(DiscoveredModelDetails? value)
    {
        if (!SetProperty(ref _selectedRoute, value, nameof(SelectedRoute))) return;
        OnPropertyChanged(nameof(SelectedRouteDisplay));
        RetirePreSendPreview();
        RaiseCommandStates();
    }

    public string SelectedRouteDisplay => SelectedRoute?.Name ?? "Маршрут не выбран";

    public ObservableCollection<AdaptationGoal> AvailableGoals { get; }

    public AdaptationGoal SelectedGoal
    {
        get => _selectedGoal;
        set
        {
            if (IsBusy || IsSessionActive) return;
            if (SetProperty(ref _selectedGoal, value))
            {
                OnPropertyChanged(nameof(SelectedGoalDisplay));
                RetirePreSendPreview();
            }
        }
    }

    public string SelectedGoalDisplay => SelectedGoal.ToDisplayName();

    public bool AllowExpandedSemanticScope
    {
        get => _allowExpandedSemanticScope;
        set { if (!IsBusy && !IsSessionActive && SetProperty(ref _allowExpandedSemanticScope, value)) RetirePreSendPreview(); }
    }

    public ObservableCollection<AdaptationFileItemViewModel> PreSendFiles { get; } = new();

    public bool HasPreSendFiles => PreSendFiles.Count > 0;

    public string CostQuotaEstimateDisplay
    {
        get => _costQuotaEstimateDisplay;
        private set => SetProperty(ref _costQuotaEstimateDisplay, value);
    }

    public string PromptPreview
    {
        get => _promptPreview;
        private set => SetProperty(ref _promptPreview, value);
    }

    public bool HasPromptPreview => !string.IsNullOrEmpty(PromptPreview);

    // ---- Candidate session state ------------------------------------------------------------

    public string? ActiveSessionId
    {
        get => _activeSessionId;
        private set
        {
            if (SetProperty(ref _activeSessionId, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ObservableCollection<WorkflowFileDiffViewModel> DiffFiles { get; } = new();

    public WorkflowFileDiffViewModel? SelectedDiffFile
    {
        get => _selectedDiffFile;
        set
        {
            if (SetProperty(ref _selectedDiffFile, value))
            {
                SelectedDiffContent = value?.UnifiedDiffText ?? string.Empty;
            }
        }
    }

    public string SelectedDiffContent
    {
        get => _selectedDiffContent;
        private set => SetProperty(ref _selectedDiffContent, value);
    }

    public string DiffSummary
    {
        get => _diffSummary;
        private set => SetProperty(ref _diffSummary, value);
    }

    public string AdaptationRationale
    {
        get => _adaptationRationale;
        private set => SetProperty(ref _adaptationRationale, value);
    }

    public ObservableCollection<SemanticRoleMappingViewModel> RoleMappings { get; } = new();

    public ObservableCollection<AdaptationValidationIssueViewModel> Issues { get; } = new();

    public bool HasIssues => Issues.Count > 0;

    public bool HasBlockers
    {
        get => _hasBlockers;
        private set
        {
            if (SetProperty(ref _hasBlockers, value))
            {
                OnPropertyChanged(nameof(HasNoBlockers));
            }
        }
    }

    public bool HasNoBlockers => !HasBlockers;

    public string BlockersSummary
    {
        get => _blockersSummary;
        private set => SetProperty(ref _blockersSummary, value);
    }

    /// <summary>
    /// Blocker issues the operator has to decide on one by one. The rows are replaced by the fresh
    /// validation of the activation engine, and a decision is only kept when the revalidation returned
    /// exactly the same issues again.
    /// </summary>
    public ObservableCollection<ActivationBlockerItemViewModel> ActivationBlockers { get; } = new();

    public bool HasActivationBlockers => ActivationBlockers.Count > 0;

    /// <summary>True while at least one shown blocker issue has no explicit decision yet.</summary>
    public bool HasUnconfirmedActivationBlockers =>
        ActivationBlockers.Any(blocker => !blocker.IsAcknowledged);

    /// <summary>
    /// True when a shown issue reports content the bounded semantic analysis could not read. Such an issue
    /// is never acknowledgeable, so the activation gate stays closed until the candidate is re-packaged so
    /// the comparison actually covers it.
    /// </summary>
    public bool HasUnverifiableSemanticBlockers => ActivationBlockers.Any(blocker => !blocker.CanBeAcknowledged);

    /// <summary>
    /// The issues the operator is confirming. They are sent to the activation engine, which re-validates
    /// the candidate and refuses anything that no longer matches this exact list.
    /// </summary>
    public IReadOnlyList<AdaptationValidationIssue> ConfirmedBlockerIssues =>
        ActivationBlockers
            .Where(blocker => blocker.IsAcknowledged)
            .Select(blocker => blocker.Issue)
            .ToArray();

    public string FollowUpPrompt
    {
        get => _followUpPrompt;
        set => SetProperty(ref _followUpPrompt, value);
    }

    public ICommand StartAdaptationCommand { get; }

    public ICommand RefreshPreSendPreviewCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand SubmitFollowUpCommand { get; }

    public ICommand AcceptAndActivateCommand { get; }

    public ICommand SaveCandidateCommand { get; }

    public ICommand DiscardCommand { get; }

    public bool CanStartAdaptation =>
        IsVisible && !IsBusy && !IsSessionActive && IsAdaptationAvailable && SourceVersion is not null && SelectedRoute is not null
        && _acceptedPreview is not null && _acceptedPreview == CapturePreviewSelection()
        && (_materialPolicies is null || _materialPolicy is { IsDeclared:true, Classification:not DataClassification.Restricted });

    public bool CanEditPreSendInputs => IsVisible && !IsBusy && !IsSessionActive;

    public bool CanSubmitFollowUp =>
        !IsBusy && IsSessionActive && ActiveSessionId is not null && !_sessionSaved && !string.IsNullOrWhiteSpace(FollowUpPrompt);

    public bool CanAcceptAndActivate =>
        !IsBusy && IsActivationAvailable && IsSessionActive && !HasUnconfirmedActivationBlockers;

    public bool CanSaveCandidate => !IsBusy && IsSessionActive && ActiveSessionId is not null && !_sessionSaved;

    public bool CanDiscard => !IsBusy && IsSessionActive;

    /// <summary>
    /// Opens the dialog for one version and project, loads the routable catalog projection and the
    /// pre-send secret scan, and shows the pre-send preview state.
    /// </summary>
    public async Task OpenForVersionAsync(
        WorkflowVersionItemViewModel version,
        WorkflowPackageItemViewModel package,
        Project project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(project);

        if (IsBusy || IsSessionActive)
        {
            ErrorMessage = "Сначала завершите или закройте текущую сессию адаптации.";
            return;
        }

        var generation = ++_dialogGeneration;
        SourceVersion = version;
        Package = package;
        Project = project;
        StatusMessage = string.Empty; ErrorMessage = string.Empty;
        _materialPolicy = null; _materialClassification = DataClassification.Restricted;
        string? materialReadError = null;
        if (_materialPolicies is not null)
        {
            IsBusy = true;
            try
            {
                var policy = await _materialPolicies.ReadAsync(version.Id,cancellationToken).ConfigureAwait(true);
                if (generation != _dialogGeneration) return;
                _materialPolicy = policy;
                _materialClassification = _materialPolicy.Classification;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { materialReadError = UiErrorMessage.Describe(exception); }
            finally { IsBusy = false; }
        }
        if (generation != _dialogGeneration) return;
        OnPropertyChanged(nameof(MaterialClassification)); OnPropertyChanged(nameof(MaterialClassificationStatus));

        ResetSessionState();
        RetirePreSendPreview();
        PreSendFiles.Clear();
        IsVisible = true;

        await LoadRoutesAsync(generation, cancellationToken).ConfigureAwait(true);
        if (!IsCurrentOpen(generation)) return;
        await RefreshPreSendPreviewAsync(cancellationToken).ConfigureAwait(true);
        if (IsCurrentOpen(generation) && materialReadError is not null) ErrorMessage = materialReadError;
    }

    /// <summary>Re-runs the pre-send secret scan and payload preview for the current selection.</summary>
    public async Task RefreshPreSendPreviewAsync(CancellationToken cancellationToken = default)
    {
        if (!IsVisible || IsBusy || IsSessionActive) return;
        RetirePreSendPreview();
        var token = _previewToken;
        var selection = CapturePreviewSelection();
        var excludedPaths = BuildExcludedPaths();

        if (_adaptationService is null || SourceVersion is null || SelectedRoute is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            var preview = await _adaptationService
                .PreparePreSendPreviewAsync(
                    SourceVersion.Id,
                    SelectedRoute.Id,
                    SelectedGoal,
                    excludedPaths,
                    AllowExpandedSemanticScope,
                    cancellationToken)
                .ConfigureAwait(true);

            if (token != _previewToken || selection != CapturePreviewSelection()) return;

            CostQuotaEstimateDisplay = BuildQuotaDisplay(preview);
            PromptPreview = preview.PromptPreview;

            _applyingPreview = true;
            try { BuildPreSendFiles(preview, excludedPaths); }
            finally { _applyingPreview = false; }
            _acceptedPreview = CapturePreviewSelection();
            RaiseCommandStates();

            StatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"Предпросмотр перед отправкой готов: {preview.IncludedFiles.Count} включено, {preview.ExcludedFiles.Count} исключено.");
        }
        catch (OperationCanceledException exception)
        {
            ReportInitialCleanupPending(exception);
            throw;
        }
        catch (Exception exception)
        {
            ErrorMessage = UiErrorMessage.Describe(exception);
            ReportInitialCleanupPending(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Runs the first adaptation turn for the selected version.</summary>
    public async Task StartAdaptationAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStartAdaptation || _adaptationService is null || SourceVersion is null || SelectedRoute is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        try
        {
            var request = new AdaptationExecutionRequest(
                SourceVersion.Id,
                SelectedRoute.Id,
                SelectedGoal,
                AllowExpandedSemanticScope,
                BuildUserExcludedFiles()) { ProjectId = Project?.Id };

            var result = await _adaptationService
                .StartAdaptationAsync(request, cancellationToken)
                .ConfigureAwait(true);

            ActiveSessionId = result.SessionId;
            ApplyCandidate(result);
            IsSessionActive = true;

            StatusMessage = result.HasBlockers
                ? "Кандидат адаптации сформирован с блокировками; проверьте их перед активацией."
                : "Кандидат адаптации сформирован.";
        }
        catch (OperationCanceledException exception)
        {
            ReportInitialCleanupPending(exception);
            throw;
        }
        catch (Exception exception)
        {
            ErrorMessage = UiErrorMessage.Describe(exception);
            ReportInitialCleanupPending(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReportInitialCleanupPending(Exception exception)
    {
        if (exception.Data["ScratchCleanupPending"] is true)
            StatusMessage = "Очистка временных файлов не завершена. Запись о каталоге сохранена; очистка будет повторена после перезапуска приложения.";
    }

    /// <summary>Submits a follow-up prompt to the same in-memory adaptation session.</summary>
    public async Task SubmitFollowUpAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || _adaptationService is null || ActiveSessionId is null || _sessionSaved)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FollowUpPrompt))
        {
            ErrorMessage = "Введите уточняющий запрос перед отправкой следующего шага.";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            var request = new AdaptationFollowUpRequest(
                ActiveSessionId,
                FollowUpPrompt,
                AllowExpandedSemanticScope);

            var result = await _adaptationService
                .SubmitFollowUpTurnAsync(request, cancellationToken)
                .ConfigureAwait(true);

            ApplyCandidate(result);
            FollowUpPrompt = string.Empty;

            StatusMessage = "Уточняющий шаг применён к текущей сессии адаптации.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = UiErrorMessage.Describe(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Packages the candidate as a new immutable version. The active binding is deliberately untouched.
    /// </summary>
    public async Task SaveCandidateAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || !IsSessionActive || _sessionSaved)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            var saved = await SaveCandidateCoreAsync(cancellationToken).ConfigureAwait(true);

            if (saved is not null)
            {
                StatusMessage = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Кандидат версии {saved.VersionNumber} ({saved.VersionId}) сохранён. Активная привязка осталась неизменной.");
                if (saved.CleanupPending) StatusMessage += " " + CleanupPendingMessage;

                await NotifyLibraryChangedAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves the candidate when still unsaved and activates it for the selected project. Every shown
    /// blocker issue needs its own decision first; the decisions are sent as the exact issues they belong
    /// to, and the activation engine refuses a candidate whose revalidation no longer matches them.
    /// </summary>
    public async Task AcceptAndActivateAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || !IsSessionActive) return;
        if (!IsActivationAvailable || Project is null || Package is null)
        {
            ErrorMessage = "Служба активации не настроена для этого окна.";
            return;
        }

        if (HasUnverifiableSemanticBlockers)
        {
            ErrorMessage = UnverifiableSemanticScopeMessage;
            return;
        }

        if (HasUnconfirmedActivationBlockers)
        {
            ErrorMessage = AcknowledgeBlockersRequiredMessage;
            return;
        }

        if (_candidateVersionId is null && ActiveSessionId is null)
        {
            ErrorMessage = "Нет кандидата для активации.";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            var candidateVersionId = _candidateVersionId;

            if (candidateVersionId is null)
            {
                var saved = await SaveCandidateCoreAsync(cancellationToken).ConfigureAwait(true);

                if (saved is null)
                {
                    return;
                }

                candidateVersionId = saved.VersionId;
            }

            // Do not forget retained scratch ownership when successful activation closes the dialog.
            if (_cleanupPending && ActiveSessionId is not null)
            {
                await _adaptationService!.DiscardSessionAsync(ActiveSessionId, cancellationToken).ConfigureAwait(true);
                _cleanupPending = false;
            }

            var decidedIssues = ConfirmedBlockerIssues;

            var request = new WorkflowActivationRequest(
                Project.Id,
                Package.Id,
                candidateVersionId,
                acknowledgeBlockers: false,
                routePolicyId: null,
                acknowledgedBlockerIssues: decidedIssues);

            var result = await _activationService!
                .ActivateVersionAsync(request, cancellationToken)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                if (result.Validation is not null)
                {
                    ApplyActivationValidation(result.Validation, decidedIssues);
                }

                ErrorMessage = result.ErrorMessage ?? "Ошибка активации.";
                return;
            }

            StatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"Активирован кандидат версии {_candidateVersionNumber} для проекта '{Project.DisplayName}'.");

            CloseDialogCore();
            // The returned activation result confirms the binding commit. Retire candidate authority
            // before the fallible parent reload so an acknowledgement fault cannot activate it twice.
            try
            {
                await NotifyLibraryChangedAsync().ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ErrorMessage = $"Версия активирована, но библиотеку не удалось обновить. {UiErrorMessage.Describe(exception)}";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = UiErrorMessage.Describe(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Discards the session and cleans the candidate scratch workspace.</summary>
    public async Task DiscardAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || !IsSessionActive)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            if ((!_sessionSaved || _cleanupPending) && _adaptationService is not null && ActiveSessionId is not null)
            {
                await _adaptationService
                    .DiscardSessionAsync(ActiveSessionId, cancellationToken)
                    .ConfigureAwait(true);
            }

            StatusMessage = _sessionSaved
                ? "Сессия закрыта; временные файлы удалены. Сохранённая версия осталась в библиотеке."
                : "Сессия адаптации отменена; временный каталог очищен.";
            CloseDialogCore();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = UiErrorMessage.Describe(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Closes an idle dialog after discarding any unsaved candidate workspace.</summary>
    public void CloseDialog() => _ = CancelAsync();

    public async Task CancelAsync()
    {
        if (IsBusy) return;
        if (!IsSessionActive)
        {
            CloseDialogCore();
            return;
        }

        try
        {
            await DiscardAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Keep the identity so cleanup can be retried; never forget an unconfirmed discard.
            ErrorMessage = "Отмена адаптации не завершена; повторите очистку.";
        }
    }

    private void CloseDialogCore()
    {
        ++_dialogGeneration;
        ResetSessionState();
        AvailableRoutes.Clear();
        SetSelectedRouteCore(null);
        SourceVersion = null;
        Package = null;
        Project = null;
        PreSendFiles.Clear();
        IsVisible = false;
        RaiseCommandStates();
    }

    private bool IsCurrentOpen(int generation) => generation == _dialogGeneration && IsVisible;

    private async Task LoadRoutesAsync(int generation, CancellationToken cancellationToken)
    {
        if (!IsCurrentOpen(generation)) return;
        AvailableRoutes.Clear();
        SetSelectedRouteCore(null);

        if (_catalogProvider is null)
        {
            ErrorMessage = "The sanitized catalog is not configured for this window.";
            return;
        }

        try
        {
            var catalog = await _catalogProvider
                .GetSanitizedCatalogAsync(cancellationToken)
                .ConfigureAwait(true);

            if (!IsCurrentOpen(generation)) return;

            foreach (var model in catalog.Models.Where(model => model.IsRoutable))
            {
                // A route is offered only when the row carries a complete route identity and the
                // backend can actually run adaptation (Cursor, Mirasim, StarCliProxy and Agy cannot).
                // Every really configured backend model of the account becomes its own route, so an
                // account with several models offers one unambiguous account+model choice each. The
                // account id is never offered as a model.
                if (!AdaptationRouteIdentity.TryCreate(model, out var identity)
                    || !identity.IsAdaptationCapable)
                {
                    continue;
                }

                foreach (var route in identity.EnumerateModelRoutes(model.SelectableBackendModelIds))
                {
                    var capabilities = model.CapabilitiesFor(route.BackendModelId!);
                    AvailableRoutes.Add(new DiscoveredModelDetails(
                        route.RouteId,
                        FormatRouteName(model, route.BackendModelId!),
                        Description: null,
                        capabilities.ContextLimit,
                        capabilities.Flags,
                        capabilities.ReasoningEfforts));
                }
            }

            SetSelectedRouteCore(AvailableRoutes.FirstOrDefault());

            if (SelectedRoute is null)
            {
                ErrorMessage = "No routable model is available in the sanitized catalog.";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (IsCurrentOpen(generation)) ErrorMessage = UiErrorMessage.Describe(exception);
        }
    }

    /// <summary>
    /// Route label that names the account and the exact backend model, so two models of one account are
    /// never presented as the same choice.
    /// </summary>
    private static string FormatRouteName(SanitizedModelInfo model, string backendModelId) =>
        string.Equals(model.DisplayName, backendModelId, StringComparison.Ordinal)
            ? backendModelId
            : $"{model.DisplayName} — {backendModelId}";

    private async Task<SaveCandidateVersionResult?> SaveCandidateCoreAsync(CancellationToken cancellationToken)
    {
        if (_adaptationService is null || ActiveSessionId is null)
        {
            return null;
        }

        try
        {
            var saved = await _adaptationService
                .SaveCandidateVersionAsync(ActiveSessionId, cancellationToken)
                .ConfigureAwait(true);

            _candidateVersionId = saved.VersionId;
            _candidateVersionNumber = saved.VersionNumber;
            _sessionSaved = true;
            _cleanupPending = saved.CleanupPending;
            if (_cleanupPending) StatusMessage = CleanupPendingMessage;

            if (saved.HasBlockers)
            {
                HasBlockers = true;
                BlockersSummary = string.Join(", ", saved.Blockers);
            }

            return saved;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = UiErrorMessage.Describe(exception);
            return null;
        }
    }

    private void ApplyCandidate(AdaptationCandidateResult result)
    {
        DiffFiles.Clear();

        foreach (var fileDiff in result.PackageDiff.FileDiffs)
        {
            DiffFiles.Add(new WorkflowFileDiffViewModel(fileDiff));
        }

        DiffSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"{result.PackageDiff.TotalFilesAdded} added, {result.PackageDiff.TotalFilesModified} modified, " +
            $"{result.PackageDiff.TotalFilesDeleted} deleted, {result.PackageDiff.TotalFilesUnchanged} unchanged; " +
            $"+{result.PackageDiff.TotalLinesAdded} -{result.PackageDiff.TotalLinesDeleted} lines.");

        SelectedDiffFile = DiffFiles.FirstOrDefault(file => file.Kind != WorkflowFileDiffKind.Unchanged)
            ?? DiffFiles.FirstOrDefault();

        RoleMappings.Clear();

        foreach (var mapping in result.Mappings)
        {
            RoleMappings.Add(new SemanticRoleMappingViewModel(mapping));
        }

        Issues.Clear();

        foreach (var issue in result.BlockingIssues)
        {
            Issues.Add(new AdaptationValidationIssueViewModel(issue));
        }

        OnPropertyChanged(nameof(HasIssues));

        SetActivationBlockers(result.BlockingIssues, retainDecisions: false);

        // A reported issue always keeps the blocker panel visible, so a decision row is never hidden
        // behind a state that claims the candidate is clean.
        HasBlockers = result.HasBlockers || result.BlockingIssues.Count > 0;
        BlockersSummary = HasBlockers
            ? string.Join(", ", result.Blockers.Count > 0
                ? result.Blockers
                : result.BlockingIssues.Select(issue => issue.Kind).Distinct().ToArray())
            : "No blockers";
        AdaptationRationale = result.Rationale;
    }

    private void ApplyActivationValidation(
        WorkflowActivationValidationResult validation,
        IReadOnlyCollection<AdaptationValidationIssue> decidedIssues)
    {
        Issues.Clear();

        foreach (var issue in validation.Issues)
        {
            Issues.Add(new AdaptationValidationIssueViewModel(issue));
        }

        OnPropertyChanged(nameof(HasIssues));

        // A revalidation that reports a different issue set invalidates the previous confirmation, so the
        // old checkmarks are dropped and every freshly reported issue is shown as an open decision.
        SetActivationBlockers(
            validation.Issues,
            validation.IsFullyAcknowledgedBy(decidedIssues));

        HasBlockers = validation.HasBlockers;
        BlockersSummary = validation.HasBlockers
            ? string.Join(", ", validation.Blockers)
            : "No blockers";
    }

    private void SetActivationBlockers(
        IEnumerable<AdaptationValidationIssue> issues,
        bool retainDecisions)
    {
        ActivationBlockers.Clear();

        foreach (var issue in issues)
        {
            ActivationBlockers.Add(new ActivationBlockerItemViewModel(
                issue,
                isAcknowledged: retainDecisions,
                onChanged: RaiseCommandStates));
        }

        OnPropertyChanged(nameof(HasActivationBlockers));
        OnPropertyChanged(nameof(HasUnverifiableSemanticBlockers));
        RaiseCommandStates();
    }

    private void BuildPreSendFiles(AdaptationPreSendPreview preview, IReadOnlyList<string> userExcludedPaths)
    {
        var excluded = new HashSet<string>(
            preview.ExcludedFiles.Select(NormalizeRelativePath),
            StringComparer.Ordinal);
        excluded.UnionWith(userExcludedPaths.Select(NormalizeRelativePath));

        var findingsByPath = preview.ScanReport.Findings
            .GroupBy(finding => NormalizeRelativePath(finding.RelativePath), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<WorkflowSecretFinding>)group.ToArray(),
                StringComparer.Ordinal);

        var recommended = new HashSet<string>(
            preview.ScanReport.RecommendedExcludedFiles.Select(NormalizeRelativePath),
            StringComparer.Ordinal);

        var paths = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in preview.IncludedFiles)
        {
            paths.Add(NormalizeRelativePath(path));
        }

        foreach (var path in excluded)
        {
            paths.Add(path);
        }

        foreach (var path in findingsByPath.Keys)
        {
            paths.Add(path);
        }

        PreSendFiles.Clear();

        foreach (var path in paths)
        {
            findingsByPath.TryGetValue(path, out var findings);

            var isExcluded = excluded.Contains(path) || findingsByPath.ContainsKey(path);

            PreSendFiles.Add(new AdaptationFileItemViewModel(
                path,
                recommended.Contains(path),
                findings,
                isExcluded,
                canChangeExclusion: () => CanEditPreSendInputs));
        }

        OnPropertyChanged(nameof(HasPreSendFiles));
    }

    private IReadOnlyList<string> BuildExcludedPaths()
    {
        return PreSendFiles
            .Where(file => file.IsExcluded)
            .Select(file => file.RelativePath)
            .ToArray();
    }

    private PreviewSelection CapturePreviewSelection() => new(SourceVersion?.Id, Package?.Id, Project?.Id,
        SelectedRoute?.Id, SelectedGoal, AllowExpandedSemanticScope,
        JsonSerializer.Serialize(BuildExcludedPaths().Select(NormalizeRelativePath).Order(StringComparer.Ordinal).ToArray()));

    private void RetirePreSendPreview()
    {
        if (_applyingPreview) return;
        ++_previewToken;
        _acceptedPreview = null;
        PromptPreview = string.Empty;
        CostQuotaEstimateDisplay = AdaptationPreSendPreview.UnknownQuotaValue;
        RaiseCommandStates();
    }

    private void OnPreviewFilesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var file in _subscribedPreviewFiles.Where(file => !PreSendFiles.Contains(file)).ToArray())
        {
            file.PropertyChanged -= OnPreviewFileChanged;
            _subscribedPreviewFiles.Remove(file);
        }
        foreach (var file in PreSendFiles)
            if (_subscribedPreviewFiles.Add(file)) file.PropertyChanged += OnPreviewFileChanged;
        RetirePreSendPreview();
    }

    private void OnPreviewFileChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AdaptationFileItemViewModel.IsExcluded)) RetirePreSendPreview();
    }

    private IReadOnlyList<string> BuildUserExcludedFiles()
    {
        return PreSendFiles
            .Where(file => file.IsExcluded)
            .Select(file => file.RelativePath)
            .ToArray();
    }

    private void ResetSessionState()
    {
        RetirePreSendPreview();
        ActiveSessionId = null;
        _candidateVersionId = null;
        _candidateVersionNumber = null;
        _sessionSaved = false;
        _cleanupPending = false;

        DiffFiles.Clear();
        SelectedDiffFile = null;
        SelectedDiffContent = string.Empty;
        DiffSummary = string.Empty;
        AdaptationRationale = string.Empty;
        RoleMappings.Clear();
        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
        HasBlockers = false;
        BlockersSummary = string.Empty;
        ActivationBlockers.Clear();
        OnPropertyChanged(nameof(HasActivationBlockers));
        OnPropertyChanged(nameof(HasUnverifiableSemanticBlockers));
        FollowUpPrompt = string.Empty;
        IsSessionActive = false;
    }

    private async Task NotifyLibraryChangedAsync()
    {
        if (OnLibraryChangedAsync is not null)
        {
            await OnLibraryChangedAsync().ConfigureAwait(true);
        }
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanEditPreSendInputs));
        foreach (var file in PreSendFiles) file.NotifyEditingStateChanged();
        OnPropertyChanged(nameof(CanEditMaterialClassification));
        OnPropertyChanged(nameof(CanStartAdaptation));
        OnPropertyChanged(nameof(CanSubmitFollowUp));
        OnPropertyChanged(nameof(CanAcceptAndActivate));
        OnPropertyChanged(nameof(CanSaveCandidate));
        OnPropertyChanged(nameof(CanDiscard));
        OnPropertyChanged(nameof(HasUnconfirmedActivationBlockers));
        OnPropertyChanged(nameof(HasUnverifiableSemanticBlockers));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private static string BuildQuotaDisplay(AdaptationPreSendPreview preview)
    {
        if (string.Equals(
            preview.QuotaState,
            AdaptationPreSendPreview.UnknownQuotaValue,
            StringComparison.Ordinal))
        {
            return AdaptationPreSendPreview.UnknownQuotaValue;
        }

        var reserve = preview.ReserveThreshold.HasValue
            ? string.Concat(
                ", reserve ",
                preview.ReserveThreshold.Value.ToString("P0", CultureInfo.InvariantCulture))
            : string.Empty;

        return string.Concat(preview.QuotaState, " (", preview.QuotaFreshness, ")", reserve);
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }
}
