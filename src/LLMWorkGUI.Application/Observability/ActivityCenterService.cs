namespace LLMWorkGUI.Application.Observability;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Bounded, thread-safe store and filter of the activity stream.
/// <para>
/// <see cref="Append"/> is the product ingestion boundary. It runs the redactor once, so the object this
/// service stores, indexes, journals, counts and hands back is already redacted: there is no second copy
/// of an event anywhere in the product that still holds a raw secret. It then takes a monotonic stamp,
/// offers the event to the durable journal, applies the capacity bound and raises <see cref="Appended"/>
/// with the counters needed to report every offered, accepted and evicted event.
/// </para>
/// <para>
/// The in-memory window is bounded, and that bound is a product property rather than a tuning knob: the
/// normative §9.2 profile is 100 000 already-saved events plus 90 000 more, and a window that grew to
/// hold all 190 000 would turn a memory requirement into a self-inflicted capacity choice. So when a
/// durable journal is composed, <b>the retained set is the journal</b>: queries are answered from it, the
/// exact match count covers every row the retention rule keeps, and paging and search can no longer
/// disagree about how much history exists. The window is the recent working set the screen and the
/// in-memory index use, and <see cref="LoadAsync"/> refills it from the journal after a restart.
/// </para>
/// </summary>
public sealed class ActivityCenterService : IActivityCenterService
{
    public const int DefaultCapacity = EventSearchIndex.DefaultCapacity;

    private readonly object _gate = new();
    private readonly Dictionary<string, ActivityEvent> _events = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly LinkedList<string> _ongoing = new();
    private readonly Dictionary<string, LinkedListNode<string>> _orderNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<string>> _ongoingNodes = new(StringComparer.Ordinal);
    private readonly IEventSearchIndex _searchIndex;
    private readonly ActivityEventRedactor _redactor;
    private readonly Func<string, string> _redactText;
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly ActivityJournalWriteQueue? _journalQueue;
    private readonly IActivityEventJournal? _journal;
    private readonly int _retentionLimit;
    private readonly ILogger<ActivityCenterService> _logger;

    private long _offered;
    private long _evicted;
    private long _replaced;
    private long _removed;
    private long _journalEvicted;
    private long _rehydrated;

