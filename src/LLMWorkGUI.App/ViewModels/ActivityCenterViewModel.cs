using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.App.ViewModels.StateControls;
using LLMWorkGUI.Application.Observability;

namespace LLMWorkGUI.App.ViewModels;

/// <summary>One selectable time window of the Activity Center filter bar.</summary>
public sealed record ActivityTimeRangeOption(ActivityTimeRange Value, string Display);

/// <summary>One selectable provenance option of the Activity Center filter bar.</summary>
public sealed record ActivitySourceOption(ActivityEventSource? Value, string Display);

/// <summary>One toggle of the role or state filter groups.</summary>
public sealed class ActivityFilterOptionViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;
    private bool _isSelected;

    public ActivityFilterOptionViewModel(
        string display,
        Action onSelectionChanged,
        ActivityEventState? state = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(display);
        ArgumentNullException.ThrowIfNull(onSelectionChanged);

        Display = display;
        State = state;
        _onSelectionChanged = onSelectionChanged;
    }

    public string Display { get; }

    public ActivityEventState? State { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onSelectionChanged();
            }
        }
    }
}

/// <summary>One row of the activity event list.</summary>
public sealed class ActivityEventItemViewModel
{
    public const string NotReportedPlaceholder = ObservableRunProjection.NotReportedPlaceholder;

    /// <summary>Longest message preview a list row renders; the detail pane shows the whole text.</summary>
    public const int PreviewLength = 240;

    public ActivityEventItemViewModel(ActivityEvent activityEvent)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);

        Model = activityEvent;
        DescriptionPreview = BuildPreview(activityEvent.Description);
    }

    private static string BuildPreview(string description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return string.Empty;
        }

        // Newlines make a row two lines tall for no reason, and a line break inside a trimming TextBlock
        // defeats the trimming entirely, so the preview is collapsed to one line before it is shortened.
        // A carriage-return/newline pair is one break, not two, so runs of whitespace collapse rather than
        // turning into a ragged gap. Only the part that is actually shown is copied: a 256 KiB body must
        // not be walked to render a single line, let alone allocated.
        var take = Math.Min(description.Length, PreviewLength);
        var buffer = new char[take];
        var length = 0;
        var pendingSeparator = false;

        for (var index = 0; index < take; index++)
        {
            var character = description[index];

            if (char.IsWhiteSpace(character))
            {
                pendingSeparator = length > 0;
                continue;
            }

            if (pendingSeparator)
            {
                buffer[length++] = ' ';
                pendingSeparator = false;
            }

            buffer[length++] = character;
        }

        var preview = new string(buffer, 0, length);

        return description.Length <= PreviewLength
            ? preview
            : string.Concat(preview.TrimEnd(), " …");
    }

    public ActivityEvent Model { get; }

    public string Id => Model.Id;

    public string Title => Model.Title;

    public string Description => Model.Description;

    /// <summary>
    /// Bounded single-line preview of the message for the list row.
    /// <para>
    /// The row template renders this into a <c>TextTrimming</c> TextBlock, and WPF measures and trims the
    /// whole string to lay a row out. The normative profile offers messages up to 256 KiB and a page holds
    /// 200 rows, so binding the full body cost a layout pass proportional to the entire retained history
    /// on every refresh - measured as the long tail of the dispatcher work, with a p50 of under two
    /// milliseconds and a p95 above a third of a second. The row only ever shows a line, so the row gets a
    /// line; the full text stays on <see cref="Description"/> for the detail pane, which is one control
    /// rather than two hundred.
    /// </para>
    /// </summary>
    public string DescriptionPreview { get; }

    public string RoleDisplay => Model.Role;

    public string KindDisplay => Model.Kind.ToString();

    public string StateDisplay => Model.State.ToString();

    public string SourceDisplay => Model.Source.ToString();

    public string TimestampDisplay => Model.OccurredAtUtc.ToString("u", CultureInfo.InvariantCulture);

    public string RouteDisplay => Model.RouteId ?? NotReportedPlaceholder;

    public string SessionDisplay => Model.SessionId ?? NotReportedPlaceholder;

    public string ExecutionDisplay => Model.ExecutionId ?? NotReportedPlaceholder;

    public bool IsSynthetic => Model.IsSynthetic;

    public string SyntheticBadge => Model.SyntheticBadge;

    public bool HasDiff => Model.HasDiff;

    public bool HasArtifact => Model.HasArtifact;

    public bool HasDetails => HasDiff || HasArtifact;

    public string DetailsBadge => HasDiff ? "DIFF" : HasArtifact ? "ARTIFACT" : string.Empty;

    public string Summary => string.Join(
        " · ",
        TimestampDisplay,
        RoleDisplay,
        StateDisplay,
        Title);
}

/// <summary>
/// Activity Center screen: the unified journal of workflow runs, sessions, health transitions and user
/// actions. It exposes multi-criteria filtering (time window, roles, states, provenance, full-text
/// query), paging with an exact filtered count, an honest empty/loading/error state and a details pane
/// that hands diffs and artifacts to the unified viewer. Free-text search goes through the redacting
/// <see cref="IEventSearchIndex"/>, so secrets are never indexed and therefore never findable (ТЗ §9.3).
/// <para>
/// The screen is event-driven. It subscribes to the append notification of the ingestion boundary and
/// coalesces the refreshes on the UI scheduler, so a 50 events/second stream costs a bounded number of
/// list rebuilds instead of fifty per second. Only the requested page is ever materialized, and the
/// latency of each newly visible event is measured from the monotonic stamp the service took inside
/// <c>Append</c> to the moment the dispatched work finished (ТЗ §9.2).
/// </para>
/// <para>
/// The query itself is not UI work. A durable page over the normative 190 000 retained rows counts every
/// row, counts every match and materializes one page, which measured p50 258.5 ms and p95 511.1 ms per
/// search; running that on the dispatcher froze the window for a quarter of a second per keystroke. The
/// durable query therefore runs on a bounded worker
/// (<see cref="IActivityQueryExecutor"/>), and only the publication - applying the page, reconciling the
/// list, stamping visibility - occupies the UI thread. Search, filters, paging and stream refreshes all
/// share one outstanding-query slot and one publication point, so the newest request is always the one
/// whose result reaches the screen and a superseded result is discarded rather than shown.
/// </para>
/// </summary>
public sealed class ActivityCenterViewModel : ObservableObject, IDisposable
{
    public const int DefaultPageSize = 200;

