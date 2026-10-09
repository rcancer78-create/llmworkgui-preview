using System.ComponentModel;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

public sealed class StatusBarViewModel : ObservableObject
{
    public const string NotReported = ObservableRunProjection.NotReportedPlaceholder;
    public const string BasicHealthLabel = "Норма";
    public const string HealthScopeNoteConst = "Только базовая проверка (исполняемый файл/процесс/протокол) до этапа 7.";
    public const string ActiveHealthScopeNote =
        "Центр здоровья активен: наблюдение областей, предохранителей и проверок восстановления.";
    public const string HealthyHealthLabel = "Готов";
    public const string HealthCenterReadyDetail = "Все предохранители в норме";
    public const string NoScopesObservedDetail = "Ни одна область здоровья пока не наблюдалась.";
    public const string IdleExecutionState = "Ожидание";
    public const string PrimaryInstanceModeText = "Основной экземпляр";
    public const string ViewOnlyInstanceModeText = "Только просмотр";

    private readonly IApplicationInstanceGuard? _instanceGuard;
    private readonly IHealthCenterService? _healthCenter;

    private string _healthIndicator;
    private string _healthDetail;

    public StatusBarViewModel(
        CliStatusViewModel cliStatus,
        IApplicationInstanceGuard? instanceGuard = null,
        IHealthCenterService? healthCenter = null)
    {
        ArgumentNullException.ThrowIfNull(cliStatus);

        _instanceGuard = instanceGuard;
        _healthCenter = healthCenter;
        _healthIndicator = healthCenter is null ? BasicHealthLabel : HealthyHealthLabel;
        _healthDetail = healthCenter is null ? string.Empty : HealthCenterReadyDetail;
        CliStatus = cliStatus;
        CliStatus.PropertyChanged += OnCliStatusPropertyChanged;
    }

    public CliStatusViewModel CliStatus { get; }

    private string _provider = NotReported;
    private string _account = NotReported;
    private string _model = NotReported;
    private string _reasoning = NotReported;
    private string _speed = NotReported;
    private string _localSessionId = NotReported;
    private string _nativeSessionId = NotReported;
    private string _sessionConfirmation = NotReported;
    private string _executionState = IdleExecutionState;

    public string Provider
    {
        get => _provider;
        private set => SetProperty(ref _provider, value);
    }

    public string Account
    {
        get => _account;
        private set => SetProperty(ref _account, value);
    }

    public string Model
    {
        get => _model;
        private set => SetProperty(ref _model, value);
    }

    public string Reasoning
    {
        get => _reasoning;
        private set => SetProperty(ref _reasoning, value);
    }