    /// <param name="redactText">Redactor used by the built-in search index when none is supplied.</param>
    /// <param name="journalQueue">
    /// Optional durable write queue. When omitted the service is memory-only, which keeps the UI-only
    /// composition graphs working; the production composition binds it to the application database.
    /// </param>
    /// <param name="journal">
    /// Optional durable journal. When composed it is the retained set queries resolve against, and the
    /// startup reload refills the in-memory window from it.
    /// </param>
    /// <param name="retentionLimit">Durable retention limit applied by <see cref="ApplyJournalRetentionAsync"/>.</param>
    public ActivityCenterService(
        Func<string, string> redactText,
        TimeProvider? timeProvider = null,
        IEventSearchIndex? searchIndex = null,
        int capacity = DefaultCapacity,
        ActivityJournalWriteQueue? journalQueue = null,
        IActivityEventJournal? journal = null,
        int retentionLimit = ActivityJournalOptions.DefaultRetentionLimit,
        ILogger<ActivityCenterService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(redactText);

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        if (retentionLimit < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionLimit),
                retentionLimit,
                "The retention limit must not be negative.");
        }

        _redactor = new ActivityEventRedactor(redactText);
        _redactText = redactText;
        _searchIndex = searchIndex ?? new EventSearchIndex(redactText, capacity);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _capacity = capacity;
        _journalQueue = journalQueue;
        _journal = journal;
        _retentionLimit = retentionLimit;
        _logger = logger ?? NullLogger<ActivityCenterService>.Instance;
    }

    public event EventHandler<ActivityEventAppendedEventArgs>? Appended;

    public event EventHandler? PersistenceChanged
    {
        add { if (_journalQueue is not null) _journalQueue.Persisted += value; }
        remove { if (_journalQueue is not null) _journalQueue.Persisted -= value; }
    }

    public int Capacity => _capacity;

    /// <summary>
    /// Events currently held in the bounded in-memory window. This is <em>not</em> the size of the retained
    /// history: with a durable journal composed, <see cref="ActivityFilterResult.TotalCount"/> and
    /// <see cref="ActivityRetentionStatistics.JournalRetained"/> report that.
    /// </summary>
    public int TotalCount
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    public ActivityRetentionStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return BuildStatisticsLocked();
            }
        }
    }

    /// <summary>Rows the durable journal reported holding, when the last reload or trim observed them.</summary>
    public long? JournalRetained { get; private set; }

    /// <summary>Rows a durable retention run has removed from the journal.</summary>
    public long JournalEvicted => Interlocked.Read(ref _journalEvicted);

    /// <summary>Events the last <see cref="LoadAsync"/> pulled back out of the durable journal.</summary>
    public long RehydratedCount => Interlocked.Read(ref _rehydrated);

    /// <summary>True when queries resolve against the durable journal rather than the in-memory window.</summary>
    public bool IsDurable => _journal is not null;

    public void Append(ActivityEvent activityEvent) => AppendCore(activityEvent, isProjection: false);

    public void ReplayJournalEvent(ActivityEvent activityEvent)
    {
        ActivityJournalReplay.Validate(activityEvent);
        AppendCore(activityEvent, isProjection: false, isReplay: true);
    }

    private void AppendCore(ActivityEvent activityEvent, bool isProjection, bool isReplay = false)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);

        ActivityEventAppendedEventArgs? notice;

        lock (_gate)
        {
            // Redaction happens once, here, at the boundary every other component is downstream of - and
            // before indexing or persistence. Only the in-memory composition needs a second
            // searchable representation; the durable journal builds its own FTS index.
            var redacted = _redactor.Redact(activityEvent);
            if (isReplay && !ActivityJournalReplay.IsDiagnostic(redacted)
                && _events.TryGetValue(redacted.Id, out var existing)
                && !ActivityJournalReplay.HasSameEvidence(existing, redacted))
                throw new InvalidOperationException("Conflicting execution-journal replay evidence.");
            var searchText = _journal is null ? _redactText(ActivityEventSearchText.Build(redacted)) : null;
            _offered++;

            if (_events.ContainsKey(redacted.Id))
            {
                _replaced++;
                RemoveLocked(redacted.Id);
            }

            _events[redacted.Id] = redacted;
            _orderNodes[redacted.Id] = _order.AddLast(redacted.Id);

            if (IsOngoing(redacted.State))
            {
                _ongoingNodes[redacted.Id] = _ongoing.AddLast(redacted.Id);
            }

            // The text is already redacted at this boundary, so the index does not repeat that work while
            // the lock is held.
            // Durable queries use SQLite FTS. Keeping a second token index duplicates every
            // document in managed memory without serving any query.
            if (searchText is not null) _searchIndex.IndexRedacted(redacted, searchText);
            EvictLocked();

            if (isReplay) _journalQueue?.TryEnqueueReplay(redacted);
            else if (isProjection) _journalQueue?.TryEnqueueProjection(redacted);
            else _journalQueue?.TryEnqueue(redacted);

            // Stamped inside the boundary, not by the caller, so the latency clock is the product's.
            var ingestedAt = _timeProvider.GetTimestamp();

            notice = new ActivityEventAppendedEventArgs(
                redacted.Id,
                ingestedAt,
                _events.Count,
                _offered,
                _evicted);
        }

        // Raised outside the lock: a handler that calls back into a query must not deadlock.
        NotifyAppended(notice);
    }

    public void AppendRange(IEnumerable<ActivityEvent> activityEvents)
    {
        ArgumentNullException.ThrowIfNull(activityEvents);

        foreach (var activityEvent in activityEvents)
        {
            Append(activityEvent);
        }
    }

    public void AppendProjection(ObservableRunProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        AppendCore(ActivityEvent.FromExecutionProjection(projection), isProjection: true);
    }

    public void AppendTimeline(ActivityTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        foreach (var item in timeline.Items) AppendProjection(item.Run);
    }

    public void AppendWorkflowTimeline(WorkflowRunTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        AppendRange(timeline.Items.Select(ActivityEvent.FromWorkflowTimelineItem));
    }

    public void AppendHealthTransition(string scopeId, string stateDisplay, string detail)
    {
        Append(ActivityEvent.FromHealthTransition(
            scopeId,
            stateDisplay,
            detail,
            _timeProvider.GetUtcNow()));
    }

    public void AppendUserAction(string id, string title, string description)
    {
        Append(ActivityEvent.UserAction(id, title, description, _timeProvider.GetUtcNow()));
    }

    public void AppendSystemEvent(
        string id,
        string title,
        string description,
        ActivityEventState state = ActivityEventState.Warning)
    {
        Append(ActivityEvent.SystemEvent(id, title, description, _timeProvider.GetUtcNow(), state));
    }

    public bool Remove(string eventId)
    {
        ArgumentNullException.ThrowIfNull(eventId);

        lock (_gate)
        {
            var removed = RemoveLocked(eventId);
            if (removed) _removed++;
            return removed;
        }
    }

    /// <summary>
    /// Empties the bounded in-memory window. The durable journal is deliberately untouched: this is a
    /// screen-level reset, and deleting persisted history is the retention rule's job, not a view model's.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _removed += _events.Count;
            _events.Clear();
            _order.Clear();
            _orderNodes.Clear();
            _ongoing.Clear();
            _ongoingNodes.Clear();
            _searchIndex.Clear();
        }
    }

    /// <summary>
    /// Records the journal row count observed by a reload or retention run so the statistics can report
    /// the durable side of the accounting instead of only the in-memory side.
    /// </summary>
    public void SetJournalRetainedCount(long? rows)
    {
        if (rows is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "Row count must not be negative.");
        }

        JournalRetained = rows;
    }

    /// <summary>
    /// Applies the durable retention rule and reports what it removed. Retention is oldest-first and
    /// explicit: it runs at startup so the bound is a fact of the product rather than an aspiration, and
    /// every removed row is counted in <see cref="ActivityRetentionStatistics.JournalEvicted"/> instead of
    /// disappearing. No in-memory row is dropped by this call, and no other table is touched.
    /// </summary>
    public async Task<ActivityJournalTrimResult> ApplyJournalRetentionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_journal is null)
        {
            throw new InvalidOperationException(
                "No durable journal is composed, so there is no retention rule to apply.");
        }

        var result = await _journal.TrimAsync(_retentionLimit, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _journalEvicted, result.RowsRemoved);
        JournalRetained = result.RowsAfter;

        return result;
    }

    /// <summary>
    /// Refills the bounded in-memory window from the durable journal, newest first, so a fresh process
    /// shows the saved history on the shipped screen instead of an empty list.
    /// <para>
    /// This is the startup reload: it drains the write queue first (so the newest rows are actually
    /// durable), applies the retention rule, reads the exact retained count, and then replays the newest
    /// rows through <see cref="Append"/>. Replaying through the boundary rather than assigning the
    /// dictionary directly is deliberate: it is the only way the reloaded rows get indexed, offered to the
    /// journal, counted and announced through <see cref="Appended"/>, which is what makes the screen
    /// refresh itself instead of waiting for the operator.
    /// </para>
    /// </summary>
    public async Task<ActivityCenterLoadReport> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_journal is null)
        {
            return ActivityCenterLoadReport.NotDurable;
        }

        if (_journalQueue is not null)
        {
            await _journalQueue.DrainAsync(cancellationToken).ConfigureAwait(false);
        }

        var trimmed = await ApplyJournalRetentionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await _journal.CountAsync(cancellationToken).ConfigureAwait(false);
        var newest = await _journal
            .LoadNewestAsync(_capacity, cancellationToken)
            .ConfigureAwait(false);

        for (var index = newest.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Append is the ingestion boundary and counts these as offered/reloaded work. Replaying them
            // would queue them for another durable write; they are already durable, so the write is
            // skipped explicitly rather than silently re-persisting rows that are on disk.
            AppendReloaded(newest[index]);
        }

        Interlocked.Exchange(ref _rehydrated, newest.Count);
        JournalRetained = rows;

        return new ActivityCenterLoadReport(
            rows,
            trimmed.RowsRemoved,
            newest.Count,
            _capacity,
            _journalQueue is null
                ? null
                : new ActivityJournalWriteQueueFacts(
                    _journalQueue.QueueCapacity,
                    _journalQueue.BatchCapacity,
                    _journalQueue.OfferedCount,
                    _journalQueue.WrittenCount,
                    _journalQueue.DroppedCount,
                    _journalQueue.FailedCount,
                    _journalQueue.IsBalanced));
    }

    public ActivityFilterResult Query(ActivityFilterCriteria? criteria = null)
    {
        criteria ??= ActivityFilterCriteria.Default;

        var limit = Math.Max(1, criteria.Limit);

        if (_journal is not null)
        {
            return QueryDurable(criteria, page: 1, pageSize: limit, limit: limit);
        }

        lock (_gate)
        {
            var matches = new List<ActivityEvent>();

            if (criteria.HasSearchQuery)
            {
                // Only the index performs free-text matching, and it only knows redacted text.
                foreach (var candidate in _searchIndex.Search(criteria.SearchQuery, _capacity))
                {
                    if (_events.TryGetValue(candidate.Id, out var current)
                        && criteria.MatchesFilters(current, now: _timeProvider.GetUtcNow()))
                    {
                        matches.Add(current);
                    }
                }
            }
            else
            {
                for (var node = _order.Last; node is not null; node = node.Previous)
                {
                    if (criteria.MatchesFilters(_events[node.Value], _timeProvider.GetUtcNow()))
                    {
                        matches.Add(_events[node.Value]);
                    }
                }
            }

            var filteredCount = matches.Count;
            var isTruncated = filteredCount > limit;

            if (isTruncated)
            {
                matches = matches.GetRange(0, limit);
            }

            return new ActivityFilterResult(_events.Count, filteredCount, isTruncated, matches, BuildStatisticsLocked());
        }
    }

    /// <summary>
    /// Materializes exactly one page while still reporting the exact filtered count. The whole stream is
    /// walked to count - that is unavoidable if the count must be exact - but only the requested window
    /// is turned into event objects, which is what keeps a deep page on a 100 000-event stream cheap.
    /// </summary>
    public ActivityFilterResult QueryPage(
        ActivityFilterCriteria? criteria = null,
        int page = 1,
        int pageSize = ActivityFilterCriteria.DefaultLimit)
    {
        criteria ??= ActivityFilterCriteria.Default;

        if (page <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "Page must be positive.");
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be positive.");
        }

        if (_journal is not null)
        {
            return QueryDurable(criteria, page, pageSize, limit: pageSize);
        }

        var now = _timeProvider.GetUtcNow();
        var offset = (long)(page - 1) * pageSize;
        var items = new List<ActivityEvent>(pageSize);
        var filteredCount = 0L;

        lock (_gate)
        {
            if (criteria.HasSearchQuery)
            {
                foreach (var id in _searchIndex.SearchIds(criteria.SearchQuery, _capacity))
                {
                    if (!_events.TryGetValue(id, out var current) || !criteria.MatchesFilters(current, now))
                    {
                        continue;
                    }

                    if (filteredCount >= offset && items.Count < pageSize)
                    {
                        items.Add(current);
                    }

                    filteredCount++;
                }
            }
            else
            {
                for (var node = _order.Last; node is not null; node = node.Previous)
                {
                    var candidate = _events[node.Value];

                    if (!criteria.MatchesFilters(candidate, now))
                    {
                        continue;
                    }

                    if (filteredCount >= offset && items.Count < pageSize)
                    {
                        items.Add(candidate);
                    }

                    filteredCount++;
                }
            }

            var pageCount = filteredCount == 0
                ? 1
                : (int)Math.Ceiling(filteredCount / (double)pageSize);
            var isTruncated = filteredCount > offset + items.Count;

            return new ActivityFilterResult(
                _events.Count,
                ClampToInt32(filteredCount),
                isTruncated,
                items,
                BuildStatisticsLocked(),
                pageCount);
        }
    }

    public IReadOnlyList<ActivityEvent> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new List<ActivityEvent>(_events.Count);

            for (var node = _order.Last; node is not null; node = node.Previous)
            {
                snapshot.Add(_events[node.Value]);
            }

            return snapshot;
        }
    }

    /// <summary>
    /// Answers one page from the durable journal, so the exact match count covers every retained row and
    /// not only the newest ones a process happens to hold. The in-memory window is not consulted: two
    /// sources of truth is exactly how the previous design ended up with a search that saw 100 000 of the
    /// 190 000 rows a pager could reach.
    /// </summary>
    private ActivityFilterResult QueryDurable(
        ActivityFilterCriteria criteria,
        int page,
        int pageSize,
        int limit)
    {
        var journal = _journal ?? throw new InvalidOperationException("No durable journal is composed.");
        var now = _timeProvider.GetUtcNow();
        var durablePage = journal.QueryPage(ActivityJournalQuery.FromCriteria(criteria, now, page, pageSize));
        var filteredCount = durablePage.MatchedRows;
        var offset = (long)(page - 1) * pageSize;
        var isTruncated = filteredCount > offset + durablePage.Items.Count;
        var items = durablePage.Items.Take(limit).Select(_redactor.Redact).ToList();

        var pageCount = filteredCount == 0
            ? 1
            : (int)Math.Ceiling(filteredCount / (double)pageSize);

        ActivityRetentionStatistics statistics;

        lock (_gate)
        {
            JournalRetained = durablePage.TotalRows;
            statistics = BuildStatisticsLocked();
            // The durable journal stores metadata, not diff/artifact bodies. Keep the
            // redacted payload of an observation that is still in the bounded live window.
            for (var index = 0; index < items.Count; index++)
            {
                var row = items[index];
                if (_events.TryGetValue(row.Id, out var live)
                    && (live.DiffText is not null || live.ArtifactContent is not null)
                    && live.OccurredAtUtc == row.OccurredAtUtc && live.State == row.State
                    && live.Title == row.Title && live.Description == row.Description
                    && live.ArtifactSha256 == row.ArtifactSha256)
                {
                    items[index] = new ActivityEvent(row.Id, row.OccurredAtUtc, row.Kind, row.Role,
                        row.State, row.Source, row.Title, row.Description, row.SessionId, row.ExecutionId,
                        row.RouteId, live.DiffText, row.ArtifactName, live.ArtifactContent,
                        row.ArtifactSizeBytes, row.ArtifactSha256, row.ArtifactChangeStatus);
                }
            }
        }

        return new ActivityFilterResult(
            ClampToInt32(durablePage.TotalRows),
            ClampToInt32(filteredCount),
            isTruncated,
            items,
            statistics,
            pageCount);
    }

    /// <summary>
    /// Stores one event read back from the durable journal. It takes the same path as an accepted append -
    /// redaction, index, eviction, monotonic stamp, <see cref="Appended"/> - but is not offered to the
    /// write queue, because the row it came from is already on disk.
    /// </summary>
    private void AppendReloaded(ActivityEvent activityEvent)
    {
        ActivityEventAppendedEventArgs? notice;

        lock (_gate)
        {
            var redacted = _redactor.Redact(activityEvent);
            var searchText = _journal is null ? _redactText(ActivityEventSearchText.Build(redacted)) : null;
            _offered++;

            if (_events.ContainsKey(redacted.Id))
            {
                _replaced++;
                RemoveLocked(redacted.Id);
            }

            _events[redacted.Id] = redacted;
            _orderNodes[redacted.Id] = _order.AddLast(redacted.Id);

            if (IsOngoing(redacted.State))
            {
                _ongoingNodes[redacted.Id] = _ongoing.AddLast(redacted.Id);
            }

            // Durable queries use SQLite FTS. Keeping a second token index duplicates every
            // document in managed memory without serving any query.
            if (searchText is not null) _searchIndex.IndexRedacted(redacted, searchText);
            EvictLocked();

            notice = new ActivityEventAppendedEventArgs(
                redacted.Id,
                _timeProvider.GetTimestamp(),
                _events.Count,
                _offered,
                _evicted);
        }

        NotifyAppended(notice);
    }

    private void NotifyAppended(ActivityEventAppendedEventArgs notice)
    {
        var handlers = Appended;
        if (handlers is null) return;
        foreach (EventHandler<ActivityEventAppendedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, notice); }
            catch (Exception exception)
            {
                _logger.LogWarning("Activity subscriber failed with {ExceptionType}.", exception.GetType().Name);
            }
        }
    }

    private ActivityRetentionStatistics BuildStatisticsLocked() =>
        new(
            _capacity,
            _events.Count,
            _offered,
            _evicted,
            JournalRetained,
            JournalEvicted,
            _journalQueue?.DroppedCount ?? 0,
            _journalQueue?.FailedCount ?? 0,
            replaced: _replaced,
            removed: _removed);

    private static int ClampToInt32(long value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;

    private bool RemoveLocked(string eventId)
    {
        if (!_events.ContainsKey(eventId))
        {
            return false;
        }

        DropLocked(eventId);
        return true;
    }

    /// <summary>
    /// Keeps the in-memory window inside its bound, oldest first, but never lets a terminal outcome be
    /// dropped while an older still-ongoing observation sits in the window.
    /// <para>
    /// A bounded window has to drop something, and dropping purely by age throws away exactly the rows an
    /// operator needs: a failed or cancelled execution whose outcome arrived early, in favour of a
    /// <c>Running</c> row for an execution that is still in flight and will report again. So the oldest
    /// ongoing row is evicted first; only when every retained row is terminal does the oldest-terminal
    /// fallback apply. Either way the drop is counted in <see cref="ActivityRetentionStatistics.Evicted"/>
    /// and surfaced by the overflow label, and the durable journal keeps the row either way - nothing is
    /// lost, only evicted from the window.
    /// </para>
    /// <para>
    /// The ongoing rows are indexed in their own list so choosing a victim stays O(1). Scanning the whole
    /// window per eviction would be 100 000 comparisons for every one of the 90 000 evictions of the
    /// normative profile.
    /// </para>
    /// </summary>
    private void EvictLocked()
    {
        while (_events.Count > _capacity)
        {
            var victim = _ongoing.First ?? _order.First;

            if (victim is null)
            {
                return;
            }

            DropLocked(victim.Value);

            // Counted, never silent: the normative profile requires every evicted event to be reported.
            _evicted++;
        }
    }

    /// <summary>
    /// Removes one event from the bounded window and from both ordering lists. Used by eviction, by a
    /// duplicate-id replacement and by the public <see cref="Remove"/>; all three must keep the window
    /// index and the ongoing-row index in step or the next eviction would pick a row that is gone.
    /// </summary>
    private void DropLocked(string eventId)
    {
        _events.Remove(eventId);
        _searchIndex.Remove(eventId);
        if (_orderNodes.Remove(eventId, out var orderNode)) _order.Remove(orderNode);
        if (_ongoingNodes.Remove(eventId, out var ongoingNode)) _ongoing.Remove(ongoingNode);
    }

    private static bool IsOngoing(ActivityEventState state) => state == ActivityEventState.Running;
}