    public const string NotReportedPlaceholder = ObservableRunProjection.NotReportedPlaceholder;

    /// <summary>
    /// Shortest interval between two dispatched list rebuilds.
    /// <para>
    /// The bound is a <em>rate</em> limit, not a quiet period. A debounce that waits for the stream to go
    /// quiet is a direct latency floor: with the normative 200 ms budget and a 50 events/second stream, a
    /// 100 ms trailing window would spend half the budget doing nothing. Bounding the refresh rate instead
    /// caps the rebuilds at twenty per second - which is what actually protects the dispatcher and the
    /// index - while adding at most this interval to any one event's visibility.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultMinimumRefreshInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long a keystroke waits before its query is started.
    /// <para>
    /// Only typing uses this. Refresh, filter and page actions bypass it entirely, so they stay
    /// predictable, and the append stream bypasses it too - folding a stream refresh into a typing pause
    /// would make the newest events wait for an operator to stop typing. The window exists because the
    /// search box is bound with <c>UpdateSourceTrigger=PropertyChanged</c>: without it every character
    /// would start a durable query over the whole retained set, and the worker would spend its life
    /// answering keystrokes the operator has already replaced.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultSearchDebounce = TimeSpan.FromMilliseconds(200);

    private readonly IActivityCenterService _service;
    private readonly TimeProvider _timeProvider;
    private readonly IClipboardService? _clipboard;
    private readonly IActivityUiScheduler _scheduler;
    private readonly IActivityQueryExecutor _queryExecutor;
    private readonly ActivityVisibilityLatencyRecorder _latency;
    private readonly TimeSpan _minimumRefreshInterval;
    private readonly TimeSpan _searchDebounce;
    private readonly List<ActivityFilterOptionViewModel> _roleFilters;
    private readonly List<ActivityFilterOptionViewModel> _stateFilters;
    private readonly int _pageSize;
    private readonly object _coalesceGate = new();

    private ActivityFilterResult _result = ActivityFilterResult.Empty;
    private ActivityTimeRangeOption _selectedTimeRange;
    private ActivitySourceOption _selectedSource;
    private ActivityEventItemViewModel? _selectedEvent;
    private EmptyStateViewModel _emptyState;
    private ErrorStateViewModel _errorState;
    private LoadingStateViewModel _loadingState;
    private string _searchQuery = string.Empty;
    private int _currentPage = 1;
    private int _pageCount = 1;
    private bool _autoScroll = true;
    private bool _isDiffViewerOpen;
    private bool _hasError;
    private bool _suppressRefresh;
    private bool _refreshScheduled;
    private bool _refreshRequestedWhileRunning;
    private bool _refreshStarted;
    private bool _refreshArmedForTyping;
    private long _debounceEpoch;
    private bool _pendingResetPage;
    private bool _isQuerying;
    private long _requestGeneration;
    private long _lastRefreshCompletedTimestamp;
    private long _coalescedAppendCount;
    private long _refreshCount;
    private long _staleRefreshCount;
    private long _staleResultCount;
    private long _queryFailureCount;
    private double _lastDispatcherWorkMilliseconds;
    private double _maxDispatcherWorkMilliseconds;
    private double _lastQueryWorkMilliseconds;
    private double _maxQueryWorkMilliseconds;
    private bool _isDisposed;

