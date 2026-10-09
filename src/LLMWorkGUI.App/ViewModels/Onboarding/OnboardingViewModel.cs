using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.StarCliProxy;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels.Onboarding;

/// <summary>
/// First-run onboarding wizard (ROADMAP Phase 11): workspace confirmation, local CLI detection, preview
/// of the built-in workflow/document template catalog with the role matrix, and the ready/safe-simulation
/// step. The flow is fully local: it never asks for API keys, never sends a network request and never
/// performs a paid model call. Completion is persisted through <see cref="IApplicationSettingsRepository"/>
/// (when wired) and the wizard can be re-opened or dismissed at any moment without blocking the shell.
/// </summary>
public sealed class OnboardingViewModel : ObservableObject
{
    public const string CompletionSettingKey = "onboarding.completed";
    public const string WorkspaceSettingKey = OpenedProjectResolver.WorkspaceSettingKey;
    public const string NotReportedPlaceholder = "Not reported";
    public const string NoPaidCallNotice =
        "Онбординг не требует API-ключей, учётных записей и платных вызовов моделей: все шаги выполняются локально.";
    public const string SafeSimulationNotice =
        "Безопасная симуляция использует только локальную synthetic-фикстуру. Записи помечены SYNTHETIC и не считаются наблюдаемыми событиями бэкенда.";
    public const string SkippedWorkspaceNotice =
        "Рабочий каталог не подтверждён; шаг можно пропустить и вернуться к нему позже.";

    private readonly CliStatusViewModel? _cliStatus;
    private readonly WorkflowLibraryViewModel? _workflowLibrary;
    private readonly IApplicationSettingsRepository? _settings;
    private readonly IProjectRepository? _projectRepository;
    private readonly IStarCliProxyExecutableResolver? _starCliProxyResolver;
    private readonly MirasimOptions? _mirasimOptions;
    private readonly TimeProvider _timeProvider;

    private int _currentStepIndex;
    private string _workspacePath = string.Empty;
    private string _workspaceStatus = SkippedWorkspaceNotice;
    private bool _isWorkspaceConfirmed;
    private bool _isSafeSimulationActive;
    private bool _isCompleted;
    private bool _hasLoadedState;
    private string _statusMessage = string.Empty;
    private string _catalogSummary = string.Empty;
    private string _roleMatrixSummary = string.Empty;
    private int _projectOpenCount;

    public OnboardingViewModel(
        CliStatusViewModel? cliStatus = null,
        WorkflowLibraryViewModel? workflowLibrary = null,
        IApplicationSettingsRepository? settings = null,
        IProjectRepository? projectRepository = null,
        TimeProvider? timeProvider = null,
        IStarCliProxyExecutableResolver? starCliProxyResolver = null,
        MirasimOptions? mirasimOptions = null)
    {
        _cliStatus = cliStatus;
        _workflowLibrary = workflowLibrary;
        _settings = settings;
        _projectRepository = projectRepository;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _starCliProxyResolver = starCliProxyResolver;
        _mirasimOptions = mirasimOptions;

        if (_cliStatus is not null)
        {
            _cliStatus.PropertyChanged += OnCliStatusPropertyChanged;
        }

        NextCommand = new RelayCommand(GoNext);
        BackCommand = new RelayCommand(GoBack, () => !IsFirstStep);
        SelectStepCommand = new RelayCommand(parameter => GoToStep(ResolveStepIndex(parameter)));
        DismissCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        CompleteCommand = new RelayCommand(() => _ = CompleteAsync(), () => !IsBusy);
        OpenWorkspaceCommand = new RelayCommand(() => _ = OpenWorkspaceAsync(), () => !IsBusy);
        ConfirmWorkspaceCommand = new RelayCommand(
            () => _ = ConfirmWorkspaceAsync(WorkspacePath),
            () => !IsBusy);
        OpenProjectCommand = new RelayCommand(
            () => _ = OpenProjectAsync(),
            () => !IsBusy && IsWorkspaceConfirmed && _projectRepository is not null);
        RefreshCliCommand = new RelayCommand(
            () => _ = RefreshCliAsync(),
            () => !IsBusy && _cliStatus is not null && !_cliStatus.IsDetectionPending);
        RefreshCatalogCommand = new RelayCommand(
            () => _ = RefreshCatalogAsync(),
            () => !IsBusy);
        RefreshBackendsCommand = new RelayCommand(RefreshBackends, () => !IsBusy);
        StartSafeSimulationCommand = new RelayCommand(
            StartSafeSimulation,
            () => !IsSafeSimulationActive);
        StopSafeSimulationCommand = new RelayCommand(
            StopSafeSimulation,
            () => IsSafeSimulationActive);
    }

