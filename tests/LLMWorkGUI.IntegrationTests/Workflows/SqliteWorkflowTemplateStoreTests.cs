using System.Globalization;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// Phase 10E: the durable workflow template store. Every test runs against a really migrated SQLite
/// database and never against the in-memory store, so the migration, the immutability guarantee, the
/// assignment pointer, project isolation and restart persistence are proven by the database itself.
/// </summary>
public sealed partial class SqliteWorkflowTemplateStoreTests : IDisposable
{
    private const string Timestamp = "2026-09-22T00:00:00Z";

    private static readonly DateTimeOffset Now = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDirectory _directory = new();

    public void Dispose()
    {
        TestSqlitePool.Clear(CreateFactory());
        _directory.Dispose();
    }

    [Fact]
    public async Task FreshDatabase_AppliesMigration4AndStoresATemplateVersion()
    {
        var factory = await CreateMigratedFactoryAsync();

        var report = await new DatabaseMigrator(factory).MigrateAsync();

        Assert.True(report.WasUpToDate);
        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Max(m => m.Version), report.CurrentSchemaVersion);

        await using (var connection = await factory.OpenConnectionAsync())
        {
            var tables = await ReadTableNamesAsync(connection);

            Assert.Contains("WorkflowTemplateVersions", tables);
            Assert.Contains("WorkflowTemplateAssignments", tables);
        }

        var store = CreateStore(factory);

        Assert.Empty(await store.ListAsync());

        await store.SaveAsync(CreateTemplate("custom-workflow", 1));

        var stored = await store.GetAsync("custom-workflow", 1);

