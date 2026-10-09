using System.Collections.ObjectModel;
using System.Windows.Input;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Health Center screen. It renders only observed health evidence from
/// <see cref="IHealthCenterService"/>: a forced route is never shown as recovered, a route awaiting a
/// probe is never shown as healthy, and the recovery audit is read-only (ТЗ §7.2, ROADMAP Phase 7).
/// </summary>
public sealed class HealthCenterViewModel : ScreenViewModel
{
    /// <summary>Shown when the health service is not configured for this window.</summary>
    public const string UnavailableIndicator = "Not reported";

    private readonly IHealthCenterService? _healthCenter;
    private readonly IHealthProbeService? _probeService;
    private readonly IImpactedSessionService? _impactedSessions;
    private readonly StatusBarViewModel? _statusBar;

    private HealthScopeViewModel? _selectedScope;
    private string _blocker = string.Empty;
    private string _probeResultSummary = string.Empty;
    private string _impactedSessionSummary = string.Empty;
    private bool _isBusy;
    private long _auditGeneration;
    private string _modelIdToProbe = string.Empty;
    private bool _costPreviewAcknowledged;
    private bool _refreshFailed;
    private string? _refreshFailureBlocker;

    public HealthCenterViewModel(
        CliStatusViewModel cliStatus,
        IHealthCenterService? healthCenter = null,
        IHealthProbeService? probeService = null,
        IImpactedSessionService? impactedSessions = null,
        StatusBarViewModel? statusBar = null)
        : base(
            ScreenId.HealthCenter,
            "Центр здоровья",
            "Ctrl+9",
            "Сбои, карантин, проверки и восстановление (ТЗ §7.2).")
    {
        ArgumentNullException.ThrowIfNull(cliStatus);

        CliStatus = cliStatus;
        _healthCenter = healthCenter;
        _probeService = probeService;
        _impactedSessions = impactedSessions;
        _statusBar = statusBar;

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => IsHealthCenterAvailable && !IsBusy);
        TestConnectionCommand = new RelayCommand(() => _ = TestConnectionAsync(), () => CanTestConnection);
        ProbeModelCommand = new RelayCommand(() => _ = ProbeModelAsync(), () => CanProbeModel);
        DisableCommand = new RelayCommand(() => _ = DisableAsync(), () => CanDisable);
        EnableCommand = new RelayCommand(() => _ = EnableAsync(), () => CanEnable);
        ForceEnableCommand = new RelayCommand(() => _ = ForceEnableAsync(), () => CanForceEnable);
        CheckCooldownCommand = new RelayCommand(() => _ = CheckCooldownAsync(), () => CanCheckCooldown);
    }

    public CliStatusViewModel CliStatus { get; }

    /// <summary>True only when the normalized health service is wired into this window.</summary>
    public bool IsHealthCenterAvailable => _healthCenter is not null;

    public string UnavailableNotice =>
        "Центр здоровья не сконфигурирован для этого окна. Зарегистрируйте службы здоровья " +
        "для мониторинга предохранителей и проверок восстановления.";

    /// <summary>Observed health scopes. Empty until the service actually reports one.</summary>
    public ObservableCollection<HealthScopeViewModel> Scopes { get; } = new();

    /// <summary>Recovery audit of the selected scope, newest first.</summary>
    public ObservableCollection<HealthAuditEntryViewModel> Audit { get; } = new();

    /// <summary>Sessions affected by the selected scope's health, newest activity first.</summary>
    public ObservableCollection<ImpactedSessionViewModel> ImpactedSessions { get; } = new();

    public bool HasImpactedSessions => ImpactedSessions.Count > 0;

    /// <summary>True only when this window can resolve impacted sessions at all.</summary>
    public bool IsImpactedSessionViewAvailable => _impactedSessions is not null;

    /// <summary>
    /// Summary of the impacted-session lookup. When the lookup could not be completed this states so,
    /// because an empty list must never read as "nothing is affected".
    /// </summary>
    public string ImpactedSessionSummary
    {
        get => _impactedSessionSummary;
        private set => SetProperty(ref _impactedSessionSummary, value);
    }

    public bool HasScopes => Scopes.Count > 0;

    public bool HasAudit => Audit.Count > 0;

    public ICommand RefreshCommand { get; }

    /// <summary>
    /// Runs the connection probe. It confirms only that the endpoint answers, so it never claims a
    /// recovery; a pinned model probe has to pass for that.
    /// </summary>
    public ICommand TestConnectionCommand { get; }

    /// <summary>
    /// Runs the pinned model probe that verifies a recovery. It is gated behind an explicit cost-preview
    /// confirmation, and it is never replaced by a "Probe passed" button.
    /// </summary>
    public ICommand ProbeModelCommand { get; }

    public ICommand DisableCommand { get; }

    public ICommand EnableCommand { get; }

    public ICommand ForceEnableCommand { get; }

    /// <summary>
    /// Re-evaluates an elapsed cooldown. Without it a <see cref="HealthState.CoolingDown"/> scope could
    /// never leave that state from the UI, because nothing else drives the expiry.
    /// </summary>
    public ICommand CheckCooldownCommand { get; }

    /// <summary>
    /// Summary indicator. Before the service reports anything it stays <see cref="UnavailableIndicator"/>
    /// rather than claiming health that was never observed.
    /// </summary>
    public string HealthIndicator
    {
        get
        {
            if (_refreshFailed) return "Состояние здоровья не обновлено";
            if (!IsHealthCenterAvailable || Scopes.Count == 0)
            {
                return UnavailableIndicator;
            }

            var quarantined = Scopes.Count(scope => scope.RequiresProbe);
            var forced = Scopes.Count(scope => scope.IsForcedWithoutVerification);
            var pending = Scopes.Count(scope => scope.Snapshot.AuthenticationFanoutPending);
            if (pending > 0) return $"{Scopes.Count} отслеживается, {pending} заблокированы: обработка авторизации не завершена";

            if (quarantined == 0 && forced == 0)
            {
                return $"{Scopes.Count} в маршрутизации";
            }

            return $"{Scopes.Count} отслеживается, {quarantined} ожидают проверки, " +
                   $"{forced} принудительно без проверки";
        }
    }

    public string ScopeNote =>
        IsHealthCenterAvailable
            ? "Состояние предохранителей, ручные проверки и журнал восстановления формируются " +
              "по наблюдаемым данным; принудительный маршрут никогда не считается подтверждённым " +
              "восстановлением."
            : UnavailableNotice;

    /// <summary>
    /// Observed detail behind <see cref="HealthIndicator"/>, published to the status bar on every
    /// refresh. It is derived from the same snapshots as the indicator, so the two can never disagree
    /// about what was observed.
    /// </summary>
    public string HealthSummary =>
        _refreshFailed
            ? "Не удалось обновить состояние здоровья. Показаны последние наблюдения; нажмите «Обновить» для повтора."
            : !IsHealthCenterAvailable || Scopes.Count == 0
            ? EmptyStateMessage
            : $"{Scopes.Count} областей здоровья наблюдается; " +
              $"{Scopes.Count(scope => scope.RequiresProbe)} ожидают проверки, " +
              $"{Scopes.Count(scope => scope.IsForcedWithoutVerification)} принудительно без проверки." +
              (Scopes.Any(scope => scope.Snapshot.AuthenticationFanoutPending)
                  ? " Есть незавершённые блокировки авторизации. Нажмите «Обновить» для повтора." : string.Empty);

    public string EmptyStateMessage =>
        IsHealthCenterAvailable
            ? "Переходы состояния здоровья пока не наблюдались."
            : UnavailableNotice;

    public HealthScopeViewModel? SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (SetProperty(ref _selectedScope, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanTestConnection));
                OnPropertyChanged(nameof(CanProbeModel));
                OnPropertyChanged(nameof(CanDisable));
                OnPropertyChanged(nameof(CanEnable));
                OnPropertyChanged(nameof(CanForceEnable));
                OnPropertyChanged(nameof(CanCheckCooldown));
                RelayCommand.RaiseCanExecuteChanged();

                _ = LoadAuditAsync();
            }
        }
    }

    public bool HasSelection => SelectedScope is not null;

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

    /// <summary>Result of the last executed probe. Empty when no probe has run in this session.</summary>
    public string ProbeResultSummary
    {
        get => _probeResultSummary;
        private set
        {
            if (SetProperty(ref _probeResultSummary, value))
            {
                OnPropertyChanged(nameof(HasProbeResult));
            }
        }
    }

    public bool HasProbeResult => !string.IsNullOrWhiteSpace(ProbeResultSummary);

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
    /// A connection probe may only run while the scope is actually awaiting one. It is a measurement,
    /// so there is no operator judgement to record here.
    /// </summary>
    public bool CanTestConnection =>
        IsProbeExecutionAvailable && !IsBusy && SelectedScope?.Snapshot.AuthenticationFanoutPending == false && SelectedScope.State == HealthState.ProbeRequired;

    /// <summary>
    /// The pinned model probe may run while the scope owes or is undergoing a probe, and only once the
    /// operator acknowledged the cost preview.
    /// </summary>
    public bool CanProbeModel =>
        IsModelProbeExecutionAvailable &&
        !IsBusy &&
        CostPreviewAcknowledged &&
        SelectedScope?.Snapshot.AuthenticationFanoutPending == false &&
        !string.IsNullOrWhiteSpace(ModelIdToProbe) &&
        SelectedScope?.State is HealthState.ProbeRequired or HealthState.Recovering;

    /// <summary>
    /// Backward-compatible convenience gate: a probe can be run whenever either the connection probe or
    /// the confirmed pinned model probe is currently offered for the selection.
    /// </summary>
    public bool CanRunProbe => CanTestConnection || CanProbeModel;

    /// <summary>
    /// Backward-compatible convenience gate: true only while the selected scope is awaiting a probe,
    /// which is the state a probe may start from. It deliberately mirrors
    /// <see cref="CanTestConnection"/> so the screen never offers a probe it cannot execute.
    /// </summary>
    public bool CanStartProbe => CanTestConnection;

    /// <summary>
    /// A cooldown may only be re-evaluated while the scope is actually cooling down. The service still
    /// decides whether the deadline has passed; the UI never shortens a cooldown.
    /// </summary>
    public bool CanCheckCooldown =>
        IsHealthCenterAvailable && !IsBusy && SelectedScope?.State == HealthState.CoolingDown;

    /// <summary>True only when the connection-probe executor is configured.</summary>
    public bool IsProbeExecutionAvailable => _probeService is not null;

    /// <summary>
    /// True when a pinned model probe can actually be run. When false the recovery cannot be verified,
    /// so the screen must say <c>Unsupported</c> rather than offer a "Probe passed" button: a successful
    /// connection check is not a verified recovery (ТЗ §6.10).
    /// </summary>
    public bool IsModelProbeExecutionAvailable => _probeService?.SupportsModelProbe == true;

    /// <summary>Model the pinned probe will exercise. Required before the probe may be confirmed.</summary>
    public string ModelIdToProbe
    {
        get => _modelIdToProbe;
        set
        {
            if (SetProperty(ref _modelIdToProbe, value))
            {
                CostPreviewAcknowledged = false;
                OnPropertyChanged(nameof(ModelProbePreview));
                OnPropertyChanged(nameof(CanProbeModel));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// True once the operator has seen the possible cost of the pinned model probe. The probe may spend
    /// provider quota, so it never runs without this acknowledgement.
    /// </summary>
    public bool CostPreviewAcknowledged
    {
        get => _costPreviewAcknowledged;
        set
        {
            if (SetProperty(ref _costPreviewAcknowledged, value))
            {
                OnPropertyChanged(nameof(CanProbeModel));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Preview of the possible cost of the pinned model probe. It is stated up front because the probe
    /// is a separate execution that may spend provider quota.
    /// </summary>
    public string ModelProbePreview =>
        string.IsNullOrWhiteSpace(ModelIdToProbe)
            ? "Выберите модель для проверки. Закреплённая проверка выполняется отдельным запуском " +
              "и может израсходовать квоту провайдера."
            : $"Закреплённая проверка для '{ModelIdToProbe}' выполняется отдельным запуском (никогда " +
              "как продолжение вашей сессии) и может израсходовать квоту провайдера. Она подтверждает " +
              "аутентификацию, выбранную модель и минимальный ход.";

    public string ProbeExecutionNote =>
        IsModelProbeExecutionAvailable
            ? "Проверка соединения подтверждает только эндпоинт; «Проверить модель» — это закреплённая " +
              "проверка, которая подтверждает восстановление."
            : "Для этого окна не настроен исполнитель закреплённой проверки модели, поэтому " +
              "подтверждённое восстановление невозможно. Проверка соединения может подтвердить " +
              "эндпоинт, а область остаётся исключённой из маршрутизации.";

    public bool CanDisable =>
        IsHealthCenterAvailable && !IsBusy && SelectedScope is { State: not HealthState.DisabledManual };

    public bool CanEnable =>
        IsHealthCenterAvailable && !IsBusy && SelectedScope?.State == HealthState.DisabledManual;

    /// <summary>A route is only worth forcing while it is out of routing without being disabled.</summary>
    public bool CanForceEnable =>
        IsHealthCenterAvailable &&
        !IsBusy &&
        SelectedScope?.State is
            HealthState.CoolingDown or HealthState.ProbeRequired or HealthState.QuarantinedAuto;

    /// <summary>Operator-supplied reason recorded verbatim in the audit.</summary>
    public string ManualActionReason { get; set; } = "Изменено оператором в Центре здоровья.";

    public async Task RefreshAsync()
    {
        if (_healthCenter is null || IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var completionFailed = false;
            if (_probeService is not null)
            {
                try { await _probeService.RetryPendingCompletionsAsync().ConfigureAwait(true); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Blocker = UiErrorMessage.Describe(exception);
                    _refreshFailureBlocker = Blocker;
                    completionFailed = true;
                }
            }
            await _healthCenter.RetryAuthenticationFanoutAsync().ConfigureAwait(true);
            var snapshots = await _healthCenter.ListAsync().ConfigureAwait(true);
            if (!completionFailed && _refreshFailed && Blocker == _refreshFailureBlocker)
                Blocker = string.Empty;
            _refreshFailed = completionFailed;
            if (!completionFailed) _refreshFailureBlocker = null;
            var previousKey = SelectedScope?.Key;

            Scopes.Clear();

            foreach (var snapshot in snapshots.OrderBy(item => item.Scope.ScopeType).ThenBy(item => item.Scope.ScopeId))
            {
                Scopes.Add(new HealthScopeViewModel(snapshot));
            }

            OnPropertyChanged(nameof(HasScopes));
            OnPropertyChanged(nameof(HealthIndicator));
            OnPropertyChanged(nameof(HealthSummary));

            // The status bar mirrors the observed screen state, so the operator sees the same truth
            // whether the Health Center is open or not (ТЗ §7.3).
            _statusBar?.UpdateHealth(HealthIndicator, HealthSummary);

            // Keep the operator on the same scope across a refresh when it still exists.
            SelectedScope = Scopes.FirstOrDefault(scope => scope.Key == previousKey) ?? Scopes.FirstOrDefault();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = UiErrorMessage.Describe(exception);
            _refreshFailureBlocker = Blocker;
            _refreshFailed = true;
        }
        finally
        {
            OnPropertyChanged(nameof(HealthIndicator));
            OnPropertyChanged(nameof(HealthSummary));
            _statusBar?.UpdateHealth(HealthIndicator, HealthSummary);
            IsBusy = false;
        }
    }

    public Task DisableAsync() =>
        ExecuteAsync(scope => _healthCenter!.DisableManuallyAsync(scope, ManualActionReason));

    public Task EnableAsync() =>
        ExecuteAsync(scope => _healthCenter!.EnableAsync(scope, ManualActionReason));

    public Task ForceEnableAsync() =>
        ExecuteAsync(scope => _healthCenter!.ForceEnableAsync(scope, ManualActionReason));

    /// <summary>
    /// Asks the service to re-evaluate an elapsed cooldown. A cooldown that has not elapsed stays
    /// untouched, and an elapsed one lands on <see cref="HealthState.ProbeRequired"/>, never Healthy.
    /// </summary>
    public Task CheckCooldownAsync() =>
        ExecuteAsync(scope => _healthCenter!.ExpireCooldownAsync(scope));

    /// <summary>
    /// Executes the connection probe. A refusal (nothing to probe, no endpoint, no executor) is surfaced
    /// as a blocker and leaves the state untouched, so "could not check" is never shown as "the check
    /// failed". A pass confirms the connection only and never claims a verified recovery.
    /// </summary>
    public Task TestConnectionAsync() => ExecuteProbeAsync(modelProbeRequest: null);

    /// <summary>
    /// Executes the pinned model probe. It requires the cost-preview acknowledgement and, when the
    /// backend cannot run it, the outcome is reported as <c>Unsupported</c> rather than as a pass.
    /// </summary>
    public Task ProbeModelAsync() =>
        ExecuteProbeAsync(HealthProbeConfirmation.ForModel(ModelIdToProbe, CostPreviewAcknowledged));

    /// <summary>
    /// Backward-compatible convenience entry point: it runs the pinned model probe once it was
    /// confirmed, and falls back to the connection probe otherwise. Both are real observations, so no
    /// caller can supply the outcome.
    /// </summary>
    public Task RunProbeAsync() => CanProbeModel ? ProbeModelAsync() : TestConnectionAsync();

    private async Task ExecuteProbeAsync(HealthProbeConfirmation? modelProbeRequest)
    {
        if (_probeService is null || _healthCenter is null || IsBusy || SelectedScope is null)
        {
            return;
        }

        var scope = SelectedScope.Scope;
        IsBusy = true;
        Blocker = string.Empty;
        ProbeResultSummary = string.Empty;

        try
        {
            var outcome = modelProbeRequest is null
                ? await _probeService.ProbeConnectionAsync(scope).ConfigureAwait(true)
                : await _probeService.ProbeModelAsync(scope, modelProbeRequest).ConfigureAwait(true);

            if (!outcome.WasExecuted)
            {
                // The probe did not run. Saying so is the only honest outcome, and an unsupported
                // backend is stated explicitly instead of being shown as a passing probe.
                Blocker = outcome.Explanation;
            }
            else
            {
                ProbeResultSummary = outcome.Explanation;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = UiErrorMessage.Describe(exception);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Runs one manual health action and refreshes the observed state. A failure surfaces as a blocker
    /// instead of leaving the screen showing a state the service never confirmed.
    /// </summary>
    private async Task ExecuteAsync(Func<HealthScope, Task<HealthSnapshot>> action)
    {
        if (_healthCenter is null || IsBusy || SelectedScope is null)
        {
            return;
        }

        var scope = SelectedScope.Scope;
        IsBusy = true;
        Blocker = string.Empty;

        try
        {
            await action(scope).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Blocker = UiErrorMessage.Describe(exception);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task LoadAuditAsync()
    {
        var generation = ++_auditGeneration;
        var scope = SelectedScope?.Scope;
        Audit.Clear();
        OnPropertyChanged(nameof(HasAudit));
        ImpactedSessions.Clear();
        OnPropertyChanged(nameof(HasImpactedSessions));
        ImpactedSessionSummary = string.Empty;

        if (_healthCenter is null || scope is null)
        {
            return;
        }

        try
        {
            var entries = await _healthCenter
                .GetAuditAsync(scope, limit: 50)
                .ConfigureAwait(true);

            if (!IsCurrentAudit(generation, scope))
            {
                return;
            }

            foreach (var entry in entries)
            {
                Audit.Add(new HealthAuditEntryViewModel(entry));
            }

            OnPropertyChanged(nameof(HasAudit));
            await LoadImpactedSessionsAsync(scope, generation).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Selection changes launch this task without awaiting it. Observe failures here and
            // publish only for the selection that initiated the lookup, without private diagnostics.
            if (IsCurrentAudit(generation, scope))
            {
                Blocker = $"Не удалось загрузить аудит здоровья ({exception.GetType().Name}).";
            }
        }
    }

    private bool IsCurrentAudit(long generation, HealthScope scope) =>
        generation == _auditGeneration && Equals(SelectedScope?.Scope, scope);

    /// <summary>
    /// Loads the sessions affected by the selected scope. An incomplete lookup is reported as such:
    /// an empty list must never be presented as "nothing is affected".
    /// </summary>
    private async Task LoadImpactedSessionsAsync(HealthScope scope, long generation)
    {
        if (_impactedSessions is null)
        {
            return;
        }

        try
        {
            var report = await _impactedSessions
                .GetImpactedSessionsAsync(scope)
                .ConfigureAwait(true);

            if (!IsCurrentAudit(generation, scope))
            {
                return;
            }

            foreach (var session in report.Sessions)
            {
                ImpactedSessions.Add(new ImpactedSessionViewModel(session));
            }

            ImpactedSessionSummary = report.Summary;
            OnPropertyChanged(nameof(HasImpactedSessions));
        }
        catch (Exception exception)
        {
            if (IsCurrentAudit(generation, scope))
            {
                ImpactedSessionSummary =
                    $"Не удалось определить затронутые сессии ({exception.GetType().Name}).";
            }
        }
    }
}
