using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using LLMWorkGUI.Application.Quotas;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Routing;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>
/// Quotas &amp; Routing Dashboard view model (ТЗ §6.4, §6.5, §6.6, §7.2).
/// Renders every connected account/model bucket with honest provenance and freshness, never fabricates
/// percentages for Unknown/Unsupported/Stale/Error states, and explains deterministic routing decisions
/// together with the exact QuotaSnapshotId used by the routing engine.
/// </summary>
public sealed class QuotasViewModel : ScreenViewModel
{
    private static readonly Regex TieBreakRegex = new(
        @"\[(?<reason>Tie broken by[^\]]*)\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IQuotaSnapshotRepository? _snapshotRepository;
    private readonly IQuotaRefreshScheduler? _refreshScheduler;
    private readonly IRoutingEngine? _routingEngine;
    private readonly IAccountRepository? _accountRepository;
    private readonly IProviderProfileRepository? _profileRepository;
    private readonly TimeProvider _timeProvider;

    private RoutingPolicy _selectedPolicy = RoutingPolicy.Balanced;
    private string _simulationProviderProfileId = string.Empty;
    private string _simulationModelId = "gpt-4o";
    private string _pinnedAccountId = string.Empty;
    private bool _optInEstimatedQuota;
    private bool _isBusy;
    private string? _statusMessage;
    private RoutingDecision? _activeRoutingDecision;

    public QuotasViewModel(
        IQuotaSnapshotRepository? snapshotRepository = null,
        IQuotaRefreshScheduler? refreshScheduler = null,
        IRoutingEngine? routingEngine = null,
        IAccountRepository? accountRepository = null,
        IProviderProfileRepository? profileRepository = null,
        TimeProvider? timeProvider = null)
        : base(
            ScreenId.Quotas,
            "Квоты",
            "Ctrl+5",
            "Все корзины квот аккаунтов/моделей с происхождением, свежестью, сбросом и детерминированным объяснением маршрутизации (ТЗ §6.6, §7.2).")
    {
        _snapshotRepository = snapshotRepository;
        _refreshScheduler = refreshScheduler;
        _routingEngine = routingEngine;
        _accountRepository = accountRepository;
        _profileRepository = profileRepository;
        _timeProvider = timeProvider ?? TimeProvider.System;

        RefreshAllCommand = new RelayCommand(() => _ = RefreshAllAsync());
        RefreshAccountCommand = new RelayCommand(parameter => _ = RefreshAccountAsync(parameter as QuotaBucketItemViewModel));
        SimulateRoutingCommand = new RelayCommand(() => _ = SimulateRoutingAsync());
    }

    public ObservableCollection<QuotaBucketItemViewModel> Buckets { get; } = new();

    public ObservableCollection<CandidateScoreViewModel> CandidateScores { get; } = new();

    public ObservableCollection<RejectedCandidateViewModel> RejectedCandidates { get; } = new();

    public IReadOnlyList<RoutingPolicy> AvailablePolicies { get; } = Enum.GetValues<RoutingPolicy>();

    public ICommand RefreshAllCommand { get; }

    public ICommand RefreshAccountCommand { get; }

    public ICommand SimulateRoutingCommand { get; }

    public RoutingPolicy SelectedPolicy
    {
        get => _selectedPolicy;
        set
        {
            if (SetProperty(ref _selectedPolicy, value))
            {
                OnPropertyChanged(nameof(RoutingPolicyDisplay));
            }
        }
    }

    public string SimulationProviderProfileId
    {
        get => _simulationProviderProfileId;
        set => SetProperty(ref _simulationProviderProfileId, value);
    }

    public string SimulationModelId
    {
        get => _simulationModelId;
        set => SetProperty(ref _simulationModelId, value);
    }

    public string PinnedAccountId
    {
        get => _pinnedAccountId;
        set => SetProperty(ref _pinnedAccountId, value);
    }

    public bool OptInEstimatedQuota
    {
        get => _optInEstimatedQuota;
        set => SetProperty(ref _optInEstimatedQuota, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public RoutingDecision? ActiveRoutingDecision
    {
        get => _activeRoutingDecision;
        private set
        {
            if (SetProperty(ref _activeRoutingDecision, value))
            {
                RebuildRoutingProjection();
                RaiseRoutingDerivedProperties();
            }
        }
    }

    public bool HasActiveRoutingDecision => _activeRoutingDecision is not null;

    public bool IsRoutingSuccess => _activeRoutingDecision?.IsSuccess == true;

    public bool RequiresReplacementSession => _activeRoutingDecision?.RequiresReplacementSession == true;

    public string RoutingOutcomeDisplay => _activeRoutingDecision switch
    {
        null => "Симуляция не проводилась.",
        { IsSuccess: true } => "МАРШРУТ ВЫБРАН",
        _ => "МАРШРУТ НЕ НАЙДЕН (выбор остановлен)"
    };

    public string SelectedAccountDisplay => _activeRoutingDecision?.SelectedAccount is { } account
        ? $"{account.DisplayName} ({account.Id})"
        : "Не выбран";

    public string? ActiveQuotaSnapshotId => _activeRoutingDecision?.QuotaSnapshotId;

    public string ActiveQuotaSnapshotIdDisplay => ActiveQuotaSnapshotId ?? "Not reported";

    public string RoutingPolicyDisplay => _activeRoutingDecision?.Policy.ToString() ?? SelectedPolicy.ToString();

    public string PolicySourceDisplay => _activeRoutingDecision?.PolicySource ?? "Не симулировалась";

    public string RoutingExplanation => _activeRoutingDecision?.ExplanationText
        ?? "Запустите симуляцию маршрутизации, чтобы увидеть детерминированное объяснение решения (ТЗ §6.5).";

    public string RoutingScoreSummary => _activeRoutingDecision?.Score is { } score
        ? $"Итого {score.TotalScore:F1} / 100 — Квота {score.QuotaScore:F1} · Здоровье {score.HealthScore:F1} · " +
          $"Приоритет {score.PriorityScore:F1} · Нагрузка {score.LoadScore:F1} · Задержка {score.LatencyScore:F1} · " +
          $"Резерв {score.ReserveScore:F1}"
        : "Числовая оценка отсутствует (выбор не оценивался или нет свежей доверенной квоты).";

    public string TieBreakDisplay
    {
        get
        {
            if (_activeRoutingDecision is null)
            {
                return "Не оценивалось";
            }

            var match = TieBreakRegex.Match(_activeRoutingDecision.ExplanationText);
            return match.Success ? match.Groups["reason"].Value : "Разрешение равенства не требовалось";
        }
    }

    public string ReplacementSessionNotice => RequiresReplacementSession
        ? "Замещающая сессия требует явного подтверждения пользователя; запрос не отправлен (ТЗ §6.5)."
        : string.Empty;

    public bool HasBuckets => Buckets.Count > 0;

    public bool HasCandidateScores => CandidateScores.Count > 0;

    public bool HasRejectedCandidates => RejectedCandidates.Count > 0;

    public string TotalBucketsDisplay => $"Всего корзин: {Buckets.Count}";

    public string FreshBucketsDisplay => $"Свежие: {Buckets.Count(bucket => bucket.IsFresh)}";

    public string DegradedBucketsDisplay => $"Ограниченные / устаревшие: {Buckets.Count(bucket => !bucket.IsFresh)}";

    public string TrustedNumericBucketsDisplay => $"Достоверные числовые: {Buckets.Count(bucket => bucket.CanCalculateScore)}";

    public string EmptyStateMessage => "Квоты не загружены.";

    public string PolicyNote =>
        "Значения квот никогда не фабрикуются: неизвестные, неподдерживаемые, устаревшие и ошибочные данные остаются текстовыми и не получают числовой оценки (ТЗ §6.6). " +
        "Панель и движок маршрутизации всегда ссылаются на один и тот же идентификатор снимка квот.";

    /// <summary>
    /// Loads every connected account/model bucket from the repositories. Accounts without any snapshot
    /// still receive an explicit Unknown row per ТЗ §6.6.
    /// </summary>
    public async Task LoadQuotasAsync(CancellationToken cancellationToken = default)
    {
        Buckets.Clear();

        if (_accountRepository is null || _snapshotRepository is null)
        {
            RaiseQuotaSummary();
            return;
        }

        var accounts = await _accountRepository.ListAllAsync(cancellationToken).ConfigureAwait(true);
        IReadOnlyList<ProviderProfile> profiles = _profileRepository is not null
            ? await _profileRepository.ListAsync(cancellationToken).ConfigureAwait(true)
            : Array.Empty<ProviderProfile>();

        var profilesById = new Dictionary<string, ProviderProfile>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            profilesById[profile.Id] = profile;
        }

        var now = _timeProvider.GetUtcNow();

        foreach (var account in accounts)
        {
            profilesById.TryGetValue(account.ProviderProfileId, out var profile);
            var refreshStatus = _refreshScheduler?.GetStatus(account.Id);

            var snapshots = await _snapshotRepository
                .ListLatestByAccountIdAsync(account.Id, cancellationToken)
                .ConfigureAwait(true);

            var latestPerModelFamily = snapshots
                .GroupBy(snapshot => snapshot.ModelId ?? string.Empty, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(snapshot => snapshot.CapturedAt)
                    .ThenBy(snapshot => snapshot.Id, StringComparer.Ordinal)
                    .First())
                .OrderBy(snapshot => snapshot.ModelId ?? string.Empty, StringComparer.Ordinal)
                .ToList();

            if (latestPerModelFamily.Count == 0)
            {
                AddSnapshotRows(QuotaSnapshot.CreateUnknown(account.Id, account.ProviderProfileId), account, profile, refreshStatus, now);
                continue;
            }

            foreach (var snapshot in latestPerModelFamily)
            {
                AddSnapshotRows(snapshot, account, profile, refreshStatus, now);
            }
        }

        RaiseQuotaSummary();
    }

    /// <summary>
    /// Manually applies a single snapshot to the dashboard. Used by visual tests and the UI designer to
    /// feed deterministic test doubles without touching persistence.
    /// </summary>
    public void ApplySnapshot(
        QuotaSnapshot snapshot,
        Account? account = null,
        ProviderProfile? profile = null,
        AccountQuotaRefreshStatus? refreshStatus = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        AddSnapshotRows(snapshot, account, profile, refreshStatus, _timeProvider.GetUtcNow());
        RaiseQuotaSummary();
    }

    /// <summary>
    /// Refreshes all eligible account quotas through the scheduler (when available) and reloads the dashboard.
    /// </summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        if (_refreshScheduler is not null)
        {
            IsBusy = true;
            StatusMessage = "Обновление квот для всех подходящих аккаунтов...";

            try
            {
                await _refreshScheduler.RefreshAllEligibleAccountsAsync(cancellationToken).ConfigureAwait(true);
                StatusMessage = "Обновление квот завершено для всех подходящих аккаунтов.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StatusMessage = $"Сбой обновления квот: {UiErrorMessage.Describe(exception)}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        await LoadQuotasAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Refreshes a single account quota through the scheduler (when available) and reloads the dashboard.
    /// The call respects the scheduler's MinRefreshInterval rapid-refresh guard (ТЗ §6.6).
    /// </summary>
    public async Task RefreshAccountAsync(QuotaBucketItemViewModel? bucket, CancellationToken cancellationToken = default)
    {
        if (bucket is not null && _refreshScheduler is not null)
        {
            IsBusy = true;
            StatusMessage = $"Обновление квот для «{bucket.AccountName}»...";

            try
            {
                await _refreshScheduler
                    .RefreshAccountNowAsync(bucket.ProviderProfileId, bucket.AccountId, bucket.ModelId, force: false, cancellationToken)
                    .ConfigureAwait(true);
                StatusMessage = $"Квоты обновлены для «{bucket.AccountName}».";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StatusMessage = $"Сбой обновления квот для «{bucket.AccountName}»: {UiErrorMessage.Describe(exception)}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        await LoadQuotasAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs a deterministic routing simulation for the selected policy and exposes the engine explanation,
    /// candidate score, rejected candidates, and the synchronized QuotaSnapshotId.
    /// </summary>
    public async Task SimulateRoutingAsync(CancellationToken cancellationToken = default)
    {
        if (_routingEngine is null)
        {
            StatusMessage = "Движок маршрутизации недоступен в данной конфигурации.";
            return;
        }

        IsBusy = true;

        try
        {
            var request = new RouteSelectionRequest
            {
                Backend = BackendType.OpenCode,
                ProviderProfileId = ResolveProviderProfileId(),
                ModelId = string.IsNullOrWhiteSpace(SimulationModelId) ? "gpt-4o" : SimulationModelId.Trim(),
                Policy = SelectedPolicy,
                PolicySource = "QuotasDashboardSimulation",
                PinnedAccountId = string.IsNullOrWhiteSpace(PinnedAccountId) ? null : PinnedAccountId.Trim(),
                OptInEstimatedQuota = OptInEstimatedQuota
            };

            var decision = await _routingEngine.SelectRouteAsync(request, cancellationToken).ConfigureAwait(true);
            ActiveRoutingDecision = decision;

            StatusMessage = decision.IsSuccess
                ? $"Симуляция маршрутизации выбрала «{decision.SelectedAccount?.DisplayName}»."
                : "Симуляция маршрутизации не нашла подходящего аккаунта.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusMessage = $"Сбой симуляции маршрутизации: {UiErrorMessage.Describe(exception)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddSnapshotRows(
        QuotaSnapshot snapshot,
        Account? account,
        ProviderProfile? profile,
        AccountQuotaRefreshStatus? refreshStatus,
        DateTimeOffset now)
    {
        if (snapshot.Buckets.Count == 0)
        {
            Buckets.Add(new QuotaBucketItemViewModel(snapshot, bucket: null, account, profile, refreshStatus, now));
            return;
        }

        foreach (var bucket in snapshot.Buckets)
        {
            Buckets.Add(new QuotaBucketItemViewModel(snapshot, bucket, account, profile, refreshStatus, now));
        }
    }

    private string ResolveProviderProfileId()
    {
        if (!string.IsNullOrWhiteSpace(SimulationProviderProfileId))
        {
            return SimulationProviderProfileId.Trim();
        }

        var firstBucket = Buckets.FirstOrDefault();
        return firstBucket?.ProviderProfileId ?? "default";
    }

    private void RebuildRoutingProjection()
    {
        CandidateScores.Clear();
        RejectedCandidates.Clear();

        if (_activeRoutingDecision is not { } decision)
        {
            return;
        }

        if (decision.Score is not null && decision.SelectedAccount is not null)
        {
            CandidateScores.Add(new CandidateScoreViewModel(
                decision.SelectedAccount.Id,
                decision.SelectedAccount.DisplayName,
                decision.Score,
                isSelected: true,
                decision.QuotaSnapshotId));
        }

        foreach (var rejected in decision.RejectedCandidates)
        {
            RejectedCandidates.Add(new RejectedCandidateViewModel(rejected.AccountId, rejected.Reason));
        }
    }

    private void RaiseQuotaSummary()
    {
        OnPropertyChanged(nameof(HasBuckets));
        OnPropertyChanged(nameof(TotalBucketsDisplay));
        OnPropertyChanged(nameof(FreshBucketsDisplay));
        OnPropertyChanged(nameof(DegradedBucketsDisplay));
        OnPropertyChanged(nameof(TrustedNumericBucketsDisplay));
    }

    private void RaiseRoutingDerivedProperties()
    {
        OnPropertyChanged(nameof(HasActiveRoutingDecision));
        OnPropertyChanged(nameof(IsRoutingSuccess));
        OnPropertyChanged(nameof(RequiresReplacementSession));
        OnPropertyChanged(nameof(RoutingOutcomeDisplay));
        OnPropertyChanged(nameof(SelectedAccountDisplay));
        OnPropertyChanged(nameof(ActiveQuotaSnapshotId));
        OnPropertyChanged(nameof(ActiveQuotaSnapshotIdDisplay));
        OnPropertyChanged(nameof(RoutingPolicyDisplay));
        OnPropertyChanged(nameof(PolicySourceDisplay));
        OnPropertyChanged(nameof(RoutingExplanation));
        OnPropertyChanged(nameof(RoutingScoreSummary));
        OnPropertyChanged(nameof(TieBreakDisplay));
        OnPropertyChanged(nameof(ReplacementSessionNotice));
        OnPropertyChanged(nameof(HasCandidateScores));
        OnPropertyChanged(nameof(HasRejectedCandidates));
    }
}
