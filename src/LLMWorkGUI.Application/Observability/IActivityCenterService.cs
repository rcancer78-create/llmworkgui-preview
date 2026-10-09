namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// Application-level aggregation point of the activity stream: workflow run projections, session
/// timelines, health transitions and explicit user actions. It keeps a bounded history and answers
/// multi-criteria queries through the redacting full-text index.
/// <para>
/// <c>Append</c> is the product ingestion boundary. It is the only entry point the normative load
/// profile of ТЗ §9.2 uses: every accepted event is redacted before it is stored, indexed or journaled,
/// stamped with a monotonic clock, persisted through the durable journal when one is composed, and then
/// announced through <see cref="Appended"/> so a screen can coalesce a refresh on its own dispatcher
/// instead of the operator pulling.
/// </para>
/// <para>
/// <see cref="AppendProjection"/> deliberately stays a convenience for execution projections: it
/// derives the event id from the execution id, so repeated updates of one execution collapse onto one
/// row. A stream that must retain every observation has to use <see cref="Append"/> with unique ids and
/// carry the execution identity in the provenance fields.
/// </para>
/// <para>
/// The in-memory history is a bounded window, not the saved history. When a durable journal is composed,
/// <see cref="LoadAsync"/> refills that window from the journal at startup and every query resolves against
/// the journal, so the exact match count and the pager cover the whole retained set. That is the only way
/// search and paging can agree about how much history exists.
/// </para>
/// </summary>
public interface IActivityCenterService
{
    /// <summary>
    /// Raised once per accepted append, after the event is stored, indexed and offered to the journal.
    /// The stamp is taken inside the append, so a handler can measure the true ingestion-to-visible
    /// latency of the product rather than of a load driver.
    /// </summary>
    event EventHandler<ActivityEventAppendedEventArgs>? Appended;

    /// <summary>Durable query results changed after a commit; this is not another ingestion notice.</summary>
    event EventHandler? PersistenceChanged;

    /// <summary>Events currently held in the bounded in-memory window.</summary>
    int TotalCount { get; }

    /// <summary>Maximum number of events retained in memory; the oldest ongoing events are evicted first.</summary>
    int Capacity { get; }

    /// <summary>Counters that make in-memory eviction, queue loss and durable retention visible.</summary>
    ActivityRetentionStatistics Statistics { get; }

    void Append(ActivityEvent activityEvent);

    /// <summary>Replays stable execution-journal evidence, verifying exact repeats in the durable journal.</summary>
    void ReplayJournalEvent(ActivityEvent activityEvent) => Append(activityEvent);

    void AppendRange(IEnumerable<ActivityEvent> activityEvents);

    void AppendProjection(ObservableRunProjection projection);

    void AppendTimeline(ActivityTimeline timeline);

    void AppendWorkflowTimeline(WorkflowRunTimeline timeline);

    void AppendHealthTransition(string scopeId, string stateDisplay, string detail);

    void AppendUserAction(string id, string title, string description);

    void AppendSystemEvent(
        string id,
        string title,
        string description,
        ActivityEventState state = ActivityEventState.Warning);

    bool Remove(string eventId);

    /// <summary>Empties the in-memory window. The durable journal is untouched.</summary>
    void Clear();

    /// <summary>
    /// Applies the criteria (newest first) and reports the exact match count over the retained durable
    /// set, or over the in-memory window when no journal is composed.
    /// </summary>
    ActivityFilterResult Query(ActivityFilterCriteria? criteria = null);

    /// <summary>
    /// Applies the criteria and materializes exactly one page. The reported totals stay exact, but at
    /// most <paramref name="pageSize"/> events are ever built, so paging a 100 000-event stream never
    /// materializes the whole stream.
    /// </summary>
    ActivityFilterResult QueryPage(
        ActivityFilterCriteria? criteria = null,
        int page = 1,
        int pageSize = ActivityFilterCriteria.DefaultLimit);

    /// <summary>Newest-first snapshot of the bounded in-memory window.</summary>
    IReadOnlyList<ActivityEvent> Snapshot();

    /// <summary>
    /// Startup reload: drains the durable write queue, applies the retention rule and refills the bounded
    /// in-memory window from the journal, announcing every reloaded row so the shipped screen shows the
    /// saved history without an operator action. Returns what it found and did.
    /// </summary>
    Task<ActivityCenterLoadReport> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the durable retention rule and reports exactly what it removed. Explicitly counted, never
    /// silent, and it never touches a table other than this journal's.
    /// </summary>
    Task<ActivityJournalTrimResult> ApplyJournalRetentionAsync(CancellationToken cancellationToken = default);
}
