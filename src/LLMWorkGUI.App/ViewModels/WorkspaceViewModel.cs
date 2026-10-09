using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Discovery;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using System.Security.Cryptography;
using System.Text;

namespace LLMWorkGUI.App.ViewModels;

public sealed partial class WorkspaceViewModel : ScreenViewModel
{
    public const string EmptyTimelineMessage =
        "Временная шкала пуста.";

    public const string SyntheticEvidenceNotice =
        "Синтетическое подтверждение для автономного режима.";

    private readonly IActivityTimelineService _timelineService;
    private readonly IOpenCodeSessionLifecycleService? _sessionLifecycleService;
    private readonly IOpenCodeClient? _openCodeClient;
    private readonly IOpenCodeCapabilityCacheService? _capabilityCacheService;
    private readonly TimeProvider? _timeProvider;
    private readonly StatusBarViewModel? _statusBar;
    private readonly LLMWorkGUI.Application.Health.IStickyRouteTurnGate? _stickyRouteGate;
    private readonly LLMWorkGUI.Application.Security.IDataClassificationGate? _dataClassificationGate;
    private readonly IProjectRepository? _projectRepository;
    private readonly OpenedProjectResolver? _openedProjectResolver;
    private readonly ICheckoutLockService? _checkoutLockService;
    private readonly IApplicationInstanceGuard? _instanceGuard;
    private readonly LLMWorkGUI.Backends.OpenCode.OpenCodeServerConnection? _serverConnection;
    private IOpenCodeServerInstance? _sessionProcess;
    private long _sessionProcessGeneration;
    // A retained token must stay alive until explicit reconciliation of the native outcome.
    private ICheckoutLockToken? _retainedWriterLock;
    private bool _turnInProgress;

    private ActivityTimeline _timeline = ActivityTimeline.Empty;
    private bool _hasTimeline;
    private bool _hasStages;
    private bool _hasSyntheticEvidence;
    private string _evidenceNotice = string.Empty;
    private string _runStateDisplay = "Запуск не наблюдался";

    private string _processStateDisplay = "Остановлен";
    private bool _isProcessStarted;
    private string _sessionStatusDisplay = "Нет";
    private bool _isSessionConfirmed;
    private string _localSessionId = "Not reported";
    private string _nativeSessionId = "Not reported";
    private SessionBinding? _currentBinding;
    private bool _isBusy;
    private string _promptInput = string.Empty;
    private string _ancestryDisplay = string.Empty;
    private bool _isDiagnosticCliFallbackVisible;
    private string _diagnosticFallbackNote = "Диагностический откат CLI — запускает машиночитаемую диагностику CLI без изменения сессии сервера";
    private string _sendBlocker = string.Empty;
    private bool _requiresReplacementSession;
    private DataClassification _projectDataClassification = DataClassification.PrivateSource;

