using System.Text.Json;
using System.Text.Json.Serialization;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

public sealed partial class SqliteWorkflowRunRepository : IWorkflowRunRepository
{
    private const string SelectColumns = """
        Id, ProjectId, WorkflowPackageId, WorkflowVersionId, SessionId, State,
        StartedAtUtc, EndedAtUtc, TerminalOutcome, EvidenceRedactedJson,
        TemplateId, TemplateVersion, TemplateGraphSnapshotJson, TemplateSchemeSnapshotJson
        """;

    // The four template columns are part of the same INSERT that creates the run, so the assigned
    // template identity and both of its snapshots become visible together or not at all. They are
    // deliberately absent from the DO UPDATE SET list: a transition save may change the mutable state and
    // the evidence payload, never the identity and snapshots the run was pinned to. The migration's
    // TR_WorkflowRuns_TemplateIdentityIsImmutable trigger enforces the same rule at the storage level, so
    // the guarantee does not depend on this statement alone.
    private const string InsertSql = """
        INSERT INTO WorkflowRuns (
            Id, ProjectId, WorkflowPackageId, WorkflowVersionId, SessionId, State,
            StartedAtUtc, EndedAtUtc, TerminalOutcome, EvidenceRedactedJson,
            TemplateId, TemplateVersion, TemplateGraphSnapshotJson, TemplateSchemeSnapshotJson
        )
        VALUES (
            $id, $projectId, $workflowPackageId, $workflowVersionId, $sessionId, $state,
            $startedAtUtc, $endedAtUtc, $terminalOutcome, $evidenceRedactedJson,
            $templateId, $templateVersion, $templateGraphSnapshotJson, $templateSchemeSnapshotJson
        )
        ON CONFLICT (Id) DO UPDATE SET
            SessionId = excluded.SessionId,
            State = excluded.State,
            EndedAtUtc = excluded.EndedAtUtc,
            TerminalOutcome = excluded.TerminalOutcome,
            EvidenceRedactedJson = excluded.EvidenceRedactedJson;
        """;

    // The artifact row is written inside the same transaction as the run row, never on its own, so a run can
    // never point at evidence whose row was rolled back. There is no ON CONFLICT clause on purpose:
    // recorded evidence is immutable, and re-using an artifact id is a caller error rather than something to
    // silently overwrite.
    private const string InsertArtifactSql = """
        INSERT INTO Artifacts (
            Id, WorkflowRunId, ExecutionId, StageId, Kind, BlobId, RelativePath,
            HashSha256, SizeBytes, DataClassification, CreatedAtUtc
        )
        VALUES (
            $id, $workflowRunId, $executionId, $stageId, $kind, $blobId, NULL,
            $hashSha256, $sizeBytes, $dataClassification, $createdAtUtc
        );
        """;

    // Run-scoped artifact rows are the source of truth for a run's evidence. Rows that carry an execution
    // instead of a run are a different aggregate's evidence and are never returned here.
    private const string SelectArtifactColumns = """
        Id, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc, ExecutionId
        """;

    // The ordinals of SelectArtifactColumns, named so a change of the projection is a change of these and
    // not a silent shift of every index below.
    private const int ArtifactIdColumn = 0;
    private const int ArtifactStageIdColumn = 1;
    private const int ArtifactKindColumn = 2;
    private const int ArtifactBlobIdColumn = 3;
    private const int ArtifactHashColumn = 4;
    private const int ArtifactSizeColumn = 5;
    private const int ArtifactClassificationColumn = 6;
    private const int ArtifactCreatedAtColumn = 7;
    private const int ArtifactExecutionIdColumn = 8;