    /// <summary>Raised when the user dismisses the wizard without completing it.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the user completes the wizard and asks for the workspace.</summary>
    public event EventHandler? WorkspaceRequested;

    public string Title => "Первый запуск";

    public string Description =>
        "Мастер запуска: рабочая область, локальные CLI, каталог шаблонов сценариев и матрица ролей, "
        + "готовность или безопасная симуляция.";

    public string NoPaidCallText => NoPaidCallNotice;

    public string SafeSimulationText => SafeSimulationNotice;

    public IReadOnlyList<OnboardingStep> Steps => OnboardingStep.All;

    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        private set
        {
            var guarded = Math.Clamp(value, 0, Steps.Count - 1);

            if (!SetProperty(ref _currentStepIndex, guarded))
            {
                return;
            }

            RaiseStepProperties();
        }
    }

    public OnboardingStep CurrentStep => Steps[CurrentStepIndex];

    public string StepProgressDisplay =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Шаг {CurrentStep.Ordinal} из {Steps.Count}");

    public string CurrentStepTitle => CurrentStep.Title;

    public string CurrentStepDescription => CurrentStep.Description;

    public string CurrentStepActionHint => CurrentStep.ActionHint;

    public string NextButtonText => IsLastStep ? "Завершить" : "Далее";

    public bool IsFirstStep => CurrentStepIndex == 0;

    public bool IsLastStep => CurrentStepIndex == Steps.Count - 1;

    public bool IsWelcomeStep => CurrentStep.Kind == OnboardingStepKind.WelcomeAndWorkspace;

    public bool IsCliStep => CurrentStep.Kind == OnboardingStepKind.LocalCliDetection;

    public bool IsCatalogStep => CurrentStep.Kind == OnboardingStepKind.WorkflowCatalogDiscovery;

    public bool IsReadyStep => CurrentStep.Kind == OnboardingStepKind.ReadySafeMode;

    public ObservableCollection<OnboardingStepViewModel> StepList { get; } = new();

    public ObservableCollection<OnboardingCatalogItemViewModel> CatalogTemplates { get; } = new();

    public ObservableCollection<OnboardingRoleMatrixItemViewModel> RoleMatrix { get; } = new();

    public ObservableCollection<string> DocumentTemplateNames { get; } = new();

    public ObservableCollection<OnboardingSimulationStepViewModel> SimulationSteps { get; } = new();

    /// <summary>Local backend inventory: CLI tools plus the star-cliproxy gateway and the Mirasim host.</summary>
    public ObservableCollection<OnboardingBackendStatusViewModel> Backends { get; } = new();

    public bool HasBackends => Backends.Count > 0;

    public bool HasCatalogTemplates => CatalogTemplates.Count > 0;

    public bool HasRoleMatrix => RoleMatrix.Count > 0;

    public bool HasDocumentTemplates => DocumentTemplateNames.Count > 0;

    public bool HasSimulationSteps => SimulationSteps.Count > 0;

    public string WorkspacePath
    {
        get => _workspacePath;
        set
        {
            if (SetProperty(ref _workspacePath, value ?? string.Empty))
            {
                IsWorkspaceConfirmed = false;
                WorkspaceStatus = "Рабочий каталог изменён. Подтвердите текущий путь.";
            }
        }
    }

    public string WorkspaceStatus
    {
        get => _workspaceStatus;
        private set => SetProperty(ref _workspaceStatus, value);
    }

    public bool IsWorkspaceConfirmed
    {
        get => _isWorkspaceConfirmed;
        private set
        {
            if (SetProperty(ref _isWorkspaceConfirmed, value))
            {
                OnPropertyChanged(nameof(WorkspaceConfirmationDisplay));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string WorkspaceConfirmationDisplay => IsWorkspaceConfirmed
        ? "Рабочий каталог подтверждён локально."
        : "Рабочий каталог ещё не подтверждён.";

    public string CliDetectionSummary =>
        _cliStatus?.DetectionSummary ?? "Локальное обнаружение CLI не сконфигурировано в этой композиции.";

    public string CliHeadline => _cliStatus?.Headline ?? CliStatusViewModel.NotCheckedHeadline;

    public string CliRecommendation => _cliStatus?.Recommendation ?? CliStatusViewModel.NotCheckedRecommendation;

    public bool IsCliDegraded => _cliStatus?.IsDegraded ?? false;

    public bool IsCliDetectionPending => _cliStatus?.IsDetectionPending ?? false;

    public ObservableCollection<CliToolStatusViewModel>? CliTools => _cliStatus?.Tools;

    public string CatalogSummary
    {
        get => _catalogSummary;
        private set => SetProperty(ref _catalogSummary, value);
    }

    public string RoleMatrixSummary
    {
        get => _roleMatrixSummary;
        private set => SetProperty(ref _roleMatrixSummary, value);
    }

    public string CatalogUnavailableNotice =>
        "Каталог сценариев недоступен в этой композиции; встроенные шаблоны документов всё равно показаны.";

    public bool IsSafeSimulationActive
    {
        get => _isSafeSimulationActive;
        private set
        {
            if (SetProperty(ref _isSafeSimulationActive, value))
            {
                OnPropertyChanged(nameof(SafeSimulationStatusDisplay));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SafeSimulationStatusDisplay => IsSafeSimulationActive
        ? "Безопасная симуляция активна (synthetic, без вызовов моделей)."
        : "Безопасная симуляция не запущена.";

    public bool IsCompleted
    {
        get => _isCompleted;
        private set
        {
            if (SetProperty(ref _isCompleted, value))
            {
                OnPropertyChanged(nameof(CompletionDisplay));
            }
        }
    }

    public string CompletionDisplay => IsCompleted
        ? "Онбординг завершён; его можно открыть снова из справки."
        : "Онбординг ещё не завершён.";

    public bool HasLoadedState => _hasLoadedState;

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

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public int ProjectOpenCount => _projectOpenCount;

    public ICommand NextCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand SelectStepCommand { get; }

    public ICommand DismissCommand { get; }

    public ICommand CompleteCommand { get; }

    public ICommand OpenWorkspaceCommand { get; }

    public ICommand ConfirmWorkspaceCommand { get; }

    public ICommand OpenProjectCommand { get; }

    public ICommand RefreshCliCommand { get; }

    public ICommand RefreshCatalogCommand { get; }

    /// <summary>Re-reads the local backend inventory (CLI tools, gateway, Mirasim host).</summary>
    public ICommand RefreshBackendsCommand { get; }

    public ICommand StartSafeSimulationCommand { get; }

    public ICommand StopSafeSimulationCommand { get; }

    /// <summary>Shows the given step regardless of the current position.</summary>
    public void GoToStep(int index) => CurrentStepIndex = index;

    public void GoToStep(OnboardingStepKind kind) =>
        GoToStep(Steps.ToList().FindIndex(step => step.Kind == kind));

    public void GoNext()
    {
        if (IsLastStep)
        {
            _ = CompleteAsync();
            return;
        }

        CurrentStepIndex++;
    }

    public void GoBack()
    {
        if (!IsFirstStep)
        {
            CurrentStepIndex--;
        }
    }

    /// <summary>
    /// Loads the persisted completion/workspace state, refreshes the CLI snapshot once and builds the
    /// template catalog. Safe to call more than once; every call is fully local.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        BuildStepList();

        if (!_hasLoadedState)
        {
            await LoadPersistedStateAsync(cancellationToken).ConfigureAwait(true);
        }

        if (_cliStatus is not null && !_cliStatus.IsChecked && !_cliStatus.IsDetectionPending)
        {
            await _cliStatus.RefreshAsync(cancellationToken).ConfigureAwait(true);
        }

        RefreshBackends();
        await RefreshCatalogAsync().ConfigureAwait(true);
        _hasLoadedState = true;
    }

    /// <summary>Marks the wizard completed, persists the flag and asks the shell to close the overlay.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        RelayCommand.RaiseCanExecuteChanged();

        try
        {
            if (!await PersistAsync(CompletionSettingKey, bool.TrueString, cancellationToken).ConfigureAwait(true)) return;
            IsCompleted = true;
            StatusMessage = CompletionDisplay;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Completes the wizard and asks the shell to open the workspace screen. No model or network call is
    /// involved.
    /// </summary>
    public async Task OpenWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        RelayCommand.RaiseCanExecuteChanged();

        try
        {
            if (!await PersistAsync(CompletionSettingKey, bool.TrueString, cancellationToken).ConfigureAwait(true)) return;
            IsCompleted = true;
            StatusMessage = "Рабочая область открыта; онбординг завершён.";
            WorkspaceRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Validates and remembers a local project directory. A non-existing or relative path is reported
    /// honestly and never creates or deletes anything.
    /// </summary>
    public async Task ConfirmWorkspaceAsync(
        string? path,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        var candidate = path?.Trim() ?? string.Empty;

        if (candidate.Length == 0)
        {
            IsWorkspaceConfirmed = false;
            WorkspaceStatus = SkippedWorkspaceNotice;
            return;
        }

        if (!Path.IsPathRooted(candidate) || candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            IsWorkspaceConfirmed = false;
            WorkspaceStatus = "Путь должен быть абсолютным локальным каталогом.";
            return;
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            IsWorkspaceConfirmed = false;
            WorkspaceStatus = $"Каталог не подтверждён: {UiErrorMessage.Describe(exception)}";
            return;
        }

        if (!Directory.Exists(fullPath))
        {
            IsWorkspaceConfirmed = false;
            WorkspaceStatus = "Каталог не найден; проверьте путь. Ничего не создаётся автоматически.";
            return;
        }

        WorkspacePath = fullPath;
        IsWorkspaceConfirmed = false;
        WorkspaceStatus = "Сохранение рабочего каталога…";
        IsBusy = true;
        RelayCommand.RaiseCanExecuteChanged();
        try
        {
            if (!await PersistAsync(WorkspaceSettingKey, fullPath, cancellationToken).ConfigureAwait(true))
            {
                WorkspaceStatus = "Рабочий каталог не сохранён. Повторите попытку.";
                return;
            }
            if (!string.Equals(WorkspacePath, fullPath, StringComparison.Ordinal))
            {
                WorkspaceStatus = "Предыдущий путь сохранён; подтвердите текущий рабочий каталог.";
                return;
            }
            IsWorkspaceConfirmed = true;
            WorkspaceStatus = "Каталог подтверждён локально; содержимое не изменялось.";
        }
        catch (OperationCanceledException)
        {
            WorkspaceStatus = "Сохранение рабочего каталога отменено.";
            throw;
        }
        finally
        {
            IsBusy = false;
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Opens the confirmed workspace as a project when a project repository is wired in. Repeated calls
    /// reuse the existing project row instead of creating duplicates.
    /// </summary>
    public async Task OpenProjectAsync(CancellationToken cancellationToken = default)
    {
        if (_projectRepository is null || !IsWorkspaceConfirmed)
        {
            return;
        }

        IsBusy = true;
        RelayCommand.RaiseCanExecuteChanged();

        try
        {
            var existing = await _projectRepository
                .GetByRootPathAsync(WorkspacePath, cancellationToken)
                .ConfigureAwait(true);

            if (existing is null)
            {
                var project = new Project(
                    BuildProjectId(WorkspacePath),
                    BuildProjectDisplayName(WorkspacePath),
                    WorkspacePath,
                    gitBranch: null,
                    isDirty: false,
                    hasRequiredInstructions: false,
                    defaultWorkflowId: null,
                    defaultRoutePolicyId: null,
                    DataClassification.PrivateSource);

                await _projectRepository.UpsertAsync(project, cancellationToken).ConfigureAwait(true);
                StatusMessage = $"Проект '{project.DisplayName}' открыт локально.";
                _projectOpenCount++;
                OnPropertyChanged(nameof(ProjectOpenCount));
            }
            else
            {
                StatusMessage = $"Проект '{existing.DisplayName}' уже открыт; повторная запись не потребовалась.";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusMessage = $"Каталог не удалось открыть как проект: {UiErrorMessage.Describe(exception)}";
        }
        finally
        {
            IsBusy = false;
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    public async Task RefreshCliAsync(CancellationToken cancellationToken = default)
    {
        if (_cliStatus is null)
        {
            return;
        }

        await _cliStatus.RefreshAsync(cancellationToken).ConfigureAwait(true);
        RefreshBackends();
        StatusMessage = "Локальное обнаружение CLI обновлено.";
    }

    /// <summary>
    /// Rebuilds the local backend inventory. OpenCode/Cursor Agent come from the CLI detector; the
    /// star-cliproxy gateway is probed through its executable resolver; Mirasim is a user-owned local host
    /// and is reported as configured/Not reported without ever contacting or restarting it.
    /// </summary>
    public void RefreshBackends()
    {
        Backends.Clear();

        if (_cliStatus is not null)
        {
            foreach (var tool in _cliStatus.Tools)
            {
                Backends.Add(new OnboardingBackendStatusViewModel(
                    tool.DisplayName,
                    tool.IsDetected ? "Обнаружен" : "Не обнаружен",
                    tool.IsDetected ? tool.PathDisplay : tool.DetectionError ?? "Not reported",
                    isDetected: tool.IsDetected));
            }
        }

        if (_starCliProxyResolver is not null)
        {
            var resolution = _starCliProxyResolver.Resolve();

            Backends.Add(new OnboardingBackendStatusViewModel(
                "star-cliproxy (управляемый шлюз)",
                resolution.IsAvailable ? "Обнаружен" : "Не обнаружен",
                resolution.ExecutablePath ?? resolution.Blocker ?? NotReportedPlaceholder,
                resolution.IsAvailable));
        }
        else
        {
            Backends.Add(new OnboardingBackendStatusViewModel(
                "star-cliproxy (управляемый шлюз)",
                NotReportedPlaceholder,
                "Резолвер исполняемого файла не сконфигурирован в этой композиции.",
                isDetected: false));
        }

        if (_mirasimOptions is not null)
        {
            Backends.Add(new OnboardingBackendStatusViewModel(
                "Mirasim (пользовательский хост)",
                "Настроен",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"http://{_mirasimOptions.Hostname}:{_mirasimOptions.Port} · LLMWorkGUI не перезапускает хост."),
                isDetected: true));
        }
        else
        {
            Backends.Add(new OnboardingBackendStatusViewModel(
                "Mirasim (пользовательский хост)",
                NotReportedPlaceholder,
                "Mirasim не сконфигурирован в этой композиции.",
                isDetected: false));
        }

        OnPropertyChanged(nameof(HasBackends));
    }

    /// <summary>Rebuilds the built-in template catalog and the role matrix. No network access.</summary>
    public async Task RefreshCatalogAsync()
    {
        CatalogTemplates.Clear();
        RoleMatrix.Clear();
        DocumentTemplateNames.Clear();

        var studio = _workflowLibrary?.Studio;

        if (studio is not null)
        {
            await studio.RefreshTemplatesAsync().ConfigureAwait(true);

            foreach (var template in studio.Templates)
            {
                CatalogTemplates.Add(new OnboardingCatalogItemViewModel(template));
            }

            foreach (var documentTemplate in studio.DocumentTemplates)
            {
                DocumentTemplateNames.Add(documentTemplate.DisplayName);
            }

            var selected = studio.SelectedTemplate ?? studio.Templates.FirstOrDefault();

            if (selected is not null)
            {
                BuildRoleMatrix(selected);
            }
        }

        if (RoleMatrix.Count == 0)
        {
            BuildStandardRoleMatrix();
        }

        CatalogSummary = CatalogTemplates.Count == 0
            ? CatalogUnavailableNotice
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Доступно шаблонов: {CatalogTemplates.Count}; шаблонов документов: {DocumentTemplateNames.Count}.");
        RoleMatrixSummary = RoleMatrix.Count == 0
            ? "Матрица ролей не объявлена."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Ролей в матрице: {RoleMatrix.Count}.");

        OnPropertyChanged(nameof(HasCatalogTemplates));
        OnPropertyChanged(nameof(HasRoleMatrix));
        OnPropertyChanged(nameof(HasDocumentTemplates));
    }

    /// <summary>Starts the local synthetic simulation. No backend or model is contacted.</summary>
    public void StartSafeSimulation()
    {
        var fixture = new SyntheticRunSequenceFixture(_timeProvider.GetUtcNow());

        SimulationSteps.Clear();

        foreach (var step in fixture.Canonical.Steps)
        {
            SimulationSteps.Add(new OnboardingSimulationStepViewModel(step));
        }

        IsSafeSimulationActive = true;
        StatusMessage = SafeSimulationStatusDisplay;
        OnPropertyChanged(nameof(HasSimulationSteps));
    }

    public void StopSafeSimulation()
    {
        IsSafeSimulationActive = false;
        SimulationSteps.Clear();
        OnPropertyChanged(nameof(HasSimulationSteps));
        StatusMessage = "Безопасная симуляция остановлена.";
    }

    private async Task LoadPersistedStateAsync(CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            var completed = await _settings
                .GetValueAsync(CompletionSettingKey, cancellationToken)
                .ConfigureAwait(true);

            IsCompleted = bool.TryParse(completed, out var parsed) && parsed;

            var workspace = await _settings
                .GetValueAsync(WorkspaceSettingKey, cancellationToken)
                .ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(workspace))
            {
                WorkspacePath = workspace;
                await ConfirmWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(true);

                if (IsWorkspaceConfirmed)
                {
                    WorkspaceStatus = "Сохранённый рабочий каталог подтверждён локально.";
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusMessage = $"Сохранённое состояние онбординга недоступно: {UiErrorMessage.Describe(exception)}";
        }
    }

    private async Task<bool> PersistAsync(string key, string value, CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            StatusMessage = "Хранилище настроек недоступно; состояние онбординга не сохранено.";
            return false;
        }

        try
        {
            await _settings.SetValueAsync(key, value, "String", cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusMessage = "Состояние онбординга не сохранено. Повторите попытку.";
            return false;
        }
    }

    private void BuildStepList()
    {
        if (StepList.Count == Steps.Count)
        {
            return;
        }

        StepList.Clear();

        foreach (var step in Steps)
        {
            StepList.Add(new OnboardingStepViewModel(step));
        }

        RaiseStepProperties();
    }

    private void BuildRoleMatrix(WorkflowTemplateItemViewModel template)
    {
        var roles = new List<OnboardingRoleMatrixItemViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in template.Definition.RoleBindings)
        {
            if (seen.Add(binding.RoleId))
            {
                roles.Add(new OnboardingRoleMatrixItemViewModel(
                    binding.RoleId,
                    binding.PrimaryRouteId ?? NotReportedPlaceholder,
                    ResolveStageDisplay(template, binding.RoleId),
                    $"{template.DisplayName} · v{template.Version}"));
            }
        }

        foreach (var node in template.Definition.Graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.RoleBinding) || !seen.Add(node.RoleBinding))
            {
                continue;
            }

            roles.Add(new OnboardingRoleMatrixItemViewModel(
                node.RoleBinding,
                string.IsNullOrWhiteSpace(node.PrimaryRouteId) ? NotReportedPlaceholder : node.PrimaryRouteId,
                node.DisplayName,
                $"{template.DisplayName} · v{template.Version}"));
        }

        foreach (var role in roles)
        {
            RoleMatrix.Add(role);
        }
    }

    private void BuildStandardRoleMatrix()
    {
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();

        foreach (var stage in scheme.Stages)
        {
            RoleMatrix.Add(new OnboardingRoleMatrixItemViewModel(
                stage.RequiredRole,
                NotReportedPlaceholder,
                stage.DisplayName,
                "Стандартная схема разработки"));
        }
    }

    private static string ResolveStageDisplay(WorkflowTemplateItemViewModel template, string roleId)
    {
        var nodes = template.Definition.Graph.Nodes
            .Where(node => string.Equals(node.RoleBinding, roleId, StringComparison.OrdinalIgnoreCase))
            .Select(node => node.DisplayName)
            .ToArray();

        return nodes.Length == 0 ? NotReportedPlaceholder : string.Join(", ", nodes);
    }

    private static string BuildProjectId(string rootPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rootPath));
        return "project-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string BuildProjectDisplayName(string rootPath)
    {
        var name = new DirectoryInfo(rootPath).Name;
        return string.IsNullOrWhiteSpace(name) ? rootPath : name;
    }

    private static int ResolveStepIndex(object? parameter)
    {
        return parameter switch
        {
            int index => index,
            OnboardingStep step => OnboardingStep.All.ToList().FindIndex(candidate => candidate.Kind == step.Kind),
            OnboardingStepKind kind => OnboardingStep.All.ToList().FindIndex(candidate => candidate.Kind == kind),
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) =>
                parsed,
            _ => throw new ArgumentException(
                "Step selection requires an index, an OnboardingStep or an OnboardingStepKind.",
                nameof(parameter))
        };
    }

    private void RaiseStepProperties()
    {
        for (var index = 0; index < StepList.Count; index++)
        {
            StepList[index].IsCurrent = index == CurrentStepIndex;
        }

        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(StepProgressDisplay));
        OnPropertyChanged(nameof(CurrentStepTitle));
        OnPropertyChanged(nameof(CurrentStepDescription));
        OnPropertyChanged(nameof(CurrentStepActionHint));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(IsWelcomeStep));
        OnPropertyChanged(nameof(IsCliStep));
        OnPropertyChanged(nameof(IsCatalogStep));
        OnPropertyChanged(nameof(IsReadyStep));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void OnCliStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CliStatusViewModel.DetectionSummary):
                OnPropertyChanged(nameof(CliDetectionSummary));
                break;
            case nameof(CliStatusViewModel.Headline):
                OnPropertyChanged(nameof(CliHeadline));
                break;
            case nameof(CliStatusViewModel.Recommendation):
                OnPropertyChanged(nameof(CliRecommendation));
                break;
            case nameof(CliStatusViewModel.IsDegraded):
                OnPropertyChanged(nameof(IsCliDegraded));
                break;
            case nameof(CliStatusViewModel.IsDetectionPending):
                OnPropertyChanged(nameof(IsCliDetectionPending));
                RelayCommand.RaiseCanExecuteChanged();
                break;
            default:
                break;
        }
    }
}

/// <summary>One row of the compact step rail.</summary>
public sealed class OnboardingStepViewModel : ObservableObject
{
    private bool _isCurrent;

    public OnboardingStepViewModel(OnboardingStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Step = step;
    }

    public OnboardingStep Step { get; }

    public OnboardingStepKind Kind => Step.Kind;

    public string Title => Step.Title;

    public string OrdinalDisplay => Step.OrdinalDisplay;

    public string SummaryDisplay => $"{Step.Ordinal}. {Step.Title}";

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }
}

