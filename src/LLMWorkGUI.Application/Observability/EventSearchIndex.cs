using System.Text;

namespace LLMWorkGUI.Application.Observability;

/// <summary>
/// In-memory, bounded, thread-safe full-text index of the activity stream. Every document is passed
/// through the configured redactor before tokenization, so the index only ever holds redacted text and
/// a query can never find a secret value (ТЗ §9.3). Indexed alphanumeric tokens are lower-cased once;
/// a match requires every complete query token, consistently with the durable full-text index.
/// </summary>
public sealed class EventSearchIndex : IEventSearchIndex
{
    public const int DefaultCapacity = 100_000;

    private readonly Func<string, string> _redactText;
    private readonly ActivityEventRedactor _eventRedactor;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();

    /// <param name="redactText">
    /// Redaction applied to the composed searchable text before indexing. The production composition
    /// passes <c>SensitiveDataFilter.Redact</c>; tests may pass an explicit equivalent.
    /// </param>
    public EventSearchIndex(Func<string, string> redactText, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(redactText);

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        _redactText = redactText;
        _eventRedactor = new ActivityEventRedactor(redactText);
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Index(ActivityEvent activityEvent)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);

        var redacted = _eventRedactor.Redact(activityEvent);
        IndexRedacted(redacted, _redactText(ActivityEventSearchText.Build(redacted)));
    }

    /// <inheritdoc />
    public void IndexRedacted(ActivityEvent activityEvent, string redactedSearchText)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);
        ArgumentNullException.ThrowIfNull(redactedSearchText);

        var entry = new Entry(activityEvent, Tokenize(redactedSearchText));

        lock (_gate)
        {
            if (_entries.TryGetValue(activityEvent.Id, out var existing))
            {
                _order.Remove(existing);
                _entries.Remove(activityEvent.Id);
            }

            _entries[activityEvent.Id] = _order.AddLast(entry);

            while (_entries.Count > _capacity)
            {
                var oldest = _order.First;

                if (oldest is null)
                {
                    break;
                }

                _entries.Remove(oldest.Value.Event.Id);
                _order.RemoveFirst();
            }
        }
    }

    public void IndexRange(IEnumerable<ActivityEvent> activityEvents)
    {
        ArgumentNullException.ThrowIfNull(activityEvents);

        foreach (var activityEvent in activityEvents)
        {
            Index(activityEvent);
        }
    }

    public bool Remove(string eventId)
    {
        ArgumentNullException.ThrowIfNull(eventId);

        lock (_gate)
        {
            if (!_entries.TryGetValue(eventId, out var existing))
            {
                return false;
            }

            _order.Remove(existing);
            _entries.Remove(eventId);

            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
        }
    }

    public IReadOnlyList<ActivityEvent> Search(string query, int limit = ActivityFilterCriteria.DefaultLimit)
    {
        var ids = SearchIds(query, limit);
        var results = new List<ActivityEvent>(ids.Count);

        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (_entries.TryGetValue(id, out var entry))
                {
                    results.Add(entry.Value.Event);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Newest-first identifiers of the documents matching every token of the query. Only identifiers
    /// are produced, so a caller paging through a match set never has to build the event objects of the
    /// matches it is not going to show.
    /// </summary>
    public IReadOnlyList<string> SearchIds(string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return Array.Empty<string>();
        }

        var tokens = Tokenize(query);

        if (tokens.Length == 0)
        {
            return Array.Empty<string>();
        }

        var ids = new List<string>();

        lock (_gate)
        {
            for (var node = _order.Last; node is not null; node = node.Previous)
            {
                if (Matches(node.Value, tokens))
                {
                    ids.Add(node.Value.Event.Id);

                    if (ids.Count >= limit)
                    {
                        break;
                    }
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// A document matches when it carries <em>every</em> token of the query as a token of its own.
    /// <para>
    /// Token equality, not substring containment, is the rule - and it is the rule the durable FTS5 index
    /// implements too. An earlier version also accepted a query token that was merely a substring of the
    /// document text, which made the two indexes disagree: "02" matched every event whose id happened to
    /// contain "02" in memory, while the durable index matched only the executions whose own identity was
    /// exec-02. Two search engines with different match sets is precisely the defect that made search and
    /// paging tell the operator different stories about the retained history, so the looser rule is gone
    /// rather than emulated. It also means the index no longer keeps a lower-cased copy of every document.
    /// </para>
    /// </summary>
    private static bool Matches(Entry entry, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (Array.IndexOf(entry.Tokens, token) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tokenizer shared with the durable full-text index, so a token the in-memory index matches on is
    /// the same token the durable FTS5 MATCH expression asks for.
    /// </summary>
    private static string[] Tokenize(string value) => ActivityEventSearchText.TokenizeQuery(value);

    private sealed class Entry
    {
        public Entry(ActivityEvent activityEvent, string[] tokens)
        {
            Event = activityEvent;
            Tokens = tokens;
        }

        public ActivityEvent Event { get; }

        public string[] Tokens { get; }
    }
}

/// <summary>
/// Builds the searchable projection of an activity event. Only metadata is indexed; artifact bodies
/// and diffs stay out of the index so a full-text search can never surface raw file content.
/// <para>
/// This is the single definition of "searchable text" for the whole product. The in-memory index calls
/// it, and the durable journal writes the same string into the <c>ActivityEventsSearch</c> full-text
/// index inside the journal transaction. Sharing one definition is what makes a token the in-memory
/// index finds a token the durable index finds, instead of two projections that drift apart and leave
/// search and paging disagreeing about the retained set.
/// </para>
/// </summary>
public static class ActivityEventSearchText
{
    public static string Build(ActivityEvent activityEvent)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);

        return string.Join(
            ' ',
            activityEvent.Id,
            activityEvent.Title,
            activityEvent.Description,
            activityEvent.Role,
            activityEvent.Kind.ToString(),
            activityEvent.State.ToString(),
            activityEvent.Source.ToString(),
            activityEvent.SessionId ?? string.Empty,
            activityEvent.ExecutionId ?? string.Empty,
            activityEvent.RouteId ?? string.Empty,
            activityEvent.ArtifactName ?? string.Empty);
    }

    /// <summary>
    /// Splits a query into the alphanumeric tokens both indexes match on. Durable FTS5 MATCH syntax is
    /// built from these tokens only, so operator input can never reach the query parser as syntax.
    /// </summary>
    public static string[] TokenizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var start = -1;

        for (var index = 0; index <= query.Length; index++)
        {
            var isWordCharacter = index < query.Length && char.IsLetterOrDigit(query[index]);

            if (isWordCharacter)
            {
                if (start < 0)
                {
                    start = index;
                }

                continue;
            }

            if (start >= 0)
            {
                tokens.Add(query[start..index].ToLowerInvariant());
                start = -1;
            }
        }

        return tokens.ToArray();
    }

    /// <summary>
    /// Builds the FTS5 MATCH expression for a query: every token must be present, which is the same
    /// rule <see cref="EventSearchIndex"/> applies in memory.
    /// <para>
    /// Every token is emitted as a quoted FTS5 string rather than a bare word, and that is the whole point
    /// of the quoting rather than a style choice. Binding the expression through <c>$match</c> stops the
    /// operator's text from becoming SQL, but it does not stop it from becoming <em>FTS5 query syntax</em>:
    /// the parser still sees whatever the string contains. Measured against real SQLite 3.41.2 with the
    /// <c>unicode61</c> tokenizer, the unquoted form raised
    /// <c>fts5: syntax error near "OR"</c> for the query <c>observation OR</c> and
    /// <c>fts5: syntax error near "'"</c> for a lone double quote - an unhandled
    /// <see cref="Microsoft.Data.Sqlite.SqliteException"/> on the WPF dispatcher, from an ordinary
    /// keystroke. Whether a reserved word happens to be legal depends on the position it lands in: a
    /// lone <c>or</c> parsed as a term, while the same word in operand position was a syntax error, so
    /// "it worked when I tried it" is not evidence of anything.
    /// </para>
    /// <para>
    /// Quoting makes the token literal in every position, and an embedded double quote is escaped by
    /// doubling it, which is the FTS5 string rule. <see cref="TokenizeQuery"/> only ever yields
    /// alphanumeric runs so a quote cannot currently reach here; the escape is kept so the expression
    /// stays literal if the tokenizer is ever widened. The all-tokens-required rule is preserved: the
    /// quoted tokens are still joined with <c>AND</c>, which is the same AND of terms the in-memory index
    /// applies, so both indexes continue to describe one match set.
    /// </para>
    /// </summary>
    public static string BuildMatchExpression(string query)
    {
        var tokens = TokenizeQuery(query);

        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        var expression = new StringBuilder(tokens.Length * 8 + (tokens.Length - 1) * 5);

        for (var index = 0; index < tokens.Length; index++)
        {
            if (index > 0)
            {
                expression.Append(" AND ");
            }

            expression.Append('"');
            expression.Append(tokens[index].Replace("\"", "\"\"", StringComparison.Ordinal));
            expression.Append('"');
        }

        return expression.ToString();
    }
}