    private static readonly JsonSerializerOptions EvidenceSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string TerminalStatesSqlList = string.Join(
        ", ",
        new[]
        {
            WorkflowRunState.Completed,
            WorkflowRunState.Failed,
            WorkflowRunState.Cancelled
        }.Select(state => $"'{SqliteRepositorySupport.FormatEnum(state)}'"));

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteWorkflowRunRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await UpsertRunAsync(connection, transaction: null, run, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveArtifactAsync(
        WorkflowRun run,
        WorkflowArtifactEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(evidence);

        if (!string.Equals(evidence.RunId, run.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The artifact belongs to run '{evidence.RunId}', not to the run '{run.Id}' being saved.",
                nameof(evidence));
        }

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (evidence.ExecutionId is { } executionId)
        {
            await ValidateArtifactExecutionAsync(connection, (SqliteTransaction)transaction, run, executionId, cancellationToken)
                .ConfigureAwait(false);
            var current = await GetByIdAsync(connection, (SqliteTransaction)transaction, run.Id, cancellationToken)
                .ConfigureAwait(false);
            if (current is null || current.IsTerminal || current.CurrentStageId != evidence.StageId
                || current.CurrentStageId != run.CurrentStageId || current.State != run.State)
                throw new LLMWorkGUI.Application.Workflows.WorkflowValidationException("The artifact target changed before commit.");
        }

        // The artifact row and the run row are one commit. If the insert is refused - a duplicate id, a
        // half-named row the schema trigger rejects, a foreign key that no longer holds - the run row is
        // rolled back with it, and the copy of the run that carried the evidence is never published.
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = InsertArtifactSql;
            command.Parameters.AddWithValue("$id", evidence.ArtifactId);
            command.Parameters.AddWithValue("$workflowRunId", evidence.RunId);
            SqliteRepositorySupport.AddNullable(command, "$executionId", evidence.ExecutionId);
            command.Parameters.AddWithValue("$stageId", evidence.StageId);
            command.Parameters.AddWithValue("$kind", evidence.Kind);
            command.Parameters.AddWithValue("$blobId", evidence.BlobId);
            command.Parameters.AddWithValue("$hashSha256", evidence.HashSha256);
            command.Parameters.AddWithValue("$sizeBytes", evidence.SizeBytes);
            command.Parameters.AddWithValue(
                "$dataClassification",
                SqliteRepositorySupport.FormatEnum(evidence.Classification));
            command.Parameters.AddWithValue(
                "$createdAtUtc",
                SqliteRepositorySupport.FormatTimestamp(evidence.CreatedAtUtc));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Execution-associated collection only adds evidence. Do not overwrite concurrent verdicts or
        // approvals with a stale aggregate: Artifacts is already the source of truth when a run is read.
        if (evidence.ExecutionId is null)
            await UpsertRunAsync(connection, (SqliteTransaction)transaction, run, cancellationToken)
                .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using var transaction = connection.BeginTransaction(deferred: true);
        return await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<WorkflowRun?> GetByIdAsync(SqliteConnection connection, SqliteTransaction transaction,
        string id, CancellationToken cancellationToken)
    {
        var row = await ReadRunRowAsync(connection, transaction, id, cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : Materialize(row, await ReadArtifactsAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using var transaction = connection.BeginTransaction(deferred: true);
        var rows = new List<RunRow>();
        var runs = new List<WorkflowRun>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT {SelectColumns} FROM WorkflowRuns
                WHERE ProjectId = $projectId
                ORDER BY StartedAtUtc, Id;
                """;
            command.Parameters.AddWithValue("$projectId", projectId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadRunRow(reader));
            }
        }

        foreach (var row in rows)
        {
            runs.Add(Materialize(
                row,
                await ReadArtifactsAsync(connection, transaction, row.Id, cancellationToken)
                    .ConfigureAwait(false)));
        }

        return runs;
    }

    public async Task<WorkflowRun?> GetActiveByProjectIdAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SelectColumns} FROM WorkflowRuns
            WHERE ProjectId = $projectId AND State NOT IN ({TerminalStatesSqlList})
            ORDER BY StartedAtUtc DESC, Id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);

        var row = await ReadRunRowAsync(command, cancellationToken).ConfigureAwait(false);

        return row is null
            ? null
            : Materialize(row, await ReadArtifactsAsync(connection, transaction, row.Id, cancellationToken).ConfigureAwait(false));
    }

    private static async Task UpsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        WorkflowRun run,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSql;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$projectId", run.ProjectId);
        command.Parameters.AddWithValue("$workflowPackageId", run.WorkflowPackageId);
        command.Parameters.AddWithValue("$workflowVersionId", run.WorkflowVersionId);
        SqliteRepositorySupport.AddNullable(command, "$sessionId", run.SessionId);
        command.Parameters.AddWithValue("$state", SqliteRepositorySupport.FormatEnum(run.State));
        command.Parameters.AddWithValue("$startedAtUtc", SqliteRepositorySupport.FormatTimestamp(run.StartedAtUtc));
        SqliteRepositorySupport.AddNullable(command, "$endedAtUtc", SqliteRepositorySupport.FormatTimestamp(run.EndedAtUtc));
        command.Parameters.AddWithValue("$terminalOutcome", SqliteRepositorySupport.FormatEnum(run.TerminalOutcome));
        command.Parameters.AddWithValue("$evidenceRedactedJson", SerializeEvidence(run));
        SqliteRepositorySupport.AddNullable(command, "$templateId", run.TemplateId);
        SqliteRepositorySupport.AddNullable(command, "$templateVersion", run.TemplateVersion);
        SqliteRepositorySupport.AddNullable(
            command,
            "$templateGraphSnapshotJson",
            run.TemplateGraphSnapshotJson);
        SqliteRepositorySupport.AddNullable(
            command,
            "$templateSchemeSnapshotJson",
            run.TemplateSchemeSnapshotJson);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RunRow?> ReadRunRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM WorkflowRuns WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        return await ReadRunRowAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RunRow?> ReadRunRowAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRunRow(reader) : null;
    }

    /// <summary>
    /// The stored artifacts of one run, newest first.
    ///
    /// A run-scoped row that names a stage is a claim to be the evidence of this run, this stage and this
    /// kind, so it either becomes a value object or the read is refused. It cannot be skipped: a skipped row
    /// is simply an absent candidate, and the current artifact of a stage is the newest candidate that is
    /// left, so dropping a newer unreadable row promotes an older one - and an approval already recorded
    /// against the older bytes would then authorize a transition over content that was never reviewed. The
    /// refusal is raised here, on the way to the caller, and therefore before the aggregate is ever asked
    /// whether it may advance.
    ///
    /// A run-scoped row that carries no stage at all predates the gate: the table had no StageId to record,
    /// so such a row was never evidence of a stage in the first place. It keeps its meaning and is still not
    /// returned as artifact evidence, exactly as before. A row that does carry one but cannot be read is
    /// something else, and it is refused rather than passed over.
    /// </summary>
    private static async Task<IReadOnlyList<WorkflowArtifactEvidence>> ReadArtifactsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SelectArtifactColumns} FROM Artifacts
            WHERE WorkflowRunId = $runId
            ORDER BY CreatedAtUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        var artifacts = new List<WorkflowArtifactEvidence>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ReadArtifact(reader, runId) is { } artifact)
            {
                artifacts.Add(artifact);
            }
        }

        return artifacts;
    }

    /// <summary>
    /// One stored row as the evidence of a stage, or null for a row that is not that. A row is either
    /// readable as recorded evidence or the run it belongs to is refused; nothing in between is allowed,
    /// because the only way a malformed row could stay harmless is if the gate did not look at the stage it
    /// names, and that is precisely what this repository cannot know.
    /// </summary>
    private static WorkflowArtifactEvidence? ReadArtifact(SqliteDataReader reader, string runId)
    {
        var stageId = ReadTextOrNull(reader, ArtifactStageIdColumn);

        // NULL and "present but unusable" are different rows. The first is legacy evidence that never named
        // a stage; the second names one and cannot be acted on, and a stage that is only whitespace is not
        // the name of a stage any scheme declares.
        if (stageId is null)
        {
            return null;
        }

        var artifactId = ReadTextOrNull(reader, ArtifactIdColumn);
        var kind = ReadTextOrNull(reader, ArtifactKindColumn);

        InvalidDataException Refuse(string reason) => new(
            $"The stored workflow artifact '{DescribeForDiagnostics(artifactId)}' of run '{runId}', stage "
                + $"'{DescribeForDiagnostics(stageId)}' and kind '{DescribeForDiagnostics(kind)}' is recorded "
                + $"for a stage but cannot be read as the evidence of that stage, so the run is refused "
                + $"instead of authorizing a transition on an older artifact of the same stage: {reason}.");

        var blobId = ReadTextOrNull(reader, ArtifactBlobIdColumn);
        var hashSha256 = ReadTextOrNull(reader, ArtifactHashColumn);
        var classification = ReadClassificationOrNull(reader);
        var sizeBytes = ReadSizeOrNull(reader, ArtifactSizeColumn);
        var createdAtUtc = ReadTimestampOrNull(reader, ArtifactCreatedAtColumn);

        // Every way a row can fail to become evidence is named here rather than left to the value object, so
        // the refusal says what is wrong with the row instead of which argument a guard disliked. None of the
        // reasons repeats the stored content or the hash of it.
        if (string.IsNullOrWhiteSpace(stageId))
        {
            throw Refuse("the row names no stage: its stage is empty or whitespace.");
        }

        if (string.IsNullOrWhiteSpace(artifactId))
        {
            throw Refuse("the row carries no identifier of its own.");
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            throw Refuse("the row names no artifact kind.");
        }

        if (blobId is null || hashSha256 is null)
        {
            throw Refuse("the row names no stored blob and hash.");
        }

        if (!string.Equals(blobId, hashSha256, StringComparison.Ordinal))
        {
            throw Refuse("the recorded blob id is not the recorded hash of the same bytes.");
        }

        if (!WorkflowArtifactEvidence.IsContentHash(blobId))
        {
            throw Refuse(
                "the recorded blob id is not a SHA-256 content hash of the form 'sha256:' followed by 64 "
                    + "lowercase hexadecimal characters.");
        }

        if (sizeBytes is null)
        {
            throw Refuse("the row records no size.");
        }

        if (sizeBytes < 0)
        {
            throw Refuse("the row records a negative size.");
        }

        if (classification is null)
        {
            throw Refuse("the row carries no declared data classification.");
        }

        if (createdAtUtc is null)
        {
            throw Refuse("the row records no creation time, or one that is not a timestamp.");
        }

        return new WorkflowArtifactEvidence(
            artifactId,
            runId,
            stageId,
            kind,
            blobId,
            hashSha256,
            createdAtUtc.Value,
            sizeBytes.Value,
            classification.Value,
            ReadTextOrNull(reader, ArtifactExecutionIdColumn));
    }

    /// <summary>
    /// The column as text, or null when the row stores nothing readable there.
    ///
    /// SQLite stores whatever it was handed under a declared column type, so a column read can fail on a
    /// value of an unexpected storage class. That is a defect of the row and not of the query, and it is
    /// reported exactly like an absent value: either way there is no recorded text to read.
    /// </summary>
    private static string? ReadTextOrNull(SqliteDataReader reader, int ordinal)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    private static long? ReadSizeOrNull(SqliteDataReader reader, int ordinal)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    private static DataClassification? ReadClassificationOrNull(SqliteDataReader reader)
    {
        var value = ReadTextOrNull(reader, ArtifactClassificationColumn);

        return value is not null
               && Enum.TryParse<DataClassification>(value, ignoreCase: false, out var classification)
               && Enum.IsDefined(classification)
            ? classification
            : null;
    }

    private static DateTimeOffset? ReadTimestampOrNull(SqliteDataReader reader, int ordinal)
    {
        var value = ReadTextOrNull(reader, ordinal);

        if (value is null)
        {
            return null;
        }

        try
        {
            return SqliteRepositorySupport.ParseTimestamp(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string DescribeForDiagnostics(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string SerializeEvidence(WorkflowRun run)
    {
        // Only the ids and hashes a transition was authorized by are mirrored here, as audit. The artifacts
        // themselves are read back from the Artifacts rows, so a tampered payload cannot manufacture
        // evidence.
        var payload = new WorkflowRunEvidencePayload
        {
            CurrentStageId = run.CurrentStageId,
            CurrentRole = run.CurrentRole,
            TerminalReason = run.TerminalReason,
            Transitions = run.Transitions,
            Verdicts = run.Verdicts,
            Approvals = run.Approvals
        };

        return JsonSerializer.Serialize(payload, EvidenceSerializerOptions);
    }

    private static RunRow ReadRunRow(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var payload = DeserializeEvidence(id, reader.IsDBNull(9) ? null : reader.GetString(9));

        return new RunRow(
            id,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            SqliteRepositorySupport.GetNullableString(reader, 4),
            SqliteRepositorySupport.ParseEnum<WorkflowRunState>(reader.GetString(5)),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(6)),
            SqliteRepositorySupport.GetNullableTimestamp(reader, 7),
            reader.IsDBNull(8)
                ? null
                : SqliteRepositorySupport.ParseEnum<WorkflowTerminalOutcome>(reader.GetString(8)),
            payload.TerminalReason,
            payload.CurrentStageId,
            payload.CurrentRole,
            payload.Transitions,
            payload.Verdicts,
            payload.Approvals,
            SqliteRepositorySupport.GetNullableString(reader, 10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            SqliteRepositorySupport.GetNullableString(reader, 12),
            SqliteRepositorySupport.GetNullableString(reader, 13));
    }

    private static WorkflowRun Materialize(RunRow row, IReadOnlyList<WorkflowArtifactEvidence> artifacts) =>
        new(
            row.Id,
            row.ProjectId,
            row.WorkflowPackageId,
            row.WorkflowVersionId,
            row.SessionId,
            row.State,
            row.CurrentStageId,
            row.CurrentRole,
            row.StartedAtUtc,
            row.EndedAtUtc,
            row.TerminalOutcome ?? WorkflowTerminalOutcome.None,
            row.TerminalReason,
            row.Transitions,
            row.Verdicts,
            row.Approvals,
            row.TemplateId,
            row.TemplateVersion,
            row.TemplateGraphSnapshotJson,
            row.TemplateSchemeSnapshotJson,
            artifacts);

    private static WorkflowRunEvidencePayload DeserializeEvidence(string runId, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException(
                $"Workflow run '{runId}' has no EvidenceRedactedJson payload.");
        }

        var payload = JsonSerializer.Deserialize<WorkflowRunEvidencePayload>(
                json,
                EvidenceSerializerOptions)
            ?? throw new InvalidDataException(
                $"Workflow run '{runId}' has an empty EvidenceRedactedJson payload.");

        if (string.IsNullOrWhiteSpace(payload.CurrentStageId) || string.IsNullOrWhiteSpace(payload.CurrentRole))
        {
            throw new InvalidDataException(
                $"Workflow run '{runId}' has an EvidenceRedactedJson payload without a current stage and role.");
        }

        return payload;
    }

    private sealed record RunRow(
        string Id,
        string ProjectId,
        string WorkflowPackageId,
        string WorkflowVersionId,
        string? SessionId,
        WorkflowRunState State,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? EndedAtUtc,
        WorkflowTerminalOutcome? TerminalOutcome,
        string? TerminalReason,
        string CurrentStageId,
        string CurrentRole,
        IReadOnlyList<WorkflowTransitionRecord> Transitions,
        IReadOnlyList<ReviewerVerdictRecord> Verdicts,
        IReadOnlyList<UserApprovalEvidence> Approvals,
        string? TemplateId,
        int? TemplateVersion,
        string? TemplateGraphSnapshotJson,
        string? TemplateSchemeSnapshotJson);

    private sealed class WorkflowRunEvidencePayload
    {
        public string CurrentStageId { get; set; } = string.Empty;

        public string CurrentRole { get; set; } = string.Empty;

        public string? TerminalReason { get; set; }

        public IReadOnlyList<WorkflowTransitionRecord> Transitions { get; set; } =
            Array.Empty<WorkflowTransitionRecord>();

        public IReadOnlyList<ReviewerVerdictRecord> Verdicts { get; set; } =
            Array.Empty<ReviewerVerdictRecord>();

        public IReadOnlyList<UserApprovalEvidence> Approvals { get; set; } =
            Array.Empty<UserApprovalEvidence>();
    }
}
