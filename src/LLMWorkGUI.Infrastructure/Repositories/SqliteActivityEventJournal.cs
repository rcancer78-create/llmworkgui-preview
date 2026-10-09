using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

/// <summary>
/// Durable SQLite store of the redacted activity stream over the <c>ActivityEvents</c> table.
/// <para>
/// Rows are written in one transaction per batch, so the normative 50 events/second profile costs one
/// commit per 256 events instead of one per event. Ordinary inserts never upsert: an id is the identity of an
/// observation, so a duplicate means the caller minted two observations with one id and that has to
/// surface rather than silently overwrite evidence. The explicit execution-projection operation alone
/// replaces a status row, atomically with its search entry.
/// </para>
/// <para>
/// Every batch also writes one row per event into the <c>ActivityEventsSearch</c> FTS5 index, inside the
/// same transaction, using the same <see cref="ActivityEventSearchText.Build"/> projection the in-memory
/// index uses. That is what keeps search and paging on one retained set: search resolves against every
/// persisted row, not against the newest N a process happens to hold.
/// </para>
/// <para>
/// Retention deletes only rows of these two tables, oldest first, and reports exactly how many journal
/// rows it removed. No other table of the application database is referenced by any statement here.
/// </para>
/// </summary>
public sealed class SqliteActivityEventJournal : IActivityEventJournal
{
    private const string SelectColumns = """
        Id, OccurredAtUtc, Kind, Role, State, Source, TitleRedacted, DescriptionRedacted,
        SessionId, ExecutionId, RouteId, ArtifactName, ArtifactSizeBytes, ArtifactSha256,
        ArtifactChangeStatus
        """;

    private static readonly string InsertStatement = """
        INSERT INTO ActivityEvents
            (Id, OccurredAtUtc, Kind, Role, State, Source, TitleRedacted, DescriptionRedacted,
             SessionId, ExecutionId, RouteId, ArtifactName, ArtifactSizeBytes, ArtifactSha256,
             ArtifactChangeStatus, IngestedAtUtc)
        VALUES
            ($id, $occurredAtUtc, $kind, $role, $state, $source, $title, $description,
             $sessionId, $executionId, $routeId, $artifactName, $artifactSizeBytes, $artifactSha256,
             $artifactChangeStatus, $ingestedAtUtc);
        """;

    /// <summary>
    /// Full-text row for the event just inserted. The body is a parameter, not an SQL expression, so the
    /// durable index is filled from the identical C# projection the in-memory index is filled from.
    /// </summary>
    private static readonly string InsertSearchStatement = """
        INSERT INTO ActivityEventsSearch (rowid, body)
        SELECT rowid, $searchText FROM ActivityEvents WHERE Id = $id;
        """;

    private static readonly string UpsertProjectionStatement = InsertStatement.TrimEnd().TrimEnd(';') + " " + """
        ON CONFLICT(Id) DO UPDATE SET
            OccurredAtUtc = excluded.OccurredAtUtc, Kind = excluded.Kind, Role = excluded.Role,
            State = excluded.State, Source = excluded.Source, TitleRedacted = excluded.TitleRedacted,
            DescriptionRedacted = excluded.DescriptionRedacted, SessionId = excluded.SessionId,
            ExecutionId = excluded.ExecutionId, RouteId = excluded.RouteId, ArtifactName = excluded.ArtifactName,
            ArtifactSizeBytes = excluded.ArtifactSizeBytes, ArtifactSha256 = excluded.ArtifactSha256,
            ArtifactChangeStatus = excluded.ArtifactChangeStatus, IngestedAtUtc = excluded.IngestedAtUtc;
        """;

