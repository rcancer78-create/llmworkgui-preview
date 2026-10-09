using System.Globalization;
using System.IO;
using System.Windows.Input;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Mirasim panel of the Workspace screen. It drives the user-owned Mirasim loopback host through
/// <see cref="IMirasimClient"/> and <see cref="IMirasimSessionLifecycleService"/>, shows only observed
/// evidence (unknown fields stay <c>Not reported</c>), keeps the route strictly <c>ManualOnly</c>, and
/// reports observed backend health under the <c>mirasim</c> backend scope (ADR-0008 §3, §7, §8, §9, §10,
/// ТЗ §6.5, §6.10).
/// </summary>
public sealed class MirasimWorkspaceViewModel : ObservableObject
{
    public const string NotReported = "Not reported";

    public const string BackendScopeId = "mirasim";

    public const string WriteExecutionMode = "Write";

    public const string DefaultHarness = "codex";

    public const string DefaultModelId = "gpt-4o";

    public const string NotProbedState = "Not probed";

    public const string AvailableState = "Ok";

    public const string UnavailableState = "Unavailable";

    public const string NoSessionState = "None";

    public const string AmbiguousLockGuidance =
        "Результат шага Mirasim неоднозначен; блокировка записи в рабочий каталог удерживается, и запрос " +
        "никогда не повторяется автоматически. Согласуйте шаг перед отправкой нового запроса (ADR-0008 §7).";

    public const string RecordingNotice =
        "Mirasim является пользовательским хостом и может вести запись необработанных запросов, ответов и журналов сессий по умолчанию. " +
        "LLMWorkGUI никогда не копирует исходные полезные нагрузки в собственные журналы и не меняет параметры записи хоста (ADR-0008 §10).";

    public const string ApprovalsNotice =
        "Возможность подтверждений не объявлена. Любой запрос подтверждения обрабатывается как UnknownHighRisk " +
        "(fail-closed); bypassPermissions не является подтверждением (ADR-0008 §9).";

    public const string RoutePolicyNotice =
        "Режим маршрутизации зафиксирован как ManualOnly: автоматическое переключение, ротация аккаунтов и скрытая перемаршрутизация " +
        "отключены до подтверждения свидетельств маршрута и аккаунта для сессии (ADR-0008 §3, §8).";

    public const string UnavailableNotice =
        "Бэкенд Mirasim не настроен для этого окна. Зарегистрируйте бэкенд Mirasim для опроса " +
        "пользовательского хоста loopback и создания сессий.";

    private static readonly string[] HarnessIds = { "codex", "antigravity", "grok" };

    private readonly IMirasimClient? _client;
    private readonly IMirasimSessionLifecycleService? _lifecycle;
    private readonly IHealthCenterService? _healthCenter;
    private readonly LLMWorkGUI.Application.Security.IDataClassificationGate? _dataClassificationGate;
    private readonly IProjectRepository? _projectRepository;
    private readonly OpenedProjectResolver? _openedProjectResolver;
    private string? _sessionRootPath;
    private string? _sessionProjectId;

    private string _hostStateDisplay = NotProbedState;
    private string _hostUrl;
    private string _hostVersion = NotReported;
    private string _instanceId = NotReported;
    private string _hostUptime = NotReported;
    private string _hostPid = NotReported;
    private string _selectedHarness = DefaultHarness;
    private string _modelId = DefaultModelId;
    private string _promptInput = string.Empty;
    private string _sessionKey = NotReported;
    private string _sessionStatus = NoSessionState;
    private string _turnId = NotReported;
    private string _status = NotReported;
    private string _errorClass = NotReported;
    private string _assistantResponse = NotReported;
    private string _observedModel = NotReported;
    private string _observedAccount = NotReported;
    private string _observedLeg = NotReported;
    private string _blocker = string.Empty;
    private string _guidance = string.Empty;
    private bool _isWriterLockHeld;
    private bool _requiresLocalCleanup;
    private bool _isBusy;
    private DataClassification _projectDataClassification = DataClassification.PrivateSource;