    public WorkspaceViewModel(
        IActivityTimelineService timelineService,
        IOpenCodeSessionLifecycleService? sessionLifecycleService = null,
        IOpenCodeClient? openCodeClient = null,
        IOpenCodeCapabilityCacheService? capabilityCacheService = null,
        TimeProvider? timeProvider = null,
        StatusBarViewModel? statusBar = null,
        CursorWorkspaceViewModel? cursor = null,
        LLMWorkGUI.Application.Health.IStickyRouteTurnGate? stickyRouteGate = null,
        MirasimWorkspaceViewModel? mirasim = null,
        LLMWorkGUI.Application.Security.IDataClassificationGate? dataClassificationGate = null,
        IProjectRepository? projectRepository = null,
        OpenedProjectResolver? openedProjectResolver = null,
        IOpenCodeExecutionJournal? executionJournal = null,
        ICheckoutLockService? checkoutLockService = null,
        IApplicationInstanceGuard? instanceGuard = null,
        NativeGatewayWorkspaceViewModel? nativeGateway = null,
        LLMWorkGUI.Backends.OpenCode.OpenCodeServerConnection? serverConnection = null)
        : base(
            ScreenId.Workspace,
            "Рабочая область",
            "Ctrl+1",
            "Диалог с моделью, ход выполнения и изменения файлов.")
    {
        ArgumentNullException.ThrowIfNull(timelineService);

        _timelineService = timelineService;
        _sessionLifecycleService = sessionLifecycleService;
        _openCodeClient = openCodeClient;
        _capabilityCacheService = capabilityCacheService;
        _timeProvider = timeProvider;
        _statusBar = statusBar;
        _stickyRouteGate = stickyRouteGate;
        _dataClassificationGate = dataClassificationGate;
        _projectRepository = projectRepository;
        _openedProjectResolver = openedProjectResolver;
        _executionJournal = executionJournal;
        _checkoutLockService = checkoutLockService;
        _instanceGuard = instanceGuard;
        _serverConnection = serverConnection;
        Cursor = cursor;
        Mirasim = mirasim;
        NativeGateway = nativeGateway;

        CreateSessionCommand = new RelayCommand(() => _ = CreateSessionAsync(),
            () => !_isProcessStarted && !IsBusy && SelectedOpenCodeRoute is not null && _sessionLifecycleService is not null);
        RefreshOpenCodeRoutesCommand = new RelayCommand(() => _ = RefreshOpenCodeRoutesAsync(), () => CanChooseOpenCodeRoute);
        SendPromptCommand = new RelayCommand(() => _ = SendPromptAsync(), () => CanSend);
        CancelTurnCommand = new RelayCommand(() => _ = CancelTurnAsync(), () => CanCancel);
        ResetSessionCommand = new RelayCommand(() => _ = ResetSessionAsync(), () => CanReset);
        NewSessionCommand = new RelayCommand(PrepareNewSession, () => CanPrepareNewSession);
        RunDiagnosticFallbackCommand = new RelayCommand(() => _ = RunDiagnosticFallbackAsync());
        InitializeOpenCodePermissionCommands();

        ConversationTurns.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasConversation));
    }

    /// <summary>
    /// Cursor ACP panel of this workspace, or null when the native Cursor backend is not composed
    /// into this window. A null panel is simply not rendered; nothing is faked in its place.
    /// </summary>
    public CursorWorkspaceViewModel? Cursor { get; }

    /// <summary>True only when the Cursor panel is actually available for rendering.</summary>
    public bool HasCursorPanel => Cursor is not null;

    /// <summary>
    /// Mirasim panel of this workspace, or null when the Mirasim backend is not composed into this
    /// window. A null panel is simply not rendered; nothing is faked in its place.
    /// </summary>
    public MirasimWorkspaceViewModel? Mirasim { get; }

    /// <summary>True only when the Mirasim panel is actually available for rendering.</summary>
    public bool HasMirasimPanel => Mirasim is not null;
    public NativeGatewayWorkspaceViewModel? NativeGateway { get; }
    public bool HasNativeGatewayPanel => NativeGateway is not null;

    public ObservableCollection<StageViewModel> Stages { get; } = new();

    public ObservableCollection<ActivityTimelineItemViewModel> TimelineItems { get; } = new();

    public bool HasTimeline
    {
        get => _hasTimeline;
        private set => SetProperty(ref _hasTimeline, value);
    }

    public bool HasSyntheticEvidence
    {
        get => _hasSyntheticEvidence;
        private set => SetProperty(ref _hasSyntheticEvidence, value);
    }

    public bool HasStages
    {
        get => _hasStages;
        private set => SetProperty(ref _hasStages, value);
    }

    public string EvidenceNotice
    {
        get => _evidenceNotice;
        private set => SetProperty(ref _evidenceNotice, value);
    }

    public string RunStateDisplay
    {
        get => _runStateDisplay;
        private set => SetProperty(ref _runStateDisplay, value);
    }

    public string ActiveRoleDisplay => _timeline.ActiveRole.ToString();

    public string TimelineEmptyState => EmptyTimelineMessage;

    public string ConversationEmptyState => "Выберите модель и создайте сессию, чтобы начать диалог.";

    public string ConversationHeader => "Диалог / Запуск";

    public string DiffSummary => "Diff не зарегистрирован";

    public string DiffEmptyState => "Здесь появятся изменения файлов, зафиксированные во время выполнения задачи.";

    public string RunReasonNote => "Решение о выборе маршрута появится после начала выполнения.";

    public string ProcessStateDisplay
    {
        get => _processStateDisplay;
        private set
        {
            if (SetProperty(ref _processStateDisplay, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsProcessStarted
    {
        get => _isProcessStarted;
        private set
        {
            if (SetProperty(ref _isProcessStarted, value))
            {
                OnPropertyChanged(nameof(CanChooseOpenCodeRoute));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SessionStatusDisplay
    {
        get => _sessionStatusDisplay;
        private set => SetProperty(ref _sessionStatusDisplay, value);
    }

    public bool IsSessionConfirmed
    {
        get => _isSessionConfirmed;
        private set
        {
            if (SetProperty(ref _isSessionConfirmed, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanReset));
                OnPropertyChanged(nameof(CanPrepareNewSession));
            }
        }
    }

    public string LocalSessionId
    {
        get => _localSessionId;
        private set => SetProperty(ref _localSessionId, value);
    }

    public string NativeSessionId
    {
        get => _nativeSessionId;
        private set => SetProperty(ref _nativeSessionId, value);
    }

    public SessionBinding? CurrentBinding
    {
        get => _currentBinding;
        private set => SetProperty(ref _currentBinding, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanChooseOpenCodeRoute));
                RelayCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanReset));
                OnPropertyChanged(nameof(CanPrepareNewSession));
            }
        }
    }

    public bool CanSend => IsSessionConfirmed && !IsBusy && !string.IsNullOrWhiteSpace(PromptInput);
    public bool CanCancel => _turnInProgress;
    public bool CanReset => CanPrepareNewSession;
    public bool CanPrepareNewSession => IsSessionConfirmed && !IsBusy && !_turnInProgress
        && !IsWriterLockRetained && !_isReplyingOpenCodePermission && _permissionEntry is null
        && !HasPendingOpenCodePermissions;
    public bool IsWriterLockRetained => _retainedWriterLock?.IsHeld == true;

    /// <summary>
    /// Explicit project override. When unset, each operation resolves the currently opened workspace.
    /// A resolved session project must never be written back into this override.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Explicit provider profile override for the data classification gate. When unset, the provider
    /// profile of the current session binding is used.
    /// </summary>
    public string? ProviderProfileId { get; set; }

    public DataClassification ProjectDataClassification
    {
        get => _projectDataClassification;
        set => SetProperty(ref _projectDataClassification, value);
    }

    public string PromptInput
    {
        get => _promptInput;
        set
        {
            if (SetProperty(ref _promptInput, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public ObservableCollection<WorkspaceTurnViewModel> ConversationTurns { get; } = new();

    public string AncestryDisplay
    {
        get => _ancestryDisplay;
        private set => SetProperty(ref _ancestryDisplay, value);
    }

    public bool IsDiagnosticCliFallbackVisible
    {
        get => _isDiagnosticCliFallbackVisible;
        private set => SetProperty(ref _isDiagnosticCliFallbackVisible, value);
    }

    public string DiagnosticFallbackNote
    {
        get => _diagnosticFallbackNote;
        private set => SetProperty(ref _diagnosticFallbackNote, value);
    }

    /// <summary>
    /// Why the last send was refused. A turn bound to a route that has left routing is not sent at all
    /// (ТЗ §6.5), so the operator learns why instead of a prompt silently vanishing.
    /// </summary>
    public string SendBlocker
    {
        get => _sendBlocker;
        private set
        {
            if (SetProperty(ref _sendBlocker, value))
            {
                OnPropertyChanged(nameof(HasSendBlocker));
            }
        }
    }

    public bool HasSendBlocker => !string.IsNullOrWhiteSpace(SendBlocker);

    /// <summary>
    /// True when the last send was refused because the sticky route left routing. A replacement session
    /// is only ever created after the user explicitly confirms one; nothing happens automatically.
    /// </summary>
    public bool RequiresReplacementSession
    {
        get => _requiresReplacementSession;
        private set
        {
            if (SetProperty(ref _requiresReplacementSession, value))
            {
                OnPropertyChanged(nameof(ReplacementSessionNotice));
            }
        }
    }

    public string ReplacementSessionNotice =>
        "Замещающая сессия требует явного подтверждения пользователя; запрос не отправлен (ТЗ §6.5).";

    public bool HasConversation => ConversationTurns.Count > 0;

    public ICommand CreateSessionCommand { get; }
    public ICommand SendPromptCommand { get; }
    public ICommand CancelTurnCommand { get; }
    public ICommand ResetSessionCommand { get; }
    public ICommand NewSessionCommand { get; }
    public ICommand RunDiagnosticFallbackCommand { get; }

    private async Task CreateSessionAsync()
    {
        if (_sessionLifecycleService == null || IsBusy || IsProcessStarted) return;
        var route = SelectedOpenCodeRoute;
        if (_executionJournal is null || route is null)
        {
            SendBlocker = "Выберите сохранённый маршрут OpenCode перед созданием сессии.";
            return;
        }
        IsBusy = true;
        ProcessStateDisplay = "Starting";
        SessionStatusDisplay = "Creating";
        _statusBar?.UpdateSessionState(null, null, "Unconfirmed");
        var callStarted = false;
        string? returnedSession = null;

        try
        {
            _instanceGuard?.EnsureSupervisorPermitted();
            if (!(await _executionJournal.ListRoutesAsync()).Contains(route.Stored))
            {
                SendBlocker = "Выбранный маршрут больше недоступен. Обновите маршруты и выберите маршрут заново.";
                SessionStatusDisplay = "Нет";
                ProcessStateDisplay = "Остановлен";
                return;
            }
            var project = _openedProjectResolver is not null
                ? await _openedProjectResolver.ResolveAsync(ProjectId)
                : _projectRepository is not null && !string.IsNullOrWhiteSpace(ProjectId)
                    ? await _projectRepository.GetByIdAsync(ProjectId) : null;
            if (project is null)
                throw new InvalidOperationException("Project not found; создание сессии заблокировано (fail-closed).");
            _sessionDirectory = ProjectLock.CanonicalizeRoot(project.RootPath);
            _sessionProjectId = project.Id;
            ProjectDataClassification = project.DataClassification;
            var request = new OpenCodeCreateSessionRequest
            {
                Model = route.NativeModelId,
                Directory = _sessionDirectory,
                ProviderProfileId = string.IsNullOrWhiteSpace(route.ProviderProfileId) ? null : route.ProviderProfileId
            };
            callStarted = true;
            var response = await _sessionLifecycleService.CreateAndConfirmSessionAsync(request);
            returnedSession = response.Id;

            _sessionProcess = _serverConnection is null ? null
                : await _serverConnection.GetInstanceAsync(route.ProviderProfileId);
            _sessionProcessGeneration = _sessionProcess?.ProcessGeneration ?? 0;

            var localId = await _executionJournal.ConfirmSessionAsync(project.Id, _sessionDirectory, response.Id, route.Stored);
            _sessionRoute = route.Stored;
            CurrentBinding = new SessionBinding(BackendType.OpenCode, route.ProviderProfileId, route.AccountId, route.ModelId, null, null, null);
            SendBlocker = string.Empty;
            LocalSessionId = localId;
            NativeSessionId = response.Id;

            IsProcessStarted = true;
            ProcessStateDisplay = "Running";
            SessionStatusDisplay = "Confirmed";
            IsSessionConfirmed = true;
            RequiresReplacementSession = false;
            _statusBar?.UpdateSessionState(LocalSessionId, NativeSessionId, "Confirmed", "Idle", CurrentBinding.ProviderProfileId, CurrentBinding.ModelId);
        }
        catch (Exception)
        {
            var known = " Маршрут " + route.Id + " (OpenCode, " + route.NativeModelId + ").";
            SendBlocker = !callStarted
                ? "Создание сессии OpenCode не начиналось." + known + " Сессия не создавалась."
                : returnedSession is null
                    ? "Создание сессии OpenCode не подтверждено." + known
                        + " Сессия не возвращена. Создание могло быть доставлено. Повтор небезопасен. Состояние здоровья не изменялось."
                    : "Результат создания сессии OpenCode получен." + known
                        + " Сессия " + returnedSession + ". Повтор небезопасен. Состояние здоровья не изменялось.";
            SessionStatusDisplay = "Failed";
            ProcessStateDisplay = "Остановлен";
            IsProcessStarted = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SendPromptAsync()
    {
        if (!CanSend || _sessionLifecycleService == null || CurrentBinding == null) return;
        // Reserve the UI operation before the first asynchronous gate.
        IsBusy = true;
        try { await SendReservedPromptAsync(); }
        catch (Exception error)
        {
            SendBlocker = new LLMWorkGUI.Application.Security.CredentialTextRedactor().RedactDiagnostic(error.Message);
        }
        finally { IsBusy = false; }
    }

    private async Task SendReservedPromptAsync()
    {
        if (_executionJournal is null || _checkoutLockService is null || _sessionRoute is null || _sessionLifecycleService is null)
        {
            SendBlocker = "Журнал выполнения или блокировка проекта недоступны; отправка заблокирована (fail-closed).";
            return;
        }
        var binding = CurrentBinding;
        var prompt = PromptInput;

        // The sticky-session rule is enforced here, at the real send path: a route that has left routing
        // stops the next turn and asks for a replacement session. The prompt is not sent, and nothing is
        // re-routed automatically (ТЗ §6.5).
        if (await IsStickyRouteSendableAsync().ConfigureAwait(true) is { } blocked)
        {
            SendBlocker = blocked;
            RequiresReplacementSession = true;
            return;
        }

        if (_dataClassificationGate is null)
        {
            SendBlocker = "Data classification gate is unavailable; prompt dispatch is blocked (fail-closed, ТЗ §6.5).";
            return;
        }

        // The opened project is resolved through the composition resolver when available; without it the
        // workspace keeps the explicit project repository path. An unresolved project never reaches the
        // gate because a guessed classification could silently widen what may be sent (ТЗ §6.5).
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
            SendBlocker = string.IsNullOrWhiteSpace(ProjectId)
                ? "Project is not configured or repository is unavailable; prompt dispatch is blocked (fail-closed, ТЗ §6.5)."
                : $"Project '{ProjectId}' not found; prompt dispatch is blocked (fail-closed, ТЗ §6.5).";
            return;
        }

        if (project.Id != _sessionProjectId || !string.Equals(ProjectLock.CanonicalizeRoot(project.RootPath),
            _sessionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            SendBlocker = "Сессия создана в другом каталоге проекта. Создайте новую сессию.";
            return;
        }

        ProjectDataClassification = project.DataClassification;

        var effectiveProfileId = binding!.ProviderProfileId;
        var gateDecision = await _dataClassificationGate
            .EvaluateAsync(project.DataClassification, effectiveProfileId, isManualOnly: false)
            .ConfigureAwait(true);

        if (!gateDecision.IsAllowed)
        {
            SendBlocker = gateDecision.Explanation ?? "Data classification gate blocked prompt dispatch.";
            return;
        }

        SendBlocker = string.Empty;
        RequiresReplacementSession = false;

        var process = _sessionProcess;
        var generation = _sessionProcessGeneration;
        if (generation <= 0 || !await IsSessionProcessCurrentAsync(process!, generation))
        {
            SendBlocker = "Поколение процесса OpenCode неизвестно или изменилось; создайте новую сессию. Запрос не отправлен.";
            RequiresReplacementSession = true;
            return;
        }

        var entry = await _executionJournal.BeginAsync(project.Id, project.RootPath, NativeSessionId,
            _sessionRoute, Guid.NewGuid().ToString("D"), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))), generation);
        ICheckoutLockToken? token = null;
        TurnResult? result = null;
        WorkspaceTurnViewModel? turn = null;
        var dispatched = false;
        try
        {
            // No native read-only mode is confirmed for this path: reserve a writer lock.
            token = await _checkoutLockService.AcquireWriterLockAsync(project.Id, project.RootPath, entry.ExecutionId, generation);
            if (!await IsSessionProcessCurrentAsync(process!, generation) || !token.IsHeld)
                throw new InvalidOperationException("Процесс OpenCode изменился до отправки. Запрос не отправлен.");
            await _executionJournal.MarkRunningAsync(entry);
            if (PromptInput == prompt) PromptInput = string.Empty;
            turn = new WorkspaceTurnViewModel { Role = "User", PromptText = prompt, Status = "Running" };
            ConversationTurns.Add(turn);
            _turnInProgress = true;
            RelayCommand.RaiseCanExecuteChanged();
            _statusBar?.UpdateSessionState(null, null, null, "Running");
            dispatched = true;
            result = await ObserveOpenCodePermissionsAsync(entry, turn,
                new OpenCodePromptRequest { Prompt = prompt, Model = entry.Route.NativeModelId,
                    DispatchAuthorization = async (native, request, uri, ct) =>
                        token.IsHeld && await IsSessionProcessCurrentAsync(process!, generation, ct)
                        && await _executionJournal.AuthorizePromptDispatchAsync(entry, native, request, uri, ct)
                        && token.IsHeld && await IsSessionProcessCurrentAsync(process!, generation, ct) });
            if (result.SessionId != entry.NativeSessionId)
                throw new InvalidOperationException("OpenCode вернул результат другой сессии; требуется reconciliation.");
            if (result.Status is not (TurnResult.CompletedStatus or TurnResult.CancelledStatus or TurnResult.FailedStatus))
                throw new InvalidOperationException("OpenCode не сообщил поддерживаемый terminal outcome.");

            turn.ResponseText = result.OutputText;
            turn.Status = result.IsDeliveryUncertain ? "Ambiguous" : result.Status;
            if (result.Status == TurnResult.FailedStatus)
                SendBlocker = new LLMWorkGUI.Application.Security.CredentialTextRedactor().RedactDiagnostic(
                    result.ErrorMessage ?? "OpenCode не подтвердил завершение ответа.");
            _statusBar?.UpdateSessionState(null, null, null, turn.Status);
        }
        catch (Exception ex)
        {
            result = null;
            SendBlocker = dispatched
                ? UiErrorMessage.Describe(ex)
                : DescribeUndeliveredOpenCodePrompt(entry, ex);
            if (turn is not null) turn.Status = dispatched ? "Ambiguous" : "Failed";
        }
        finally
        {
            _turnInProgress = false;
            RelayCommand.RaiseCanExecuteChanged();
            result ??= new TurnResult { SessionId = entry.NativeSessionId, OutputText = string.Empty,
                Status = TurnResult.FailedStatus, IsDeliveryUncertain = dispatched };
            try
            {
                if (result.IsDeliveryUncertain)
                {
                    _retainedWriterLock = token;
                    IsSessionConfirmed = false;
                    SessionStatusDisplay = "Ambiguous";
                    SendBlocker = OpenCodeTurnNotice.Uncertain(entry.Route.Id, entry.Route.NativeModelId, entry.NativeSessionId);
                }
                // Keep durable and physical checkout ownership until the outcome commits.
                // A failed commit must leave a lock that recovery can reconcile.
                await _executionJournal.CompleteAsync(entry, result);
                if (!result.IsDeliveryUncertain && token is not null)
                    await token.ReleaseAsync("OpenCode durable terminal or pre-dispatch refusal committed");
            }
            catch (Exception)
            {
                _retainedWriterLock = token?.IsHeld == true ? token : null;
                IsSessionConfirmed = false;
                SessionStatusDisplay = "Ambiguous";
                SendBlocker = OpenCodeTurnNotice.JournalCompletionFailed(entry.Route.Id, entry.Route.NativeModelId, entry.NativeSessionId);
            }
            OnPropertyChanged(nameof(IsWriterLockRetained));
            _statusBar?.UpdateSessionState(null, null, IsSessionConfirmed ? "Confirmed" : "Unconfirmed",
                IsSessionConfirmed ? result.Status : "Ambiguous");
        }
    }

    private async Task<bool> IsSessionProcessCurrentAsync(IOpenCodeServerInstance process, long generation,
        CancellationToken cancellationToken = default)
    {
        if (_serverConnection is null || _sessionRoute is null || !ReferenceEquals(_sessionProcess, process)
            || !process.IsAlive || generation <= 0 || process.ProcessGeneration != generation) return false;
        return await IsProcessCurrentAsync(process, generation, _sessionRoute.ProviderProfileId, cancellationToken);
    }

    private async Task<bool> IsProcessCurrentAsync(IOpenCodeServerInstance process, long generation,
        string profileId, CancellationToken cancellationToken = default)
    {
        _instanceGuard?.EnsureSupervisorPermitted();
        if (_serverConnection is null || !process.IsAlive || generation <= 0
            || process.ProcessGeneration != generation) return false;
        var current = await _serverConnection.GetInstanceAsync(profileId, cancellationToken);
        _instanceGuard?.EnsureSupervisorPermitted();
        return ReferenceEquals(current, process) && process.IsAlive && process.ProcessGeneration == generation;
    }

    private static string DescribeUndeliveredOpenCodePrompt(OpenCodeJournalEntry entry, Exception exception)
    {
        var notice = OpenCodeTurnNotice.PromptNotDelivered(entry.Route.Id, entry.Route.NativeModelId);
        return exception is ProjectLockConflictException
            ? UiErrorMessage.Describe(exception) + " " + notice
            : notice;
    }

    /// <summary>
    /// Consults the sticky-route gate for the current binding. Returns <c>null</c> when the turn may be
    /// sent, or the reason it is refused. Without a gate (or without a binding) the turn is unaffected,
    /// so a graph that does not carry a Health Center keeps working exactly as before.
    /// </summary>
    private async Task<string?> IsStickyRouteSendableAsync()
    {
        var binding = CurrentBinding;

        if (_stickyRouteGate is null || binding is null)
        {
            return null;
        }

        var decision = await _stickyRouteGate
            .EvaluateAsync(binding.AccountId, binding.ModelId)
            .ConfigureAwait(true);

        return decision.IsBlocked ? decision.Explanation : null;
    }

    private async Task CancelTurnAsync()
    {
        if (_sessionLifecycleService == null || !_turnInProgress) return;

        try
        {
            var turn = ConversationTurns.LastOrDefault();
            var confirmed = await _sessionLifecycleService.CancelTurnAsync(NativeSessionId);
            if (confirmed && turn is not null)
            {
                if (turn.Status == "Running")
                {
                    turn.Status = TurnResult.CancelledStatus;
                    _statusBar?.UpdateSessionState(null, null, null, "Canceled");
                }
            }
        }
        catch (Exception)
        {
            // Ignore
        }
    }

    private void PrepareNewSession()
    {
        if (!CanPrepareNewSession) return;
        // This only retires the UI's confirmed idle selection. It never stops a process, releases
        // ownership, or treats an ambiguous execution as completed.
        IsSessionConfirmed = false;
        IsProcessStarted = false;
        CurrentBinding = null;
        LocalSessionId = "Not reported";
        NativeSessionId = "Not reported";
        _sessionRoute = null;
        _sessionProjectId = null;
        _sessionDirectory = null;
        _sessionProcess = null;
        _sessionProcessGeneration = 0;
        AncestryDisplay = string.Empty;
        RequiresReplacementSession = false;
        SessionStatusDisplay = "Нет";
        SendBlocker = "Выберите маршрут и создайте новую сессию для текущего проекта.";
        ConversationTurns.Add(new WorkspaceTurnViewModel
            { Role = "System", PromptText = "Выбор новой сессии. Предыдущий диалог сохранён.", Status = "Completed" });
        _statusBar?.UpdateSessionState(LocalSessionId, NativeSessionId, "Unconfirmed", "Idle",
            "Not reported", "Not reported");
    }

    private async Task ResetSessionAsync()
    {
        if (!CanReset || _sessionLifecycleService == null || CurrentBinding == null || _executionJournal is null || _sessionRoute is null) return;
        IsBusy = true;
        var resetStarted = false;
        try
        {
            _instanceGuard?.EnsureSupervisorPermitted();
            var project = _openedProjectResolver is not null
                ? await _openedProjectResolver.ResolveAsync(ProjectId)
                : _projectRepository is not null && !string.IsNullOrWhiteSpace(ProjectId)
                    ? await _projectRepository.GetByIdAsync(ProjectId) : null;
            if (project is null || project.Id != _sessionProjectId
                || !string.Equals(ProjectLock.CanonicalizeRoot(project.RootPath), _sessionDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                RequiresReplacementSession = true;
                SendBlocker = "Открытый проект изменился. Выберите «Новая сессия» и создайте сессию для текущего проекта.";
                return;
            }
            if (!(await _executionJournal.ListRoutesAsync()).Contains(_sessionRoute))
            {
                RequiresReplacementSession = true;
                SendBlocker = "Маршрут сессии больше недоступен. Выберите «Новая сессия» и обновите маршруты.";
                return;
            }
            // GetInstanceAsync keeps the backend's ownership barrier: a dead/unconfirmed server
            // cannot be replaced by this UI operation.
            var process = _serverConnection is null ? null
                : await _serverConnection.GetInstanceAsync(_sessionRoute.ProviderProfileId);
            var generation = process?.ProcessGeneration ?? 0;
            if (process is null || !await IsProcessCurrentAsync(process, generation, _sessionRoute.ProviderProfileId))
                throw new InvalidOperationException("The OpenCode process is not confirmed.");
            var request = new OpenCodeCreateSessionRequest
            {
                Model = _sessionRoute.NativeModelId,
                Directory = _sessionDirectory,
                ProviderProfileId = string.IsNullOrWhiteSpace(CurrentBinding.ProviderProfileId) ? null : CurrentBinding.ProviderProfileId
            };
            resetStarted = true;
            var response = await _sessionLifecycleService.ResetSessionAsync(NativeSessionId, request);
            if (!await IsProcessCurrentAsync(process, generation, _sessionRoute.ProviderProfileId))
                throw new InvalidOperationException("The OpenCode process changed during reset.");
            var localId = await _executionJournal.ConfirmSessionAsync(_sessionProjectId!, _sessionDirectory!, response.Id, _sessionRoute);
            var ancestry = _sessionLifecycleService.GetAncestry(response.Id);
            if (!await IsProcessCurrentAsync(process, generation, _sessionRoute.ProviderProfileId))
                throw new InvalidOperationException("The OpenCode process changed during confirmation.");

            // Publish the new native identity together with its confirmed process generation.
            CurrentBinding = new SessionBinding(BackendType.OpenCode, CurrentBinding.ProviderProfileId, CurrentBinding.AccountId, CurrentBinding.ModelId, null, null, null);
            _sessionProcess = process;
            _sessionProcessGeneration = generation;
            LocalSessionId = localId;
            NativeSessionId = response.Id;
            AncestryDisplay = string.Join(" <- ", ancestry);
            IsSessionConfirmed = true;
            RequiresReplacementSession = false;
            SendBlocker = string.Empty;
            SessionStatusDisplay = "Confirmed";
            ProcessStateDisplay = "Running";

            var resetTurn = new WorkspaceTurnViewModel { Role = "System", PromptText = "Session Reset", Status = "Completed" };
            ConversationTurns.Add(resetTurn);
            _statusBar?.UpdateSessionState(LocalSessionId, NativeSessionId, "Confirmed", "Idle");
        }
        catch (Exception)
        {
            if (resetStarted) IsSessionConfirmed = false;
            SendBlocker = resetStarted
                ? OpenCodeTurnNotice.ResetUncertain(_sessionRoute.Id, _sessionRoute.NativeModelId, NativeSessionId)
                : OpenCodeTurnNotice.ResetNotSent(_sessionRoute.Id, _sessionRoute.NativeModelId, NativeSessionId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunDiagnosticFallbackAsync()
    {
        var diagnosticTurn = new WorkspaceTurnViewModel { Role = "System", PromptText = "DiagnosticCliFallback run", Status = "Completed", ResponseText = "Diagnostic result (DiagnosticCliFallback)" };
        ConversationTurns.Add(diagnosticTurn);
        await Task.CompletedTask;
    }

    public void HandleMalformedEvent()
    {
        // Safe to ignore or log
    }

    public void HandleServerReconnect()
    {
        if (IsProcessStarted && !IsSessionConfirmed)
        {
            ProcessStateDisplay = "Running";
            SessionStatusDisplay = "Нет";
        }
    }

    public void LoadTimeline(IReadOnlyList<ObservableRunProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(projections);

        _timeline = _timelineService.BuildTimeline(projections);

        TimelineItems.Clear();

        foreach (var item in _timeline.Items)
        {
            TimelineItems.Add(new ActivityTimelineItemViewModel(item));
        }

        RebuildStages();

        HasTimeline = !_timeline.IsEmpty;
        HasStages = !_timeline.IsEmpty;
        HasSyntheticEvidence = _timeline.HasSyntheticItems;
        EvidenceNotice = HasSyntheticEvidence ? SyntheticEvidenceNotice : string.Empty;
        RunStateDisplay = BuildRunStateDisplay();

        OnPropertyChanged(nameof(ActiveRoleDisplay));
    }

    private void RebuildStages()
    {
        Stages.Clear();

        var stageIndexes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in _timeline.Items)
        {
            var stage = new StageViewModel(item.DisplayLabel, item.Role, item.State, item.IsActive);

            if (stageIndexes.TryGetValue(item.DisplayLabel, out var existingIndex))
            {
                Stages[existingIndex] = stage;
            }
            else
            {
                stageIndexes[item.DisplayLabel] = Stages.Count;
                Stages.Add(stage);
            }
        }
    }

    private string BuildRunStateDisplay()
    {
        if (_timeline.IsEmpty)
        {
            return "Запуск не наблюдался";
        }

        if (_timeline.CurrentItem is { } currentItem)
        {
            return $"Active: {currentItem.DisplayLabel} ({currentItem.State})";
        }

        var lastItem = _timeline.Items[^1];

        return $"Last: {lastItem.DisplayLabel} ({lastItem.State})";
    }
}