    public ActivityCenterViewModel(
        IActivityCenterService service,
        DiffArtifactViewerViewModel? diffViewer = null,
        TimeProvider? timeProvider = null,
        IClipboardService? clipboard = null,
        int pageSize = DefaultPageSize,
        IActivityUiScheduler? scheduler = null,
        ActivityVisibilityLatencyRecorder? latencyRecorder = null,
        TimeSpan? minimumRefreshInterval = null,
        IActivityQueryExecutor? queryExecutor = null,
        TimeSpan? searchDebounce = null)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be positive.");
        }

        if (searchDebounce is { } debounce && debounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(searchDebounce),
                debounce,
                "The search debounce must not be negative.");
        }

        _service = service;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clipboard = clipboard;
        _pageSize = pageSize;
        _scheduler = scheduler ?? ImmediateActivityUiScheduler.Instance;
        // Inline by default so the headless compositions stay deterministic; the production shell binds
        // the bounded worker, and the two are interchangeable because the seam carries no WPF knowledge.
        _queryExecutor = queryExecutor ?? InlineActivityQueryExecutor.Instance;
        _latency = latencyRecorder ?? new ActivityVisibilityLatencyRecorder(_timeProvider);
        _minimumRefreshInterval = minimumRefreshInterval ?? DefaultMinimumRefreshInterval;
        _searchDebounce = searchDebounce ?? DefaultSearchDebounce;

        DiffViewer = diffViewer ?? new DiffArtifactViewerViewModel(clipboard);

        TimeRangeOptions = new[]
        {
            new ActivityTimeRangeOption(ActivityTimeRange.All, ActivityFilterCriteria.DescribeTimeRange(ActivityTimeRange.All)),
            new ActivityTimeRangeOption(ActivityTimeRange.Last15Minutes, ActivityFilterCriteria.DescribeTimeRange(ActivityTimeRange.Last15Minutes)),
            new ActivityTimeRangeOption(ActivityTimeRange.LastHour, ActivityFilterCriteria.DescribeTimeRange(ActivityTimeRange.LastHour)),
            new ActivityTimeRangeOption(ActivityTimeRange.Last24Hours, ActivityFilterCriteria.DescribeTimeRange(ActivityTimeRange.Last24Hours))
        };

        SourceOptions = new[]
        {
            new ActivitySourceOption(null, "Все источники"),
            new ActivitySourceOption(ActivityEventSource.Native, "Нативные"),
            new ActivitySourceOption(ActivityEventSource.Synthetic, "Синтетические")
        };

        _selectedTimeRange = TimeRangeOptions[0];
        _selectedSource = SourceOptions[0];

        _roleFilters = ActivityRoleNames.All
            .Select(role => new ActivityFilterOptionViewModel(role, OnFilterChanged))
            .ToList();

        _stateFilters = Enum.GetValues<ActivityEventState>()
            .Select(state => new ActivityFilterOptionViewModel(state.ToString(), OnFilterChanged, state))
            .ToList();

        RoleFilters = new ReadOnlyCollection<ActivityFilterOptionViewModel>(_roleFilters);
        StateFilters = new ReadOnlyCollection<ActivityFilterOptionViewModel>(_stateFilters);

        _emptyState = BuildEmptyState();
        _errorState = BuildErrorState();
        _loadingState = new LoadingStateViewModel("Загрузка событий активности…");

        ResetFiltersCommand = new RelayCommand(ResetFilters);
        RefreshCommand = new RelayCommand(() => RequestRefresh(resetPage: true, isTyping: false, isAppend: false));
        ClearSelectionCommand = new RelayCommand(() => SelectedEvent = null);
        DismissErrorCommand = new RelayCommand(DismissError);
        PreviousPageCommand = new RelayCommand(() => GoToPage(_currentPage - 1), () => HasPreviousPage);
        NextPageCommand = new RelayCommand(() => GoToPage(_currentPage + 1), () => HasNextPage);
        FirstPageCommand = new RelayCommand(() => GoToPage(1), () => HasPreviousPage);
        OpenSelectedDiffCommand = new RelayCommand(
            OpenSelectedDiff,
            () => SelectedEvent?.HasDetails == true);
        CloseDiffViewerCommand = new RelayCommand(CloseDiffViewer);

        // The screen follows the stream instead of waiting to be pulled. The handler only records the
        // stamp and asks for one coalesced refresh; it never queries on the appending thread.
        _service.Appended += OnServiceAppended;
        _service.PersistenceChanged += OnPersistenceChanged;

        RequestRefresh(resetPage: true, isTyping: false, isAppend: false);
    }

    public string Title => "Центр активности";

    public string Description =>
        "Единый журнал запусков процессов, сессий, переходов здоровья и действий пользователя.";

    public string RoleFilterNote =>
        "Роли — это операторские метки: событие сохраняет роль, сообщённую доменом, а неизвестные метки отображаются как System.";

    public string SearchNote =>
        "Полнотекстовый поиск выполняется только по очищенному тексту; секретные значения никогда не индексируются и не появляются в результатах.";

    public ObservableCollection<ActivityEventItemViewModel> Events { get; } = new();

    public IReadOnlyList<ActivityTimeRangeOption> TimeRangeOptions { get; }

    public IReadOnlyList<ActivitySourceOption> SourceOptions { get; }

    public IReadOnlyList<ActivityFilterOptionViewModel> RoleFilters { get; }

    public IReadOnlyList<ActivityFilterOptionViewModel> StateFilters { get; }

    public DiffArtifactViewerViewModel DiffViewer { get; }

    public ICommand ResetFiltersCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand ClearSelectionCommand { get; }

    public ICommand DismissErrorCommand { get; }

    public ICommand PreviousPageCommand { get; }

    public ICommand NextPageCommand { get; }

    public ICommand FirstPageCommand { get; }

    public ICommand OpenSelectedDiffCommand { get; }

    public ICommand CloseDiffViewerCommand { get; }

    public ActivityTimeRangeOption SelectedTimeRangeOption
    {
        get => _selectedTimeRange;
        set
        {
            if (value is not null && SetProperty(ref _selectedTimeRange, value))
            {
                OnFilterChanged();
            }
        }
    }

    public ActivitySourceOption SelectedSourceOption
    {
        get => _selectedSource;
        set
        {
            if (value is not null && SetProperty(ref _selectedSource, value))
            {
                OnFilterChanged();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var normalized = value ?? string.Empty;

            if (SetProperty(ref _searchQuery, normalized))
            {
                OnPropertyChanged(nameof(HasSearchQuery));

                if (!_suppressRefresh)
                {
                    // Typed, and therefore the only request kind that waits out the debounce. Routing this
                    // through the filter path would put every keystroke on the 50 ms refresh rate limit
                    // instead, which is a durable query per character on a 190 000-row journal.
                    RequestRefresh(resetPage: true, isTyping: true, isAppend: false);
                }
            }
        }
    }

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(_searchQuery);

    public ActivityEventItemViewModel? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (SetProperty(ref _selectedEvent, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                LoadSelectionIntoViewer(value);
            }
        }
    }

    public bool HasSelection => _selectedEvent is not null;

    public bool IsDiffViewerOpen
    {
        get => _isDiffViewerOpen;
        private set => SetProperty(ref _isDiffViewerOpen, value);
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public int PageSize => _pageSize;

    public int CurrentPage => _currentPage;

    public int PageCount => _pageCount;

    public int TotalCount => _result.TotalCount;

    public int FilteredCount => _result.FilteredCount;

    public bool HasEvents => Events.Count > 0;

    /// <summary>
    /// True when the screen has nothing to show and is not on its way to something.
    /// <para>
    /// The loading state suppresses the empty state rather than stacking on top of it. The three overlays
    /// share one cell in the view, so an empty list with a query in flight would paint the "no events"
    /// message and the spinner at the same time - and the empty message would be a lie, because the
    /// retained history is only being counted.
    /// </para>
    /// </summary>
    public bool IsEmpty => !_hasError && !_isQuerying && Events.Count == 0;

    /// <summary>True while a durable query is running and its result has not been published yet.</summary>
    public bool IsLoading => _isQuerying;

    public bool HasError => _hasError;

    public string TotalCountDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"{_result.TotalCount} событий записано");

    public string FilteredCountDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"{_result.FilteredCount} найдено из {_result.TotalCount}");

    public string PageDisplay => string.Create(
        CultureInfo.InvariantCulture,
        $"Страница {_currentPage} из {_pageCount}");

    public bool HasPreviousPage => _currentPage > 1;

    public bool HasNextPage => _currentPage < _pageCount;

    public bool IsTruncated => _result.IsTruncated;

    public string TruncationDisplay => _result.IsTruncated
        ? "More events match the current filters; use the pager or narrow the filters."
        : string.Empty;

    /// <summary>True once the product has dropped an event to stay inside its bound.</summary>
    public bool HasOverflowed => _result.HasOverflowed;

    /// <summary>
    /// Always non-empty overflow accounting. Silent eviction cannot be a pass: an operator looking at a
    /// bounded stream has to be able to see, without opening a report, that older events were dropped and
    /// how many.
    /// </summary>
    public string OverflowDisplay => _result.Retention.OverflowDisplay;

    /// <summary>Machine-readable retention facts for the measurement report.</summary>
    public IReadOnlyDictionary<string, string> RetentionFacts => _result.Retention.ToFacts();

    /// <summary>
    /// Number of list rebuilds this screen actually performed. Compared against the number of appends it
    /// received it is the evidence that the refreshes really were coalesced.
    /// </summary>
    public long RefreshCount => Interlocked.Read(ref _refreshCount);

    /// <summary>Appends that were folded into an already-scheduled refresh.</summary>
    public long CoalescedAppendCount => Interlocked.Read(ref _coalescedAppendCount);

    /// <summary>Dispatched callbacks that found no outstanding work to redo.</summary>
    public long StaleRefreshCount => Interlocked.Read(ref _staleRefreshCount);

    /// <summary>
    /// Query results and query errors that arrived after a newer request had already been made, and were
    /// therefore discarded instead of published.
    /// <para>
    /// This is the number the previous synchronous design could not produce at all, because a query and its
    /// publication were the same uninterrupted block on the UI thread. It is also the number that would
    /// grow if a slow query were ever published anyway: a screen that showed a superseded result would
    /// report a match count for criteria the operator has already replaced.
    /// </para>
    /// </summary>
    public long StaleResultCount => Interlocked.Read(ref _staleResultCount);

    /// <summary>Durable queries that failed and were reported through the error state.</summary>
    public long QueryFailureCount => Interlocked.Read(ref _queryFailureCount);

    /// <summary>Wall time of the most recent dispatched refresh, in milliseconds.</summary>
    public double LastDispatcherWorkMilliseconds => _lastDispatcherWorkMilliseconds;

    /// <summary>Slowest dispatched refresh observed, in milliseconds.</summary>
    public double MaxDispatcherWorkMilliseconds => _maxDispatcherWorkMilliseconds;

    /// <summary>Wall time of the most recent off-dispatcher durable query, in milliseconds.</summary>
    public double LastQueryWorkMilliseconds => _lastQueryWorkMilliseconds;

    /// <summary>Slowest off-dispatcher durable query observed, in milliseconds.</summary>
    public double MaxQueryWorkMilliseconds => _maxQueryWorkMilliseconds;

    /// <summary>Latency recorder that spans the ingestion stamp and the WPF-visible stamp.</summary>
    public ActivityVisibilityLatencyRecorder Latency => _latency;

    /// <summary>
    /// Harvests the latency samples accumulated so far. The workload driver calls this outside its send
    /// loop, so the producer is never charged for the measurement.
    /// </summary>
    public IReadOnlyList<ActivityLatencySample> HarvestLatencySamples() => _latency.DrainSamples();

    /// <summary>Stops following the stream. The screen keeps its last state and stops scheduling work.</summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _service.Appended -= OnServiceAppended;
        _service.PersistenceChanged -= OnPersistenceChanged;

        lock (_coalesceGate)
        {
            // A query already on the worker is left to finish: the durable read cannot be un-issued, and
            // abandoning it would be a lie about a snapshot that is being taken. What disposal does stop
            // is publication, which is what would otherwise resurrect a disposed screen.
            _refreshRequestedWhileRunning = false;
            _refreshScheduled = false;
            _refreshStarted = false;
            _refreshArmedForTyping = false;
            _debounceEpoch++;
        }
    }

    public EmptyStateViewModel EmptyState
    {
        get => _emptyState;
        private set => SetProperty(ref _emptyState, value);
    }

    public LoadingStateViewModel LoadingState
    {
        get => _loadingState;
        private set => SetProperty(ref _loadingState, value);
    }

    public ErrorStateViewModel ErrorState
    {
        get => _errorState;
        private set => SetProperty(ref _errorState, value);
    }

    public string ErrorMessage { get; private set; } = string.Empty;

    /// <summary>Re-runs the query with the current criteria.</summary>
    public void Refresh() => RequestRefresh(resetPage: true, isTyping: false, isAppend: false);

    /// <summary>Clears every filter and returns to the first page.</summary>
    public void ResetFilters()
    {
        _suppressRefresh = true;

        try
        {
            SelectedTimeRangeOption = TimeRangeOptions[0];
            SelectedSourceOption = SourceOptions[0];
            SearchQuery = string.Empty;

            foreach (var filter in _roleFilters)
            {
                filter.IsSelected = false;
            }

            foreach (var filter in _stateFilters)
            {
                filter.IsSelected = false;
            }
        }
        finally
        {
            _suppressRefresh = false;
        }

        RequestRefresh(resetPage: true, isTyping: false, isAppend: false);
    }

    public void GoToPage(int page)
    {
        var target = Math.Clamp(page, 1, _pageCount);

        if (target == _currentPage)
        {
            return;
        }

        _currentPage = target;
        NotifyPageState();
        RequestRefresh(resetPage: false, isTyping: false, isAppend: false);
    }

    /// <summary>Shows the standard error state; used by the composition when a data source fails.</summary>
    public void ShowError(string title, string message, string? technicalDetails = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _hasError = true;
        ErrorMessage = message;
        ErrorState = new ErrorStateViewModel(title, message, technicalDetails, RefreshCommand, _clipboard);
        NotifyQueryState();
    }

    public void DismissError()
    {
        _hasError = false;
        ErrorMessage = string.Empty;
        ErrorState = BuildErrorState();
        NotifyQueryState();
    }

    public void OpenSelectedDiff()
    {
        if (SelectedEvent?.HasDetails == true)
        {
            IsDiffViewerOpen = true;
        }
    }

    public void CloseDiffViewer() => IsDiffViewerOpen = false;

    private void OnFilterChanged()
    {
        if (_suppressRefresh)
        {
            return;
        }

        RequestRefresh(resetPage: true, isTyping: false, isAppend: false);
    }

    /// <summary>
    /// Append notification of the ingestion boundary. Runs on whichever thread produced the event, so it
    /// does the minimum: record the stamp the service took, then ask for one coalesced refresh. A burst
    /// of fifty appends inside the coalescing window costs one list rebuild, not fifty.
    /// </summary>
    private void OnServiceAppended(object? sender, ActivityEventAppendedEventArgs notice)
    {
        if (_isDisposed)
        {
            return;
        }

        _latency.RecordIngested(notice.EventId, notice.IngestedTimestamp);
        RequestCoalescedRefresh();
    }

    private void OnPersistenceChanged(object? sender, EventArgs args)
    {
        if (!_isDisposed) RequestCoalescedRefresh();
    }

    /// <summary>
    /// Asks for a refresh without ever running the query here, on this thread, or more than once per
    /// outstanding slot. At most one refresh is ever outstanding: appends that arrive while a refresh is
    /// running or waiting are folded into a single follow-up, which is what keeps the dispatcher queue
    /// bounded under a 50 events/second stream. The follow-up is delayed only by whatever is left of the
    /// minimum interval, so an idle screen refreshes immediately.
    /// <para>
    /// The pending work is <em>not</em> cancelled by a later append. Cancelling it is what an earlier
    /// version of this did, by treating every append as a new generation: the already-posted refresh then
    /// found itself superseded, returned without refreshing, and - because nothing re-posted - the screen
    /// silently froze on whatever it had shown when the stream started. Under a steady stream every
    /// refresh was stale, so the operator watched a list that stopped growing.
    /// </para>
    /// </summary>
    public void RequestCoalescedRefresh() => RequestRefresh(resetPage: false, isTyping: false, isAppend: true);

    /// <summary>
    /// The single entry point every refresh goes through: a search keystroke, a filter toggle, a page
    /// change, the refresh button and the append stream.
    /// <para>
    /// It records a request generation, applies the page reset, and either takes the one outstanding
    /// refresh slot or folds into the refresh that already holds it. Folding is the whole coalescing
    /// contract; cancelling is not, for the reason spelled out on <see cref="RequestCoalescedRefresh"/>.
    /// The generation is what makes "the latest request wins" decidable at publication time, when the
    /// result of a query the operator has already replaced is handed back.
    /// </para>
    /// <para>
    /// Called from the UI thread for every operator action. Called from an appending thread for the
    /// stream, which is why the only field a non-UI caller can touch is <c>resetPage</c> - and it always
    /// passes <c>false</c> there, so the page number is never mutated off the UI thread.
    /// </para>
    /// </summary>
    private void RequestRefresh(bool resetPage, bool isTyping, bool isAppend)
    {
        TimeSpan? delay = null;
        var takesSlot = false;
        var folded = false;

        lock (_coalesceGate)
        {
            if (_isDisposed)
            {
                return;
            }

            if (resetPage)
            {
                _pendingResetPage = true;
                _currentPage = 1;
            }

            if (!isAppend)
            {
                // Only an operator request makes the current answer the wrong one. An append does not: the
                // query reads the journal as it stands when it runs, so a query already in flight is
                // answering the newest data and must not be thrown away because events arrived while it
                // ran. Bumping the generation on appends would discard every in-flight result under a live
                // stream, and the screen would then never publish anything at all.
                _requestGeneration++;
            }

            if (_refreshScheduled)
            {
                var supersedableTypingPause = _refreshArmedForTyping && !_refreshStarted;

                if (!supersedableTypingPause || isTyping)
                {
                    // Folding, not cancelling. The follow-up flag is only set when the outstanding refresh has
                    // already snapshotted its criteria - a query that has not started yet reads the criteria
                    // when it does, so it is already answering the newest text and re-querying after it would
                    // be a second durable search over the whole retained set for the same answer.
                    //
                    // An append always sets it, whether or not a query has started. The stream follows itself:
                    // a refresh that is already posted is a refresh of the current criteria, but the
                    // requirement is that an append during a query always leads to one, not to none.
                    if (isAppend || _refreshStarted)
                    {
                        _refreshRequestedWhileRunning = true;
                    }

                    if (isAppend)
                    {
                        _coalescedAppendCount++;
                    }

                    folded = true;
                }
                else
                {
                    // A deliberate action, or a live stream, supersedes a typing pause that has not elapsed yet.
                    // Without this the refresh button, a filter toggle and every stream append would sit behind the
                    // operator's 200 ms typing pause, and that pause would then fire one redundant query: the
                    // timer is invalidated here and finds the slot consumed when it eventually runs.
                    _debounceEpoch++;
                }
            }

            if (!folded)
            {
                _refreshScheduled = true;
                _refreshStarted = false;
                _refreshArmedForTyping = isTyping;
                delay = isTyping ? _searchDebounce : RateLimitDelayLocked();
                takesSlot = true;
            }
        }

        if (resetPage)
        {
            NotifyPageState();
        }

        if (takesSlot)
        {
            long epoch;

            lock (_coalesceGate)
            {
                epoch = _debounceEpoch;
            }

            _scheduler.Post(() => RunCoalescedRefresh(epoch, isTyping), delay);
        }
    }

    /// <summary>
    /// The dispatched half of a refresh: it takes the criteria as they are right now and hands them to
    /// the query worker. It never queries here, and it never publishes.
    /// </summary>
    private void RunCoalescedRefresh(long debounceEpoch, bool isTyping)
    {
        bool resetPage;
        long generation;

        lock (_coalesceGate)
        {
            if (isTyping && debounceEpoch != _debounceEpoch)
            {
                // The keystroke pause this timer was armed for has been superseded by a deliberate action
                // that has already taken the slot. The slot is deliberately left alone: clearing it here
                // would leave the newer post with nothing to consume and the screen would freeze.
                Interlocked.Increment(ref _staleRefreshCount);
                return;
            }

            if (!_refreshScheduled || _isDisposed || _refreshStarted)
            {
                // The slot is already consumed by a run that is either finished or still querying. Nothing to
                // redo: a query is already in flight, and starting a second one would put two durable reads
                // against the journal at once and race the one publication slot they would both publish to.
                Interlocked.Increment(ref _staleRefreshCount);
                return;
            }

            _refreshStarted = true;
            _refreshArmedForTyping = false;
            resetPage = _pendingResetPage;
            _pendingResetPage = false;
            generation = _requestGeneration;
        }

        StartQuery(generation, resetPage);
    }

    /// <summary>
    /// Snapshots the criteria and starts the durable query on the worker.
    /// <para>
    /// The criteria are snapshotted here, on the UI thread, and the whole of them - text, roles, states,
    /// provenance, window and page - travel to the worker as one value. Reading the filter state from the
    /// worker instead would be a data race against a keystroke, and the query would answer a mixture of
    /// two requests.
    /// </para>
    /// </summary>
    private void StartQuery(long generation, bool resetPage)
    {
        if (_isDisposed)
        {
            return;
        }

        var criteria = BuildCriteria();
        var page = resetPage ? 1 : _currentPage;

        SetQuerying(true);

        _queryExecutor.Enqueue(() => RunQuery(generation, criteria, page));
    }

    /// <summary>
    /// Runs the durable query on the query worker. This is the only place a durable count, match count or
    /// page is materialized, and it is deliberately not the dispatcher.
    /// <para>
    /// The synchronous <see cref="IActivityCenterService.QueryPage"/> and
    /// <see cref="IActivityEventJournal.QueryPage"/> contracts are unchanged - one deferred WAL read
    /// transaction still spans the retained total, the match count and the page, so concurrent commits
    /// cannot make the match count exceed the total. What changed is the thread: the same three
    /// statements now run here, and the UI thread is free to keep taking input while they do.
    /// </para>
    /// <para>
    /// Every exception is caught here rather than allowed to reach the dispatcher. A malformed query used
    /// to surface as a <c>SqliteException</c> from a keystroke handler, which on the WPF dispatcher is an
    /// unhandled exception and a lost session.
    /// </para>
    /// </summary>
    private void RunQuery(long generation, ActivityFilterCriteria criteria, int page)
    {
        var startedAt = _latency.Stamp();
        ActivityFilterResult? result = null;
        Exception? failure = null;

        try
        {
            result = _service.QueryPage(criteria, page, _pageSize);
            // A retention/removal can shrink the match set while the operator stays on its last page.
            // Load the valid page on this same worker; never publish the old empty offset as that page.
            if (page > result.PageCount)
                result = _service.QueryPage(criteria, result.PageCount, _pageSize);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var elapsed = _latency.ElapsedMilliseconds(startedAt, _latency.Stamp());
        _latency.AddQueryWorkSample(elapsed);

        // Posted, not awaited: the only place UI state is written is the publication below, and it is
        // written on the UI thread. A query that outlives its screen is discarded there.
        _scheduler.Post(() => PublishQueryOutcome(generation, result, failure, elapsed));
    }

    /// <summary>
    /// The one UI publication point. Everything the screen shows after a query passes through here, which
    /// is what makes a search and a stream refresh unable to race: they share the slot, the generation
    /// check and the dispatcher, so <see cref="ApplyPage"/> and the visibility stamp are never
    /// interleaved.
    /// </summary>
    private void PublishQueryOutcome(
        long generation,
        ActivityFilterResult? result,
        Exception? failure,
        double queryMilliseconds)
    {
        if (_isDisposed)
        {
            return;
        }

        bool stale;

        lock (_coalesceGate)
        {
            stale = generation != _requestGeneration;
        }

        var dispatcherStartedAt = _latency.Stamp();
        var rebuilt = false;

        if (stale)
        {
            // Discarded, not published. Both halves: a superseded result would report a match count for
            // criteria the operator has already replaced, and a superseded error would show a failure for a
            // query nobody is waiting for any more.
            Interlocked.Increment(ref _staleResultCount);
        }
        else if (failure is OperationCanceledException)
        {
            // A cancelled query is not an error and is not a result; the screen keeps what it had.
        }
        else if (failure is not null)
        {
            Interlocked.Increment(ref _queryFailureCount);
            ShowError("Запрос активности не выполнен", "Не удалось загрузить события активности.", failure.GetType().Name);
        }
        else if (result is not null)
        {
            ApplyResult(result);
            rebuilt = true;
        }

        if (rebuilt)
        {
            _lastDispatcherWorkMilliseconds = _latency.ElapsedMilliseconds(
                dispatcherStartedAt,
                _latency.Stamp());
            _maxDispatcherWorkMilliseconds = Math.Max(
                _maxDispatcherWorkMilliseconds,
                _lastDispatcherWorkMilliseconds);
            _latency.AddDispatcherWorkSample(_lastDispatcherWorkMilliseconds);
            Interlocked.Increment(ref _refreshCount);
        }

        _lastQueryWorkMilliseconds = queryMilliseconds;
        _maxQueryWorkMilliseconds = Math.Max(_maxQueryWorkMilliseconds, queryMilliseconds);

        bool again;
        TimeSpan? delay = null;

        lock (_coalesceGate)
        {
            _refreshStarted = false;
            _refreshArmedForTyping = false;
            _lastRefreshCompletedTimestamp = _latency.Stamp();
            again = _refreshRequestedWhileRunning;
            _refreshRequestedWhileRunning = false;

            if (!again || _isDisposed)
            {
                _refreshScheduled = false;
            }
            else
            {
                delay = RateLimitDelayLocked();
            }
        }

        if (again && !_isDisposed)
        {
            // The loading state stays up across the gap: a query is genuinely about to run, and dropping
            // it for a few milliseconds would flash the empty state at an operator who is mid-search.
            _scheduler.Post(() => RunCoalescedRefresh(_debounceEpoch, isTyping: false), delay);
        }
        else
        {
            SetQuerying(false);
        }
    }

    /// <summary>
    /// Applies one published page: the exact durable totals, the reconciled list, the visibility stamp and
    /// the derived states. Runs on the UI thread, after the query, never during it.
    /// </summary>
    private void ApplyResult(ActivityFilterResult result)
    {
        _result = result;
        _pageCount = result.PageCount;

        if (_currentPage > _pageCount)
        {
            _currentPage = _pageCount;
        }

        _hasError = false;
        ErrorMessage = string.Empty;

        ApplyPage(result.Items);

        // Stamped after the list has been rebuilt: this is the moment the row is on the screen, so the
        // sample spans ingestion to visibility rather than ingestion to query-return. It is taken on the
        // dispatcher, after publication - never when the worker finished, which would understate the
        // latency by the whole wait for this callback to be dispatched.
        _latency.MarkVisible(VisibleIds());

        EmptyState = BuildEmptyState();
        ErrorState = BuildErrorState();
        NotifyQueryState();
    }

    /// <summary>Remaining wait before the next refresh is allowed, or null when there is none.</summary>
    private TimeSpan? RateLimitDelayLocked()
    {
        // The first refresh after a quiet period goes out immediately; a refresh that follows another one
        // waits out the remainder of the rate limit.
        if (_lastRefreshCompletedTimestamp == 0)
        {
            return null;
        }

        var sinceLast = _timeProvider.GetElapsedTime(_lastRefreshCompletedTimestamp);

        return sinceLast < _minimumRefreshInterval ? _minimumRefreshInterval - sinceLast : null;
    }

    private void SetQuerying(bool isQuerying)
    {
        if (_isQuerying == isQuerying)
        {
            return;
        }

        _isQuerying = isQuerying;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Reconciles the bound page with the queried page, touching only what actually changed.
    /// <para>
    /// The obvious implementation - clear the collection and add the page again - costs one full list
    /// teardown and re-template per refresh, and it pays that cost even when the page is identical to what
    /// is already bound. Measured on the shipped screen that was the difference between about one
    /// millisecond for a filter change that does not alter the list and over half a second for a press of
    /// the refresh button, with 200 rows the norm and 100 000 events behind them: under a 50 events/second
    /// stream that single line was the whole of the p95 latency.
    /// </para>
    /// <para>
    /// The reconciliation is expressed as <see cref="ObservableCollection{T}.Move"/> plus
    /// <see cref="ObservableCollection{T}.Insert"/>, never as remove-and-reinsert. A row that is still on
    /// the page but has shifted is <em>moved</em>, so the virtualizing panel keeps its realized container
    /// instead of throwing it away and re-templating it. Under a live stream the page shifts by a few
    /// positions per refresh, so this reduces a refresh to a handful of operations.
    /// </para>
    /// <para>
    /// The order matters and is not interchangeable: a row that had been removed and re-added instead of
    /// moved leaves the recycling <c>ItemContainerGenerator</c> disagreeing with the collection about
    /// which index was removed, and WPF throws the moment the list is next read.
    /// </para>
    /// </summary>
    private void ApplyPage(IReadOnlyList<ActivityEvent> page)
    {
        var desired = new ActivityEventItemViewModel[page.Count];

        for (var index = 0; index < page.Count; index++)
        {
            desired[index] = new ActivityEventItemViewModel(page[index]);
        }

        // Phase one: put the correct row at each position, moving it there when it is already bound and
        // inserting it when it is new. Items pushed out of the window end up at the tail.
        for (var position = 0; position < desired.Length; position++)
        {
            if (position < Events.Count
                && string.Equals(Events[position].Id, desired[position].Id, StringComparison.Ordinal))
            {
                continue;
            }

            var existing = IndexOfId(Events, desired[position].Id, position + 1);

            if (existing >= 0)
            {
                Events.Move(existing, position);
            }
            else
            {
                Events.Insert(position, desired[position]);
            }
        }

        // Phase two: everything past the page is a row that left the window.
        while (Events.Count > desired.Length)
        {
            Events.RemoveAt(Events.Count - 1);
        }
    }

    private static int IndexOfId(
        ObservableCollection<ActivityEventItemViewModel> items,
        string id,
        int fromIndex)
    {
        for (var index = fromIndex; index < items.Count; index++)
        {
            if (string.Equals(items[index].Id, id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private List<string> VisibleIds() => Events.Select(item => item.Id).ToList();

    private ActivityFilterCriteria BuildCriteria() => new()
    {
        TimeRange = _selectedTimeRange.Value,
        Roles = _roleFilters
            .Where(filter => filter.IsSelected)
            .Select(filter => filter.Display)
            .ToArray(),
        States = _stateFilters
            .Where(filter => filter.IsSelected && filter.State is not null)
            .Select(filter => filter.State!.Value)
            .ToArray(),
        Source = _selectedSource.Value,
        SearchQuery = _searchQuery,
        Limit = _pageSize
    };

    private void LoadSelectionIntoViewer(ActivityEventItemViewModel? item)
    {
        if (item is null)
        {
            DiffViewer.Clear();
            IsDiffViewerOpen = false;
            return;
        }

        var model = item.Model;

        if (model.HasDiff)
        {
            DiffViewer.LoadDiff(model.DiffText!, item.Title, model.ArtifactName);
            IsDiffViewerOpen = true;
            return;
        }

        if (model.HasArtifact)
        {
            DiffViewer.LoadArtifact(
                model.ArtifactName!,
                model.ArtifactContent!,
                model.ArtifactSizeBytes,
                model.ArtifactSha256,
                ParseChangeStatus(model.ArtifactChangeStatus));
            IsDiffViewerOpen = true;
            return;
        }

        DiffViewer.Clear();
        IsDiffViewerOpen = false;
    }

    private EmptyStateViewModel BuildEmptyState()
    {
        if (_result.TotalCount == 0)
        {
            return new EmptyStateViewModel(
                "Событий активности пока нет",
                "Запуски процессов, сессии, переходы здоровья и действия пользователя появятся здесь, как только будут зарегистрированы.",
                "◻");
        }

        return new EmptyStateViewModel(
            "Нет событий, соответствующих текущим фильтрам",
            string.Create(
                CultureInfo.InvariantCulture,
                $"0 найдено из {_result.TotalCount}. Расширьте период, роли, состояния, источник или запрос."),
            "⌕",
            "Сбросить фильтры",
            ResetFiltersCommand);
    }

    private ErrorStateViewModel BuildErrorState() =>
        new(
            "Запрос активности не выполнен",
            string.IsNullOrWhiteSpace(ErrorMessage) ? "Ошибка не сообщена." : ErrorMessage,
            technicalDetails: null,
            retryCommand: RefreshCommand,
            clipboard: _clipboard);

    private void NotifyQueryState()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(TotalCountDisplay));
        OnPropertyChanged(nameof(FilteredCountDisplay));
        OnPropertyChanged(nameof(PageDisplay));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(HasEvents));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsTruncated));
        OnPropertyChanged(nameof(TruncationDisplay));
        OnPropertyChanged(nameof(HasOverflowed));
        OnPropertyChanged(nameof(OverflowDisplay));

        // The pager commands are guarded by HasPreviousPage / HasNextPage, and WPF only re-evaluates a
        // command on user input. A refresh that arrives from the stream - which is now the normal way this
        // screen updates - therefore left the pager buttons stuck at whatever state they had before, so
        // "next page" stayed disabled on a two-page result until the operator happened to move the mouse.
        // Re-querying here is what keeps the pager honest under a live stream.
        RaisePagerCanExecuteChanged();
    }

    /// <summary>
    /// Raises only the page-dependent notifications. A search that resets the page shows page one from the
    /// moment it is requested, not from the moment its result lands: with the query off the dispatcher
    /// that gap is real time, and a pager that still claimed page three while the operator was already
    /// typing a new query would be reporting a page the screen was about to leave.
    /// </summary>
    private void NotifyPageState()
    {
        OnPropertyChanged(nameof(PageDisplay));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));

        RaisePagerCanExecuteChanged();
    }

    private static void RaisePagerCanExecuteChanged()
    {
        try
        {
            RelayCommand.RaiseCanExecuteChanged();
        }
        catch (InvalidOperationException)
        {
            // No command manager on this thread (a headless composition that never pumps messages).
            // The commands still evaluate correctly; only the automatic button refresh is unavailable.
        }
    }

    private static ArtifactChangeStatus ParseChangeStatus(string? value) =>
        Enum.TryParse<ArtifactChangeStatus>(value, ignoreCase: true, out var status)
            ? status
            : ArtifactChangeStatus.NotReported;
}