    public MirasimWorkspaceViewModel(
        IMirasimClient? client = null,
        IMirasimSessionLifecycleService? lifecycle = null,
        IHealthCenterService? healthCenter = null,
        LLMWorkGUI.Application.Security.IDataClassificationGate? dataClassificationGate = null,
        IProjectRepository? projectRepository = null,
        OpenedProjectResolver? openedProjectResolver = null)
    {
        _client = client;
        _lifecycle = lifecycle;
        _healthCenter = healthCenter;
        _dataClassificationGate = dataClassificationGate;
        _projectRepository = projectRepository;
        _openedProjectResolver = openedProjectResolver;
        _hostUrl = client?.BaseUrl.ToString() ?? NotReported;

        ProbeHostCommand = new RelayCommand(() => _ = ProbeHostAsync(), () => CanProbeHost);
        CreateSessionCommand = new RelayCommand(() => _ = CreateSessionAsync(), () => CanCreateSession);
        SendPromptCommand = new RelayCommand(() => _ = SendPromptAsync(), () => CanSendPrompt);
        CancelTurnCommand = new RelayCommand(() => _ = CancelTurnAsync(), () => CanCancelTurn);
        ReconcileTurnCommand = new RelayCommand(() => _ = ReconcileTurnAsync(), () => CanReconcileTurn);
    }

    /// <summary>True only when the Mirasim host surface is actually composed into this window.</summary>
    public bool IsBackendAvailable => _client is not null || _lifecycle is not null;

    /// <summary>True only when a Health Center service is composed, so failures can be accounted.</summary>
    public bool IsHealthReportingAvailable => _healthCenter is not null;

    public string UnavailableNoticeText => UnavailableNotice;

    public string RecordingNoticeText => RecordingNotice;

    public string ApprovalsNoticeText => ApprovalsNotice;

    public string RoutePolicyNoticeText => RoutePolicyNotice;

    public IReadOnlyList<string> AvailableHarnesses => HarnessIds;

    /// <summary>
    /// The enforced route policy. It is never editable: an opaque Mirasim route is excluded from
    /// automatic failover, account rotation and silent rerouting (ADR-0008 §3, §8).
    /// </summary>
    public string RouteMode => MirasimRouteModes.ManualOnly;

    public bool IsRouteModeEditable => false;

    /// <summary>Operator-entered local access token, passed per call and never persisted (ADR-0008 §6).</summary>
    public string? AuthToken { get; set; }

    /// <summary>Explicit project override; null follows the currently opened workspace on each operation.</summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Explicit provider profile override for the data classification gate. When unset, the panel's
    /// canonical <c>mirasim</c> backend scope id is used.
    /// </summary>
    public string? ProviderProfileId { get; set; }

    /// <summary>Canonical checkout root the writer lock protects.</summary>
    public string? CanonicalRootPath { get; set; }

    /// <summary>Optional absolute workspace override; otherwise the opened project's root is used.</summary>
    public string? WorkspacePath { get; set; }

    public DataClassification ProjectDataClassification
    {
        get => _projectDataClassification;
        set => SetProperty(ref _projectDataClassification, value);
    }

    public string HostStateDisplay
    {
        get => _hostStateDisplay;
        private set => SetProperty(ref _hostStateDisplay, value);
    }

    public string HostUrl
    {
        get => _hostUrl;
        private set => SetProperty(ref _hostUrl, value);
    }

    public string HostVersion
    {
        get => _hostVersion;
        private set => SetProperty(ref _hostVersion, value);
    }