/// <summary>One built-in workflow template of the onboarding catalog preview.</summary>
public sealed class OnboardingCatalogItemViewModel
{
    public OnboardingCatalogItemViewModel(WorkflowTemplateItemViewModel template)
    {
        ArgumentNullException.ThrowIfNull(template);

        DisplayName = template.DisplayName;
        TemplateId = template.TemplateId;
        Version = template.Version;
        IsBuiltIn = template.IsBuiltIn;
        NodeCount = template.NodeCount;

        RolesDisplay = string.Join(
            ", ",
            template.Definition.RoleBindings
                .Select(binding => binding.RoleId)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public string DisplayName { get; }

    public string TemplateId { get; }

    public int Version { get; }

    public bool IsBuiltIn { get; }

    public int NodeCount { get; }

    public string RolesDisplay { get; }

    public string SummaryDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"{DisplayName} · v{Version}{(IsBuiltIn ? " · встроенный" : " · пользовательский")} · {NodeCount} {PluralizeNodes(NodeCount)}");

    private static string PluralizeNodes(int count)
    {
        var mod10 = count % 10;
        var mod100 = count % 100;

        if (mod100 >= 11 && mod100 <= 14)
        {
            return "узлов";
        }

        return mod10 switch
        {
            1 => "узел",
            >= 2 and <= 4 => "узла",
            _ => "узлов"
        };
    }
}

/// <summary>One role binding of the onboarding role-matrix preview.</summary>
public sealed class OnboardingRoleMatrixItemViewModel
{
    public OnboardingRoleMatrixItemViewModel(
        string roleDisplay,
        string routeDisplay,
        string stageDisplay,
        string sourceDisplay)
    {
        RoleDisplay = roleDisplay;
        RouteDisplay = routeDisplay;
        StageDisplay = stageDisplay;
        SourceDisplay = sourceDisplay;
    }

