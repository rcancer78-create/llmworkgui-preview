using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

/// <summary>
/// The durable reviewer-execution bindings, read through the <c>Executions</c> row they belong to so that
/// the state a binding authorizes is the state that was actually persisted rather than a copy of it.
/// <para>
/// The join is the point of this repository. <c>WorkflowReviewExecutions</c> records what a reviewer turn
/// was about and which route was requested; <c>Executions</c> records whether it succeeded, was cancelled
/// or stayed ambiguous. Only the pair answers "may this authorize a transition", so a binding is never
/// returned without the execution state beside it, and a binding whose execution row has been deleted is
/// simply not returned at all.
/// </para>
/// </summary>
public sealed class SqliteWorkflowReviewEvidenceRepository : IWorkflowReviewEvidenceRepository
{
    private const string TableName = "WorkflowReviewExecutions";

    // The observed route is read from e, never from b, and that is the whole point of the projection. The
    // binding's own ObservedRouteId column is a copy of what the execution recorded and nothing more: a
    // reader that trusted the copy would be trusting a field this same process wrote. Executions is where a
    // backend's answer is persisted, so e.ObservedRouteId is the observation and b.ObservedRouteId is at
    // best an echo of it - and migration 009's trigger refuses a binding whose copy disagrees with it.
    //
    // The trailing blank line inside the literal is what gives the concatenated forms below their separating
    // newline: a raw string literal does not keep the line break that precedes its closing delimiter, so
    // without it the last line of this projection would be glued to the WHERE clause of every query built
    // from it.
    private const string SelectColumns = """
        SELECT
            b.Id, b.ExecutionId, b.SessionId, b.WorkflowRunId, b.ReviewerRole, b.StageId,
            b.ReviewedArtifactId, b.ReviewedArtifactHash, b.IsReadOnly, b.RequestedRouteId,
            e.ObservedRouteId, e.State
        FROM WorkflowReviewExecutions b
        INNER JOIN Executions e ON e.Id = b.ExecutionId

        """;

    private const string SelectByRunSql = SelectColumns + """
        WHERE b.WorkflowRunId = $workflowRunId
        ORDER BY b.RequestedAtUtc, b.Id;
        """;

    private const string SelectByExecutionSql = SelectColumns + """
        WHERE b.ExecutionId = $executionId;
        """;

    // The ordinals of SelectColumns, named so a change of the projection is a change of these and not a
    // silent shift of every index in the reader below. Ordinal 0 is the binding row's own primary key and is
    // deliberately never read: nothing outside this repository ever names a binding, only the execution it
    // belongs to.
    private const int ExecutionIdColumn = 1;
    private const int SessionIdColumn = 2;
    private const int WorkflowRunIdColumn = 3;
    private const int ReviewerRoleColumn = 4;
    private const int StageIdColumn = 5;
    private const int ReviewedArtifactIdColumn = 6;
    private const int ReviewedArtifactHashColumn = 7;
    private const int IsReadOnlyColumn = 8;
    private const int RequestedRouteIdColumn = 9;
    private const int ObservedRouteIdColumn = 10;
    private const int ExecutionStateColumn = 11;

    private const string InsertSql = """
        INSERT INTO WorkflowReviewExecutions (
            Id, ExecutionId, SessionId, WorkflowRunId, ReviewerRole, StageId,
            ReviewedArtifactId, ReviewedArtifactHash, IsReadOnly, RequestedRouteId,
            ObservedRouteId, RequestedAtUtc, UpdatedAtUtc
        )
        VALUES (
            $id, $executionId, $sessionId, $workflowRunId, $reviewerRole, $stageId,
            $reviewedArtifactId, $reviewedArtifactHash, $isReadOnly, $requestedRouteId,
            NULL, $requestedAtUtc, $updatedAtUtc
        );
        """;

