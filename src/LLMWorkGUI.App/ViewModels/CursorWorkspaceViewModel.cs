using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Projects;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Cursor ACP panel of the Workspace screen. It drives the native Cursor backend through
/// <see cref="ICursorAcpSessionLifecycleService"/> and shows only observed evidence: unknown fields
/// stay <c>Not reported</c>, unsupported capabilities are disabled instead of being faked, and a
/// permission request always waits for an explicit user decision (ТЗ §6.9, ADR-0003 §4.2, §7.2).
/// </summary>
public sealed partial class CursorWorkspaceViewModel : ObservableObject, IDisposable
{
    private int _disposed;
    private bool _stopUnconfirmed;
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public const string NotReported = "Not reported";

    public const string BackendStoppedState = "Stopped";

    public const string NoSessionState = "None";

    private readonly ICursorAcpSessionLifecycleService? _lifecycle;
    private readonly ICursorAcpModePolicy? _modePolicy;
    private readonly ICheckoutLockService? _checkoutLockService;
    private readonly LLMWorkGUI.Application.Security.IDataClassificationGate? _dataClassificationGate;
    private readonly IProjectRepository? _projectRepository;
    private readonly OpenedProjectResolver? _openedProjectResolver;

    private string _backendStateDisplay = BackendStoppedState;
    private bool _isBackendReady;
    private string _sessionStatusDisplay = NoSessionState;
    private string _nativeSessionId = NotReported;
    private string _protocolVersionDisplay = NotReported;
    private string _blocker = string.Empty;
    private string _guidance = string.Empty;
    private string _promptInput = string.Empty;
    private string _turnStateDisplay = NotReported;
    private string _lastStopReason = NotReported;
    private bool _isBusy;
    private bool _isWriterLockRetained;
    private bool _isAwaitingPermission;
    private string _pendingPermissionDescription = string.Empty;
    private string _pendingPermissionId = string.Empty;
    private string _selectedModeId = "ask";
    private CapabilityState _selectedModeState = CapabilityState.Unknown;
    private CursorAcpModeAccess _selectedModeAccess = CursorAcpModeAccess.Unknown;
    private IReadOnlyList<string>? _sessionModeIds;
    private string? _sessionRootPath;
    private string? _sessionProjectId;
    private bool _turnInProgress;
    private readonly Queue<CursorAcpStreamEvent.PermissionRequest> _pendingPermissions = new();
    private bool _canAllowPendingPermission;
    private bool _isReplyingPermission;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private DataClassification _projectDataClassification = DataClassification.PrivateSource;

    public CursorWorkspaceViewModel(
        ICursorAcpSessionLifecycleService? lifecycle = null,
        ICursorAcpModePolicy? modePolicy = null,
        ICheckoutLockService? checkoutLockService = null,
        LLMWorkGUI.Application.Security.IDataClassificationGate? dataClassificationGate = null,
        IProjectRepository? projectRepository = null,
        OpenedProjectResolver? openedProjectResolver = null,
        ICursorAcpExecutionJournal? executionJournal = null)
    {
        _lifecycle = lifecycle;
        _modePolicy = modePolicy;
        _checkoutLockService = checkoutLockService;
        _dataClassificationGate = dataClassificationGate;
        _projectRepository = projectRepository;
        _openedProjectResolver = openedProjectResolver;
        _executionJournal = executionJournal;
        RefreshRoutesCommand = new RelayCommand(() => _ = RefreshRoutesAsync(), () => !IsBusy);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        _dispatcher = dispatcher;

        StartBackendCommand = new RelayCommand(() => _ = StartBackendAsync(), () => CanStartBackend);
        StopBackendCommand = new RelayCommand(() => _ = StopBackendAsync(), () => CanStopBackend);
        CreateSessionCommand = new RelayCommand(() => _ = CreateSessionAsync(), () => CanCreateSession);
        SendPromptCommand = new RelayCommand(() => _ = SendPromptAsync(), () => CanSendPrompt);
        CancelTurnCommand = new RelayCommand(() => _ = CancelTurnAsync(), () => CanCancelTurn);
        ResetSessionCommand = new RelayCommand(() => _ = ResetSessionAsync(), () => CanCreateSession);
        AllowOnceCommand = new RelayCommand(() => _ = ReplyPermissionAsync(allow: true), () => !IsDisposed && !_stopUnconfirmed && IsAwaitingPermission && _canAllowPendingPermission && !_isReplyingPermission);
        DenyPermissionCommand = new RelayCommand(() => _ = ReplyPermissionAsync(allow: false), () => !IsDisposed && IsAwaitingPermission && !_isReplyingPermission);

        if (_lifecycle is not null)
        {
            _lifecycle.StreamEventObserved += OnStreamEvent;
            _lifecycle.PermissionRequested += OnPermissionRequested;
            _lifecycle.TurnStateChanged += OnTurnStateChanged;
        }
    }

