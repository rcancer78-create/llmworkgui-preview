using System.Globalization;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests;

public sealed class DatabaseMigrationTests : IDisposable
{
    private const string Timestamp = "2026-09-22T00:00:00Z";

    private readonly TestDirectory _directory = new();

    public void Dispose()
    {
        TestSqlitePool.Clear(CreateFactory());
        _directory.Dispose();
    }

    [Fact]
    public async Task Migrate_CreatesEveryDeclaredTable()
    {
        var factory = CreateFactory();
        var migrator = new DatabaseMigrator(factory);

        var report = await migrator.MigrateAsync();

        Assert.False(report.WasUpToDate);
        Assert.Equal(0, report.PreviousSchemaVersion);
        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Max(m => m.Version), report.CurrentSchemaVersion);
        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Count, report.AppliedMigrations.Count);
        Assert.Equal("InitialSchema", report.AppliedMigrations[0].Name);
        Assert.Equal("SecretReferenceMetadata", report.AppliedMigrations[2].Name);
        Assert.Equal("WorkflowTemplateStore", report.AppliedMigrations[3].Name);
        Assert.Equal("WorkflowRunTemplatePinning", report.AppliedMigrations[4].Name);
        Assert.Equal("WorkflowRunArtifactGate", report.AppliedMigrations[5].Name);
        Assert.Equal("RunScopedArtifactIdentity", report.AppliedMigrations[6].Name);
        Assert.Equal("WorkflowReviewExecutionProvenance", report.AppliedMigrations[7].Name);
        Assert.Equal("WorkflowReviewObservedRouteAuthority", report.AppliedMigrations[8].Name);
        Assert.Equal(53, DatabaseSchema.TableNames.Count);

        await using var connection = await factory.OpenConnectionAsync();
        var tables = await ReadTableNamesAsync(connection);

        Assert.Contains(DatabaseSchema.MigrationsTableName, tables);

        foreach (var tableName in DatabaseSchema.TableNames)
        {
            Assert.Contains(tableName, tables);
        }

        Assert.Equal(DatabaseSchema.TableNames.Count + 1, tables.Count);
    }

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        var factory = CreateFactory();

        var first = await new DatabaseMigrator(factory).MigrateAsync();
        var second = await new DatabaseMigrator(factory).MigrateAsync();

        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Count, first.AppliedMigrations.Count);
        Assert.True(second.WasUpToDate);
        Assert.Empty(second.AppliedMigrations);
        Assert.Equal(first.CurrentSchemaVersion, second.CurrentSchemaVersion);

        await using var connection = await factory.OpenConnectionAsync();

        Assert.Equal(
            (long)DatabaseMigrator.LoadEmbeddedMigrations().Count,
            Convert.ToInt64(
                await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM _schema_migrations;"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ApprovalRuleAuditMigration_BackfillsExistingRulesAndRejectsMutation()
    {
        var factory = CreateFactory();
        var initial = DatabaseMigrator.LoadEmbeddedMigrations()[0];
        await new DatabaseMigrator(factory, new[] { initial }).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('project', 'Project', 'C:\\project', 'PrivateSource', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('provider', 'Provider', 'OpenCode', 'PrivateSource', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO ApprovalRules (Id, Backend, ProviderProfileId, ProjectId, Kind, Operation, CreatedBy, CreatedAtUtc)
                VALUES ('old-rule', 'OpenCode', 'provider', 'project', 'ReadFile', 'read', 'admin', '2026-09-22T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var report = await new DatabaseMigrator(factory).MigrateAsync();
        Assert.Equal(1, report.PreviousSchemaVersion);
        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Max(m => m.Version), report.CurrentSchemaVersion);

        await using var upgraded = await factory.OpenConnectionAsync();
        Assert.Equal("Existing", await ExecuteScalarAsync(upgraded,
            "SELECT Action FROM ApprovalRuleAudit WHERE RuleId = 'old-rule';"));
        Assert.Equal(Timestamp, await ExecuteScalarAsync(upgraded,
            "SELECT CreatedAtUtc FROM ApprovalRuleAudit WHERE RuleId = 'old-rule';"));
        await using var mutation = upgraded.CreateCommand();
        mutation.CommandText = "DELETE FROM ApprovalRuleAudit WHERE RuleId = 'old-rule';";
        await Assert.ThrowsAsync<SqliteException>(() => mutation.ExecuteNonQueryAsync());
        mutation.CommandText = "UPDATE ApprovalRuleAudit SET Action = 'Created' WHERE RuleId = 'old-rule';";
        await Assert.ThrowsAsync<SqliteException>(() => mutation.ExecuteNonQueryAsync());
        Assert.Equal(1L, Convert.ToInt64(await ExecuteScalarAsync(upgraded,
            "SELECT COUNT(*) FROM ApprovalRuleAudit WHERE RuleId = 'old-rule';"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Migrate_EnablesForeignKeysAndWalJournalMode()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using var connection = await factory.OpenConnectionAsync();

        Assert.Equal(
            1L,
            Convert.ToInt64(
                await ExecuteScalarAsync(connection, "PRAGMA foreign_keys;"),
                CultureInfo.InvariantCulture));

        Assert.Equal("wal", await ExecuteScalarAsync(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task ForeignKeys_RejectOrphanRows()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using var connection = await factory.OpenConnectionAsync();

        var exception = await Assert.ThrowsAsync<SqliteException>(() => InsertAccountAsync(
            connection,
            "account-1",
            "missing-provider-profile"));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task ForeignKeys_AllowLinkedRows()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using var connection = await factory.OpenConnectionAsync();

        await using (var providerCommand = connection.CreateCommand())
        {
            providerCommand.CommandText = $"""
                INSERT INTO ProviderProfiles
                    (Id, DisplayName, Backend, MaxDataClass, IsEnabled, CreatedAtUtc, UpdatedAtUtc)
                VALUES
                    ('provider-1', 'Demo provider', 'OpenCode', 'PrivateSource', 1, '{Timestamp}', '{Timestamp}');
                """;

            await providerCommand.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await InsertAccountAsync(connection, "account-1", "provider-1"));
    }

    [Fact]
    public async Task SecretReferenceColumns_RejectNonUrnValues()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using var connection = await factory.OpenConnectionAsync();

        await using (var providerCommand = connection.CreateCommand())
        {
            providerCommand.CommandText = $"""
                INSERT INTO ProviderProfiles
                    (Id, DisplayName, Backend, MaxDataClass, IsEnabled, CreatedAtUtc, UpdatedAtUtc)
                VALUES
                    ('provider-1', 'Demo provider', 'OpenCode', 'PrivateSource', 1, '{Timestamp}', '{Timestamp}');
                """;

            await providerCommand.ExecuteNonQueryAsync();
        }

        await using var accountCommand = connection.CreateCommand();
        accountCommand.CommandText = $"""
            INSERT INTO Accounts
                (Id, ProviderProfileId, DisplayName, AuthState, Health, SecretReference, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ('account-1', 'provider-1', 'Demo account', 'Valid', 'Healthy', 'sk-plaintext-api-key', '{Timestamp}', '{Timestamp}');
            """;

        await Assert.ThrowsAsync<SqliteException>(() => accountCommand.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Migrate_RejectsMigrationWithChangedChecksum()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        var tamperedMigrations = new[]
        {
            new DatabaseMigration(1, "InitialSchema", "CREATE TABLE Tampered (Id TEXT NOT NULL PRIMARY KEY);")
        };

        var migrator = new DatabaseMigrator(factory, tamperedMigrations);

        await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync());
    }

    [Fact]
    public async Task Migrate_RejectsDatabaseNewerThanKnownMigrations()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO _schema_migrations (version, name, checksum, applied_at_utc)
                VALUES (99, 'FutureSchema', 'future', '{Timestamp}');
                """;

            await command.ExecuteNonQueryAsync();
        }

        var migrator = new DatabaseMigrator(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync());
    }

    [Fact]
    public void LoadEmbeddedMigrations_ProvidesInitialSchemaForAllTables()
    {
        var migrations = DatabaseMigrator.LoadEmbeddedMigrations();

        Assert.Equal(31, migrations.Count);
        Assert.Equal("project_data_policy_audit", migrations[30].Name);
        Assert.Equal("model_capability_context", migrations[29].Name);
        Assert.Equal("model_capability_invalidation", migrations[28].Name);
        Assert.Equal("opencode_dispatch_decision", migrations[27].Name);
        Assert.Contains("DispatchNativeModelId", migrations[27].Sql);
        Assert.Equal("account_credential_ownership", migrations[26].Name);
        Assert.Contains("TR_AccountCredential_HeldExecution", migrations[26].Sql);
        Assert.Equal("adaptation_runtime_ownership", migrations[25].Name);
        Assert.Contains("WorkflowAdaptationRuntimeOwners", migrations[25].Sql);
        Assert.Equal("workflow_adaptation_transport", migrations[24].Name);
        Assert.Contains("WorkflowAdaptationNativeBindings", migrations[24].Sql);
        Assert.Equal("workflow_material_policy", migrations[23].Name);
        Assert.Contains("WorkflowMaterialPolicies", migrations[23].Sql);
        Assert.Equal("workflow_review_responses", migrations[22].Name);
        Assert.Equal("ArtifactExecutionChronology", migrations[21].Name);
        Assert.Equal("ArtifactExecutionAssociation", migrations[20].Name);
        Assert.Contains("TR_Artifacts_ExecutionBelongsToRun", migrations[20].Sql);
        Assert.Contains("TR_Artifacts_ExecutionAssociationIsImmutable", migrations[20].Sql);

        var initialSchema = migrations[0];

        Assert.Equal(1, initialSchema.Version);
        Assert.Equal("InitialSchema", initialSchema.Name);

        foreach (var tableName in DatabaseSchema.TableNames.Where(name => !AddedAfterInitialSchema.Contains(name)))
        {
            Assert.Contains($"CREATE TABLE {tableName} (", initialSchema.Sql, StringComparison.Ordinal);
        }

        Assert.Contains("CREATE TABLE ApprovalRuleAudit (", migrations[1].Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE SecretReferences (", migrations[2].Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE SecretReferenceOwners (", migrations[2].Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE WorkflowTemplateVersions (", migrations[3].Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE WorkflowTemplateAssignments (", migrations[3].Sql, StringComparison.Ordinal);
        Assert.Equal(4, migrations[3].Version);
        Assert.Equal("WorkflowTemplateStore", migrations[3].Name);

        Assert.Equal(5, migrations[4].Version);
        Assert.Equal("WorkflowRunTemplatePinning", migrations[4].Name);
        Assert.Contains(
            "ALTER TABLE WorkflowRuns ADD COLUMN TemplateId TEXT NULL;",
            migrations[4].Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE WorkflowRuns ADD COLUMN TemplateGraphSnapshotJson TEXT NULL;",
            migrations[4].Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE WorkflowRuns ADD COLUMN TemplateSchemeSnapshotJson TEXT NULL;",
            migrations[4].Sql,
            StringComparison.Ordinal);

        Assert.Equal(6, migrations[5].Version);
        Assert.Equal("WorkflowRunArtifactGate", migrations[5].Name);
        Assert.Contains(
            "ALTER TABLE Artifacts ADD COLUMN StageId TEXT NULL;",
            migrations[5].Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "TR_Artifacts_RunScopedArtifactIsComplete",
            migrations[5].Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "TR_Artifacts_RunScopedArtifactIsImmutable",
            migrations[5].Sql,
            StringComparison.Ordinal);

        // 007 and 008 are additive and rewrite nothing the one before it created, and neither does 009.
        Assert.Equal(7, migrations[6].Version);
        Assert.Equal("RunScopedArtifactIdentity", migrations[6].Name);
        Assert.Contains(
            "TR_Artifacts_RunScopedArtifactIsNamed",
            migrations[6].Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", migrations[6].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", migrations[6].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", migrations[6].Sql, StringComparison.OrdinalIgnoreCase);

        // 008 keeps the exact text an already migrated database recorded as its checksum, and 009 is one more
        // trigger on the table 008 created - which is what makes the observed route the execution's to say.
        Assert.Equal(8, migrations[7].Version);
        Assert.Equal("WorkflowReviewExecutionProvenance", migrations[7].Name);
        Assert.Equal(9, migrations[8].Version);
        Assert.Equal("WorkflowReviewObservedRouteAuthority", migrations[8].Name);
        Assert.Contains(
            "TR_WorkflowReviewExecutions_ObservedRouteIsObserved",
            migrations[8].Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", migrations[8].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", migrations[8].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", migrations[8].Sql, StringComparison.OrdinalIgnoreCase);

        // 010 gives the Activity Center a durable home. Like 007 and 009 it is purely additive: it creates
        // one new table and two indexes, and rewrites and drops nothing that already exists.
        Assert.Equal(10, migrations[9].Version);
        Assert.Equal("ActivityEventJournal", migrations[9].Name);
        Assert.Contains("CREATE TABLE IF NOT EXISTS ActivityEvents (", migrations[9].Sql, StringComparison.Ordinal);
        Assert.Contains("IX_ActivityEvents_OccurredAtUtc", migrations[9].Sql, StringComparison.Ordinal);
        Assert.Contains("IX_ActivityEvents_ExecutionId", migrations[9].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", migrations[9].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", migrations[9].Sql, StringComparison.OrdinalIgnoreCase);

        // 011 gives the journal an exact durable search. It has to stay additive like every migration
        // before it: it creates the full-text index and backfills it, and alters and drops nothing, so a
        // database migrated by an earlier build keeps exactly the rows it had.
        Assert.Equal(11, migrations[10].Version);
        Assert.Equal("ActivityEventFullTextSearch", migrations[10].Name);
        Assert.Contains(
            "CREATE VIRTUAL TABLE IF NOT EXISTS ActivityEventsSearch USING fts5",
            migrations[10].Sql,
            StringComparison.Ordinal);
        Assert.Contains("IX_ActivityEvents_RoleStateTime", migrations[10].Sql, StringComparison.Ordinal);
        Assert.Contains("IX_ActivityEvents_SourceTime", migrations[10].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", migrations[10].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", migrations[10].Sql, StringComparison.OrdinalIgnoreCase);

        // 012 gives the gateway-native reviewer identity a place to be stored. It is the one migration here
        // that alters an existing table, and it may only add columns: it rewrites no row, backfills nothing,
        // and creates no table, so a database migrated by an earlier build keeps exactly the rows it had and
        // every one of them stays unbound.
        Assert.Equal(12, migrations[11].Version);
        Assert.Equal("ReviewerNativeRouteIdentity", migrations[11].Name);
        Assert.Contains(
            "ALTER TABLE ProviderProfiles ADD COLUMN GatewayNativeId TEXT NULL",
            migrations[11].Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE Accounts ADD COLUMN GatewayNativeId TEXT NULL",
            migrations[11].Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE Models ADD COLUMN GatewayNativeId TEXT NULL",
            migrations[11].Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE Routes ADD COLUMN GatewayRouteKey TEXT NULL",
            migrations[11].Sql,
            StringComparison.Ordinal);
        Assert.Contains("IX_ProviderProfiles_BackendGatewayNativeId", migrations[11].Sql, StringComparison.Ordinal);
        Assert.Contains("IX_Accounts_ProviderProfileGatewayNativeId", migrations[11].Sql, StringComparison.Ordinal);
        Assert.Contains("IX_Models_ProviderProfileGatewayNativeId", migrations[11].Sql, StringComparison.Ordinal);
        Assert.Contains("IX_Routes_ProviderProfileGatewayRouteKey", migrations[11].Sql, StringComparison.Ordinal);

        // Nothing rewrites, and in particular nothing derives an identity from a name, a path, an alias or a
        // past request - a fabricated value in these columns would be indistinguishable from an observation.
        Assert.DoesNotContain("UPDATE ", migrations[11].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO", migrations[11].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", migrations[11].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", migrations[11].Sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The artifact gate is a storage guarantee and not only an application one: a run-scoped row that does
    /// not name its stage, its stored blob and the hash of those bytes is refused by the database, and a row
    /// that was recorded once cannot afterwards be pointed at different content.
    /// </summary>
    [Fact]
    public async Task ArtifactGateMigration_RefusesAnIncompleteOrRewrittenRunScopedArtifact()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('project', 'Project', 'C:\project', 'PrivateSource', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO WorkflowPackages (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('package', 'package.zip', 'Imported', 'hash-1', 'blob-1', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
                VALUES ('version-1', 'package', 1, 'blob-1', 'hash-1', 'Imported', '2026-09-22T00:00:00Z');
                INSERT INTO WorkflowRuns (Id, ProjectId, WorkflowPackageId, WorkflowVersionId, State, StartedAtUtc, EvidenceRedactedJson)
                VALUES ('run-1', 'project', 'package', 'version-1', 'Running', '2026-09-22T00:00:00Z',
                        '{"currentStageId":"stage-1","currentRole":"Coordinator"}');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        const string hash = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string otherHash = "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        const string upperCaseHash =
            "sha256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        await using var upgraded = await factory.OpenConnectionAsync();

        // A complete run-scoped row is accepted.
        await using (var insert = upgraded.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
                VALUES ('artifact-ok', 'run-1', 'stage-1', 'DocumentBundle', $blob, $blob, 12, 'PrivateSource', '2026-09-22T00:00:00Z');
                """;
            insert.Parameters.AddWithValue("$blob", hash);
            await insert.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(upgraded,
            """
            INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
            VALUES ('artifact-no-stage', 'run-1', NULL, 'DocumentBundle', $blob, $blob, 12, 'PrivateSource', '2026-09-22T00:00:00Z');
            """,
            ("$blob", hash)));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(upgraded,
            """
            INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
            VALUES ('artifact-no-hash', 'run-1', 'stage-1', 'DocumentBundle', $blob, $other, 12, 'PrivateSource', '2026-09-22T00:00:00Z');
            """,
            ("$blob", hash),
            ("$other", otherHash)));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(upgraded,
            """
            INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
            VALUES ('artifact-upper-case', 'run-1', 'stage-1', 'DocumentBundle', $blob, $blob, 12, 'PrivateSource', '2026-09-22T00:00:00Z');
            """,
            ("$blob", upperCaseHash)));

        // An execution-only row keeps working exactly as it did before this migration.
        await using (var executionSeed = upgraded.CreateCommand())
        {
            executionSeed.CommandText = """
                INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('provider', 'Provider', 'OpenCode', 'PrivateSource', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('account', 'provider', 'Account', 'Unverified', 'Unknown', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO Models (
                    Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState, Provenance,
                    Health, DiscoveredAtUtc)
                VALUES ('model', 'OpenCode', 'provider', 'model-1', 'Model', 'Unknown', 'Imported', 'Unknown',
                        '2026-09-22T00:00:00Z');
                INSERT INTO Routes (Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, Health, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('route', 'OpenCode', 'provider', 'account', 'model', 'PrivateSource', 'Unknown',
                        '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO Sessions (
                    Id, ProjectId, Backend, ProviderProfileId, AccountId, ModelId, WorkspaceRootPath, State,
                    ReconciliationOutcome, CloseReason, CreatedAtUtc, LastEventAtUtc)
                VALUES ('session', 'project', 'OpenCode', 'provider', 'account', 'model', 'C:\workspace', 'Idle',
                        'None', 'None', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO Executions (Id, SessionId, ClientRequestId, State, FailureReason, RequestedRouteId, CreatedAtUtc)
                VALUES ('execution', 'session', 'client-request', 'Succeeded', 'None', 'route', '2026-09-22T00:00:00Z');
                INSERT INTO Artifacts (Id, ExecutionId, Kind, DataClassification, CreatedAtUtc)
                VALUES ('artifact-legacy', 'execution', 'Diff', 'PrivateSource', '2026-09-22T00:00:00Z');
                """;
            await executionSeed.ExecuteNonQueryAsync();
        }

        // The one complete run-scoped row and the one execution-only row, and none of the refused inserts.
        Assert.Equal(
            1L,
            Convert.ToInt64(
                await ExecuteScalarAsync(upgraded, "SELECT COUNT(*) FROM Artifacts WHERE WorkflowRunId IS NOT NULL;"),
                CultureInfo.InvariantCulture));
        Assert.Equal(
            1L,
            Convert.ToInt64(
                await ExecuteScalarAsync(upgraded, "SELECT COUNT(*) FROM Artifacts WHERE ExecutionId IS NOT NULL;"),
                CultureInfo.InvariantCulture));
        Assert.Null(await ExecuteNullableScalarAsync(
            upgraded,
            "SELECT StageId FROM Artifacts WHERE Id = 'artifact-legacy';",
            ("$id", "artifact-legacy")));

        // And recorded evidence cannot afterwards be pointed at other content.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(upgraded,
            "UPDATE Artifacts SET HashSha256 = $other WHERE Id = 'artifact-ok';",
            ("$other", otherHash)));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(upgraded,
            "UPDATE Artifacts SET StageId = 'stage-2' WHERE Id = 'artifact-ok';"));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(upgraded,
            "UPDATE Artifacts SET Kind = 'OtherKind' WHERE Id = 'artifact-ok';"));

        Assert.Equal(
            hash,
            await ExecuteScalarAsync(upgraded, "SELECT HashSha256 FROM Artifacts WHERE Id = 'artifact-ok';"));
    }

    /// <summary>
    /// The second half of the artifact gate: a run-scoped row also has to name itself. 006 validated the
    /// stage, the kind, the blob and the hash of a new row but never the row's own Id, and `Id TEXT NOT NULL`
    /// accepts an empty or whitespace string.
    ///
    /// The migration is additive, so it is exercised on a database that is genuinely on the 006 schema with
    /// 006-era rows already in it - including the blank-identifier row a pre-007 writer could have left
    /// behind. Those rows are neither refused nor rewritten: what the trigger guards is a *new* insert, and
    /// a stored row nobody may rename is the repository read path's business, not the schema's.
    /// </summary>
    [Fact]
    public async Task ArtifactIdentityMigration_RefusesANewBlankRunScopedIdAndKeepsEveryExistingRow()
    {
        const string hash = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string otherHash = "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        const string thirdHash = "sha256:00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

        var factory = CreateFactory();
        var migrations = DatabaseMigrator.LoadEmbeddedMigrations();

        await new DatabaseMigrator(factory, migrations.Where(m => m.Version < 7).ToArray()).MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('project', 'Project', 'C:\project', 'PrivateSource', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO WorkflowPackages (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('package', 'package.zip', 'Imported', 'hash-1', 'blob-1', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
                VALUES ('version-1', 'package', 1, 'blob-1', 'hash-1', 'Imported', '2026-09-22T00:00:00Z');
                INSERT INTO WorkflowRuns (Id, ProjectId, WorkflowPackageId, WorkflowVersionId, State, StartedAtUtc, EvidenceRedactedJson)
                VALUES ('run-1', 'project', 'package', 'version-1', 'Running', '2026-09-22T00:00:00Z',
                        '{"currentStageId":"stage-1","currentRole":"Coordinator"}');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var connection = await factory.OpenConnectionAsync())
        {
            // Complete in every respect 006 validates, and unnamed. 006 lets this through, so a database can
            // reach 007 already holding a row that names a stage it cannot be read as.
            await ExecuteAsync(
                connection,
                """
                INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
                VALUES ($id, 'run-1', 'stage-1', 'DocumentBundle', $blob, $blob, 12, 'PrivateSource', '2026-09-22T00:00:00Z');
                """,
                ("$id", "   "),
                ("$blob", hash));

            await ExecuteAsync(
                connection,
                """
                INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
                VALUES ('artifact-ok', 'run-1', 'stage-1', 'DocumentBundle', $blob, $blob, 12, 'PrivateSource', '2026-09-22T00:00:00Z');
                """,
                ("$blob", otherHash));

            // An execution-only row, which is a different aggregate's evidence and is outside the trigger.
            await using var executionSeed = connection.CreateCommand();
            executionSeed.CommandText = """
                INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('provider', 'Provider', 'OpenCode', 'PrivateSource', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('account', 'provider', 'Account', 'Unverified', 'Unknown', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO Models (
                    Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState, Provenance,
                    Health, DiscoveredAtUtc)
                VALUES ('model', 'OpenCode', 'provider', 'model-1', 'Model', 'Unknown', 'Imported', 'Unknown',
                        '2026-09-22T00:00:00Z');
                INSERT INTO Routes (Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, Health, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('route', 'OpenCode', 'provider', 'account', 'model', 'PrivateSource', 'Unknown',
                        '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO Sessions (
                    Id, ProjectId, Backend, ProviderProfileId, AccountId, ModelId, WorkspaceRootPath, State,
                    ReconciliationOutcome, CloseReason, CreatedAtUtc, LastEventAtUtc)
                VALUES ('session', 'project', 'OpenCode', 'provider', 'account', 'model', 'C:\workspace', 'Idle',
                        'None', 'None', '2026-09-22T00:00:00Z', '2026-09-22T00:00:00Z');
                INSERT INTO Executions (Id, SessionId, ClientRequestId, State, FailureReason, RequestedRouteId, CreatedAtUtc)
                VALUES ('execution', 'session', 'client-request', 'Succeeded', 'None', 'route', '2026-09-22T00:00:00Z');
                INSERT INTO Artifacts (Id, ExecutionId, Kind, DataClassification, CreatedAtUtc)
                VALUES ($id, 'execution', 'Diff', 'PrivateSource', '2026-09-22T00:00:00Z');
                """;
            executionSeed.Parameters.AddWithValue("$id", "  ");
            await executionSeed.ExecuteNonQueryAsync();
        }

        var report = await new DatabaseMigrator(factory).MigrateAsync();

        Assert.Equal(6, report.PreviousSchemaVersion);
        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Max(m => m.Version), report.CurrentSchemaVersion);

        // 007, 008, 009, 010, 011 and 012 are the pending ones, in order, and none of them rewrites anything
        // another created.
        Assert.Equal(
            new[]
            {
                "RunScopedArtifactIdentity",
                "WorkflowReviewExecutionProvenance",
                "WorkflowReviewObservedRouteAuthority",
                "ActivityEventJournal",
                "ActivityEventFullTextSearch",
                "ReviewerNativeRouteIdentity",
                "GatewayApiKeyPurpose",
                "HealthFailureHistory",
                "ReviewerInsertObservationAuthority",
                "ProviderHeaders", "WorkflowReviewTerminalRetries", "ProviderSecretDeletionQueue", "ModelRouteHealthIdentity", "HealthAuthenticationFanout", "ArtifactExecutionAssociation", "ArtifactExecutionChronology", "workflow_review_responses", "workflow_material_policy", "workflow_adaptation_transport", "adaptation_runtime_ownership", "account_credential_ownership", "opencode_dispatch_decision", "model_capability_invalidation", "model_capability_context", "project_data_policy_audit"
            },
            report.AppliedMigrations.Select(migration => migration.Name));

        await using var upgraded = await factory.OpenConnectionAsync();

        // Everything that existed before the upgrade is still there, byte for byte, and nothing was refused
        // by the migration itself - including the row a new insert of the same shape would not survive.
        Assert.Equal(
            $"   |run-1|stage-1|DocumentBundle|{hash}",
            await ExecuteScalarAsync(
                upgraded,
                "SELECT Id || '|' || WorkflowRunId || '|' || StageId || '|' || Kind || '|' || HashSha256 "
                    + "FROM Artifacts WHERE WorkflowRunId = 'run-1' AND StageId = 'stage-1' "
                    + "AND HashSha256 = $hash;",
                ("$hash", hash)));
        Assert.Equal(
            otherHash,
            await ExecuteScalarAsync(
                upgraded,
                "SELECT HashSha256 FROM Artifacts WHERE Id = 'artifact-ok';",
                ("$id", "artifact-ok")));
        Assert.Equal(
            "  ",
            await ExecuteScalarAsync(
                upgraded,
                "SELECT Id FROM Artifacts WHERE ExecutionId = 'execution';",
                ("$id", "execution")));

        // A new run-scoped row of this stage still has to be named.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            upgraded,
            """
            INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
            VALUES ($id, 'run-1', 'stage-1', 'DocumentBundle', $blob, $blob, 12, 'PrivateSource', '2026-09-22T00:00:00Z');
            """,
            ("$id", string.Empty),
            ("$blob", thirdHash)));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            upgraded,
            """
            INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
            VALUES ($id, 'run-1', 'stage-1', 'DocumentBundle', $blob, $blob, 12, 'PrivateSource', '2026-09-22T00:00:00Z');
            """,
            ("$id", "\t "),
            ("$blob", thirdHash)));

        // A named run-scoped row of this stage is accepted, and so is one of a different stage: 007 is about
        // the row's own identity and changes nothing about which stage a row belongs to.
        await ExecuteAsync(
            upgraded,
            """
            INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
            VALUES ('artifact-after', 'run-1', 'stage-1', 'DocumentBundle', $blob, $blob, 12, 'PrivateSource', '2026-09-22T00:00:00Z');
            """,
            ("$blob", thirdHash));

        await ExecuteAsync(
            upgraded,
            """
            INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
            VALUES ('artifact-other-stage', 'run-1', 'stage-2', 'DocumentBundle', $blob, $blob, 12, 'PrivateSource', '2026-09-22T00:00:00Z');
            """,
            ("$blob", otherHash));

        // An execution-only row is untouched by the guard, whatever its identifier looks like.
        await ExecuteAsync(
            upgraded,
            """
            INSERT INTO Artifacts (Id, ExecutionId, Kind, DataClassification, CreatedAtUtc)
            VALUES ($id, 'execution', 'Diff', 'PrivateSource', '2026-09-22T00:00:00Z');
            """,
            ("$id", "\t"));

        Assert.Equal(
            2L,
            Convert.ToInt64(
                await ExecuteScalarAsync(upgraded, "SELECT COUNT(*) FROM Artifacts WHERE ExecutionId = 'execution';"),
                CultureInfo.InvariantCulture));

        // The stored row a new insert of the same shape cannot have is still removable, so an operator can
        // clear it deliberately; what it is not is something the read path may route around.
        await ExecuteAsync(upgraded, "DELETE FROM Artifacts WHERE Id = '   ';");

        Assert.Equal(
            3L,
            Convert.ToInt64(
                await ExecuteScalarAsync(upgraded, "SELECT COUNT(*) FROM Artifacts WHERE WorkflowRunId = 'run-1';"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SecretReferenceMetadataMigration_BackfillsCanonicalReferencesAndSkipsLegacyValues()
    {
        var factory = CreateFactory();
        var migrations = DatabaseMigrator.LoadEmbeddedMigrations();
        await new DatabaseMigrator(factory, migrations.Where(m => m.Version < 3).ToArray()).MigrateAsync();

        // A value the migration must accept, values it must skip without aborting, and values that
        // pass the loose SQL guard but not the canonical URN contract of SecretReference.IsValid. The
        // last-character and one-character cases are the ones a partial walk would accept: an uppercase
        // single character, and a valid identifier whose final character is invalid.
        const string canonicalProfile = "urn:llmworkgui:secret:alpha-key";
        const string canonicalShared = "urn:llmworkgui:secret:shared-key";
        const string canonicalSingleCharacter = "urn:llmworkgui:secret:a";
        const string legacyNonCanonical = "urn:llmworkgui:secret:Legacy.Key";
        const string legacyUppercaseSingleCharacter = "urn:llmworkgui:secret:A";
        const string legacyTrailingDot = "urn:llmworkgui:secret:alpha.";
        const string legacyEmptyIdentifier = "urn:llmworkgui:secret:";

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = $"""
                INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, ApiKeySecretReference, CreatedAtUtc, UpdatedAtUtc)
                VALUES
                    ('profile-alpha', 'Alpha', 'OpenCode', 'PrivateSource', '{canonicalProfile}', '{Timestamp}', '{Timestamp}'),
                    ('profile-shared', 'Shared', 'OpenCode', 'PrivateSource', '{canonicalShared}', '{Timestamp}', '{Timestamp}'),
                    ('profile-legacy', 'Legacy', 'OpenCode', 'PrivateSource', '{legacyNonCanonical}', '{Timestamp}', '{Timestamp}'),
                    ('profile-uppercase-single', 'Uppercase single character', 'OpenCode', 'PrivateSource', '{legacyUppercaseSingleCharacter}', '{Timestamp}', '{Timestamp}'),
                    ('profile-trailing-dot', 'Trailing dot', 'OpenCode', 'PrivateSource', '{legacyTrailingDot}', '{Timestamp}', '{Timestamp}'),
                    ('profile-empty-id', 'Empty identifier', 'OpenCode', 'PrivateSource', '{legacyEmptyIdentifier}', '{Timestamp}', '{Timestamp}'),
                    ('profile-keyless', 'Keyless', 'OpenCode', 'PrivateSource', NULL, '{Timestamp}', '{Timestamp}');

                INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, SecretReference, CreatedAtUtc, UpdatedAtUtc)
                VALUES
                    ('account-shared', 'profile-shared', 'Shares the profile URN', 'Valid', 'Healthy', '{canonicalShared}', '{Timestamp}', '{Timestamp}'),
                    ('account-own', 'profile-alpha', 'Own URN', 'Valid', 'Healthy', 'urn:llmworkgui:secret:account-key', '{Timestamp}', '{Timestamp}'),
                    ('account-single-char', 'profile-alpha', 'Single character URN', 'Valid', 'Healthy', '{canonicalSingleCharacter}', '{Timestamp}', '{Timestamp}');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var report = await new DatabaseMigrator(factory).MigrateAsync();

        Assert.Equal(2, report.PreviousSchemaVersion);
        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Max(m => m.Version), report.CurrentSchemaVersion);

        await using var upgraded = await factory.OpenConnectionAsync();

        // Only the canonical references are registered; a value the application would reject gets no
        // row and is reported as Missing when it is resolved.
        Assert.Equal(
            4L,
            Convert.ToInt64(
                await ExecuteScalarAsync(upgraded, "SELECT COUNT(*) FROM SecretReferences;"),
                CultureInfo.InvariantCulture));

        foreach (var skipped in new[]
                 {
                     legacyNonCanonical,
                     legacyEmptyIdentifier,
                     legacyUppercaseSingleCharacter,
                     legacyTrailingDot
                 })
        {
            Assert.Null(await ExecuteScalarAsync(
                upgraded,
                "SELECT Reference FROM SecretReferences WHERE Reference = $reference;",
                ("$reference", skipped)));

            // A skipped reference must not get an owner binding either, or a cleanup would try to act
            // on a URN the application itself would reject.
            Assert.Empty(await ReadOwnerKindsAsync(upgraded, skipped));
        }

        // A single-character identifier is the shortest form a full character walk has to accept.
        Assert.Equal(
            "Active",
            await ExecuteScalarAsync(
                upgraded,
                "SELECT State FROM SecretReferences WHERE Reference = $reference;",
                ("$reference", canonicalSingleCharacter)));

        Assert.Equal(
            "Account",
            Assert.Single(await ReadOwnerKindsAsync(upgraded, canonicalSingleCharacter)));

        Assert.Equal(
            "Active",
            await ExecuteScalarAsync(
                upgraded,
                "SELECT State FROM SecretReferences WHERE Reference = $reference;",
                ("$reference", canonicalProfile)));

        Assert.Equal(
            "Unspecified",
            await ExecuteScalarAsync(
                upgraded,
                "SELECT Kind FROM SecretReferences WHERE Reference = $reference;",
                ("$reference", canonicalProfile)));

        // The creation time and rotation history of a pre-existing reference are unknown, so they are
        // not invented by the migration.
        Assert.Null(await ExecuteNullableScalarAsync(
            upgraded,
            "SELECT LastRotatedAtUtc FROM SecretReferences WHERE Reference = $reference;",
            ("$reference", canonicalProfile)));

        // One URN shared by a profile and an account stays a single reference with two owners: the
        // schema does not impose a single-owner invariant.
        Assert.Equal(
            1L,
            Convert.ToInt64(
                await ExecuteScalarAsync(
                    upgraded,
                    "SELECT COUNT(*) FROM SecretReferences WHERE Reference = $reference;",
                    ("$reference", canonicalShared)),
                CultureInfo.InvariantCulture));

        var sharedOwners = await ReadOwnerKindsAsync(upgraded, canonicalShared);

        Assert.Equal(2, sharedOwners.Count);
        Assert.Contains("ProviderProfile", sharedOwners);
        Assert.Contains("Account", sharedOwners);

        Assert.Equal(
            5L,
            Convert.ToInt64(
                await ExecuteScalarAsync(upgraded, "SELECT COUNT(*) FROM SecretReferenceOwners;"),
                CultureInfo.InvariantCulture));

        // The staging table of the backfill must not survive the migration.
        var tables = await ReadTableNamesAsync(upgraded);

        Assert.DoesNotContain("Migration003CanonicalReferences", tables);
    }

    [Fact]
    public async Task SecretReferences_RejectNonUrnValues()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SecretReferences (Reference, Kind, State, CreatedAtUtc)
            VALUES ('sk-plaintext-api-key', 'ProviderApiKey', 'Active', '2026-09-22T00:00:00Z');
            """;

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task SecretReferenceOwners_RejectReferenceWithoutMetadata()
    {
        var factory = CreateFactory();
        await new DatabaseMigrator(factory).MigrateAsync();

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO SecretReferenceOwners (Reference, OwnerKind, OwnerId)
            VALUES ('urn:llmworkgui:secret:unregistered', 'ProviderProfile', 'profile-1');
            """;

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    private static readonly string[] AddedAfterInitialSchema =
    [
        "ProjectDataPolicyChanges",
        "WorkflowMaterialPolicies", "WorkflowMaterialPolicyChanges", "WorkflowAdaptationPolicyChecks",
        "WorkflowAdaptationPromptAdmissions", "WorkflowAdaptationNativeBindings", "WorkflowAdaptationTransportChecks",
        "OpenCodeAdaptationAccountMappings", "WorkflowAdaptationRuntimeOwners", "WorkflowAdaptationRuntimeChecks",
        "WorkflowReviewResponses",
        "HealthAuthenticationFanout",
        "HealthAuthenticationFanoutAccounts",
        "ApprovalRuleAudit",
        "SecretReferences",
        "SecretReferenceOwners",
        "PendingSecretDeletions",
        "ProviderProfileRevisions",
        "WorkflowTemplateVersions",
        "WorkflowTemplateAssignments",
        "WorkflowReviewExecutions",
        "ActivityEvents",
        // 011 creates the Activity Center's full-text index. It is a virtual table, so the schema is
        // declared as CREATE VIRTUAL TABLE rather than CREATE TABLE, and the five shadow tables SQLite
        // creates to back it are named here rather than being treated as declared application tables.
        "ActivityEventsSearch",
        "ActivityEventsSearch_data",
        "ActivityEventsSearch_idx",
        "ActivityEventsSearch_docsize",
        "ActivityEventsSearch_config",
        "ActivityEventsSearch_content"
    ];

    private SqliteConnectionFactory CreateFactory()
    {
        return new SqliteConnectionFactory(_directory.GetPath("llmworkgui.db"));
    }

    private static async Task<int> InsertAccountAsync(
        SqliteConnection connection,
        string accountId,
        string providerProfileId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO Accounts
                (Id, ProviderProfileId, DisplayName, AuthState, Health, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ('{accountId}', '{providerProfileId}', 'Demo account', 'Unknown', 'Healthy', '{Timestamp}', '{Timestamp}');
            """;

        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadTableNamesAsync(SqliteConnection connection)
    {
        var tableNames = new List<string>();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            tableNames.Add(reader.GetString(0));
        }

        return tableNames;
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string sql,
        (string Name, string Value) parameter)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);

        return await command.ExecuteScalarAsync();
    }

    private static async Task<string?> ExecuteNullableScalarAsync(
        SqliteConnection connection,
        string sql,
        (string Name, string Value) parameter)
    {
        var value = await ExecuteScalarAsync(connection, sql, parameter);

        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<List<string>> ReadOwnerKindsAsync(SqliteConnection connection, string reference)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OwnerKind FROM SecretReferenceOwners WHERE Reference = $reference ORDER BY OwnerKind;";
        command.Parameters.AddWithValue("$reference", reference);

        var ownerKinds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ownerKinds.Add(reader.GetString(0));
        }

        return ownerKinds;
    }
}