    public string Speed
    {
        get => _speed;
        private set => SetProperty(ref _speed, value);
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

    public string SessionConfirmation
    {
        get => _sessionConfirmation;
        private set => SetProperty(ref _sessionConfirmation, value);
    }

    public string ExecutionState
    {
        get => _executionState;
        private set => SetProperty(ref _executionState, value);
    }

    public string QuotaSummary => "Неизвестно";

    public string QuotaFreshness => "Никогда";

    /// <summary>
    /// Scope note shown under the health section. Without the Health Center the status bar can only
    /// report the basic executable/process/protocol checks; with it the monitored scopes are named.
    /// </summary>
    public string HealthScopeNote => _healthCenter is null ? HealthScopeNoteConst : ActiveHealthScopeNote;

    /// <summary>
    /// Highest-severity observed health state, or <see cref="BasicHealthLabel"/> when the Health Center
    /// is not configured for this window. It defaults to Healthy only because no failure has been
    /// observed yet; a refresh replaces it with what the scopes actually report.
    /// </summary>
    public string HealthIndicator
    {
        get => _healthCenter is null ? BasicHealthLabel : _healthIndicator;
        private set => SetProperty(ref _healthIndicator, value);
    }

    /// <summary>
    /// Health detail. Without the Health Center it is the CLI detection evidence; with it the observed
    /// scope summary is shown instead of a health claim the service never made.
    /// </summary>
    public string HealthDetail
    {
        get => _healthCenter is null ? CliHealthDetail : _healthDetail;
        private set => SetProperty(ref _healthDetail, value);
    }

    private string CliHealthDetail => CliStatus.HasDetectionFailed
        ? "Ошибка обнаружения CLI"
        : CliStatus.IsDegraded
            ? "Исполняемый файл бэкенда не обнаружен"
            : $"{CliStatus.DetectedCount} из {CliStatus.ToolCount} CLI бэкендов обнаружено";

    public string ProjectWorkspace => NotReported;

    public string WorkflowRoleStage => NotReported;

    public string RoutingDecisionReason => NotReported;

    public bool IsDegraded => CliStatus.IsDegraded;

    public string DegradedIndicatorText => IsDegraded ? "Ограниченный режим" : "Режим в норме";

    public bool IsDetectionPending => CliStatus.IsDetectionPending;

    public bool IsPrimarySupervisor => _instanceGuard?.IsPrimarySupervisor ?? true;

    public bool IsViewOnly => !IsPrimarySupervisor;

    public string InstanceModeIndicator => IsViewOnly ? ViewOnlyInstanceModeText : PrimaryInstanceModeText;

    private void OnCliStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CliStatusViewModel.IsDegraded):
            case nameof(CliStatusViewModel.HasDetectionFailed):
            case nameof(CliStatusViewModel.HasAnyDetected):
            case nameof(CliStatusViewModel.DetectedCount):
            case nameof(CliStatusViewModel.ToolCount):
                OnPropertyChanged(nameof(IsDegraded));
                OnPropertyChanged(nameof(DegradedIndicatorText));
                OnPropertyChanged(nameof(HealthDetail));
                break;
            case nameof(CliStatusViewModel.IsDetectionPending):
                OnPropertyChanged(nameof(IsDetectionPending));
                break;
            default:
                break;
        }
    }

    public void UpdateSessionState(string? localSessionId, string? nativeSessionId, string? sessionConfirmation, string? executionState = null, string? provider = null, string? model = null)
    {
        if (localSessionId != null) LocalSessionId = localSessionId;
        if (nativeSessionId != null) NativeSessionId = nativeSessionId;
        if (sessionConfirmation != null) SessionConfirmation = sessionConfirmation;
        if (executionState != null) ExecutionState = executionState;
        if (provider != null) Provider = provider;
        if (model != null) Model = model;
    }

    /// <summary>
    /// Re-reads the observed scopes from the Health Center and shows the highest-severity state. The
    /// aggregation order is Quarantined &gt; Disabled &gt; Cooling down &gt; Probe required &gt;
    /// Recovering &gt; Forced enabled &gt; Degraded &gt; Healthy, so a forced or degraded route never
    /// hides a quarantined one. Without a Health Center this is a no-op: the status bar keeps reporting
    /// the basic checks only (ТЗ §7.3).
    /// </summary>
    public async Task RefreshHealthAsync(CancellationToken cancellationToken = default)
    {
        if (_healthCenter is null)
        {
            return;
        }

        var snapshots = await _healthCenter.ListAsync(cancellationToken).ConfigureAwait(true);

        if (snapshots.Count == 0)
        {
            UpdateHealth(HealthyHealthLabel, NoScopesObservedDetail);
            return;
        }

        var indicator = DescribeState(AggregateState(snapshots));

        UpdateHealth(indicator, $"Областей здоровья: {snapshots.Count}; худшее состояние: {indicator}.");
    }

    /// <summary>
    /// Directly publishes an observed health state. The detail is left untouched when none is supplied,
    /// so a caller can update the indicator without discarding the explanation it already had.
    /// </summary>
    public void UpdateHealth(string indicator, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indicator);

        HealthIndicator = indicator;

        if (detail is not null)
        {
            HealthDetail = detail;
        }
    }

    private static HealthState AggregateState(IReadOnlyList<HealthSnapshot> snapshots)
    {
        if (snapshots.Any(snapshot => snapshot.State == HealthState.QuarantinedAuto)) return HealthState.QuarantinedAuto;
        if (snapshots.Any(snapshot => snapshot.State == HealthState.DisabledManual)) return HealthState.DisabledManual;
        if (snapshots.Any(snapshot => snapshot.State == HealthState.CoolingDown)) return HealthState.CoolingDown;
        if (snapshots.Any(snapshot => snapshot.State == HealthState.ProbeRequired)) return HealthState.ProbeRequired;
        if (snapshots.Any(snapshot => snapshot.State == HealthState.Recovering)) return HealthState.Recovering;
        if (snapshots.Any(snapshot => snapshot.State == HealthState.ForcedEnabled)) return HealthState.ForcedEnabled;
        if (snapshots.Any(snapshot => snapshot.State == HealthState.Degraded)) return HealthState.Degraded;

        return HealthState.Healthy;
    }

    private static string DescribeState(HealthState state) => state switch
    {
        HealthState.QuarantinedAuto => "Карантин",
        HealthState.DisabledManual => "Отключено",
        HealthState.CoolingDown => "Кулдаун",
        HealthState.ProbeRequired => "Требуется проверка",
        HealthState.Recovering => "Восстановление",
        HealthState.ForcedEnabled => "Принудительно включён",
        HealthState.Degraded => "Ограниченный режим",
        _ => HealthyHealthLabel
    };
}
