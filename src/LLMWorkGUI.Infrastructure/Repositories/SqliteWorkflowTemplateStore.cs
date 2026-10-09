using System.Text.Json;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

/// <summary>
/// The durable, immutable workflow template store. It replaces the in-memory store in the product
/// composition so a template version and the project-to-template pointer survive a restart, while
/// keeping the guarantees the Studio depends on:
///
/// <list type="bullet">
///   <item>a (TemplateId, Version) pair is written once and a second save is refused, never applied;</item>
///   <item>the database itself refuses an UPDATE or a DELETE of a saved version;</item>
///   <item>an assignment may only name a version that exists, so a pointer can never dangle;</item>
///   <item>a project has at most one pointer, and the pointer of one project is never visible to
///         another one;</item>
///   <item>ordering is deterministic: shipped versions first, then template id, then version.</item>
/// </list>
///
/// The store owns no workflow run, no imported package and no imported version blob: it never writes
/// to WorkflowRuns, WorkflowPackages or WorkflowVersions, and reading a template has no effect on any
/// of them. The graph is validated before it is written and re-validated when it is read, so a
/// corrupted row is reported instead of being handed to the Studio as a working graph.
///
/// <see cref="WorkflowTemplateVersions.GraphJson"/> is opaque and holds the graph snapshot document
/// verbatim, written and read by <see cref="WorkflowGraphSnapshot"/> - the same document and the same
/// reader a run pins. A node's declared stage gates therefore survive storage without a second
/// serializer to keep in step, and a row written before gates were preserved stays readable as a graph
/// whose nodes declare no gates at all.
/// </summary>
public sealed class SqliteWorkflowTemplateStore : IWorkflowTemplateStore
{
    /// <summary>The primary error code SQLite reports for every constraint failure.</summary>
    private const int ConstraintErrorCode = 19;

    /// <summary>SQLITE_CONSTRAINT_PRIMARYKEY: the refusal a duplicate template version produces.</summary>
    private const int ConstraintPrimaryKeyExtendedErrorCode = 1555;

    /// <summary>SQLITE_CONSTRAINT_UNIQUE: the same refusal when a unique index is the one hit.</summary>
    private const int ConstraintUniqueExtendedErrorCode = 2067;

    /// <summary>SQLITE_CONSTRAINT_FOREIGNKEY: raised when an assignment names a missing version.</summary>
    private const int ConstraintForeignKeyExtendedErrorCode = 787;

    private const string SelectColumns = """
        TemplateId, Version, DisplayName, Description, IsBuiltIn,
        GraphJson, RoleBindingsJson, RequiredDocumentTemplatesJson, CreatedAtUtc
        """;

    private const string InsertSql = """
        INSERT INTO WorkflowTemplateVersions (
            TemplateId, Version, DisplayName, Description, IsBuiltIn,
            GraphJson, RoleBindingsJson, RequiredDocumentTemplatesJson, CreatedAtUtc
        )
        VALUES (
            $templateId, $version, $displayName, $description, $isBuiltIn,
            $graphJson, $roleBindingsJson, $requiredDocumentTemplatesJson, $createdAtUtc
        );
        """;

    private const string InsertSeedSql = """
        INSERT INTO WorkflowTemplateVersions (
            TemplateId, Version, DisplayName, Description, IsBuiltIn,
            GraphJson, RoleBindingsJson, RequiredDocumentTemplatesJson, CreatedAtUtc
        )
        VALUES (
            $templateId, $version, $displayName, $description, $isBuiltIn,
            $graphJson, $roleBindingsJson, $requiredDocumentTemplatesJson, $createdAtUtc
        )
        ON CONFLICT (TemplateId, Version) DO NOTHING;
        """;