        Assert.NotNull(stored);
        Assert.Equal("custom-workflow", stored!.TemplateId);
        Assert.Equal(1, stored.Version);
        Assert.False(stored.IsBuiltIn);
    }

    [Fact]
    public async Task UpgradeFromSchema3_AddsTheTemplateTablesAndLeavesEveryImportedRowUntouched()
    {
        var factory = CreateFactory();
        var migrations = DatabaseMigrator.LoadEmbeddedMigrations();

        var before = await new DatabaseMigrator(factory, migrations.Where(m => m.Version < 4).ToArray())
            .MigrateAsync();

        Assert.Equal(3, before.CurrentSchemaVersion);

        var importedBytes = "imported-workflow-zip"u8.ToArray();
        var importedHash = WorkflowBlobStore.ComputeBlobId(importedBytes);
        var blobStore = new WorkflowBlobStore(_directory.Root);

        await using (var content = new MemoryStream(importedBytes))
        {
            var blob = await blobStore.SaveBlobAsync(content);

            Assert.Equal(importedHash, blob.BlobId);
        }

        await SeedImportedWorkflowAsync(factory, importedHash);

        var blobPath = blobStore.GetBlobPath(importedHash);
        var blobHashBefore = await ComputeFileHashAsync(blobPath);

        await using (var connection = await factory.OpenConnectionAsync())
        {
            Assert.DoesNotContain("WorkflowTemplateVersions", await ReadTableNamesAsync(connection));
        }

        var report = await new DatabaseMigrator(factory).MigrateAsync();

        Assert.Equal(3, report.PreviousSchemaVersion);
        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Max(m => m.Version), report.CurrentSchemaVersion);
        Assert.Equal(
            new[]
            {
                "WorkflowTemplateStore",
                "WorkflowRunTemplatePinning",
                "WorkflowRunArtifactGate",
                "RunScopedArtifactIdentity",
                "WorkflowReviewExecutionProvenance",
            "WorkflowReviewObservedRouteAuthority",
            "ActivityEventJournal",
            "ActivityEventFullTextSearch",
            "ReviewerNativeRouteIdentity",
            "GatewayApiKeyPurpose",
            "HealthFailureHistory",
            "ReviewerInsertObservationAuthority", "ProviderHeaders", "WorkflowReviewTerminalRetries", "ProviderSecretDeletionQueue", "ModelRouteHealthIdentity", "HealthAuthenticationFanout", "ArtifactExecutionAssociation", "ArtifactExecutionChronology", "workflow_review_responses", "workflow_material_policy", "workflow_adaptation_transport", "adaptation_runtime_ownership", "account_credential_ownership", "opencode_dispatch_decision", "model_capability_invalidation", "model_capability_context", "project_data_policy_audit"
        },
            report.AppliedMigrations.Select(migration => migration.Name));

        await using (var upgraded = await factory.OpenConnectionAsync())
        {
            var tables = await ReadTableNamesAsync(upgraded);

            Assert.Contains("WorkflowTemplateVersions", tables);
            Assert.Contains("WorkflowTemplateAssignments", tables);

            // The upgrade is additive: every imported row keeps its identity and its content, and the
            // new tables start empty instead of inheriting a fabricated package, version or run.
            Assert.Equal(
                $"package-imported|imported.zip|{importedHash}",
                await ReadSingleAsync(
                    upgraded,
                    "SELECT Id || '|' || Name || '|' || OriginalBlobId FROM WorkflowPackages;"));

            Assert.Equal(
                $"version-imported-1|package-imported|{importedHash}",
                await ReadSingleAsync(
                    upgraded,
                    "SELECT Id || '|' || WorkflowPackageId || '|' || OriginalHash FROM WorkflowVersions;"));

            Assert.Equal(
                $"run-imported-1|project-imported|version-imported-1|Running|"
                    + "{\"stage\":\"task-specification\"}",
                await ReadSingleAsync(
                    upgraded,
                    "SELECT Id || '|' || ProjectId || '|' || WorkflowVersionId || '|' || State || '|' "
                    + "|| EvidenceRedactedJson FROM WorkflowRuns;"));

            // A run that existed before pinning existed stays a legacy run: all four new columns are NULL
            // and the row itself is otherwise untouched.
            Assert.Equal(
                "0|0|0|0",
                await ReadSingleAsync(
                    upgraded,
                    "SELECT COUNT(TemplateId) || '|' || COUNT(TemplateVersion) || '|' "
                    + "|| COUNT(TemplateGraphSnapshotJson) || '|' || COUNT(TemplateSchemeSnapshotJson) "
                    + "FROM WorkflowRuns;"));

            Assert.Equal("0", await ReadSingleAsync(upgraded, "SELECT COUNT(*) FROM WorkflowTemplateVersions;"));
            Assert.Equal("0", await ReadSingleAsync(upgraded, "SELECT COUNT(*) FROM WorkflowTemplateAssignments;"));
        }

        // Template work on the upgraded database leaves the imported ZIP, its rows and its run alone.
        var store = CreateStore(factory);

        await store.SaveAsync(CreateTemplate("custom-workflow", 1));
        await store.SaveAssignmentAsync(
            new WorkflowTemplateAssignment("assignment-1", "project-1", "custom-workflow", 1, Now));

        await using (var connection = await factory.OpenConnectionAsync())
        {
            Assert.Equal("1", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowPackages;"));
            Assert.Equal("1", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowVersions;"));
            Assert.Equal("1", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowRuns;"));
            Assert.Equal(
                $"run-imported-1|project-imported|version-imported-1|Running|"
                    + "{\"stage\":\"task-specification\"}",
                await ReadSingleAsync(
                    connection,
                    "SELECT Id || '|' || ProjectId || '|' || WorkflowVersionId || '|' || State || '|' "
                    + "|| EvidenceRedactedJson FROM WorkflowRuns;"));
        }

        Assert.Equal(blobHashBefore, await ComputeFileHashAsync(blobPath));
    }

    [Fact]
    public async Task SaveAsync_RefusesASecondSaveOfTheSameVersionAndTheDatabaseRejectsMutation()
    {
        var factory = await CreateMigratedFactoryAsync();
        var store = CreateStore(factory);

        var template = CreateTemplate("custom-workflow", 1);
        await store.SaveAsync(template);

        var rewritten = new WorkflowTemplateDefinition(
            "custom-workflow",
            1,
            "Rewritten name",
            "Rewritten description",
            CreateGraph("custom-workflow", "Rewritten node"),
            template.RoleBindings,
            template.RequiredDocumentTemplates,
            isBuiltIn: false,
            createdAtUtc: Now.AddDays(1));

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.SaveAsync(rewritten));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);

        var stored = await store.GetAsync("custom-workflow", 1);

        Assert.NotNull(stored);
        Assert.Equal("Custom workflow v1", stored!.DisplayName);
        Assert.Equal("Custom description", stored.Description);
        Assert.Equal(template.Graph.EntryNodeId, stored.Graph.EntryNodeId);
        Assert.Equal(template.CreatedAtUtc, stored.CreatedAtUtc);

        // The immutability is the database's and not only the store's: a direct UPDATE or a DELETE of a
        // saved version is aborted, so no other code path can rewrite the graph a run depends on.
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE WorkflowTemplateVersions SET DisplayName = 'Rewritten name'
                WHERE TemplateId = 'custom-workflow' AND Version = 1;
                """;
            await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());

            await using var delete = connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM WorkflowTemplateVersions
                WHERE TemplateId = 'custom-workflow' AND Version = 1;
                """;
            await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
        }

        Assert.Equal("Custom workflow v1", (await store.GetAsync("custom-workflow", 1))!.DisplayName);
    }

    [Fact]
    public async Task SaveAsync_RefusesAGraphThatIsNotAValidWorkflowGraph()
    {
        var factory = await CreateMigratedFactoryAsync();
        var store = CreateStore(factory);

        // The domain constructor accepts the graph, the domain validator does not: the store refuses it
        // with a named error instead of writing a version that could never be executed.
        var invalid = new WorkflowTemplateDefinition(
            "broken-workflow",
            1,
            "Broken workflow",
            "The entry node is not declared",
            new WorkflowGraph(
                "missing-entry",
                new[]
                {
                    new WorkflowNodeDefinition(
                        "only",
                        WorkflowNodeKind.TerminalOutcome,
                        "Only",
                        "role-reviewer",
                        primaryRouteId: WorkflowStudioService.DefaultPrimaryRouteId)
                }),
            new[] { new RoleBindingDefinition("role-reviewer") },
            new[] { DocumentTemplateKind.ProblemStatement },
            isBuiltIn: false,
            createdAtUtc: Now);

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.SaveAsync(invalid));

        Assert.Contains("is not a valid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("missing-entry", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Database_RejectsAVersionWhoseStoredGraphIsNotAGraph()
    {
        var factory = await CreateMigratedFactoryAsync();

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WorkflowTemplateVersions (
                TemplateId, Version, DisplayName, Description, IsBuiltIn,
                GraphJson, RoleBindingsJson, RequiredDocumentTemplatesJson, CreatedAtUtc
            )
            VALUES (
                'broken-workflow', 1, 'Broken', 'Broken', 0,
                '{"entryNodeId":"","nodes":[]}', '[]', '[]', '2026-09-28T09:00:00.0000000+00:00'
            );
            """;

        // A row that lost its entry node or all of its nodes is refused by the database, so a stored
        // version is always a graph document and never an empty placeholder.
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ConcurrentSaves_OfTheSameVersion_StoreExactlyOneVersion()
    {
        var factory = await CreateMigratedFactoryAsync();

        // Two independent store instances over the same database, so the seed and the version insert of
        // both of them really race instead of being serialized by one in-process gate.
        var first = CreateStore(factory, seed: true);
        var second = CreateStore(factory, seed: true);

        var attempts = Enumerable
            .Range(0, 8)
            .Select(index => Task.Run(async () =>
            {
                var store = index % 2 == 0 ? first : second;

                try
                {
                    await store.SaveAsync(CreateTemplate("custom-workflow", 1));

                    return false;
                }
                catch (WorkflowValidationException)
                {
                    return true;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(isRefused => !isRefused));
        Assert.Equal(7, results.Count(isRefused => isRefused));
        await using var connection = await factory.OpenConnectionAsync();

        // Exactly one row for the saved version and one for the shipped seed: no duplicate of either.
        Assert.Equal(
            "1",
            await ReadSingleAsync(
                connection,
                "SELECT COUNT(*) FROM WorkflowTemplateVersions WHERE TemplateId = 'custom-workflow';"));

        Assert.Equal(
            "2",
            await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowTemplateVersions;"));

        Assert.Equal(2, (await first.ListAsync()).Count);
    }

    [Fact]
    public async Task Assignment_IsScopedToItsProjectAndSurvivesAReopen()
    {
        var factory = await CreateMigratedFactoryAsync();
        var store = CreateStore(factory);

        await store.SaveAsync(CreateTemplate("alpha-workflow", 1));
        await store.SaveAsync(CreateTemplate("beta-workflow", 1));

        await store.SaveAssignmentAsync(
            new WorkflowTemplateAssignment("assignment-alpha", "project-alpha", "alpha-workflow", 1, Now));

        await store.SaveAssignmentAsync(
            new WorkflowTemplateAssignment("assignment-beta", "project-beta", "beta-workflow", 1, Now));

        var alpha = await store.GetAssignmentAsync("project-alpha");

        Assert.NotNull(alpha);
        Assert.Equal("project-alpha", alpha!.ProjectId);
        Assert.Equal("alpha-workflow", alpha.TemplateId);
        Assert.Equal(1, alpha.TemplateVersion);
        Assert.Equal("assignment-alpha", alpha.AssignmentId);
        Assert.Equal(Now, alpha.AssignedAtUtc);

        var beta = await store.GetAssignmentAsync("project-beta");

        Assert.NotNull(beta);
        Assert.Equal("beta-workflow", beta!.TemplateId);
        Assert.Equal("assignment-beta", beta.AssignmentId);

        // Isolation: the pointer of one project is never returned for another project, and a project
        // without an assignment has none.
        Assert.Null(await store.GetAssignmentAsync("project-gamma"));
        Assert.Null(await store.GetAssignmentAsync("alpha-workflow"));

        // Restart: a second store instance over the same database file sees exactly the same pointers.
        TestSqlitePool.Clear(CreateFactory());

        var reopened = CreateStore(factory);
        var reopenedAlpha = await reopened.GetAssignmentAsync("project-alpha");

        Assert.NotNull(reopenedAlpha);
        Assert.Equal("assignment-alpha", reopenedAlpha!.AssignmentId);
        Assert.Equal("alpha-workflow", reopenedAlpha.TemplateId);
        Assert.Equal(1, reopenedAlpha.TemplateVersion);
        Assert.Equal(Now, reopenedAlpha.AssignedAtUtc);

        var reopenedBeta = await reopened.GetAssignmentAsync("project-beta");

        Assert.NotNull(reopenedBeta);
        Assert.Equal("beta-workflow", reopenedBeta!.TemplateId);
        Assert.Equal("assignment-beta", reopenedBeta.AssignmentId);
        Assert.Null(await reopened.GetAssignmentAsync("project-gamma"));

        await using var connection = await factory.OpenConnectionAsync();

        Assert.Equal("2", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowTemplateAssignments;"));
    }

    [Fact]
    public async Task Assignment_MovesOnlyToAVersionThatExists()
    {
        var factory = await CreateMigratedFactoryAsync();
        var store = CreateStore(factory);

        await store.SaveAsync(CreateTemplate("alpha-workflow", 1));
        await store.SaveAsync(CreateTemplate("alpha-workflow", 2));

        await store.SaveAssignmentAsync(
            new WorkflowTemplateAssignment("assignment-1", "project-alpha", "alpha-workflow", 1, Now));

        var missingVersion = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.SaveAssignmentAsync(new WorkflowTemplateAssignment(
                "assignment-2",
                "project-alpha",
                "alpha-workflow",
                3,
                Now.AddMinutes(1))));

        Assert.Contains("does not exist", missingVersion.Message, StringComparison.Ordinal);
        Assert.Equal(1, (await store.GetAssignmentAsync("project-alpha"))!.TemplateVersion);

        var missingTemplate = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.SaveAssignmentAsync(new WorkflowTemplateAssignment(
                "assignment-3",
                "project-alpha",
                "unknown-workflow",
                1,
                Now.AddMinutes(2))));

        Assert.Contains("does not exist", missingTemplate.Message, StringComparison.Ordinal);
        Assert.Equal(1, (await store.GetAssignmentAsync("project-alpha"))!.TemplateVersion);

        // The move to an existing version succeeds and replaces the pointer of that one project only.
        await store.SaveAssignmentAsync(
            new WorkflowTemplateAssignment(
                "assignment-4",
                "project-alpha",
                "alpha-workflow",
                2,
                Now.AddMinutes(3)));

        var moved = await store.GetAssignmentAsync("project-alpha");

        Assert.NotNull(moved);
        Assert.Equal(2, moved!.TemplateVersion);
        Assert.Equal("assignment-4", moved.AssignmentId);
        Assert.Equal(Now.AddMinutes(3), moved.AssignedAtUtc);

        await using var connection = await factory.OpenConnectionAsync();

        Assert.Equal("1", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowTemplateAssignments;"));

        // The versions themselves are untouched by the move: an assignment never edits a template.
        Assert.Equal(
            "Custom workflow v1|Custom workflow v2",
            await ReadSingleAsync(
                connection,
                "SELECT (SELECT DisplayName FROM WorkflowTemplateVersions "
                    + "WHERE TemplateId = 'alpha-workflow' AND Version = 1) || '|' || "
                    + "(SELECT DisplayName FROM WorkflowTemplateVersions "
                    + "WHERE TemplateId = 'alpha-workflow' AND Version = 2);"));
    }

    [Fact]
    public async Task Versions_AreIsolatedAndListAndLatestAreDeterministicallyOrdered()
    {
        var factory = await CreateMigratedFactoryAsync();
        var store = CreateStore(factory, seed: true);

        await store.SaveAsync(CreateTemplate("beta-workflow", 1));
        await store.SaveAsync(CreateTemplate("alpha-workflow", 2));
        await store.SaveAsync(CreateTemplate("alpha-workflow", 1));

        var latest = await store.GetLatestAsync("alpha-workflow");

        Assert.NotNull(latest);
        Assert.Equal(2, latest!.Version);

        // The shipped seed is listed first, then template id in ordinal order, then version: the same
        // order the in-memory store produces for the same set.
        var templates = await store.ListAsync();

        Assert.Equal(
            new[]
            {
                $"{WorkflowStudioService.StandardTemplateId}@1",
                "alpha-workflow@1",
                "alpha-workflow@2",
                "beta-workflow@1"
            },
            templates
                .Select(template => $"{template.TemplateId}@{template.Version}")
                .ToArray());

        Assert.True(templates[0].IsBuiltIn);
        Assert.All(templates.Skip(1), template => Assert.False(template.IsBuiltIn));

        Assert.Null(await store.GetLatestAsync("gamma-workflow"));
        Assert.Null(await store.GetAsync("alpha-workflow", 3));

        // Saving a later version never changes the graph of an earlier one.
        var first = await store.GetAsync("alpha-workflow", 1);
        var second = await store.GetAsync("alpha-workflow", 2);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("Custom workflow v1 node", first!.Graph.Nodes[0].DisplayName);
        Assert.Equal("Custom workflow v2 node", second!.Graph.Nodes[0].DisplayName);
        Assert.Equal(Now, first.CreatedAtUtc);
        Assert.Equal(Now, second.CreatedAtUtc);
    }

    [Fact]
    public async Task Reads_RoundTripTheWholeStoredGraphAndItValidates()
    {
        var factory = await CreateMigratedFactoryAsync();
        var store = CreateStore(factory);

        var template = CreateRichTemplate("rich-workflow", 1);
        await store.SaveAsync(template);

        var roundTripped = await store.GetAsync("rich-workflow", 1);

        Assert.NotNull(roundTripped);

        var restored = roundTripped!;

        Assert.Equal(template.DisplayName, restored.DisplayName);
        Assert.Equal(template.Description, restored.Description);
        Assert.Equal(template.IsBuiltIn, restored.IsBuiltIn);
        Assert.Equal(template.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(template.Graph.EntryNodeId, restored.Graph.EntryNodeId);
        Assert.Equal(template.RequiredDocumentTemplates, restored.RequiredDocumentTemplates);
        Assert.Equal(
            template.RoleBindings.Select(binding => binding.RoleId),
            restored.RoleBindings.Select(binding => binding.RoleId));

        var original = template.Graph.Nodes;
        var stored = restored.Graph.Nodes;

        Assert.Equal(original.Count, stored.Count);

        for (var index = 0; index < original.Count; index++)
        {
            AssertNodeEquals(original[index], stored[index]);
        }

        // The restored graph is a working graph and not a JSON document that only looks like one.
        restored.Graph.Validate();

        var binding = restored.RoleBindings[0];

        Assert.Equal("route-opencode", binding.PrimaryRouteId);
        Assert.Equal("route-fallback", Assert.Single(binding.FallbackRouteIds));
        Assert.Equal("cap-review", Assert.Single(binding.RequiredCapabilities));
        Assert.Equal("model-x", binding.ModelId);
        Assert.True(binding.AllowsRoute("route-fallback"));
        Assert.Empty(restored.RoleBindings[1].AllowedRouteIds);
    }

    [Fact]
    public async Task Reads_ReportAStoredGraphThatNoLongerValidatesInsteadOfHandingItToTheStudio()
    {
        var factory = await CreateMigratedFactoryAsync();
        var store = CreateStore(factory);

        await store.SaveAsync(CreateTemplate("alpha-workflow", 1));

        // A row whose graph stopped validating can only come from outside this store, because the
        // database refuses every mutation of a saved version. The triggers are dropped here to simulate
        // exactly that: a stored graph document of the right shape that cannot be executed.
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = """
                DROP TRIGGER TR_WorkflowTemplateVersions_NoUpdate;
                DROP TRIGGER TR_WorkflowTemplateVersions_NoDelete;
                """;
            await drop.ExecuteNonQueryAsync();

            await using var rewrite = connection.CreateCommand();
            rewrite.CommandText = """
                DELETE FROM WorkflowTemplateVersions WHERE TemplateId = 'alpha-workflow' AND Version = 1;

                INSERT INTO WorkflowTemplateVersions (
                    TemplateId, Version, DisplayName, Description, IsBuiltIn,
                    GraphJson, RoleBindingsJson, RequiredDocumentTemplatesJson, CreatedAtUtc
                )
                VALUES (
                    'alpha-workflow', 1, 'Custom workflow v1', 'Custom description', 0,
                    '{"entryNodeId":"start","nodes":[{"nodeId":"start","kind":"Prompt",'
                        || '"displayName":"Broken node","roleBinding":"role-coder",'
                        || '"requiredCapabilities":[],"primaryRouteId":"route-opencode",'
                        || '"fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,'
                        || '"successTargetNodeId":"missing-target","failureTargetNodeId":null,'
                        || '"conditionExpression":null,"artifactContract":null,"permissionIntent":null}]}',
                    '[{"roleId":"role-coder","primaryRouteId":"route-opencode","modelId":null,'
                        || '"fallbackRouteIds":[],"requiredCapabilities":[]}]',
                    '["ProblemStatement"]',
                    '2026-09-28T09:00:00.0000000+00:00'
                );
                """;
            await rewrite.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync("alpha-workflow", 1));

        Assert.Contains("is not a readable and valid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("alpha-workflow", exception.Message, StringComparison.Ordinal);
        Assert.Contains("missing-target", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StudioComposition_UsesTheSqliteStoreAndFabricatesNoImportedOrRunRow()
    {
        await using var provider = CreateProductProviderAsync();

        await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync();

        var store = provider.GetRequiredService<IWorkflowTemplateStore>();

        // The product composition resolves the durable store and not a private in-memory fallback.
        Assert.IsType<SqliteWorkflowTemplateStore>(store);
        Assert.Same(store, provider.GetRequiredService<IWorkflowTemplateStore>());

        var studio = provider.GetRequiredService<IWorkflowStudioService>();
        var runRepository = provider.GetRequiredService<IWorkflowRunRepository>();
        var packageRepository = provider.GetRequiredService<IWorkflowPackageRepository>();

        var clone = await studio.CloneTemplateAsync(
            WorkflowStudioService.StandardTemplateId,
            "product-clone",
            "Product clone");


        var versionTwo = await studio.CreateTemplateVersionAsync("product-clone", 2);

        var assignment = await studio.AssignTemplateToProjectAsync("product-project", "product-clone", 2);

        Assert.True(assignment.IsAssigned);
        Assert.Equal("product-clone", assignment.TemplateId);
        Assert.Equal(2, assignment.TemplateVersion);
        Assert.NotNull(assignment.AssignmentId);

        // The shipped template stays assignable through the same store, which is why the store seeds it.
        var builtInAssignment = await studio.AssignTemplateToProjectAsync(
            "product-built-in-project",
            WorkflowStudioService.StandardTemplateId,
            1);

        Assert.True(builtInAssignment.IsAssigned);
        Assert.Equal(1, builtInAssignment.TemplateVersion);

        var listed = await studio.ListTemplatesAsync();

        Assert.Contains(listed, template => template.TemplateId == "product-clone" && template.Version == 1);
        Assert.Contains(listed, template => template.TemplateId == "product-clone" && template.Version == 2);
        Assert.Single(
            listed,
            template => template.TemplateId == WorkflowStudioService.StandardTemplateId);

        // This slice stores templates only: no run is started or advanced, and no imported package,
        // imported version or run row is fabricated to satisfy a foreign key.
        Assert.Empty(await runRepository.GetByProjectIdAsync("product-project"));
        Assert.Empty(await packageRepository.ListAsync());

        await using var connection = await provider
            .GetRequiredService<ISqliteConnectionFactory>()
            .OpenConnectionAsync();

        Assert.Equal("0", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowPackages;"));
        Assert.Equal("0", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowVersions;"));
        Assert.Equal("0", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowRuns;"));
        Assert.Equal("2", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowTemplateAssignments;"));
        Assert.Equal("3", await ReadSingleAsync(connection, "SELECT COUNT(*) FROM WorkflowTemplateVersions;"));

        // A second composition over the same app data directory is the restart of the application: the
        // saved versions, both assignments and the shipped seed are all still there.
        TestSqlitePool.Clear(CreateFactory());

        await using var restarted = CreateProductProviderAsync();

        await restarted.GetRequiredService<DatabaseMigrator>().MigrateAsync();

        var restartedStore = restarted.GetRequiredService<IWorkflowTemplateStore>();

        Assert.IsType<SqliteWorkflowTemplateStore>(restartedStore);

        var restored = await restartedStore.GetAsync("product-clone", 2);

        Assert.NotNull(restored);
        Assert.Equal("Product clone", restored!.DisplayName);
        Assert.Equal(
            WorkflowStudioService.CreateStandardTemplate().Graph.Nodes.Count,
            restored.Graph.Nodes.Count);

        var restoredProject = await restartedStore.GetAssignmentAsync("product-project");
        var restoredBuiltIn = await restartedStore.GetAssignmentAsync("product-built-in-project");

        Assert.NotNull(restoredProject);
        Assert.Equal("product-clone", restoredProject!.TemplateId);
        Assert.Equal(2, restoredProject.TemplateVersion);
        Assert.Equal(assignment.AssignmentId, restoredProject.AssignmentId);

        Assert.NotNull(restoredBuiltIn);
        Assert.Equal(WorkflowStudioService.StandardTemplateId, restoredBuiltIn!.TemplateId);
        Assert.Equal(1, restoredBuiltIn.TemplateVersion);

        // The saved version is still immutable after the restart.
        await Assert.ThrowsAsync<WorkflowValidationException>(() => restartedStore.SaveAsync(clone));
    }

    [Fact]
    public async Task Reads_RoundTripTheDeclaredGatesOfEveryNodeThroughTheRealStore()
    {
        var factory = await CreateMigratedFactoryAsync();
        var store = CreateStore(factory);

        await store.SaveAsync(CreateGatedTemplate("gated-workflow", 1));
        await store.SaveAsync(WorkflowStudioService.CreateStandardTemplate());

        var gated = await store.GetAsync("gated-workflow", 1);

        Assert.NotNull(gated);

        foreach (var node in CreateGatedTemplate("gated-workflow", 1).Graph.Nodes)
        {
            var restored = gated!.Graph.GetRequiredNode(node.NodeId);

            Assert.NotNull(restored.GateMetadata);
            Assert.Equal(node.GateMetadata!.StageKind, restored.GateMetadata!.StageKind);
            Assert.Equal(node.GateMetadata.RequiredReviewerRoles, restored.GateMetadata.RequiredReviewerRoles);
            Assert.Equal(node.GateMetadata.RequiresUserApproval, restored.GateMetadata.RequiresUserApproval);
            Assert.Equal(node.GateMetadata.ArtifactRequirement, restored.GateMetadata.ArtifactRequirement);
        }

        // The shipped seed goes through the same insert, so the built-in standard template keeps the
        // reviewer, approval and artifact requirements of its scheme after a real round trip as well.
        var builtIn = await store.GetAsync(WorkflowStudioService.StandardTemplateId, 1);
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();

        Assert.NotNull(builtIn);

        foreach (var stage in scheme.Stages)
        {
            var gate = builtIn!.Graph.GetRequiredNode(stage.StageId).GateMetadata;

            Assert.NotNull(gate);
            Assert.Equal(stage.StageKind, gate!.StageKind);
            Assert.Equal(stage.RequiredReviewerRoles, gate.RequiredReviewerRoles);
            Assert.Equal(stage.RequiresUserApproval, gate.RequiresUserApproval);
            Assert.Equal(stage.ArtifactRequirement, gate.ArtifactRequirement);
        }

        builtIn.Graph.Validate();
    }

    [Fact]
    public async Task AStoredGraphWrittenBeforeGatesWerePreservedIsStillReadableAsDeclaringNoGates()
    {
        var factory = await CreateMigratedFactoryAsync();

        // The exact node document the previous writer produced, inserted straight into the table: the
        // opaque GraphJson column has no gate property in it at all.
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO WorkflowTemplateVersions (
                    TemplateId, Version, DisplayName, Description, IsBuiltIn,
                    GraphJson, RoleBindingsJson, RequiredDocumentTemplatesJson, CreatedAtUtc
                )
                VALUES (
                    'legacy-workflow', 1, 'Legacy', 'Written before gates', 0,
                    '{"entryNodeId":"legacy-first","nodes":[{"nodeId":"legacy-first","kind":"Prompt",'
                        || '"displayName":"Legacy node","roleBinding":"role-coder",'
                        || '"requiredCapabilities":[],"primaryRouteId":"route-opencode",'
                        || '"fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,'
                        || '"successTargetNodeId":"legacy-last","failureTargetNodeId":null,'
                        || '"conditionExpression":null,"artifactContract":null,"permissionIntent":null},'
                        || '{"nodeId":"legacy-last","kind":"TerminalOutcome","displayName":"Legacy outcome",'
                        || '"roleBinding":"role-reviewer","requiredCapabilities":[],'
                        || '"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,'
                        || '"retryBudget":0,"successTargetNodeId":null,"failureTargetNodeId":null,'
                        || '"conditionExpression":null,"artifactContract":null,"permissionIntent":null}]}',
                    '[]',
                    '[]',
                    '2026-09-28T09:00:00.0000000+00:00'
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = CreateStore(factory);
        var restored = await store.GetAsync("legacy-workflow", 1);

        Assert.NotNull(restored);
        Assert.Null(restored!.Graph.GetRequiredNode("legacy-first").GateMetadata);
        Assert.Null(restored.Graph.GetRequiredNode("legacy-last").GateMetadata);

        restored.Graph.Validate();

        // It is still a working template version, and the Studio can still assign a project to it.
        var assignment = new WorkflowTemplateAssignment(
            "assignment-legacy",
            "project-legacy",
            "legacy-workflow",
            1,
            Now);

        await store.SaveAssignmentAsync(assignment);

        Assert.Equal(
            assignment.AssignmentId,
            (await store.GetAssignmentAsync("project-legacy"))!.AssignmentId);
    }

    [Fact]
    public async Task AStoredGateThatCannotBeReadIsReportedInsteadOfBecomingAGateFreeNode()
    {
        var factory = await CreateMigratedFactoryAsync();
        var store = CreateStore(factory);

        await store.SaveAsync(CreateTemplate("alpha-workflow", 1));

        // The immutability triggers are dropped to reach a row the store itself refuses to write: a node
        // document whose gate names an unknown stage kind. Nothing may read that node as gate-free.
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = """
                DROP TRIGGER TR_WorkflowTemplateVersions_NoUpdate;
                DROP TRIGGER TR_WorkflowTemplateVersions_NoDelete;
                """;
            await drop.ExecuteNonQueryAsync();

            await using var rewrite = connection.CreateCommand();
            rewrite.CommandText = """
                DELETE FROM WorkflowTemplateVersions WHERE TemplateId = 'alpha-workflow' AND Version = 1;

                INSERT INTO WorkflowTemplateVersions (
                    TemplateId, Version, DisplayName, Description, IsBuiltIn,
                    GraphJson, RoleBindingsJson, RequiredDocumentTemplatesJson, CreatedAtUtc
                )
                VALUES (
                    'alpha-workflow', 1, 'Custom workflow v1', 'Custom description', 0,
                    '{"entryNodeId":"start","nodes":[{"nodeId":"start","kind":"Prompt",'
                        || '"displayName":"Gated node","roleBinding":"role-coder",'
                        || '"requiredCapabilities":[],"primaryRouteId":"route-opencode",'
                        || '"fallbackRouteIds":[],"timeoutTicks":null,"retryBudget":0,'
                        || '"successTargetNodeId":"finish","failureTargetNodeId":null,'
                        || '"conditionExpression":null,"artifactContract":null,"permissionIntent":null,'
                        || '"gateMetadata":{"stageKind":"SignOff","requiredReviewerRoles":["Reviewer"],'
                        || '"requiresUserApproval":true,"artifactRequirement":"ApprovedDocument"}},'
                        || '{"nodeId":"finish","kind":"TerminalOutcome","displayName":"Accept",'
                        || '"roleBinding":"role-coder","requiredCapabilities":[],'
                        || '"primaryRouteId":"route-opencode","fallbackRouteIds":[],"timeoutTicks":null,'
                        || '"retryBudget":0,"successTargetNodeId":null,"failureTargetNodeId":null,'
                        || '"conditionExpression":null,"artifactContract":null,"permissionIntent":null,'
                        || '"gateMetadata":null}]}',
                    '[]',
                    '[]',
                    '2026-09-28T09:00:00.0000000+00:00'
                );
                """;
            await rewrite.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.GetAsync("alpha-workflow", 1));

        Assert.Contains("is not a readable and valid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid-node-gate-metadata", exception.Message, StringComparison.Ordinal);
        Assert.Contains("alpha-workflow", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unknown stage kind 'SignOff'", exception.Message, StringComparison.Ordinal);
    }

    private static WorkflowTemplateDefinition CreateGatedTemplate(string templateId, int version)
    {
        var nodes = new[]
        {
            new WorkflowNodeDefinition(
                "gated-start",
                WorkflowNodeKind.Prompt,
                "Collect the task",
                "role-coder",
                primaryRouteId: WorkflowStudioService.DefaultPrimaryRouteId,
                successTargetNodeId: "gated-finish",
                gateMetadata: new WorkflowNodeGateMetadata(
                    WorkflowStageKind.DocumentReview,
                    new[] { "Reviewer", "Architect" },
                    requiresUserApproval: false,
                    artifactRequirement: "DocumentBundle")),
            new WorkflowNodeDefinition(
                "gated-finish",
                WorkflowNodeKind.TerminalOutcome,
                "Accept the result",
                "role-reviewer",
                primaryRouteId: WorkflowStudioService.DefaultPrimaryRouteId,
                gateMetadata: new WorkflowNodeGateMetadata(
                    WorkflowStageKind.Custom,
                    Array.Empty<string>(),
                    requiresUserApproval: false,
                    artifactRequirement: null))
        };

        return new WorkflowTemplateDefinition(
            templateId,
            version,
            "Gated workflow",
            "A template whose nodes declare stage gates",
            new WorkflowGraph("gated-start", nodes),
            new[] { new RoleBindingDefinition("role-coder", WorkflowStudioService.DefaultPrimaryRouteId) },
            new[] { DocumentTemplateKind.ReviewReport },
            isBuiltIn: false,
            createdAtUtc: Now);
    }

    private static void AssertNodeEquals(WorkflowNodeDefinition expected, WorkflowNodeDefinition actual)
    {
        Assert.Equal(expected.NodeId, actual.NodeId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.RoleBinding, actual.RoleBinding);
        Assert.Equal(expected.RequiredCapabilities, actual.RequiredCapabilities);
        Assert.Equal(expected.PrimaryRouteId, actual.PrimaryRouteId);
        Assert.Equal(expected.FallbackRouteIds, actual.FallbackRouteIds);
        Assert.Equal(expected.Timeout, actual.Timeout);
        Assert.Equal(expected.RetryBudget, actual.RetryBudget);
        Assert.Equal(expected.SuccessTargetNodeId, actual.SuccessTargetNodeId);
        Assert.Equal(expected.FailureTargetNodeId, actual.FailureTargetNodeId);
        Assert.Equal(expected.ConditionExpression, actual.ConditionExpression);
        Assert.Equal(expected.ArtifactContract, actual.ArtifactContract);
        Assert.Equal(expected.PermissionIntent, actual.PermissionIntent);
        Assert.Equal(expected.GateMetadata?.StageKind, actual.GateMetadata?.StageKind);
        Assert.Equal(
            expected.GateMetadata?.RequiredReviewerRoles,
            actual.GateMetadata?.RequiredReviewerRoles);
        Assert.Equal(expected.GateMetadata?.RequiresUserApproval, actual.GateMetadata?.RequiresUserApproval);
        Assert.Equal(expected.GateMetadata?.ArtifactRequirement, actual.GateMetadata?.ArtifactRequirement);
    }

    private static WorkflowTemplateDefinition CreateTemplate(string templateId, int version) =>
        new(
            templateId,
            version,
            $"Custom workflow v{version}",
            "Custom description",
            CreateGraph(templateId, $"Custom workflow v{version} node"),
            new[] { new RoleBindingDefinition("role-coder", WorkflowStudioService.DefaultPrimaryRouteId) },
            new[] { DocumentTemplateKind.ProblemStatement, DocumentTemplateKind.AcceptanceReport },
            isBuiltIn: false,
            createdAtUtc: Now);

    private static WorkflowTemplateDefinition CreateRichTemplate(string templateId, int version)
    {
        var nodes = new[]
        {
            new WorkflowNodeDefinition(
                "start",
                WorkflowNodeKind.Prompt,
                "Collect the task",
                "role-coder",
                requiredCapabilities: new[] { "cap-read" },
                primaryRouteId: "route-opencode",
                fallbackRouteIds: new[] { "route-fallback" },
                timeout: TimeSpan.FromSeconds(90),
                retryBudget: 0,
                successTargetNodeId: "check",
                artifactContract: "artifact-task",
                permissionIntent: "read"),
            new WorkflowNodeDefinition(
                "check",
                WorkflowNodeKind.Condition,
                "Decide whether the task is complete",
                "role-coder",
                primaryRouteId: "route-opencode",
                retryBudget: 0,
                successTargetNodeId: "finish",
                failureTargetNodeId: "retry",
                conditionExpression: "artifacts.complete == true"),
            new WorkflowNodeDefinition(
                "retry",
                WorkflowNodeKind.Retry,
                "Spend the retry budget before another attempt",
                "role-coder",
                primaryRouteId: "route-opencode",
                retryBudget: 2,
                successTargetNodeId: "start",
                failureTargetNodeId: "finish"),
            new WorkflowNodeDefinition(
                "finish",
                WorkflowNodeKind.TerminalOutcome,
                "Accept the result",
                "role-reviewer",
                primaryRouteId: "route-opencode",
                retryBudget: 0,
                successTargetNodeId: null,
                failureTargetNodeId: null)
        };

        return new WorkflowTemplateDefinition(
            templateId,
            version,
            "Rich workflow",
            "A template that exercises every stored field",
            new WorkflowGraph("start", nodes),
            new[]
            {
                new RoleBindingDefinition(
                    "role-coder",
                    "route-opencode",
                    new[] { "route-fallback" },
                    new[] { "cap-review" },
                    "model-x"),
                new RoleBindingDefinition("role-reviewer")
            },
            new[]
            {
                DocumentTemplateKind.ProblemStatement,
                DocumentTemplateKind.Architecture,
                DocumentTemplateKind.TechnicalSpecification,
                DocumentTemplateKind.Roadmap,
                DocumentTemplateKind.TaskPacket,
                DocumentTemplateKind.ReviewReport,
                DocumentTemplateKind.AcceptanceReport
            },
            isBuiltIn: false,
            createdAtUtc: Now);
    }

    private static WorkflowGraph CreateGraph(string templateId, string nodeDisplayName) =>
        new(
            $"{templateId}-first",
            new[]
            {
                new WorkflowNodeDefinition(
                    $"{templateId}-first",
                    WorkflowNodeKind.Prompt,
                    nodeDisplayName,
                    "role-coder",
                    primaryRouteId: WorkflowStudioService.DefaultPrimaryRouteId,
                    successTargetNodeId: $"{templateId}-last"),
                new WorkflowNodeDefinition(
                    $"{templateId}-last",
                    WorkflowNodeKind.TerminalOutcome,
                    $"{nodeDisplayName} outcome",
                    "role-reviewer",
                    primaryRouteId: WorkflowStudioService.DefaultPrimaryRouteId)
            });

    private SqliteConnectionFactory CreateFactory() =>
        new(_directory.GetPath("llmworkgui.db"));

    private async Task<SqliteConnectionFactory> CreateMigratedFactoryAsync()
    {
        var factory = CreateFactory();

        await new DatabaseMigrator(factory).MigrateAsync();

        return factory;
    }

    private static SqliteWorkflowTemplateStore CreateStore(SqliteConnectionFactory factory, bool seed = false) =>
        new(
            factory,
            graphValidator: new WorkflowGraphValidator(),
            seedVersions: seed ? new[] { WorkflowStudioService.CreateStandardTemplate() } : null);

    private ServiceProvider CreateProductProviderAsync()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure(_directory.Root);
        services.AddWorkflowServices();

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task SeedImportedWorkflowAsync(
        SqliteConnectionFactory factory,
        string importedHash)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                'project-imported', 'Imported project', 'C:\imported', 'PrivateSource',
                '{{Timestamp}}', '{{Timestamp}}'
            );

            INSERT INTO WorkflowPackages (
                Id, Name, OriginalHash, OriginalBlobId, SourceType, CreatedAtUtc, UpdatedAtUtc
            )
            VALUES (
                'package-imported', 'imported.zip', '{{importedHash}}', '{{importedHash}}', 'ImportedZip',
                '{{Timestamp}}', '{{Timestamp}}'
            );

            INSERT INTO WorkflowVersions (
                Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc
            )
            VALUES (
                'version-imported-1', 'package-imported', 1, '{{importedHash}}', '{{importedHash}}',
                'ImportedZip', '{{Timestamp}}'
            );

            INSERT INTO WorkflowRuns (
                Id, ProjectId, WorkflowPackageId, WorkflowVersionId, State, StartedAtUtc, EvidenceRedactedJson
            )
            VALUES (
                'run-imported-1', 'project-imported', 'package-imported', 'version-imported-1', 'Running',
                '{{Timestamp}}', '{"stage":"task-specification"}'
            );
            """;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadTableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

        var tableNames = new List<string>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            tableNames.Add(reader.GetString(0));
        }

        return tableNames;
    }

    private static async Task<string> ReadSingleAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<string> ComputeFileHashAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
