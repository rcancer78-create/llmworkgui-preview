namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// Durable, application-data-backed activity journal (ТЗ §9.2, Phase 11).
/// <para>
/// The in-memory Activity Center used to be the only store, so "100 000 already saved events" could only
/// mean 100 000 objects a process happened to be holding. This abstraction makes the saved history a
/// product fact: rows live in the application database, are reloaded after a process restart, and have
/// an explicit retention rule instead of disappearing without trace.
/// </para>
/// <para>
/// Every implementation must persist only events that already passed the
/// <see cref="ActivityEventRedactor"/> boundary. The journal is a durability concern, not a second
/// place where redaction is optional.
/// </para>
/// <para>
/// The journal is also the retained set that search and paging must agree on. The in-memory window is
/// bounded and therefore cannot answer "how many of everything ever saved match this query"; the journal
/// can, and it answers from its own full-text index rather than from whatever happens to be in memory.
/// </para>
/// </summary>
public interface IActivityEventJournal
{
    /// <summary>Appends one already-redacted event. Ids are unique; a duplicate must surface.</summary>
    Task AppendAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists missing execution-journal events and verifies exact existing evidence atomically.
    /// Only the two canonical corrupt-journal diagnostic snapshots may refresh their previous snapshot.
    /// Ordinary append identities retain their duplicate-error contract.
    /// </summary>
    Task ReplayJournalEventsAsync(IReadOnlyList<ActivityEvent> activityEvents, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This journal does not support exact execution-journal replay.");

    /// <summary>Appends a batch inside one durable transaction.</summary>
    Task AppendRangeAsync(
        IReadOnlyList<ActivityEvent> activityEvents,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces execution projection rows and their search entries. Ordinary observations
    /// remain append-only; implementations without projection support must refuse this operation.
    /// </summary>
    Task UpsertProjectionsAsync(IReadOnlyList<ActivityEvent> projections, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This journal does not support execution projection updates.");

    /// <summary>Newest-first page of persisted events, used to rehydrate the in-memory projection.</summary>
    Task<IReadOnlyList<ActivityEvent>> LoadNewestAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Number of rows currently persisted.</summary>
    Task<long> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the explicit retention rule and reports exactly what it dropped. Retention only ever
    /// removes rows of this journal; it must never touch another table of the application database.
    /// </summary>
    Task<ActivityJournalTrimResult> TrimAsync(
        int retentionLimit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exact newest-first page over every persisted row, plus the exact number of rows that match. This
    /// is synchronous and invoked by the shipped screen's bounded query worker, outside the dispatcher.
    /// A running query finishes before the latest queued request; superseded results are not published.
    /// The durable index resolves exact counts and one page without loading the entire match set.
    /// </summary>
    ActivityJournalPage QueryPage(ActivityJournalQuery query);
}

/// <summary>
/// One durable query over the journal: the same filter axes the in-memory window supports, resolved
/// against every retained row rather than against whatever the process happens to be holding.
/// </summary>
public sealed class ActivityJournalQuery
{
    public ActivityJournalQuery(
        DateTimeOffset? sinceUtc = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<ActivityEventState>? states = null,
        ActivityEventSource? source = null,
        string searchQuery = "",
        int offset = 0,
        int limit = ActivityFilterCriteria.DefaultLimit)
    {
        SinceUtc = sinceUtc;
        Roles = roles ?? Array.Empty<string>();
        States = states ?? Array.Empty<ActivityEventState>();
        Source = source;
        SearchQuery = searchQuery ?? string.Empty;

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset must not be negative.");
        }

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit must be positive.");
        }

        Offset = offset;
        Limit = limit;
    }

    /// <summary>Inclusive lower bound of the time window, or null for every retained row.</summary>
    public DateTimeOffset? SinceUtc { get; }

    /// <summary>Selected roles; empty means every role.</summary>
    public IReadOnlyList<string> Roles { get; }

    /// <summary>Selected states; empty means every state.</summary>
    public IReadOnlyList<ActivityEventState> States { get; }

    /// <summary>Selected provenance; null means native and synthetic together.</summary>
    public ActivityEventSource? Source { get; }

    /// <summary>Free-text query, matched against the durable redacted full-text index.</summary>
    public string SearchQuery { get; }

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    /// <summary>Zero-based offset of the first requested row inside the newest-first match set.</summary>
    public int Offset { get; }

    /// <summary>Maximum number of rows to materialize.</summary>
    public int Limit { get; }

    /// <summary>Translates a screen filter into a durable query for one page.</summary>
    public static ActivityJournalQuery FromCriteria(
        ActivityFilterCriteria criteria,
        DateTimeOffset now,
        int page,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (page <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "The page must be positive.");
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "The page size must be positive.");
        }

        return new ActivityJournalQuery(
            criteria.ResolveLowerBound(now),
            criteria.Roles,
            criteria.States,
            criteria.Source,
            criteria.SearchQuery,
            (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue),
            pageSize);
    }
}

/// <summary>
/// Result of one durable page query: the exact retained total, the exact match count and the page itself.
/// </summary>
public sealed class ActivityJournalPage
{
    public ActivityJournalPage(long totalRows, long matchedRows, IReadOnlyList<ActivityEvent> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (totalRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalRows), totalRows, "Must not be negative.");
        }

        if (matchedRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(matchedRows), matchedRows, "Must not be negative.");
        }

        if (matchedRows > totalRows)
        {
            throw new ArgumentException(
                "Matched rows can never exceed the retained rows.",
                nameof(matchedRows));
        }

        if (items.Count > matchedRows)
        {
            throw new ArgumentException("A page can never hold more rows than the match set.", nameof(items));
        }

        TotalRows = totalRows;
        MatchedRows = matchedRows;
        Items = items;
    }

    /// <summary>Every row the retention rule currently keeps, filters ignored.</summary>
    public long TotalRows { get; }

    /// <summary>Rows matching every axis of the query. Exact, never an estimate.</summary>
    public long MatchedRows { get; }

    /// <summary>The requested page, newest first.</summary>
    public IReadOnlyList<ActivityEvent> Items { get; }
}

/// <summary>Outcome of one retention run over the durable journal.</summary>
public sealed class ActivityJournalTrimResult
{
    public ActivityJournalTrimResult(long rowsBefore, long rowsAfter, long rowsRemoved)
    {
        if (rowsBefore < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowsBefore), rowsBefore, "Must not be negative.");
        }

        if (rowsAfter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowsAfter), rowsAfter, "Must not be negative.");
        }

        if (rowsRemoved < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowsRemoved), rowsRemoved, "Must not be negative.");
        }

        if (rowsAfter > rowsBefore)
        {
            throw new ArgumentException("A retention run can never grow the journal.", nameof(rowsAfter));
        }

        if (rowsBefore - rowsAfter != rowsRemoved)
        {
            throw new ArgumentException(
                "Removed rows must equal the difference between the rows before and after the run.",
                nameof(rowsRemoved));
        }

        RowsBefore = rowsBefore;
        RowsAfter = rowsAfter;
        RowsRemoved = rowsRemoved;
    }

    public long RowsBefore { get; }

    public long RowsAfter { get; }

    public long RowsRemoved { get; }

    public bool RemovedAnything => RowsRemoved > 0;
}

/// <summary>
/// Retention rule of the durable journal. The limit is explicit and counted: a bounded journal that
/// silently forgets rows would make the normative "offered / persisted / evicted" accounting impossible.
/// </summary>
public sealed class ActivityJournalOptions
{
    public const int DefaultRetentionLimit = 250_000;

    public int RetentionLimit { get; init; } = DefaultRetentionLimit;
}