    private const string UpsertAssignmentSql = """
        INSERT INTO WorkflowTemplateAssignments (
            ProjectId, AssignmentId, TemplateId, TemplateVersion, AssignedAtUtc
        )
        VALUES (
            $projectId, $assignmentId, $templateId, $templateVersion, $assignedAtUtc
        )
        ON CONFLICT (ProjectId) DO UPDATE SET
            AssignmentId = excluded.AssignmentId,
            TemplateId = excluded.TemplateId,
            TemplateVersion = excluded.TemplateVersion,
            AssignedAtUtc = excluded.AssignedAtUtc;
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IWorkflowGraphValidator _graphValidator;
    private readonly IReadOnlyList<WorkflowTemplateDefinition> _seedVersions;
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    // Publish the verified seed identity together with successful seed completion.
    private volatile bool _seedApplied;
    private bool _currentStandardSeedVerified;

    /// <summary>
    /// Creates the store. <paramref name="seedVersions"/> are the shipped template versions that must
    /// exist before a project can be pointed at them. They are written once through the same immutable
    /// insert and are ignored when the version is already stored, so neither a restart nor a second
    /// store instance ever rewrites a version. The seed is applied on the first store operation, which
    /// keeps the constructor free of I/O while still guaranteeing the shipped versions before any
    /// assignment can name one.
    /// </summary>
    public SqliteWorkflowTemplateStore(
        ISqliteConnectionFactory connectionFactory,
        IWorkflowGraphValidator? graphValidator = null,
        IEnumerable<WorkflowTemplateDefinition>? seedVersions = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _graphValidator = graphValidator ?? new WorkflowGraphValidator();
        _seedVersions = seedVersions is null
            ? Array.Empty<WorkflowTemplateDefinition>()
            : seedVersions.ToArray();
    }

    public async Task SaveAsync(
        WorkflowTemplateDefinition template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureGraphCanBeStored(template);

        await EnsureSeedAppliedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteAsync(InsertSql, template, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsDuplicateVersion(exception))
        {
            throw new WorkflowValidationException(
                $"Template '{template.TemplateId}' version {template.Version} already exists. Saved "
                + "template versions are immutable; create a new version instead of overwriting one.",
                exception);
        }
    }

    public async Task<WorkflowTemplateDefinition?> GetAsync(
        string templateId,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureSeedAppliedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM WorkflowTemplateVersions
            WHERE TemplateId = $templateId AND Version = $version;
            """;
        command.Parameters.AddWithValue("$templateId", templateId);
        command.Parameters.AddWithValue("$version", version);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTemplate(reader)
            : null;
    }

    public async Task<WorkflowTemplateDefinition?> GetLatestAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureSeedAppliedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM WorkflowTemplateVersions
            WHERE TemplateId = $templateId
            ORDER BY Version DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$templateId", templateId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadTemplate(reader)
            : null;
    }

    public async Task<IReadOnlyList<WorkflowTemplateDefinition>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureSeedAppliedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // The order the in-memory store produces: shipped versions first, then template id in ordinal
        // order, then version. SQLite orders text by its binary collation, which is the ordinal order
        // of the identifiers the Studio uses.
        command.CommandText = $"""
            SELECT {SelectColumns} FROM WorkflowTemplateVersions
            ORDER BY IsBuiltIn DESC, TemplateId ASC, Version ASC;
            """;

        var templates = new List<WorkflowTemplateDefinition>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Retire only the exact known historical built-in, and only after the actual
            // replacement row has been verified. Direct reads and assigned old pins still fail closed.
            if (_currentStandardSeedVerified
                && reader.GetString(0) == WorkflowStudioService.LegacyStandardTemplateId
                && reader.GetInt32(1) == 1 && reader.GetInt32(4) == 1
                && WorkflowStudioService.IsRetiredStandardGraphSnapshot(reader.GetString(5)))
            {
                continue;
            }
            templates.Add(ReadTemplate(reader));
        }

        return templates;
    }

    public async Task SaveAssignmentAsync(
        WorkflowTemplateAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureSeedAppliedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = await _connectionFactory
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = UpsertAssignmentSql;
            command.Parameters.AddWithValue("$projectId", assignment.ProjectId);
            command.Parameters.AddWithValue("$assignmentId", assignment.AssignmentId);
            command.Parameters.AddWithValue("$templateId", assignment.TemplateId);
            command.Parameters.AddWithValue("$templateVersion", assignment.TemplateVersion);
            command.Parameters.AddWithValue(
                "$assignedAtUtc",
                SqliteRepositorySupport.FormatTimestamp(assignment.AssignedAtUtc));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsForeignKeyViolation(exception))
        {
            throw new WorkflowValidationException(
                $"Template '{assignment.TemplateId}' version {assignment.TemplateVersion} does not exist, "
                + $"so project '{assignment.ProjectId}' cannot be assigned to it. Save the template version "
                + "first: an assignment may only name a version that is stored.",
                exception);
        }
    }

    public async Task<WorkflowTemplateAssignment?> GetAssignmentAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureSeedAppliedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AssignmentId, ProjectId, TemplateId, TemplateVersion, AssignedAtUtc
            FROM WorkflowTemplateAssignments
            WHERE ProjectId = $projectId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new WorkflowTemplateAssignment(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            SqliteRepositorySupport.ParseTimestamp(reader.GetString(4)));
    }

    private static bool IsDuplicateVersion(SqliteException exception) =>
        exception.SqliteErrorCode == ConstraintErrorCode
        && exception.SqliteExtendedErrorCode
            is ConstraintPrimaryKeyExtendedErrorCode or ConstraintUniqueExtendedErrorCode;

    private void EnsureGraphCanBeStored(WorkflowTemplateDefinition template)
    {
        try
        {
            _graphValidator.Validate(template.Graph);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new WorkflowValidationException(
                $"The graph of template '{template.TemplateId}' version {template.Version} is not a valid "
                + $"workflow graph and is therefore not stored: {exception.Message}",
                exception);
        }
    }

    private static bool IsForeignKeyViolation(SqliteException exception) =>
        exception.SqliteErrorCode == ConstraintErrorCode
        && exception.SqliteExtendedErrorCode == ConstraintForeignKeyExtendedErrorCode;

    private async Task EnsureSeedAppliedAsync(CancellationToken cancellationToken)
    {
        if (_seedApplied || _seedVersions.Count == 0)
        {
            return;
        }

        await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_seedApplied)
            {
                return;
            }

            // Validate the entire immutable batch before any write. A later invalid seed must never
            // strand an earlier version that ordinary save cannot overwrite.
            foreach (var template in _seedVersions) EnsureGraphCanBeStored(template);
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            foreach (var template in _seedVersions)
                await ExecuteAsync(connection, transaction, InsertSeedSql, template, cancellationToken).ConfigureAwait(false);
            var canonical = WorkflowStudioService.CreateStandardTemplate();
            var hasCurrentStandardSeed = _seedVersions.Any(seed =>
                seed.TemplateId == canonical.TemplateId && seed.Version == canonical.Version);
            if (hasCurrentStandardSeed)
                await VerifyCurrentStandardSeedAsync(connection, transaction, canonical, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _currentStandardSeedVerified = hasCurrentStandardSeed;
            _seedApplied = true;
        }
        finally
        {
            _seedGate.Release();
        }
    }

    private static async Task VerifyCurrentStandardSeedAsync(
        SqliteConnection connection, SqliteTransaction transaction, WorkflowTemplateDefinition canonical,
        CancellationToken cancellationToken)
    {
        var payload = Serialize(canonical);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM WorkflowTemplateVersions WHERE TemplateId = $id AND Version = $version;";
        command.Parameters.AddWithValue("$id", canonical.TemplateId);
        command.Parameters.AddWithValue("$version", canonical.Version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetString(2) != canonical.DisplayName || reader.GetString(3) != canonical.Description
            || reader.GetInt32(4) != 1 || reader.GetString(5) != payload.GraphJson
            || reader.GetString(6) != payload.RoleBindingsJson
            || reader.GetString(7) != payload.RequiredDocumentTemplatesJson
            || reader.GetString(8) != SqliteRepositorySupport.FormatTimestamp(canonical.CreatedAtUtc))
        {
            throw new WorkflowValidationException(
                $"[built-in-version-conflict] The stored template '{canonical.TemplateId}' version {canonical.Version} "
                + "conflicts with the current shipped definition. No version or assignment was overwritten. "
                + "The immutable conflicting identity requires explicit administrative reconciliation; "
                + "exporting a copy alone does not remove this conflict. Existing rows will not be overwritten.");
        }
    }

    private async Task ExecuteAsync(
        string commandText,
        WorkflowTemplateDefinition template,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(connection, null, commandText, template, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string commandText, WorkflowTemplateDefinition template, CancellationToken cancellationToken)
    {
        var payload = Serialize(template);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$templateId", template.TemplateId);
        command.Parameters.AddWithValue("$version", template.Version);
        command.Parameters.AddWithValue("$displayName", template.DisplayName);
        command.Parameters.AddWithValue("$description", template.Description);
        command.Parameters.AddWithValue("$isBuiltIn", template.IsBuiltIn ? 1 : 0);
        command.Parameters.AddWithValue("$graphJson", payload.GraphJson);
        command.Parameters.AddWithValue("$roleBindingsJson", payload.RoleBindingsJson);
        command.Parameters.AddWithValue(
            "$requiredDocumentTemplatesJson",
            payload.RequiredDocumentTemplatesJson);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            SqliteRepositorySupport.FormatTimestamp(template.CreatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private WorkflowTemplateDefinition ReadTemplate(SqliteDataReader reader)
    {
        var templateId = reader.GetString(0);
        var version = reader.GetInt32(1);

        WorkflowTemplateDefinition template;

        // A row that cannot be turned back into a template, or whose graph no longer validates, is
        // corruption and not a working template. It is reported as one named error carrying the
        // underlying message instead of being handed to the Studio as an executable graph or surfacing
        // a raw domain, format or timestamp error out of the data layer.
        try
        {
            var graph = DeserializeGraph(reader.GetString(5), templateId, version);

            template = new WorkflowTemplateDefinition(
                templateId,
                version,
                reader.GetString(2),
                reader.GetString(3),
                graph,
                DeserializeRoleBindings(reader.GetString(6), templateId, version),
                DeserializeDocumentKinds(reader.GetString(7), templateId, version),
                reader.GetInt32(4) == 1,
                SqliteRepositorySupport.ParseTimestamp(reader.GetString(8)));

            _graphValidator.Validate(graph);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException
            or FormatException
            or OverflowException)
        {
            throw new InvalidDataException(
                $"The stored version {version} of template '{templateId}' is not a readable and valid "
                + $"workflow template: {exception.Message}",
                exception);
        }

        return template;
    }

    private static StoredTemplatePayload Serialize(WorkflowTemplateDefinition template)
    {
        // The graph is written as the very document a run pins, by the very serializer that reads it
        // back: a second node shape here would be a second place where a node's declared gates, review,
        // approval or artifact requirement could be dropped on the way into the database.
        var roleBindings = template.RoleBindings
            .Select(binding => new StoredRoleBinding(
                binding.RoleId,
                binding.PrimaryRouteId,
                binding.ModelId,
                binding.FallbackRouteIds,
                binding.RequiredCapabilities))
            .ToArray();

        return new StoredTemplatePayload(
            WorkflowGraphSnapshot.Serialize(template.Graph),
            JsonSerializer.Serialize(roleBindings, JsonOptions),
            JsonSerializer.Serialize(
                template.RequiredDocumentTemplates
                    .Select(kind => SqliteRepositorySupport.FormatEnum(kind))
                    .ToArray(),
                JsonOptions));
    }

    private static WorkflowGraph DeserializeGraph(
        string graphJson,
        string templateId,
        int version)
    {
        // Read through the graph snapshot reader, for the same reason it is written through the snapshot
        // serializer, and report an unusable document as the corruption this store reports every other
        // unreadable row as rather than as a partially rebuilt graph.
        try
        {
            return WorkflowGraphSnapshot.ReadStoredTemplateGraph(graphJson, templateId, version);
        }
        catch (Exception exception) when (exception is WorkflowValidationException
            or ArgumentException
            or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"The stored version {version} of template '{templateId}' is not a readable and valid "
                    + $"workflow template: {exception.Message}",
                exception);
        }
    }

    private static IReadOnlyList<RoleBindingDefinition> DeserializeRoleBindings(
        string roleBindingsJson,
        string templateId,
        int version)
    {
        var stored = ReadJson<List<StoredRoleBinding>>(
            roleBindingsJson,
            templateId,
            version,
            "role bindings");

        return stored
            .Select(binding => new RoleBindingDefinition(
                binding.RoleId,
                binding.PrimaryRouteId,
                binding.FallbackRouteIds,
                binding.RequiredCapabilities,
                binding.ModelId))
            .ToArray();
    }

    private static IReadOnlyList<DocumentTemplateKind> DeserializeDocumentKinds(
        string documentKindsJson,
        string templateId,
        int version)
    {
        var stored = ReadJson<List<string>>(
            documentKindsJson,
            templateId,
            version,
            "required document templates");

        return stored
            .Select(SqliteRepositorySupport.ParseEnum<DocumentTemplateKind>)
            .ToArray();
    }

    private static T ReadJson<T>(string json, string templateId, int version, string part)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidDataException(
                    $"The stored {part} of template '{templateId}' version {version} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The stored {part} of template '{templateId}' version {version} is not readable: "
                + exception.Message,
                exception);
        }
    }

    private sealed record StoredTemplatePayload(
        string GraphJson,
        string RoleBindingsJson,
        string RequiredDocumentTemplatesJson);

    private sealed record StoredRoleBinding(
        string RoleId,
        string? PrimaryRouteId,
        string? ModelId,
        IReadOnlyList<string> FallbackRouteIds,
        IReadOnlyList<string> RequiredCapabilities);
}
