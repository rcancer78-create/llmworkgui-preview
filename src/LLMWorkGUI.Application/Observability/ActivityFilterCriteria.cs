namespace LLMWorkGUI.Application.Observability;

/// <summary>Time window the Activity Center filters on, relative to the current time.</summary>
public enum ActivityTimeRange
{
    All,
    Last15Minutes,
    LastHour,
    Last24Hours
}

/// <summary>
/// Immutable multi-criteria filter of the activity stream: time window, roles, states, provenance and
/// a free-text query. The query is always resolved through <see cref="IEventSearchIndex"/>, which only
/// ever indexes pre-redacted text, so secret material can never be found (ТЗ §9.3).
/// </summary>
public sealed class ActivityFilterCriteria
{
    public const int DefaultLimit = 500;

    public static ActivityFilterCriteria Default { get; } = new();

    public ActivityTimeRange TimeRange { get; init; } = ActivityTimeRange.All;

    /// <summary>Selected operator roles; empty means all roles.</summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>Selected states; empty means all states.</summary>
    public IReadOnlyList<ActivityEventState> States { get; init; } = Array.Empty<ActivityEventState>();

    /// <summary>Selected provenance; null means native and synthetic together.</summary>
    public ActivityEventSource? Source { get; init; }

    public string SearchQuery { get; init; } = string.Empty;

    /// <summary>Maximum number of items returned by one query; must be positive.</summary>
    public int Limit { get; init; } = DefaultLimit;

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    public bool IsDefault =>
        TimeRange == ActivityTimeRange.All
        && Roles.Count == 0
        && States.Count == 0
        && Source is null
        && !HasSearchQuery;

    public bool HasRoleFilter(string role)
    {
        foreach (var candidate in Roles)
        {
            if (string.Equals(candidate, role, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The inclusive lower bound of the selected window, or null for <see cref="ActivityTimeRange.All"/>.</summary>
    public DateTimeOffset? ResolveLowerBound(DateTimeOffset now) => TimeRange switch
    {
        ActivityTimeRange.Last15Minutes => now.AddMinutes(-15),
        ActivityTimeRange.LastHour => now.AddHours(-1),
        ActivityTimeRange.Last24Hours => now.AddHours(-24),
        _ => null
    };

    /// <summary>
    /// Matches every criterion except the free-text query. The query is resolved by the search index
    /// so that secret-bearing text never enters the comparison path.
    /// </summary>
    public bool MatchesFilters(ActivityEvent activityEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);

        if (ResolveLowerBound(now) is { } lowerBound && activityEvent.OccurredAtUtc < lowerBound)
        {
            return false;
        }

        if (Roles.Count > 0 && !HasRoleFilter(activityEvent.Role))
        {
            return false;
        }

        if (States.Count > 0 && !States.Contains(activityEvent.State))
        {
            return false;
        }

        if (Source is { } source && activityEvent.Source != source)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Matches all criteria including the query, for callers that hold already-redacted text.
    /// <para>
    /// The query rule is the same one <see cref="IEventSearchIndex"/> and the durable full-text index
    /// apply: every alphanumeric token of the query must be a token of the event. Keeping the three
    /// implementations on one rule is what stops search from reporting a different history than the pager.
    /// </para>
    /// </summary>
    public bool Matches(ActivityEvent activityEvent, DateTimeOffset now)
    {
        if (!MatchesFilters(activityEvent, now))
        {
            return false;
        }

        if (!HasSearchQuery)
        {
            return true;
        }

        var tokens = ActivityEventSearchText.TokenizeQuery(SearchQuery);

        if (tokens.Length == 0)
        {
            // A query of nothing but punctuation matches nothing, exactly as the index and the durable
            // search do. Treating it as "no filter" would hand back history the operator did not ask for.
            return false;
        }

        var documentTokens = ActivityEventSearchText.TokenizeQuery(ActivityEventSearchText.Build(activityEvent));

        foreach (var token in tokens)
        {
            if (Array.IndexOf(documentTokens, token) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Human-readable label of a time window; used by the filter panel and the empty state.</summary>
    public static string DescribeTimeRange(ActivityTimeRange range) => range switch
    {
        ActivityTimeRange.Last15Minutes => "Последние 15 минут",
        ActivityTimeRange.LastHour => "Последний час",
        ActivityTimeRange.Last24Hours => "Последние 24 часа",
        _ => "За всё время"
    };
}

/// <summary>
/// Result of one Activity Center query: exact totals plus the requested page of events, and the
/// retention counters that keep capacity eviction visible instead of silent.
/// </summary>
public sealed class ActivityFilterResult
{
    public static ActivityFilterResult Empty { get; } = new(
        totalCount: 0,
        filteredCount: 0,
        isTruncated: false,
        Array.Empty<ActivityEvent>(),
        retention: ActivityRetentionStatistics.Empty);

    public ActivityFilterResult(
        int totalCount,
        int filteredCount,
        bool isTruncated,
        IReadOnlyList<ActivityEvent> items,
        ActivityRetentionStatistics? retention = null,
        int pageCount = 1)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (totalCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCount), totalCount, "Total count must not be negative.");
        }

        if (filteredCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(filteredCount), filteredCount, "Filtered count must not be negative.");
        }

        if (filteredCount > totalCount)
        {
            throw new ArgumentException("Filtered count can never exceed the total count.", nameof(filteredCount));
        }

        if (pageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount), pageCount, "Page count must be positive.");
        }

        TotalCount = totalCount;
        FilteredCount = filteredCount;
        IsTruncated = isTruncated;
        Items = items;
        Retention = retention ?? ActivityRetentionStatistics.Empty;
        PageCount = pageCount;
    }

    public int TotalCount { get; }

    public int FilteredCount { get; }

    public bool IsTruncated { get; }

    public IReadOnlyList<ActivityEvent> Items { get; }

    /// <summary>Retention counters at the moment of the query.</summary>
    public ActivityRetentionStatistics Retention { get; }

    /// <summary>Number of pages the exact filtered count spans for the page size that produced it.</summary>
    public int PageCount { get; }

    /// <summary>True when the product has already dropped events to stay inside its bound.</summary>
    public bool HasOverflowed => Retention.HasOverflowed;
}