    private static readonly string DeleteSearchStatement = """
        DELETE FROM ActivityEventsSearch WHERE rowid IN (
            SELECT rowid FROM ActivityEvents WHERE Id = $id);
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqliteActivityEventJournal(
        ISqliteConnectionFactory connectionFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task AppendAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activityEvent);

        await AppendRangeAsync(new[] { activityEvent }, cancellationToken).ConfigureAwait(false);
    }

    public Task AppendRangeAsync(
        IReadOnlyList<ActivityEvent> activityEvents,
        CancellationToken cancellationToken = default) => WriteRangeAsync(activityEvents, false, false, cancellationToken);

    public Task ReplayJournalEventsAsync(IReadOnlyList<ActivityEvent> activityEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activityEvents);
        foreach (var item in activityEvents) ActivityJournalReplay.Validate(item);
        return WriteRangeAsync(activityEvents, false, true, cancellationToken);
    }

    public Task UpsertProjectionsAsync(IReadOnlyList<ActivityEvent> projections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projections);
        foreach (var projection in projections)
        {
            ArgumentNullException.ThrowIfNull(projection);
            if (projection.Kind != ActivityEventKind.Execution || string.IsNullOrWhiteSpace(projection.ExecutionId)
                || !string.Equals(projection.Id, $"execution:{projection.ExecutionId}", StringComparison.Ordinal))
                throw new ArgumentException("Only execution projection identities can be updated.", nameof(projections));
        }
        return WriteRangeAsync(projections, true, false, cancellationToken);
    }

    private async Task WriteRangeAsync(IReadOnlyList<ActivityEvent> activityEvents, bool upsertProjection,
        bool replay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activityEvents);

        if (activityEvents.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();

        foreach (var activityEvent in activityEvents)
        {
            var refreshDiagnostic = replay && ActivityJournalReplay.IsDiagnostic(activityEvent);
            if (replay)
            {
                // This transaction owns the writer admission before checking and inserting an ID.
                // Exact repeats skip both the row and FTS insertion; conflicting evidence never replaces it.
                await using var existingCommand = connection.CreateCommand();
                existingCommand.Transaction = transaction;
                existingCommand.CommandText = $"SELECT {SelectColumns} FROM ActivityEvents WHERE Id=$id";
                existingCommand.Parameters.AddWithValue("$id", activityEvent.Id);
                await using var existingReader = await existingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await existingReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var existing = Read(existingReader);
                    ActivityJournalReplay.Validate(existing);
                    if (!refreshDiagnostic)
                    {
                        if (!ActivityJournalReplay.HasSameEvidence(existing, activityEvent))
                            throw new InvalidOperationException("Conflicting persisted execution-journal replay evidence.");
                        continue;
                    }
                }
            }
            if (upsertProjection || refreshDiagnostic)
            {
                // Remove the previous FTS row inside the same transaction as the projection update.
                // A failed batch restores both the old projection and its old searchable text.
                await using var deleteSearch = connection.CreateCommand();
                deleteSearch.Transaction = transaction;
                deleteSearch.CommandText = DeleteSearchStatement;
                deleteSearch.Parameters.AddWithValue("$id", activityEvent.Id);
                await deleteSearch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = upsertProjection || refreshDiagnostic ? UpsertProjectionStatement : InsertStatement;

            command.Parameters.AddWithValue("$id", activityEvent.Id);
            command.Parameters.AddWithValue(
                "$occurredAtUtc",
                SqliteRepositorySupport.FormatTimestamp(activityEvent.OccurredAtUtc));
            command.Parameters.AddWithValue("$kind", SqliteRepositorySupport.FormatEnum(activityEvent.Kind));
            command.Parameters.AddWithValue("$role", activityEvent.Role);
            command.Parameters.AddWithValue("$state", SqliteRepositorySupport.FormatEnum(activityEvent.State));
            command.Parameters.AddWithValue("$source", SqliteRepositorySupport.FormatEnum(activityEvent.Source));
            command.Parameters.AddWithValue("$title", activityEvent.Title);
            command.Parameters.AddWithValue("$description", activityEvent.Description);
            SqliteRepositorySupport.AddNullable(command, "$sessionId", activityEvent.SessionId);
            SqliteRepositorySupport.AddNullable(command, "$executionId", activityEvent.ExecutionId);
            SqliteRepositorySupport.AddNullable(command, "$routeId", activityEvent.RouteId);
            SqliteRepositorySupport.AddNullable(command, "$artifactName", activityEvent.ArtifactName);
            SqliteRepositorySupport.AddNullable(command, "$artifactSizeBytes", activityEvent.ArtifactSizeBytes);
            SqliteRepositorySupport.AddNullable(command, "$artifactSha256", activityEvent.ArtifactSha256);
            SqliteRepositorySupport.AddNullable(
                command,
                "$artifactChangeStatus",
                activityEvent.ArtifactChangeStatus);
            command.Parameters.AddWithValue(
                "$ingestedAtUtc",
                SqliteRepositorySupport.FormatTimestamp(_timeProvider.GetUtcNow()));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var searchCommand = connection.CreateCommand();
            searchCommand.Transaction = transaction;
            searchCommand.CommandText = InsertSearchStatement;
            searchCommand.Parameters.AddWithValue("$id", activityEvent.Id);
            searchCommand.Parameters.AddWithValue(
                "$searchText",
                ActivityEventSearchText.Build(activityEvent));

            await searchCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public async Task<IReadOnlyList<ActivityEvent>> LoadNewestAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit must be positive.");
        }

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM ActivityEvents
            ORDER BY OccurredAtUtc DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<ActivityEvent>(Math.Min(limit, 1024));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(Read(reader));
        }

        return events;
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ActivityEvents;";

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull ? 0L : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Exact newest-first page over every persisted row.
    /// <para>
    /// Two statements on one connection rather than one statement with a window count: the combined form
    /// measured 100-290 ms on a 190 000-row journal, while the split form costs 2.9 ms for the retained
    /// total, 0.1 ms for an unfiltered first page and about 4 ms for an indexed match count. Splitting is
    /// what keeps the screen inside the §9.2 budget while still reporting an exact match count.
    /// </para>
    /// </summary>
    public ActivityJournalPage QueryPage(ActivityJournalQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        // A fresh read transaction gives all three statements one WAL snapshot. Without it,
        // concurrent commits can make matched > total. That rejects the page and leaves
        // the screen showing its previous result until a later refresh succeeds.
        using var transaction = connection.BeginTransaction(deferred: true);
        var filter = new DurableFilter(query);
        var total = ReadCount(connection, "SELECT COUNT(*) FROM ActivityEvents;", transaction);
        var matched = ReadCount(connection, filter.MatchedSql, transaction, filter);

        var items = new List<ActivityEvent>(query.Limit);

        using (var pageCommand = connection.CreateCommand())
        {
            pageCommand.Transaction = transaction;
            pageCommand.CommandText = filter.PageSql;
            filter.Bind(pageCommand);

            pageCommand.Parameters.AddWithValue("$limit", query.Limit);
            pageCommand.Parameters.AddWithValue("$offset", query.Offset);

            using var reader = pageCommand.ExecuteReader();

            while (reader.Read())
            {
                items.Add(Read(reader));
            }
        }

        transaction.Commit();
        return new ActivityJournalPage(total, matched, items);
    }

    /// <summary>
    /// Deletes the oldest rows until at most <paramref name="retentionLimit"/> remain. The cut point is
    /// found with a single ordered scan, and the delete is bounded by the same index the page walk uses,
    /// so a retention pass on a large journal stays a range delete rather than a full rewrite. The
    /// full-text row goes in the same transaction, so the durable search can never outlive its journal row.
    /// </summary>
    public async Task<ActivityJournalTrimResult> TrimAsync(
        int retentionLimit,
        CancellationToken cancellationToken = default)
    {
        if (retentionLimit < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionLimit),
                retentionLimit,
                "The retention limit must not be negative.");
        }

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        // The count and cutoff must share the same write snapshot; otherwise an append between
        // them leaves more rows than this retention pass reports.
        using var transaction = connection.BeginTransaction();
        long before;

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM ActivityEvents;";
            before = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? 0L,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var excess = before - retentionLimit;

        if (excess <= 0)
        {
            transaction.Commit();
            return new ActivityJournalTrimResult(before, before, 0);
        }

        var cut = new List<string>((int)Math.Min(excess, 4096));

        await using (var cutCommand = connection.CreateCommand())
        {
            cutCommand.Transaction = transaction;
            cutCommand.CommandText = $"""
                SELECT Id FROM ActivityEvents
                ORDER BY OccurredAtUtc ASC, Id ASC
                LIMIT $limit;
                """;
            cutCommand.Parameters.AddWithValue("$limit", excess);

            await using var cutReader = await cutCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await cutReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cut.Add(cutReader.GetString(0));
            }
        }

        long removed = 0;

        foreach (var id in cut)
        {
            await using (var searchCommand = connection.CreateCommand())
            {
                searchCommand.Transaction = transaction;
                searchCommand.CommandText = DeleteSearchStatement;
                searchCommand.Parameters.AddWithValue("$id", id);

                await searchCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM ActivityEvents WHERE Id = $id;";
            deleteCommand.Parameters.AddWithValue("$id", id);

            removed += await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();

        return new ActivityJournalTrimResult(before, before - removed, removed);
    }

    private static long ReadCount(SqliteConnection connection, string sql, SqliteTransaction transaction, DurableFilter? filter = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        filter?.Bind(command);

        var result = command.ExecuteScalar();

        return result is null or DBNull
            ? 0L
            : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The SQL shape of one durable query. The filter axes are always ANDed with the free-text match when
    /// there is one, and every value is bound as a parameter: operator input never becomes SQL, and an
    /// FTS5 MATCH expression is built only from lower-cased alphanumeric tokens of the query.
    /// </summary>
    private sealed class DurableFilter
    {
        private readonly ActivityJournalQuery _query;
        private readonly string _matchExpression;
        private readonly List<string> _predicates = new();
        private readonly List<string> _roleParameters = new();
        private readonly List<string> _stateParameters = new();
        private readonly bool _searched;

        public DurableFilter(ActivityJournalQuery query)
        {
            _query = query;

            // Built once and bound as a parameter everywhere. The expression is quoted per token by
            // ActivityEventSearchText.BuildMatchExpression, so an operator typing "observation OR" produces
            // "observation" AND "or" - two literal terms - instead of a query FTS5 rejects with a syntax
            // error. The bound value is still a parameter, so operator text never becomes SQL either.
            _matchExpression = ActivityEventSearchText.BuildMatchExpression(query.SearchQuery);
            _searched = query.HasSearchQuery && _matchExpression.Length > 0;

            if (_searched)
            {
                _predicates.Add(
                    "a.rowid IN (SELECT rowid FROM ActivityEventsSearch WHERE ActivityEventsSearch MATCH $match)");
            }
            else if (query.HasSearchQuery)
            {
                // A query made only of punctuation tokenizes to nothing, so it can match no document.
                // Dropping the predicate instead would turn "()" into "no filter" and hand the operator
                // the entire history they did not ask for.
                _predicates.Add("0");
            }

            if (query.SinceUtc is not null)
            {
                _predicates.Add("a.OccurredAtUtc >= $since");
            }

            foreach (var _ in query.Roles)
            {
                _roleParameters.Add($"$role{_roleParameters.Count + 1}");
            }

            if (_roleParameters.Count > 0)
            {
                _predicates.Add($"a.Role IN ({string.Join(", ", _roleParameters)})");
            }

            foreach (var _ in query.States)
            {
                _stateParameters.Add($"$state{_stateParameters.Count + 1}");
            }

            if (_stateParameters.Count > 0)
            {
                _predicates.Add($"a.State IN ({string.Join(", ", _stateParameters)})");
            }

            if (query.Source is not null)
            {
                _predicates.Add("a.Source = $source");
            }

            var where = _predicates.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", _predicates);
            var from = _searched
                ? "FROM ActivityEventsSearch s JOIN ActivityEvents a ON a.rowid = s.rowid" + where
                : "FROM ActivityEvents a" + where;

            MatchedSql = $"SELECT COUNT(*) {from};";
            PageSql = $"SELECT {SelectColumns} {from} ORDER BY a.OccurredAtUtc DESC, a.Id DESC "
                + "LIMIT $limit OFFSET $offset;";
        }

        public string MatchedSql { get; }

        public string PageSql { get; }

        public void Bind(SqliteCommand command)
        {
            if (_searched)
            {
                command.Parameters.AddWithValue("$match", _matchExpression);
            }

            if (_query.SinceUtc is { } since)
            {
                command.Parameters.AddWithValue("$since", SqliteRepositorySupport.FormatTimestamp(since));
            }

            for (var index = 0; index < _query.Roles.Count; index++)
            {
                command.Parameters.AddWithValue(_roleParameters[index], _query.Roles[index]);
            }

            for (var index = 0; index < _query.States.Count; index++)
            {
                command.Parameters.AddWithValue(
                    _stateParameters[index],
                    SqliteRepositorySupport.FormatEnum(_query.States[index]));
            }

            if (_query.Source is { } source)
            {
                command.Parameters.AddWithValue("$source", SqliteRepositorySupport.FormatEnum(source));
            }
        }
    }

    private static ActivityEvent Read(SqliteDataReader reader)
    {
        var artifactSizeBytes = reader.IsDBNull(12)
            ? (long?)null
            : reader.GetInt64(12);

        return new ActivityEvent(
            id: reader.GetString(0),
            occurredAtUtc: SqliteRepositorySupport.ParseTimestamp(reader.GetString(1)),
            kind: SqliteRepositorySupport.ParseEnum<ActivityEventKind>(reader.GetString(2)),
            role: reader.GetString(3),
            state: SqliteRepositorySupport.ParseEnum<ActivityEventState>(reader.GetString(4)),
            source: SqliteRepositorySupport.ParseEnum<ActivityEventSource>(reader.GetString(5)),
            title: reader.GetString(6),
            description: reader.GetString(7),
            sessionId: SqliteRepositorySupport.GetNullableString(reader, 8),
            executionId: SqliteRepositorySupport.GetNullableString(reader, 9),
            routeId: SqliteRepositorySupport.GetNullableString(reader, 10),
            diffText: null,
            artifactName: SqliteRepositorySupport.GetNullableString(reader, 11),
            artifactContent: null,
            artifactSizeBytes: artifactSizeBytes,
            artifactSha256: SqliteRepositorySupport.GetNullableString(reader, 13),
            artifactChangeStatus: SqliteRepositorySupport.GetNullableString(reader, 14));
    }
}