    public string RoleDisplay { get; }

    public string RouteDisplay { get; }

    public string StageDisplay { get; }

    public string SourceDisplay { get; }

    public string SummaryDisplay => $"{RoleDisplay} → {RouteDisplay} · {StageDisplay}";
}

/// <summary>One row of the local backend inventory shown by the onboarding CLI detection step.</summary>
public sealed class OnboardingBackendStatusViewModel
{
    public OnboardingBackendStatusViewModel(
        string backendDisplay,
        string statusDisplay,
        string detailDisplay,
        bool isDetected)
    {
        BackendDisplay = backendDisplay;
        StatusDisplay = statusDisplay;
        DetailDisplay = detailDisplay;
        IsDetected = isDetected;
    }

    public string BackendDisplay { get; }

    public string StatusDisplay { get; }

    public string DetailDisplay { get; }

    public bool IsDetected { get; }

    public string SummaryDisplay => $"{BackendDisplay}: {StatusDisplay}";
}

/// <summary>One step of the local synthetic simulation, explicitly marked SYNTHETIC.</summary>
public sealed class OnboardingSimulationStepViewModel
{
    public const string SyntheticBadge = "SYNTHETIC";

    public OnboardingSimulationStepViewModel(SyntheticRunStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        SequenceDisplay = step.Projection.ExecutionId;
        StageDisplay = step.StageLabel;
        RoleDisplay = step.Role.ToString();
        StateDisplay = step.Projection.State.ToString();
    }

    public string SequenceDisplay { get; }

    public string StageDisplay { get; }

    public string RoleDisplay { get; }

    public string StateDisplay { get; }

    public string SyntheticLabel => SyntheticBadge;

    public string SummaryDisplay =>
        $"{SequenceDisplay} · {StageDisplay} · {RoleDisplay} · {StateDisplay} · {SyntheticBadge}";
}