    // Only the observed route and the update stamp are written. The other eleven columns are deliberately
    // absent from the SET list, and the migration's TR_WorkflowReviewExecutions_BindingIsImmutable trigger
    // refuses an update that touches them, so a caller cannot repoint a recorded turn at another run,
    // stage, role or artifact - and cannot promote a requested route into an observed one.
    //
    // The EXISTS clause is the other half of the same rule, and it is a condition rather than a post-hoc
    // check so that a refused promotion is a no-op instead of a fault: a value the execution row does not
    // already carry simply does not match a row, so the statement writes nothing and the binding keeps
    // whatever it had. It is the repository-level twin of TR_WorkflowReviewExecutions_ObservedRouteIsObserved,
    // which refuses the same statement for anyone who writes the SQL directly. Both directions are
    // fail-closed: an observed route that no execution recorded is not evidence, and the binding is left
    // unobserved rather than corrected.
    private const string UpdateObservedSql = """
        UPDATE WorkflowReviewExecutions
        SET ObservedRouteId = $observedRouteId,
            UpdatedAtUtc = $updatedAtUtc
        WHERE ExecutionId = $executionId
          AND EXISTS (
              SELECT 1
              FROM Executions e
              WHERE e.Id = WorkflowReviewExecutions.ExecutionId
                AND e.ObservedRouteId IS $observedRouteId);
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqliteWorkflowReviewEvidenceRepository(
        ISqliteConnectionFactory connectionFactory,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ReviewerExecutionEvidence>> ListByRunIdAsync(
        string workflowRunId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowRunId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await ReadByRunAsync(connection, workflowRunId, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsTableNotMigratedYet(exception))
        {
            return Array.Empty<ReviewerExecutionEvidence>();
        }
    }

    public async Task<ReviewerExecutionEvidence?> GetByExecutionIdAsync(
        string executionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await ReadByExecutionAsync(connection, executionId, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsTableNotMigratedYet(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the only tolerated storage fault is a database that has not applied migration 008 yet.
    /// <para>
    /// Answering "there is no evidence" for such a database is fail-closed and not fail-open: an empty
    /// binding list makes a pinned model-review gate refuse every verdict, and it leaves a legacy run
    /// exactly where it was, because a legacy run never consults this table. Every other fault - a
    /// corrupt row, a locked database, a missing column - still propagates, because those say something
    /// about the evidence rather than about the schema version.
    /// </para>
    /// </summary>
    private static bool IsTableNotMigratedYet(SqliteException exception) =>
        exception.SqliteErrorCode == 1
        && exception.Message.Contains(
            $"no such table: {TableName}",
            StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<ReviewerExecutionEvidence>> ReadByRunAsync(
        SqliteConnection connection,
        string workflowRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SelectByRunSql;
        command.Parameters.AddWithValue("$workflowRunId", workflowRunId);

        var evidence = new List<ReviewerExecutionEvidence>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            evidence.Add(Read(reader));
        }

        return evidence;
    }

    private static async Task<ReviewerExecutionEvidence?> ReadByExecutionAsync(
        SqliteConnection connection,
        string executionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SelectByExecutionSql;
        command.Parameters.AddWithValue("$executionId", executionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task SaveAsync(
        ReviewerExecutionEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.ObservedRouteId is not null)
        {
            throw new ArgumentException(
                "A reviewer execution binding is written before the turn is dispatched and therefore cannot "
                    + "already carry an observed route.",
                nameof(evidence));
        }

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();

        await SaveAsync(connection, null, evidence, now, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SaveAsync(SqliteConnection connection, SqliteTransaction? transaction,
        ReviewerExecutionEvidence evidence, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSql;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$executionId", evidence.ExecutionId);
        command.Parameters.AddWithValue("$sessionId", evidence.SessionId);
        command.Parameters.AddWithValue("$workflowRunId", evidence.WorkflowRunId);
        command.Parameters.AddWithValue("$reviewerRole", evidence.ReviewerRole);
        command.Parameters.AddWithValue("$stageId", evidence.StageId);
        command.Parameters.AddWithValue("$reviewedArtifactId", evidence.ReviewedArtifactId);
        command.Parameters.AddWithValue("$reviewedArtifactHash", evidence.ReviewedArtifactHash);
        command.Parameters.AddWithValue("$isReadOnly", evidence.IsReadOnly ? 1 : 0);
        command.Parameters.AddWithValue("$requestedRouteId", evidence.RequestedRouteId);
        command.Parameters.AddWithValue(
            "$requestedAtUtc",
            SqliteRepositorySupport.FormatTimestamp(now));
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            SqliteRepositorySupport.FormatTimestamp(now));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateObservedAsync(
        ReviewerExecutionEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await UpdateObservedAsync(connection, null, evidence, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task UpdateObservedAsync(SqliteConnection connection, SqliteTransaction? transaction,
        ReviewerExecutionEvidence evidence, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpdateObservedSql;
        SqliteRepositorySupport.AddNullable(command, "$observedRouteId", evidence.ObservedRouteId);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            SqliteRepositorySupport.FormatTimestamp(now));
        command.Parameters.AddWithValue("$executionId", evidence.ExecutionId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One row as a value object, or a refusal.
    /// <para>
    /// A binding that cannot be read is not silently skipped. A skipped binding is an absent candidate, and
    /// the gate then reads as if that reviewer turn never existed, which is exactly the fail-open reading
    /// this repository exists to prevent - so a malformed row is reported here, on the way out, and the run
    /// is refused rather than advanced over a broken piece of evidence.
    /// </para>
    /// </summary>
    private static ReviewerExecutionEvidence Read(SqliteDataReader reader)
    {
        var executionId = SqliteRepositorySupport.GetNullableString(reader, ExecutionIdColumn);
        var stageId = SqliteRepositorySupport.GetNullableString(reader, StageIdColumn);
        var reviewedArtifactId = SqliteRepositorySupport.GetNullableString(reader, ReviewedArtifactIdColumn);
        var reviewedArtifactHash = SqliteRepositorySupport.GetNullableString(reader, ReviewedArtifactHashColumn);

        InvalidDataException Refuse(string reason) => new(
            "A stored reviewer execution binding"
                + $" (execution '{Describe(executionId)}', stage '{Describe(stageId)}')"
                + $" cannot be read as evidence of a model review, so the run is refused rather than"
                + $" advanced over it: {reason}.");

        try
        {
            return new ReviewerExecutionEvidence(
                executionId!,
                SqliteRepositorySupport.GetNullableString(reader, SessionIdColumn)!,
                SqliteRepositorySupport.GetNullableString(reader, WorkflowRunIdColumn)!,
                SqliteRepositorySupport.GetNullableString(reader, ReviewerRoleColumn)!,
                stageId!,
                SqliteRepositorySupport.GetNullableString(reader, RequestedRouteIdColumn)!,
                SqliteRepositorySupport.GetNullableString(reader, ObservedRouteIdColumn),
                reviewedArtifactId!,
                reviewedArtifactHash!,
                reader.GetInt64(IsReadOnlyColumn) != 0,
                SqliteRepositorySupport.ParseEnum<ExecutionState>(reader.GetString(ExecutionStateColumn)));
        }
        catch (ArgumentException exception)
        {
            throw Refuse(exception.Message);
        }
        catch (InvalidDataException exception)
        {
            throw Refuse(exception.Message);
        }
    }

    private static string Describe(string? value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;
}