    /// <summary>True only when the backend is available in this build/configuration.</summary>
    public bool IsBackendAvailable => _lifecycle is not null;

    public string UnavailableNotice =>
        "Бэкенд Cursor ACP не настроен для этого окна. Установите Cursor Agent и включите " +
        "бэкенд для создания нативных сессий.";

    public ObservableCollection<CursorWorkspaceEventViewModel> StreamEvents { get; } = new();

    /// <summary>False until at least one normalized event has actually been observed.</summary>
    public bool HasStreamEvents => StreamEvents.Count > 0;

    public ObservableCollection<string> Ancestry { get; } = new();

    public ICommand StartBackendCommand { get; }

    public ICommand StopBackendCommand { get; }

    public ICommand CreateSessionCommand { get; }

    public ICommand SendPromptCommand { get; }

    public ICommand CancelTurnCommand { get; }

    public ICommand ResetSessionCommand { get; }

    public ICommand AllowOnceCommand { get; }

    public ICommand DenyPermissionCommand { get; }

    public string BackendStateDisplay
    {
        get => _backendStateDisplay;
        private set => SetProperty(ref _backendStateDisplay, value);
    }

    public bool IsBackendReady
    {
        get => _isBackendReady;
        private set
        {
            if (SetProperty(ref _isBackendReady, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SessionStatusDisplay
    {
        get => _sessionStatusDisplay;
        private set => SetProperty(ref _sessionStatusDisplay, value);
    }

    /// <summary>Native session id confirmed by the agent, or <c>Not reported</c>.</summary>
    public string NativeSessionId
    {
        get => _nativeSessionId;
        private set => SetProperty(ref _nativeSessionId, value);
    }

    /// <summary>Protocol version from the handshake evidence, or <c>Not reported</c>.</summary>
    public string ProtocolVersionDisplay
    {
        get => _protocolVersionDisplay;
        private set => SetProperty(ref _protocolVersionDisplay, value);
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

    /// <summary>Normative execution state of the active turn, or <c>Not reported</c> (ТЗ §6.7).</summary>
    public string TurnStateDisplay
    {
        get => _turnStateDisplay;
        private set => SetProperty(ref _turnStateDisplay, value);
    }

    public string LastStopReason
    {
        get => _lastStopReason;
        private set => SetProperty(ref _lastStopReason, value);
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

    /// <summary>
    /// True when the last turn ended without confirmed terminal evidence and the checkout writer
    /// lock is therefore still held pending reconciliation (ADR-0003 §10.4).
    /// </summary>
    public bool IsWriterLockRetained
    {
        get => _isWriterLockRetained;
        private set => SetProperty(ref _isWriterLockRetained, value);
    }

    public bool IsAwaitingPermission
    {
        get => _isAwaitingPermission;
        private set
        {
            if (SetProperty(ref _isAwaitingPermission, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PendingPermissionDescription
    {
        get => _pendingPermissionDescription;
        private set => SetProperty(ref _pendingPermissionDescription, value);
    }

    /// <summary>
    /// Every Cursor permission request is normalized to <c>UnknownHighRisk</c> until a per-kind
    /// fixture proves a narrower mapping, so the UI never implies a low-risk operation.
    /// </summary>
    public string PendingPermissionKindDisplay => NormalizedApprovalKind.UnknownHighRisk.ToString();

    /// <summary>Available mode identifiers; the selection is validated by the mode policy.</summary>
    public IReadOnlyList<string> AvailableModeIds { get; } = new[] { "ask", "plan", "agent" };

    /// <summary>
    /// Discovered capability state of the selected mode. It stays <see cref="CapabilityState.Unknown"/>
    /// until per-mode discovery actually proves support, and an Unknown mode can never be sent
    /// (ADR-0003 §5). Supported is derived only from the current session's validated native mode list.
    /// </summary>
    public CapabilityState SelectedModeState
    {
        get => _selectedModeState;
        set
        {
            if (SetProperty(ref _selectedModeState, value))
            {
                OnPropertyChanged(nameof(ModeRequiresWriterLock));
                OnPropertyChanged(nameof(SelectedModeStateDisplay));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Honest display of the discovered capability state of the selected mode.</summary>
    public string SelectedModeStateDisplay => SelectedModeState.ToString();

    /// <summary>
    /// Declared access of the selected mode. Unknown access is never treated as read-only, so the
    /// writer lock stays required until the access is actually observed.
    /// </summary>
    public CursorAcpModeAccess SelectedModeAccess
    {
        get => _selectedModeAccess;
        set
        {
            if (SetProperty(ref _selectedModeAccess, value))
            {
                OnPropertyChanged(nameof(ModeRequiresWriterLock));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Explicit project override. Null follows the currently opened workspace on each operation;
    /// the resolved session identity is kept separately and never turns into an override.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>
    /// Explicit provider profile override for the data classification gate. When unset, the panel's
    /// canonical <c>cursor</c> profile id is used.
    /// </summary>
    public string? ProviderProfileId { get; set; }

    /// <summary>Canonical checkout root the writer lock protects.</summary>
    public string? CanonicalRootPath { get; set; }

    public DataClassification ProjectDataClassification
    {
        get => _projectDataClassification;
        set => SetProperty(ref _projectDataClassification, value);
    }

    /// <summary>
    /// True when a lock-requiring mode can actually obtain the lock: the service, the project
    /// identity and the checkout root must all be present. Without them the turn is refused rather
    /// than sent without protection.
    /// </summary>
    public bool CanAcquireWriterLock =>
        _checkoutLockService is not null &&
        !string.IsNullOrWhiteSpace(ProjectId ?? _sessionProjectId) &&
        !string.IsNullOrWhiteSpace(CanonicalRootPath ?? _sessionRootPath);

    public string SelectedModeId
    {
        get => _selectedModeId;
        set
        {
            if (SetProperty(ref _selectedModeId, value))
            {
                RefreshDiscoveredMode();
                OnPropertyChanged(nameof(ModeRequiresWriterLock));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// True when the selected mode needs the checkout writer lock. Unknown modes always require it,
    /// so an unverified mode is never presented as read-only (ADR-0003 §5).
    /// </summary>
    public bool ModeRequiresWriterLock => EvaluateSelectedMode()?.RequiresWriterLock ?? true;

    public bool CanStartBackend => !IsDisposed && !_stopUnconfirmed && IsBackendAvailable && !IsBackendReady && !IsBusy;

    public bool CanStopBackend => !IsDisposed && IsBackendAvailable && (IsBackendReady || _stopUnconfirmed) && !IsBusy;

    public bool CanCreateSession => !IsDisposed && IsBackendReady && !IsBusy;

    public bool CanSendPrompt =>
        !IsDisposed && IsBackendReady &&
        !IsBusy &&
        !IsAwaitingPermission &&
        !string.IsNullOrWhiteSpace(PromptInput) &&
        HasConfirmedSession &&
        EvaluateSelectedMode() is { CanSend: true };

    public bool CanCancelTurn => !IsDisposed && (IsBackendReady || _stopUnconfirmed) && _turnInProgress && HasConfirmedSession;

    private bool HasConfirmedSession =>
        !string.Equals(NativeSessionId, NotReported, StringComparison.Ordinal);

    private void RefreshDiscoveredMode()
    {
        SelectedModeState = _sessionModeIds?.Contains(SelectedModeId, StringComparer.Ordinal) == true
            ? CapabilityState.Supported : CapabilityState.Unknown;
        // Native mode IDs/descriptions establish availability, not an access guarantee.
        SelectedModeAccess = CursorAcpModeAccess.Unknown;
    }

    private CursorAcpModeDecision? EvaluateSelectedMode()
    {
        if (_modePolicy is null)
        {
            return null;
        }

        try
        {
            var mode = _modePolicy.ParseModeId(SelectedModeId);

            // The policy decides fail-closed: the state and access below come from discovery and
            // default to Unknown, so an unverified mode can never be sent.
            return _modePolicy.Evaluate(mode, SelectedModeAccess, SelectedModeState);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task StartBackendAsync(string executionId = "workspace-cursor")
    {
        if (IsDisposed || _stopUnconfirmed || _lifecycle is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        ClearDiagnostics();
        var route = SelectedRoute;
        var sessionId = NativeSessionId;
        var startStarted = false;
        var callReturned = false;

        try
        {
            startStarted = true;
            var result = await _lifecycle.StartBackendAsync(executionId).ConfigureAwait(true);
            callReturned = true;
            if (IsDisposed) return;

            if (result.IsReady)
            {
                IsBackendReady = true;
                BackendStateDisplay = "Ready";
                ProtocolVersionDisplay = result.Handshake!.ProtocolVersion.ToString();
                return;
            }

            IsBackendReady = false;
            BackendStateDisplay = $"Degraded ({result.FailureKind})";
            Blocker = result.Blocker ?? string.Empty;
            Guidance = result.Guidance ?? string.Empty;
        }
        catch (Exception)
        {
            if (IsDisposed) return;
            IsBackendReady = false;
            BackendStateDisplay = "Запуск не подтверждён";
            var known = DescribeStopIdentity(route, sessionId);
            var sessionNotReturned = string.IsNullOrWhiteSpace(sessionId)
                || string.Equals(sessionId, NotReported, StringComparison.Ordinal);
            Blocker = !startStarted
                ? "Запуск Cursor не начинался." + known + " Сессия не запускалась."
                : callReturned
                    ? "Результат запуска Cursor получен." + known
                        + (sessionNotReturned ? " Сессия не возвращена." : string.Empty)
                        + " Повтор небезопасен."
                    : "Запуск Cursor не подтверждён." + known
                        + (sessionNotReturned ? " Сессия не возвращена." : string.Empty)
                        + " Запуск мог быть доставлен. Повтор небезопасен. Состояние здоровья не изменялось.";
        }
        finally
        {
            if (!IsDisposed) IsBusy = false;
        }
    }

    public async Task StopBackendAsync()
    {
        if (IsDisposed || _lifecycle is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        var route = SelectedRoute;
        var sessionId = NativeSessionId;
        var stopStarted = false;
        var callReturned = false;

        try
        {
            stopStarted = true;
            await _lifecycle.StopBackendAsync().ConfigureAwait(true);
            callReturned = true;
            if (IsDisposed) return;
            ClearDiagnostics();
            _stopUnconfirmed = false;
            IsBackendReady = false;
            BackendStateDisplay = BackendStoppedState;
            SessionStatusDisplay = NoSessionState;
            NativeSessionId = NotReported;
            _sessionRootPath = null;
            _sessionProjectId = null;
            _sessionModeIds = null;
            RefreshDiscoveredMode();
            ProtocolVersionDisplay = NotReported;
            TurnStateDisplay = NotReported;
            ResetPendingPermission();
        }
        catch (Exception)
        {
            if (IsDisposed) return;
            _stopUnconfirmed = true;
            IsBackendReady = false;
            BackendStateDisplay = "Остановка не подтверждена";
            var known = DescribeStopIdentity(route, sessionId);
            // Keep the last observed identity and permissions. UI failure is not native terminal evidence.
            Blocker = !stopStarted
                ? "Остановка Cursor не начиналась." + known + " Остановка не доставлялась."
                : callReturned
                    ? "Результат остановки Cursor получен." + known + " Повтор небезопасен."
                    : "Остановка Cursor не подтверждена." + known
                        + " Остановка могла быть доставлена. Повтор небезопасен. Состояние здоровья не изменялось.";
        }
        finally
        {
            if (!IsDisposed) IsBusy = false;
        }
    }

    private static string DescribeStopIdentity(CursorAcpStoredRoute? route, string sessionId)
    {
        var text = string.Empty;
        if (route is not null)
        {
            text += " Маршрут " + route.Id + " (Cursor, " + route.NativeModelId + ").";
        }

        if (!string.IsNullOrWhiteSpace(sessionId)
            && !string.Equals(sessionId, NotReported, StringComparison.Ordinal))
        {
            text += " Нативная сессия " + sessionId + ".";
        }

        return text;
    }

    public Task CreateSessionAsync(string? workingDirectory = null) =>
        OpenSessionAsync(workingDirectory, isReset: false);

    public Task ResetSessionAsync(string? workingDirectory = null) =>
        OpenSessionAsync(workingDirectory, isReset: true);

    private async Task OpenSessionAsync(string? workingDirectory, bool isReset)
    {
        if (IsDisposed || _lifecycle is null || !IsBackendReady || IsBusy)
        {
            return;
        }

        IsBusy = true;
        ClearDiagnostics();
        var route = SelectedRoute;
        var callStarted = false;
        var callReturned = false;
        string? returnedSession = null;

        try
        {
            // The command has no explicit directory: bind it to the opened project, never to the
            // application's process directory. Explicit protocol callers still get a send-time
            // comparison against the resolved project before classification or lock acquisition.
            var directory = workingDirectory;
            string? sessionProjectId = null;
            if (directory is null)
            {
                var project = _openedProjectResolver is not null
                    ? await _openedProjectResolver.ResolveAsync(ProjectId).ConfigureAwait(true)
                    : _projectRepository is not null && !string.IsNullOrWhiteSpace(ProjectId)
                        ? await _projectRepository.GetByIdAsync(ProjectId).ConfigureAwait(true)
                        : null;
                if (IsDisposed) return;
                if (project is null)
                {
                    Blocker = "Открытый проект не определён. Выберите проект перед созданием сессии Cursor.";
                    return;
                }
                directory = project.RootPath;
                sessionProjectId = project.Id;
            }

            if (!Path.IsPathFullyQualified(directory))
            {
                Blocker = "Рабочий каталог Cursor должен быть абсолютным путём.";
                return;
            }
            var sessionRoot = ProjectLock.CanonicalizeRoot(directory);
            var request = new CursorAcpNewSessionRequest
            {
                WorkingDirectory = sessionRoot
            };

            // Once native creation is attempted, a failed/ambiguous reset must not keep the old
            // session eligible for sending (even if a caller later changes mode capability fields).
            NativeSessionId = NotReported;
            _sessionRootPath = null;
            _sessionProjectId = null;
            _sessionModeIds = null;
            RefreshDiscoveredMode();
            ResetPendingPermission();
            callStarted = true;
            var result = isReset
                ? await _lifecycle.ResetSessionAsync(request).ConfigureAwait(true)
                : await _lifecycle.CreateSessionAsync(request).ConfigureAwait(true);
            callReturned = true;
            returnedSession = result.Evidence?.SessionId;

            if (IsDisposed) return;

            if (result.IsReady && result.Evidence is not null)
            {
                SessionStatusDisplay = "Confirmed";
                NativeSessionId = result.Evidence.SessionId;
                _sessionRootPath = sessionRoot;
                _sessionProjectId = sessionProjectId;
                _sessionModeIds = result.Evidence.AvailableModeIds?.ToArray();
                RefreshDiscoveredMode();
                SyncAncestry();
                return;
            }

            SessionStatusDisplay = $"Degraded ({result.FailureKind})";
            _sessionModeIds = null;
            RefreshDiscoveredMode();
            Blocker = result.Blocker ?? string.Empty;
            Guidance = result.Guidance ?? string.Empty;
        }
        catch (Exception)
        {
            var known = DescribeStopIdentity(route, NotReported);
            SessionStatusDisplay = "Degraded";
            if (!callStarted)
            {
                Blocker = isReset
                    ? "Сброс сессии Cursor не начинался." + known + " Сессия не сбрасывалась."
                    : "Создание сессии Cursor не начиналось." + known + " Сессия не создавалась.";
            }
            else if (!callReturned)
            {
                Blocker = isReset
                    ? "Сброс сессии Cursor не подтверждён." + known
                        + " Сессия не возвращена. Сброс мог быть доставлен. Повтор небезопасен. Состояние здоровья не изменялось."
                    : "Создание сессии Cursor не подтверждено." + known
                        + " Сессия не возвращена. Создание могло быть доставлено. Повтор небезопасен. Состояние здоровья не изменялось.";
            }
            else
            {
                var session = string.IsNullOrWhiteSpace(returnedSession)
                    ? " Сессия не возвращена."
                    : " Сессия " + returnedSession + ".";
                Blocker = isReset
                    ? "Результат сброса сессии Cursor получен." + known + session + " Повтор небезопасен."
                    : "Результат создания сессии Cursor получен." + known + session + " Повтор небезопасен.";
            }
        }
        finally
        {
            if (!IsDisposed) IsBusy = false;
        }
    }

    public async Task SendPromptAsync()
    {
        if (_lifecycle is null || !CanSendPrompt)
        {
            return;
        }

        // Reserve the panel before the first asynchronous project/profile lookup. A second send,
        // reset or stop must not race preparation of a turn for the current native directory.
        IsBusy = true;
        try
        {
            await SendPromptCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            if (!IsDisposed) IsBusy = false;
        }
    }

    private async Task SendPromptCoreAsync()
    {
        var prompt = PromptInput;
        var projectId = ProjectId;
        var configuredRoot = CanonicalRootPath;
        var selectedRoute = SelectedRoute;
        var effectiveProfileId = selectedRoute?.ProviderProfileId ?? ProviderProfileId ?? "cursor";
        var mode = EvaluateSelectedMode();

        if (mode is null || !mode.CanSend)
        {
            Blocker = mode?.Blocker ??
                "The selected Cursor ACP mode cannot be sent; the turn was refused before dispatch.";
            return;
        }

        if (_dataClassificationGate is null)
        {
            Blocker = "Data classification gate is unavailable; prompt dispatch is blocked (fail-closed, ТЗ §6.5).";
            return;
        }

        // The project and the protected checkout root are resolved before the turn is allowed to reach
        // the lock, so a lock-requiring mode is refused when the project context cannot be established
        // instead of being offered an unprotected send (ТЗ §6.5, §6.12).
        Project? project;

        if (_openedProjectResolver is not null)
        {
            project = await _openedProjectResolver.ResolveAsync(projectId).ConfigureAwait(true);
        }
        else if (_projectRepository is not null && !string.IsNullOrWhiteSpace(projectId))
        {
            project = await _projectRepository.GetByIdAsync(projectId).ConfigureAwait(true);
        }
        else
        {
            project = null;
        }

        if (IsDisposed) return;

        if (project is null)
        {
            Blocker = string.IsNullOrWhiteSpace(ProjectId)
                ? "Project is not configured or repository is unavailable; prompt dispatch is blocked (fail-closed, ТЗ §6.5)."
                : $"Project '{ProjectId}' not found; prompt dispatch is blocked (fail-closed, ТЗ §6.5).";
            return;
        }

        ProjectDataClassification = project.DataClassification;

        string projectRoot;
        string? canonicalConfiguredRoot;
        try
        {
            if (!Path.IsPathFullyQualified(project.RootPath) ||
                (!string.IsNullOrWhiteSpace(configuredRoot) && !Path.IsPathFullyQualified(configuredRoot)))
            {
                Blocker = "Рабочий каталог проекта должен быть абсолютным путём.";
                return;
            }
            projectRoot = ProjectLock.CanonicalizeRoot(project.RootPath);
            canonicalConfiguredRoot = string.IsNullOrWhiteSpace(configuredRoot)
                ? null : ProjectLock.CanonicalizeRoot(configuredRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Blocker = "Не удалось проверить рабочий каталог проекта. Исправьте путь в настройках проекта.";
            return;
        }

        if ((_sessionProjectId is not null && _sessionProjectId != project.Id)
            || _sessionRootPath is null || !string.Equals(
            _sessionRootPath,
            projectRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            Blocker = "Рабочий каталог сессии Cursor не совпадает с открытым проектом. Создайте новую сессию.";
            return;
        }

        if (canonicalConfiguredRoot is not null && !string.Equals(
            canonicalConfiguredRoot,
            projectRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            Blocker = "Canonical checkout root does not match resolved project root; prompt dispatch is blocked (fail-closed, ТЗ §6.5).";
            return;
        }

        // Resolve an explicit-directory session only after its root matches the current project.
        // Keep this observation out of the optional project/root overrides used on the next turn.
        _sessionProjectId ??= project.Id;

        var gateDecision = await _dataClassificationGate
            .EvaluateAsync(project.DataClassification, effectiveProfileId, isManualOnly: false)
            .ConfigureAwait(true);

        if (IsDisposed) return;

        if (!gateDecision.IsAllowed)
        {
            Blocker = gateDecision.Explanation ?? "Data classification gate blocked prompt dispatch.";
            return;
        }

        ClearDiagnostics();

        if (_executionJournal is null || selectedRoute is null)
        {
            Blocker = "Выберите сохранённый маршрут Cursor. Без маршрута и журнала выполнения отправка недоступна.";
            return;
        }
        var processSession = _lifecycle?.Current?.Session;
        if (processSession is null || !processSession.IsRunning || processSession.ProcessGeneration <= 0)
        {
            Blocker = "Управляемый процесс Cursor для маршрута " + selectedRoute.Id
                + " (" + selectedRoute.NativeModelId + ") не подтверждён. Запрос не доставлялся. Перезапустите backend.";
            return;
        }
        var processGeneration = processSession.ProcessGeneration;
        var promptRequest = CursorAcpPromptRequest.Create(NativeSessionId, prompt);
        CursorAcpJournalEntry entry;
        try
        {
            entry = await _executionJournal.BeginAsync(project.Id, projectRoot, NativeSessionId,
                selectedRoute, mode.ModeId, promptRequest.ClientRequestId, promptRequest.PromptHash).ConfigureAwait(true);
        }
        catch (Exception)
        {
            Blocker = "Регистрация выполнения Cursor не выполнена. Маршрут " + selectedRoute.Id
                + " (Cursor, " + selectedRoute.NativeModelId + "). Запрос не доставлялся.";
            return;
        }
        promptRequest = promptRequest with { Model = entry.Route.NativeModelId, RequireModelAcknowledgement = true };
        ICheckoutLockToken? lockToken = null;
        CursorAcpTurnResult? turnResult = null;
        var dispatched = false;

        try
        {
            if (IsDisposed) return;
            if (mode.RequiresWriterLock)
            {
                lockToken = await TryAcquireWriterLockAsync(project.Id, projectRoot, entry.ExecutionId, processGeneration).ConfigureAwait(true);

                if (lockToken is null)
                {
                    // Without the lock the turn is refused rather than sent unprotected.
                    return;
                }
            }

            if (IsDisposed) return;

            if (PromptInput == prompt) PromptInput = string.Empty;
            bool DispatchOwnerIsCurrent() => !IsDisposed
                && ReferenceEquals(_lifecycle?.Current?.Session, processSession)
                && processSession.IsRunning && processSession.ProcessGeneration == processGeneration
                && (lockToken is null || lockToken.IsHeld && lockToken.ExecutionId == entry.ExecutionId
                    && lockToken.ProjectId == project.Id
                    && string.Equals(lockToken.CanonicalRootPath, projectRoot, StringComparison.OrdinalIgnoreCase));
            promptRequest = promptRequest with { AuthorizeDispatchAsync = async (actual, token) =>
            {
                if (!DispatchOwnerIsCurrent()) return false;
                var authorized = await _executionJournal.AuthorizePromptDispatchAsync(
                    entry, project.Id, projectRoot, actual, lockToken?.LockId, processGeneration, token).ConfigureAwait(false);
                return authorized && DispatchOwnerIsCurrent();
            } };
            _turnInProgress = true;
            RelayCommand.RaiseCanExecuteChanged();
            dispatched = true;
            var result = await _lifecycle!
                .ExecuteTurnAsync(new CursorAcpTurnRequest
                {
                    Prompt = promptRequest,
                    Mode = mode,
                    LockToken = lockToken
                })
                .ConfigureAwait(true);
            turnResult = result;

            if (IsDisposed) return;

            TurnStateDisplay = result.Outcome.ToString();
            LastStopReason = result.StopReason ?? NotReported;
            IsWriterLockRetained = result.LockRetained;

            if (result.LockRetained)
            {
                Guidance =
                    "The checkout writer lock is retained because the turn outcome is unconfirmed; " +
                    "reconciliation must resolve it before the checkout is released (ADR-0003 §10.4).";
            }

            if (result.Blocker is { Length: > 0 } blocker)
            {
                Blocker = blocker;
            }
        }
        catch (Exception)
        {
            TurnStateDisplay = dispatched
                ? CursorAcpTurnOutcome.Ambiguous.ToString()
                : CursorAcpTurnOutcome.Rejected.ToString();
            IsWriterLockRetained = dispatched && lockToken?.IsHeld == true;
            Blocker = dispatched
                ? CursorTurnNotice.Uncertain(entry.Route.Id, entry.Route.NativeModelId, entry.NativeSessionId)
                : "Отправка Cursor не начата. Модель запрос не получала.";
        }
        finally
        {
            _turnInProgress = false;
            RelayCommand.RaiseCanExecuteChanged();
            if (!IsDisposed) ResetPendingPermission();
            try
            {
                await _executionJournal.CompleteAsync(entry, turnResult ?? new CursorAcpTurnResult
                {
                    SessionId = entry.NativeSessionId, ClientRequestId = entry.ClientRequestId,
                    Outcome = dispatched ? CursorAcpTurnOutcome.Ambiguous : CursorAcpTurnOutcome.Rejected,
                    // A reservation that never reached the lifecycle cannot own a native turn.
                    // Persist its definite rejection before releasing the exact local lease.
                    LockRetained = dispatched && lockToken?.IsHeld == true
                }).ConfigureAwait(true);
                if (!dispatched && lockToken?.IsHeld == true)
                    await lockToken.ReleaseAsync("Cursor prompt refused before lifecycle dispatch").ConfigureAwait(true);
                if (!dispatched) IsWriterLockRetained = lockToken?.IsHeld == true;
            }
            catch (Exception)
            {
                // A failed durable rejection must retain the original admission and lease. Do not
                // overwrite it with an Ambiguous completion or fabricate a successful release.
                if (!dispatched) IsWriterLockRetained = lockToken?.IsHeld == true;
                Blocker = dispatched
                    ? CursorTurnNotice.JournalCompletionFailed(entry.Route.Id, entry.Route.NativeModelId, entry.NativeSessionId)
                    : "Завершение записи выполнения Cursor не выполнено. Маршрут " + entry.Route.Id
                        + " (Cursor, " + entry.Route.NativeModelId + "). Запрос не доставлялся.";
            }
        }
    }

    /// <summary>
    /// Acquires the checkout writer lock for the next turn. The supervisor owns the release, so this
    /// method never disposes the token itself.
    /// </summary>
    private async Task<ICheckoutLockToken?> TryAcquireWriterLockAsync(string projectId, string rootPath, string executionId,
        long processGeneration)
    {
        if (_checkoutLockService is null)
        {
            Blocker =
                "The selected Cursor ACP mode requires the checkout writer lock, but no project " +
                "identity and checkout root are configured for this panel; the turn was refused " +
                "before dispatch.";

            return null;
        }

        try
        {
            return await _checkoutLockService
                .AcquireWriterLockAsync(
                    projectId,
                    rootPath,
                    executionId,
                    processGeneration)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var route = SelectedRoute;
            Blocker = "Захват writer lock Cursor не выполнен."
                + (route is null
                    ? string.Empty
                    : " Маршрут " + route.Id + " (Cursor, " + route.NativeModelId + ").")
                + " Запрос не доставлялся.";

            return null;
        }
    }

    public async Task CancelTurnAsync()
    {
        if (IsDisposed || _lifecycle is null || (!IsBackendReady && !_stopUnconfirmed) || (IsBusy && !_turnInProgress))
        {
            return;
        }

        var result = await _lifecycle.CancelTurnAsync().ConfigureAwait(true);

        if (result.IsSent)
        {
            // The ack is not a terminal confirmation: the turn awaits terminal evidence.
            TurnStateDisplay = CursorAcpTurnState.Cancelling.ToString();
            return;
        }

        Blocker = result.Blocker ?? string.Empty;
        Guidance = result.Guidance ?? string.Empty;
    }

    public async Task ReplyPermissionAsync(bool allow)
    {
        if (IsDisposed || _lifecycle is null || !IsAwaitingPermission || _isReplyingPermission || (allow && (_stopUnconfirmed || !_canAllowPendingPermission)))
        {
            return;
        }

        var permissionId = _pendingPermissionId;
        _isReplyingPermission = true;
        RelayCommand.RaiseCanExecuteChanged();

        try
        {
            var result = await _lifecycle
                .ReplyPermissionAsync(new CursorAcpPermissionReplyRequest
                {
                    PermissionId = permissionId,
                    SessionId = HasConfirmedSession ? NativeSessionId : null,
                    Decision = allow
                        ? CursorAcpPermissionDecision.AllowOnce
                        : CursorAcpPermissionDecision.Deny
                })
                .ConfigureAwait(true);

            if (!result.IsSent)
            {
                Blocker = result.Blocker ?? string.Empty;
            }
            else if (_pendingPermissionId == permissionId)
            {
                if (_pendingPermissions.Count > 0) _pendingPermissions.Dequeue();
                ShowNextPermission();
            }
        }
        finally
        {
            _isReplyingPermission = false;
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnStreamEvent(object? sender, CursorAcpStreamEvent streamEvent) => OnUiThread(() =>
    {
        if (streamEvent.SessionId is not null && streamEvent.SessionId != NativeSessionId) return;
        StreamEvents.Add(CursorWorkspaceEventViewModel.FromStreamEvent(streamEvent));
        OnPropertyChanged(nameof(HasStreamEvents));
    });

    private void OnPermissionRequested(object? sender, CursorAcpStreamEvent.PermissionRequest request) => OnUiThread(() =>
    {
        if (request.SessionId != NativeSessionId || _pendingPermissions.Any(p => p.RequestId == request.RequestId)) return;
        _pendingPermissions.Enqueue(request);
        ShowNextPermission();
    });

    private void ShowNextPermission()
    {
        var pending = _pendingPermissions.TryPeek(out var request) ? request : null;
        _pendingPermissionId = pending?.RequestId ?? string.Empty;
        PendingPermissionDescription = pending?.Description ?? string.Empty;
        _canAllowPendingPermission = pending?.CanAllowOnce ?? false;
        IsAwaitingPermission = pending is not null;
        if (pending is not null) TurnStateDisplay = CursorAcpTurnState.WaitingApproval.ToString();
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void OnTurnStateChanged(object? sender, CursorAcpTurnStateChangedEventArgs args) =>
        OnUiThread(() => { if (args.SessionId == NativeSessionId) TurnStateDisplay = args.State.ToString(); });

    private void OnUiThread(Action action)
    {
        if (IsDisposed) return;
        void Publish() { if (!IsDisposed) action(); }
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke((Action)Publish);
        }
        else Publish();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_lifecycle is not null)
        {
            _lifecycle.StreamEventObserved -= OnStreamEvent;
            _lifecycle.PermissionRequested -= OnPermissionRequested;
            _lifecycle.TurnStateChanged -= OnTurnStateChanged;
        }
        // Host/backend lifetime owns stop, native uncertainty and physical lock/journal completion.
        // Disposal retires this panel and its observers; admitted preparation settles its own
        // undispatched reservation after it returns. It does not assert native termination.
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void SyncAncestry()
    {
        if (_lifecycle is null)
        {
            return;
        }

        Ancestry.Clear();

        foreach (var sessionId in _lifecycle.Ancestry)
        {
            Ancestry.Add(sessionId);
        }
    }

    private void ResetPendingPermission()
    {
        _pendingPermissions.Clear();
        _canAllowPendingPermission = false;
        _pendingPermissionId = string.Empty;
        PendingPermissionDescription = string.Empty;
        IsAwaitingPermission = false;
    }

    private void ClearDiagnostics()
    {
        Blocker = string.Empty;
        Guidance = string.Empty;
    }
}

/// <summary>Display projection of one normalized Cursor ACP stream event.</summary>
public sealed class CursorWorkspaceEventViewModel
{
    private CursorWorkspaceEventViewModel(string kind, string sessionId, string text)
    {
        Kind = kind;
        SessionId = sessionId;
        Text = text;
    }

    public string Kind { get; }

    /// <summary>Native session of the event, or <c>Not reported</c> when the agent omitted it.</summary>
    public string SessionId { get; }

    public string Text { get; }

    public static CursorWorkspaceEventViewModel FromStreamEvent(CursorAcpStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        var sessionId = string.IsNullOrWhiteSpace(streamEvent.SessionId)
            ? CursorWorkspaceViewModel.NotReported
            : streamEvent.SessionId!;

        return streamEvent switch
        {
            CursorAcpStreamEvent.TextChunk chunk =>
                new CursorWorkspaceEventViewModel("Text", sessionId, chunk.Text),
            CursorAcpStreamEvent.Thought thought =>
                new CursorWorkspaceEventViewModel("Thought", sessionId, thought.Text),
            CursorAcpStreamEvent.ToolCall toolCall =>
                new CursorWorkspaceEventViewModel("ToolCall", sessionId, toolCall.ToolName),
            CursorAcpStreamEvent.StatusUpdate status =>
                new CursorWorkspaceEventViewModel("Status", sessionId, status.Phase),
            CursorAcpStreamEvent.PermissionRequest permission =>
                new CursorWorkspaceEventViewModel("PermissionRequest", sessionId, permission.Description),
            _ => new CursorWorkspaceEventViewModel("Unknown", sessionId, CursorWorkspaceViewModel.NotReported)
        };
    }
}