    /// <summary>Instance id of the last successful probe; sessions are never created without it.</summary>
    public string InstanceId
    {
        get => _instanceId;
        private set
        {
            if (SetProperty(ref _instanceId, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string HostUptime
    {
        get => _hostUptime;
        private set => SetProperty(ref _hostUptime, value);
    }

    public string HostPid
    {
        get => _hostPid;
        private set => SetProperty(ref _hostPid, value);
    }

    public string SelectedHarness
    {
        get => _selectedHarness;
        set
        {
            if (SetProperty(ref _selectedHarness, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ModelId
    {
        get => _modelId;
        set
        {
            if (SetProperty(ref _modelId, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PromptInput
    {
        get => _promptInput;
        set
        {
            if (SetProperty(ref _promptInput, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SessionKey
    {
        get => _sessionKey;
        private set
        {
            if (SetProperty(ref _sessionKey, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SessionStatus
    {
        get => _sessionStatus;
        private set => SetProperty(ref _sessionStatus, value);
    }

    public string TurnId
    {
        get => _turnId;
        private set
        {
            if (SetProperty(ref _turnId, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Normative status of the last observed turn, or <c>Not reported</c> before one exists.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Normalized error class of the last observed turn, or <c>Not reported</c>.</summary>
    public string ErrorClass
    {
        get => _errorClass;
        private set => SetProperty(ref _errorClass, value);
    }

    public string AssistantResponse
    {
        get => _assistantResponse;
        private set => SetProperty(ref _assistantResponse, value);
    }

    public string ObservedModel
    {
        get => _observedModel;
        private set => SetProperty(ref _observedModel, value);
    }

    public string ObservedAccount
    {
        get => _observedAccount;
        private set => SetProperty(ref _observedAccount, value);
    }

    public string ObservedLeg
    {
        get => _observedLeg;
        private set => SetProperty(ref _observedLeg, value);
    }

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

    public string Guidance
    {
        get => _guidance;
        private set => SetProperty(ref _guidance, value);
    }

    /// <summary>
    /// True while the lifecycle retains the checkout writer lock because the turn outcome is not
    /// terminal. It is raised for an ambiguous turn and cleared only by a terminal outcome or a
    /// conclusive reconciliation (ADR-0008 §7).
    /// </summary>
    public bool IsWriterLockHeld
    {
        get => _isWriterLockHeld;
        private set
        {
            if (SetProperty(ref _isWriterLockHeld, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
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
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand ProbeHostCommand { get; }

    public ICommand CreateSessionCommand { get; }

    public ICommand SendPromptCommand { get; }

    public ICommand CancelTurnCommand { get; }

    public ICommand ReconcileTurnCommand { get; }

    public bool CanProbeHost => _client is not null && !IsBusy;

    public bool CanCreateSession =>
        _lifecycle is not null &&
        !IsBusy &&
        !IsWriterLockHeld &&
        HasProbedInstance &&
        IsHarnessSupported(SelectedHarness) &&
        !string.IsNullOrWhiteSpace(ModelId);

    public bool CanSendPrompt =>
        _lifecycle is not null &&
        !IsBusy &&
        HasSession &&
        !IsWriterLockHeld &&
        !string.IsNullOrWhiteSpace(PromptInput);

    public bool CanCancelTurn => _lifecycle is not null && !IsBusy && HasSession && HasTurnId && IsWriterLockHeld;

    public bool CanReconcileTurn => _lifecycle is not null && !IsBusy && IsWriterLockHeld && (HasTurnId || _requiresLocalCleanup);

    private bool HasProbedInstance => !string.Equals(InstanceId, NotReported, StringComparison.Ordinal);

    private bool HasSession => !string.Equals(SessionKey, NotReported, StringComparison.Ordinal);

    private bool HasTurnId => !string.Equals(TurnId, NotReported, StringComparison.Ordinal);

    private string? CurrentAuthToken => string.IsNullOrWhiteSpace(AuthToken) ? null : AuthToken;

    /// <summary>
    /// Probes the loopback host and stores the observed instance id for session creation. A failed
    /// probe is reported to the Health Center as <see cref="HealthErrorClass.NetworkOrTimeout"/>; a
    /// passing probe never claims a verified recovery and never completes a probe
    /// (<see cref="IHealthCenterService.CompleteProbeAsync"/> is never called from the UI).
    /// </summary>
    public async Task ProbeHostAsync()
    {
        if (_client is null)
        {
            Blocker = "Проба хоста Mirasim не выполнялась." + DescribeProbeIdentity();
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ClearDiagnostics();
        var probeStarted = false;

        try
        {
            MirasimHealthStatus status;

            try
            {
                probeStarted = true;
                status = await _client.ProbeHealthAsync().ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var identity = DescribeProbeIdentity();
                if (!probeStarted)
                {
                    ApplyProbeFailure("Проба хоста Mirasim не выполнялась." + identity);
                    return;
                }

                ApplyProbeFailure("Проба хоста Mirasim не подтверждена." + identity
                    + " Результат пробы неизвестен. Повтор небезопасен.");
                await ReportProbeFailureAsync().ConfigureAwait(true);
                if (_healthCenter is not null)
                {
                    Blocker += " Зафиксирован существующий сбой пробы.";
                }

                return;
            }

            if (status.Ok)
            {
                ApplyProbeSuccess(status);
                return;
            }

            ApplyProbeFailure(
                status.ErrorMessage ?? "The Mirasim host did not report a healthy loopback instance.");
            await ReportProbeFailureAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Creates a session for the probed instance, the selected harness and the entered model. The
    /// route mode is never sent as an operator choice: the panel stays ManualOnly.
    /// </summary>
    public async Task CreateSessionAsync()
    {
        if (_lifecycle is null || !CanCreateSession)
        {
            return;
        }

        IsBusy = true;
        ClearDiagnostics();

        var instanceId = InstanceId;
        var harness = SelectedHarness;
        var modelId = ModelId;
        var createStarted = false;
        var callReturned = false;
        string? returnedSession = null;
        try
        {
            var project = _openedProjectResolver is not null
                ? await _openedProjectResolver.ResolveAsync(ProjectId).ConfigureAwait(true)
                : _projectRepository is not null && !string.IsNullOrWhiteSpace(ProjectId)
                    ? await _projectRepository.GetByIdAsync(ProjectId).ConfigureAwait(true)
                    : null;
            var workspacePath = string.IsNullOrWhiteSpace(WorkspacePath) ? project?.RootPath : WorkspacePath;
            if (project is null)
            {
                Blocker = "Для создания Mirasim-сессии требуется сохранённый открытый проект и его policy.";
                return;
            }
            if (string.IsNullOrWhiteSpace(workspacePath) || !Path.IsPathFullyQualified(workspacePath) ||
                !Path.IsPathFullyQualified(project.RootPath))
            {
                Blocker = "Для сессии нужен абсолютный рабочий каталог открытого проекта или явно указанный workspace.";
                return;
            }
            var sessionRoot = ProjectLock.CanonicalizeRoot(workspacePath);
            if (!string.Equals(sessionRoot,
                ProjectLock.CanonicalizeRoot(project.RootPath), StringComparison.OrdinalIgnoreCase))
            {
                Blocker = "Рабочий каталог сессии Mirasim не совпадает с открытым проектом.";
                return;
            }

            createStarted = true;
            var binding = await _lifecycle
                .CreateSessionAsync(instanceId, harness, modelId, sessionRoot, CurrentAuthToken,
                    projectContext: new(project.Id, ProviderProfileId ?? BackendScopeId))
                .ConfigureAwait(true);
            callReturned = true;
            returnedSession = binding.SessionKey;

            if (!Path.IsPathFullyQualified(binding.WorkspacePath) || !string.Equals(sessionRoot,
                ProjectLock.CanonicalizeRoot(binding.WorkspacePath), StringComparison.OrdinalIgnoreCase))
            {
                SessionKey = NotReported;
                SessionStatus = "Failed";
                _sessionRootPath = null;
                _sessionProjectId = null;
                Blocker = "Mirasim не подтвердил рабочий каталог запрошенной сессии.";
                return;
            }
            _sessionRootPath = sessionRoot;
            _sessionProjectId = project.Id;
            SessionKey = Display(binding.SessionKey);
            SessionStatus = "Confirmed";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SessionKey = NotReported;
            _sessionRootPath = null;
            _sessionProjectId = null;
            SessionStatus = "Failed";
            var known = "Harness " + harness + ", модель " + modelId + ".";
            if (!createStarted)
            {
                Blocker = "Создание сессии Mirasim не начиналось. " + known + " Сессия не создавалась.";
            }
            else if (!callReturned)
            {
                Blocker = "Создание сессии Mirasim не подтверждено. " + known
                    + " Сессия не возвращена. Создание могло быть доставлено. Повтор небезопасен. Состояние здоровья не изменялось.";
            }
            else
            {
                Blocker = "Результат создания сессии Mirasim получен. " + known
                    + (string.IsNullOrWhiteSpace(returnedSession) ? string.Empty : " Сессия " + returnedSession + ".")
                    + " Повтор небезопасен.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Executes one turn through <see cref="IMirasimSessionLifecycleService.ExecuteTurnAsync"/> with
    /// <c>Write</c> execution mode, so the lifecycle owns the checkout writer lock. The prompt is
    /// passed only through the lifecycle request and never through argv, URI or logs (ТЗ §9.3).
    /// </summary>
    public async Task SendPromptAsync()
    {
        if (_lifecycle is null || !CanSendPrompt)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await SendPromptCoreAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = "Подготовка хода Mirasim не выполнена. Harness " + SelectedHarness
                + ", модель " + ModelId + ". Запрос не доставлялся.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SendPromptCoreAsync()
    {
        var prompt = PromptInput;

        if (_dataClassificationGate is null)
        {
            Blocker = "Data classification gate is unavailable; prompt dispatch is blocked (fail-closed, ТЗ §6.5).";
            return;
        }

        // The project and the protected checkout root are resolved before the turn reaches the
        // lifecycle, so the writer lock is only ever acquired for the project that actually owns the
        // checkout (ТЗ §6.5, §6.12, ADR-0008 §7).
        Project? project;

        if (_openedProjectResolver is not null)
        {
            project = await _openedProjectResolver.ResolveAsync(ProjectId).ConfigureAwait(true);
        }
        else if (_projectRepository is not null && !string.IsNullOrWhiteSpace(ProjectId))
        {
            project = await _projectRepository.GetByIdAsync(ProjectId).ConfigureAwait(true);
        }
        else
        {
            project = null;
        }

        if (project is null)
        {
            Blocker = string.IsNullOrWhiteSpace(ProjectId)
                ? "Project is not configured or repository is unavailable; prompt dispatch is blocked (fail-closed, ТЗ §6.5)."
                : $"Project '{ProjectId}' not found; prompt dispatch is blocked (fail-closed, ТЗ §6.5).";
            return;
        }

        ProjectDataClassification = project.DataClassification;

        if (!Path.IsPathFullyQualified(project.RootPath) ||
            (!string.IsNullOrWhiteSpace(CanonicalRootPath) && !Path.IsPathFullyQualified(CanonicalRootPath)))
        {
            Blocker = "Рабочий каталог проекта должен быть абсолютным путём.";
            return;
        }
        var projectRoot = ProjectLock.CanonicalizeRoot(project.RootPath);
        if (_sessionProjectId != project.Id || _sessionRootPath is null
            || !string.Equals(_sessionRootPath, projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            Blocker = "Рабочий каталог сессии Mirasim не совпадает с открытым проектом. Создайте новую сессию.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(CanonicalRootPath) && !string.Equals(
            ProjectLock.CanonicalizeRoot(CanonicalRootPath),
            ProjectLock.CanonicalizeRoot(project.RootPath),
            StringComparison.OrdinalIgnoreCase))
        {
            Blocker = "Canonical checkout root does not match resolved project root; prompt dispatch is blocked (fail-closed, ТЗ §6.5).";
            return;
        }

        var effectiveProfileId = ProviderProfileId ?? BackendScopeId;
        var gateDecision = await _dataClassificationGate
            .EvaluateAsync(project.DataClassification, effectiveProfileId, isManualOnly: false)
            .ConfigureAwait(true);

        if (!gateDecision.IsAllowed)
        {
            Blocker = gateDecision.Explanation ?? "Data classification gate blocked prompt dispatch.";
            return;
        }

        ClearDiagnostics();
        PromptInput = string.Empty;

        var callReturned = false;
        try
        {
            var result = await _lifecycle!
                .ExecuteTurnAsync(new MirasimTurnRequest
                {
                    SessionKey = SessionKey,
                    Prompt = prompt,
                    ExecutionMode = WriteExecutionMode,
                    RequestedHarness = SelectedHarness,
                    RequestedModelId = ModelId,
                    ProjectId = project.Id,
                    ProviderProfileId = effectiveProfileId,
                    CanonicalRootPath = projectRoot,
                    ExecutionId = $"mirasim-{Guid.NewGuid():N}",
                    ProcessGeneration = 0,
                    AuthToken = CurrentAuthToken
                })
                .ConfigureAwait(true);

            callReturned = true;
            ApplyTurnResult(result);
            await ReportTurnOutcomeAsync(result).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = callReturned
                ? "Результат хода Mirasim получен. Harness " + SelectedHarness + ", модель " + ModelId
                    + ". Сессия " + SessionKey + ". Повтор небезопасен."
                : "Ход Mirasim не подтверждён. Harness " + SelectedHarness + ", модель " + ModelId
                    + ". Сессия " + SessionKey + ". Запрос мог быть доставлен. Повтор небезопасен. Состояние здоровья не изменялось.";
        }
    }

    /// <summary>
    /// Requests cancellation of the last observed turn. The status becomes <c>Cancelled</c> only when
    /// the host confirms a terminal cancellation; an acknowledgement alone is not terminal evidence.
    /// </summary>
    public async Task CancelTurnAsync()
    {
        if (_lifecycle is null || !CanCancelTurn)
        {
            return;
        }

        IsBusy = true;
        ClearDiagnostics();

        var callReturned = false;
        try
        {
            var result = await _lifecycle
                .CancelTurnAsync(SessionKey, TurnId, CurrentAuthToken)
                .ConfigureAwait(true);
            callReturned = true;

            if (result.IsTerminal)
            {
                ApplyCancelResult(result);
                await ReportTurnOutcomeAsync(result).ConfigureAwait(true);
                return;
            }

            // The host acknowledged the cancel request without a terminal outcome; the turn stays
            // unresolved and the writer lock remains retained (ADR-0008 §7).
            IsWriterLockHeld = true;
            Blocker = result.ErrorMessage ??
                "The Mirasim host did not confirm cancellation with a terminal outcome.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = callReturned
                ? "Результат отмены Mirasim получен. Harness " + SelectedHarness + ", модель " + ModelId
                    + ". Сессия " + SessionKey + ". Повтор небезопасен."
                : "Отмена Mirasim не подтверждена. Harness " + SelectedHarness + ", модель " + ModelId
                    + ". Сессия " + SessionKey + ". Отмена могла быть доставлена. Повтор небезопасен. Состояние здоровья не изменялось.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reconciles an ambiguous turn against the host. Only a terminal result clears
    /// <see cref="IsWriterLockHeld"/>; an inconclusive reconciliation keeps the lock retained.
    /// </summary>
    public async Task ReconcileTurnAsync()
    {
        if (_lifecycle is null || !CanReconcileTurn)
        {
            return;
        }

        IsBusy = true;
        ClearDiagnostics();

        var callReturned = false;
        try
        {
            var result = await _lifecycle
                .ReconcileTurnAsync(SessionKey, _requiresLocalCleanup ? string.Empty : TurnId, CurrentAuthToken)
                .ConfigureAwait(true);
            callReturned = true;

            ApplyTurnResult(result);
            await ReportTurnOutcomeAsync(result).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = callReturned
                ? "Результат сверки Mirasim получен. Harness " + SelectedHarness + ", модель " + ModelId
                    + ". Сессия " + SessionKey + ". Повтор небезопасен."
                : "Сверка Mirasim не подтверждена. Harness " + SelectedHarness + ", модель " + ModelId
                    + ". Сессия " + SessionKey + ". Сверка могла быть доставлена. Повтор небезопасен. Состояние здоровья не изменялось.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyProbeSuccess(MirasimHealthStatus status)
    {
        HostStateDisplay = AvailableState;
        HostUrl = _client!.BaseUrl.ToString();
        HostVersion = Display(status.Version);
        InstanceId = Display(status.InstanceId);
        HostUptime = status.Uptime?.ToString(CultureInfo.InvariantCulture) ?? NotReported;
        HostPid = status.Pid?.ToString(CultureInfo.InvariantCulture) ?? NotReported;
    }

    private void ApplyProbeFailure(string reason)
    {
        HostStateDisplay = UnavailableState;
        HostVersion = NotReported;
        InstanceId = NotReported;
        HostUptime = NotReported;
        HostPid = NotReported;
        Blocker = reason;
    }

    private string DescribeProbeIdentity()
    {
        var text = string.Empty;
        if (!string.IsNullOrWhiteSpace(HostUrl)
            && !string.Equals(HostUrl, NotReported, StringComparison.Ordinal))
        {
            text += " Хост " + HostUrl + ".";
        }

        if (!string.IsNullOrWhiteSpace(SelectedHarness))
        {
            text += " Harness " + SelectedHarness + ".";
        }

        return text;
    }

    private void ApplyTurnResult(MirasimTurnResult result)
    {
        TurnId = Display(result.TurnId);
        Status = result.Status.ToString();
        ErrorClass = result.ErrorClass.ToString();
        AssistantResponse = Display(result.AssistantResponse);
        ObservedModel = Display(result.ObservedModel);
        ObservedAccount = Display(result.ObservedAccount);
        ObservedLeg = Display(result.ObservedLeg);

        // The lock is retained for every non-terminal outcome and cleared only by terminal evidence.
        IsWriterLockHeld = !result.IsTerminal;
        _requiresLocalCleanup = result.RequiresLocalCleanup;
        OnPropertyChanged(nameof(CanReconcileTurn));

        if (result.Status == MirasimTurnStatus.Ambiguous)
        {
            Guidance = AmbiguousLockGuidance;
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            Blocker = result.ErrorMessage!;
        }
    }

    private void ApplyCancelResult(MirasimTurnResult result)
    {
        TurnId = Display(result.TurnId);
        Status = result.Status.ToString();
        ErrorClass = result.ErrorClass.ToString();
        IsWriterLockHeld = !result.IsTerminal;

        // A cancel response carries no assistant or route evidence; the previously observed fields are
        // deliberately preserved instead of being erased.
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            Blocker = result.ErrorMessage!;
        }
    }

    /// <summary>
    /// Reports the observed turn outcome under the <c>mirasim</c> backend scope. Only a confirmed
    /// <see cref="MirasimTurnStatus.Completed"/> turn is a success; ambiguous and cancelled outcomes
    /// are recorded with error classes that policy excludes from the breaker. Health reporting is
    /// bookkeeping and never changes the turn outcome.
    /// </summary>
    private async Task ReportTurnOutcomeAsync(MirasimTurnResult result)
    {
        if (_healthCenter is null)
        {
            return;
        }

        var errorClass = MapHealthErrorClass(result);
        var scope = errorClass == HealthErrorClass.ModelUnavailableOrMismatch
            ? HealthScope.ForModelRoute(BackendScopeId, ModelId)
            : HealthScope.ForBackend(BackendScopeId);

        try
        {
            if (result.Status == MirasimTurnStatus.Completed)
            {
                await _healthCenter
                    .ReportSuccessAsync(scope, BuildSuccessReason(result), CancellationToken.None)
                    .ConfigureAwait(true);
                return;
            }

            if (result.Status is
                MirasimTurnStatus.Pending or
                MirasimTurnStatus.Running or
                MirasimTurnStatus.RefusedByLock or
                MirasimTurnStatus.RefusedByPolicy)
            {
                // A running turn is not a failure yet, and a local writer-lock refusal says nothing
                // about the Mirasim host, so neither moves the backend health scope.
                return;
            }

            await _healthCenter
                .ReportFailureAsync(
                    scope,
                    errorClass,
                    BuildFailureReason(result),
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Health reporting must never change the observed turn outcome.
        }
    }

    private async Task ReportProbeFailureAsync()
    {
        if (_healthCenter is null)
        {
            return;
        }

        try
        {
            await _healthCenter
                .ReportFailureAsync(
                    HealthScope.ForBackend(BackendScopeId),
                    HealthErrorClass.NetworkOrTimeout,
                    "The Mirasim host health probe failed; no healthy loopback instance was observed.",
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Health reporting must never change the observed probe outcome.
        }
    }

    private static HealthErrorClass MapHealthErrorClass(MirasimTurnResult result) =>
        result.Status switch
        {
            MirasimTurnStatus.Ambiguous => HealthErrorClass.UnknownOrAmbiguousCompletion,
            MirasimTurnStatus.Cancelled => HealthErrorClass.UserCancellation,
            _ => result.ErrorClass switch
            {
                MirasimTurnErrorClass.UpstreamUnavailable503 or
                    MirasimTurnErrorClass.PlatformBusy422 => HealthErrorClass.Provider4xx5xx,
                MirasimTurnErrorClass.ConnectionDrop or
                    MirasimTurnErrorClass.Timeout => HealthErrorClass.NetworkOrTimeout,
                MirasimTurnErrorClass.RouteMismatch => HealthErrorClass.ModelUnavailableOrMismatch,
                MirasimTurnErrorClass.IncompletePayload => HealthErrorClass.MalformedProtocolEvent,
                MirasimTurnErrorClass.UnsupportedChannel => HealthErrorClass.Provider4xx5xx,
                MirasimTurnErrorClass.DoneWithError => HealthErrorClass.Provider4xx5xx,
                _ => HealthErrorClass.UnknownOrAmbiguousCompletion
            }
        };

    /// <summary>
    /// Health reasons are built exclusively from the turn status and the normalized error class, so a
    /// prompt, token or raw payload can never leak into the Health Center (ТЗ §6.10, ADR-0008 §10).
    /// </summary>
    private static string BuildSuccessReason(MirasimTurnResult result) =>
        $"Mirasim turn '{DescribeTurnId(result.TurnId)}' completed with a confirmed terminal outcome.";

    private static string BuildFailureReason(MirasimTurnResult result) =>
        $"Mirasim turn '{DescribeTurnId(result.TurnId)}' ended with status {result.Status} and error class {result.ErrorClass}.";

    private static string DescribeTurnId(string? turnId) =>
        string.IsNullOrWhiteSpace(turnId) ? "unknown" : turnId;

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? NotReported : value;

    private static bool IsHarnessSupported(string? harness) =>
        harness is not null && Array.Exists(HarnessIds, candidate => string.Equals(candidate, harness, StringComparison.Ordinal));

    private void ClearDiagnostics()
    {
        Blocker = string.Empty;
        Guidance = string.Empty;
    }
}
