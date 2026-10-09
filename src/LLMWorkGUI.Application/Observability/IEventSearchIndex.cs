namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// Thread-safe full-text index over the activity stream. Implementations must redact every indexed
/// document before it becomes searchable: no raw secret value may ever be retrievable (ТЗ §9.3).
/// </summary>
public interface IEventSearchIndex
{
    /// <summary>Number of currently indexed documents.</summary>
    int Count { get; }

    /// <summary>Maximum number of documents retained; oldest entries are evicted first.</summary>
    int Capacity { get; }

    /// <summary>Indexes (or replaces) one event after running the configured redactor.</summary>
    void Index(ActivityEvent activityEvent);

    /// <summary>
    /// Indexes one event whose display fields and searchable projection are already redacted by the caller.
    /// <para>
    /// The Activity Center redacts at the ingestion boundary and then indexes, so without this overload the
    /// same text would be run through every one of the redactor's patterns twice for every event. That is
    /// not just wasted work: the second pass happens while the service holds its lock, and the ingestion
    /// boundary is what the UI thread queries. On a 256 KiB message that turned a query into a wait for a
    /// regex scan, which is visible directly in the p95 event-to-visible latency.
    /// </para>
    /// </summary>
    void IndexRedacted(ActivityEvent activityEvent, string redactedSearchText);

    void IndexRange(IEnumerable<ActivityEvent> activityEvents);

    bool Remove(string eventId);

    void Clear();

    /// <summary>
    /// Searches for events whose redacted searchable text contains every alphanumeric query
    /// token as a complete token, ignoring case. Results are returned newest first.
    /// </summary>
    IReadOnlyList<ActivityEvent> Search(string query, int limit = ActivityFilterCriteria.DefaultLimit);

    /// <summary>
    /// Identifiers of the matching documents, newest first. A caller that only needs to page through the
    /// matches uses this instead of <see cref="Search"/>: resolving ids to events for one page keeps a
    /// full-stream search from materializing every match.
    /// </summary>
    IReadOnlyList<string> SearchIds(string query, int limit);
}
