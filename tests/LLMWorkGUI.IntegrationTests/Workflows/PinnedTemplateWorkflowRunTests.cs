using System.Globalization;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// A run pinned to an assigned workflow template version, against a real migrated SQLite database and the
/// product-composed run service.
///
/// The two identities are kept apart throughout: the run still names the source workflow version it was
/// started from, and it additionally records the assigned template version together with the exact graph it
/// held and the execution scheme derived from it. Nothing about a run is allowed to depend on a template
/// that is edited or an assignment that moves later.
/// </summary>
public sealed class PinnedTemplateWorkflowRunTests : IDisposable
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionOneId = "version-1";
    private const string VersionTwoId = "version-2";
    private const string TemplateId = "linear-template";
    private const string ImportedBytes = "imported-workflow-zip";

    private static readonly DateTimeOffset AssignedAt = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TemplateCreatedAt = new(2026, 9, 28, 7, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();

    public PinnedTemplateWorkflowRunTests()
    {
        // A real migrated database: an imported source package with two versions, its blob on disk and a
        // project row the runs can point at.
        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task TheProductComposedRunServiceReadsTheDurableAssignment()
    {
        await using var provider = CreateProvider();

        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await SaveAndAssignAsync(templateStore, 1);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        Assert.Equal(TemplateId, run.TemplateId);
        Assert.Equal(1, run.TemplateVersion);
        Assert.IsType<SqliteWorkflowTemplateStore>(templateStore);
    }

    [Fact]
    public async Task ASourceVersionAndATemplateProduceARunCarryingBothIdentities()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();
        var runRepository = provider.GetRequiredService<IWorkflowRunRepository>();

        await SaveAndAssignAsync(templateStore, 1);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        Assert.Equal(VersionTwoId, run.WorkflowVersionId);
        Assert.Equal(PackageId, run.WorkflowPackageId);
        Assert.Equal(TemplateId, run.TemplateId);
        Assert.Equal(1, run.TemplateVersion);
        Assert.Equal("node-a", run.CurrentStageId);
        Assert.Equal("Role node-a", run.CurrentRole);

        // The pinned graph is exactly the graph the assigned version holds.
        var assigned = await templateStore.GetAsync(TemplateId, 1);
        Assert.NotNull(assigned);
        AssertGraphEquals(assigned!.Graph, WorkflowGraphSnapshot.Deserialize(run.TemplateGraphSnapshotJson!, run.Id));

        // The pinned scheme is exactly the scheme derived from that graph: one stage per node, nothing added.
        var scheme = WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson!, run.Id).Scheme;
        Assert.Equal(assigned.Graph.EntryNodeId, scheme.InitialStageId);
        Assert.Equal(
            assigned.Graph.Nodes.Select(node => node.NodeId),
            scheme.Stages.Select(stage => stage.StageId));

        foreach (var stage in scheme.Stages)
        {
            var node = assigned.Graph.GetRequiredNode(stage.StageId);

            Assert.Equal(node.DisplayName, stage.DisplayName);
            Assert.Equal(node.RoleBinding, stage.RequiredRole);
            Assert.Equal(WorkflowStageKind.Custom, stage.StageKind);
            Assert.Empty(stage.RequiredReviewerRoles);
            Assert.False(stage.RequiresUserApproval);
            Assert.Null(stage.ArtifactRequirement);
            Assert.Null(stage.FailureStageId);
            Assert.Equal(node.SuccessTargetNodeId, stage.NextStageId);
        }

        // And the same four values come back out of the database.
        var stored = await runRepository.GetByIdAsync(run.Id);
        Assert.NotNull(stored);
        Assert.Equal(TemplateId, stored!.TemplateId);
        Assert.Equal(1, stored.TemplateVersion);
        Assert.Equal(run.TemplateGraphSnapshotJson, stored.TemplateGraphSnapshotJson);
        Assert.Equal(run.TemplateSchemeSnapshotJson, stored.TemplateSchemeSnapshotJson);
    }

    [Fact]
    public async Task MovingTheAssignmentAndEditingATemplateLeavesTheFirstRunUntouched()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();
        var runRepository = provider.GetRequiredService<IWorkflowRunRepository>();

        await SaveAndAssignAsync(templateStore, 1);

        var first = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        var firstGraph = first.TemplateGraphSnapshotJson;
        var firstScheme = first.TemplateSchemeSnapshotJson;

        // The assignment moves to another saved version, and a third version is saved from a third graph.
        // Neither may reach the run that already exists.
        await SaveAndAssignAsync(templateStore, 2);
        await templateStore.SaveAsync(CreateLinearTemplate(TemplateId, 3, new[] { "only-node" }));

        var reread = await runRepository.GetByIdAsync(first.Id);

        Assert.NotNull(reread);
        Assert.Equal(1, reread!.TemplateVersion);
        Assert.Equal(firstGraph, reread.TemplateGraphSnapshotJson);
        Assert.Equal(firstScheme, reread.TemplateSchemeSnapshotJson);

        // The next run picks up the new assignment, and pins the graph of that version instead.
        var second = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        Assert.Equal(2, second.TemplateVersion);
        Assert.NotEqual(firstGraph, second.TemplateGraphSnapshotJson);
        Assert.Equal("node-a", second.CurrentStageId);

        var secondGraph = WorkflowGraphSnapshot.Deserialize(second.TemplateGraphSnapshotJson!, second.Id);
        Assert.Equal(
            new[] { "node-a", "node-x", "node-y" },
            secondGraph.Nodes.Select(node => node.NodeId));
    }

    [Fact]
    public async Task ReopeningAdvancesEveryRunOnItsOwnPinnedScheme()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await SaveAndAssignAsync(templateStore, 1);
        var onVersionOne = await runService.StartRunAsync(ProjectId, PackageId, VersionOneId);

        await SaveAndAssignAsync(templateStore, 2);
        var onVersionTwo = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        // A full reopen: brand new repositories, repository and store, over the same database.
        await using var reopened = CreateProvider();
        var reopenedService = reopened.GetRequiredService<IWorkflowRunService>();

        var advancedFirst = await reopenedService.AdvanceStageAsync(onVersionOne.Id, "reopened first");
        var advancedSecond = await reopenedService.AdvanceStageAsync(onVersionTwo.Id, "reopened second");

        Assert.Equal(1, advancedFirst.TemplateVersion);
        Assert.Equal("node-b", advancedFirst.CurrentStageId);
        Assert.Equal(2, advancedSecond.TemplateVersion);
        Assert.Equal("node-x", advancedSecond.CurrentStageId);

        await using var reread = CreateProvider();
        var runRepository = reread.GetRequiredService<IWorkflowRunRepository>();

        Assert.Equal(
            1,
            (await runRepository.GetByIdAsync(onVersionOne.Id))!.TemplateVersion);
        Assert.Equal(
            2,
            (await runRepository.GetByIdAsync(onVersionTwo.Id))!.TemplateVersion);
    }

    [Fact]
    public async Task ASavedTransitionDoesNotRewriteThePinnedIdentityOrTheSnapshots()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();
        var runRepository = provider.GetRequiredService<IWorkflowRunRepository>();

        await SaveAndAssignAsync(templateStore, 1);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);
        var graphSnapshot = run.TemplateGraphSnapshotJson;
        var schemeSnapshot = run.TemplateSchemeSnapshotJson;
        var evidence = await ReadSingleAsync(
            "SELECT EvidenceRedactedJson FROM WorkflowRuns WHERE Id = $id;",
            ("$id", run.Id));

        var advanced = await runService.AdvanceStageAsync(run.Id, "transition");

        var reread = await runRepository.GetByIdAsync(run.Id);
        var rereadEvidence = await ReadSingleAsync(
            "SELECT EvidenceRedactedJson FROM WorkflowRuns WHERE Id = $id;",
            ("$id", run.Id));

        Assert.Equal(graphSnapshot, reread!.TemplateGraphSnapshotJson);
        Assert.Equal(schemeSnapshot, reread.TemplateSchemeSnapshotJson);
        Assert.Equal(TemplateId, reread.TemplateId);
        Assert.Equal(1, reread.TemplateVersion);
        Assert.Equal("node-b", advanced.CurrentStageId);

        // The evidence payload that changes on every transition never carries the snapshots.
        Assert.NotEqual(evidence, rereadEvidence);
        Assert.DoesNotContain("Template", rereadEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("entryNodeId", rereadEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("initialStageId", rereadEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALegacyRunKeepsFollowingTheStandardScheme()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var runRepository = provider.GetRequiredService<IWorkflowRunRepository>();

        // No assignment exists at all, and the legacy start path is used explicitly.
        var run = await runService.StartLegacyRunAsync(ProjectId, PackageId, VersionTwoId);

        Assert.False(run.IsTemplateBacked);
        Assert.Null(run.TemplateId);
        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, run.CurrentStageId);

        // The standard scheme is artifact-gated, so the legacy run has to record its document before it can
        // move on. What this test is about is which scheme it follows, not the gate itself.
        run = await runService.RecordStageArtifactAsync(
            run.Id,
            WorkflowScheme.TaskSpecificationStageId,
            "TaskSpecificationDocument",
            new MemoryStream("task specification"u8.ToArray(), writable: false),
            DataClassification.PrivateSource);

        var advanced = await runService.AdvanceStageAsync(run.Id, "legacy transition");

        Assert.Equal(WorkflowScheme.ArchitectureStageId, advanced.CurrentStageId);

        var stored = await runRepository.GetByIdAsync(run.Id);
        Assert.Null(stored!.TemplateId);
        Assert.Equal("0|0|0|0", await ReadTemplateColumnCountsAsync(run.Id));
    }

    [Fact]
    public async Task ARunThatExistedBeforeMigrationStaysALegacyRunAfterTheUpgrade()
    {
        using var directory = new TestDirectory();
        var factory = new SqliteConnectionFactory(directory.GetPath("llmworkgui.db"));
        var migrations = DatabaseMigrator.LoadEmbeddedMigrations();

        await new DatabaseMigrator(factory, migrations.Where(m => m.Version < 5).ToArray())
            .MigrateAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('project-1', 'Project', 'C:\project', 'PrivateSource', '2026-09-28T00:00:00Z', '2026-09-28T00:00:00Z');
                INSERT INTO WorkflowPackages (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
                VALUES ('package-1', 'package-1.zip', 'Imported', 'hash-1', 'blob-1', '2026-09-28T00:00:00Z', '2026-09-28T00:00:00Z');
                INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
                VALUES ('version-2', 'package-1', 2, 'blob-1', 'hash-1', 'Imported', '2026-09-28T00:00:00Z');
                INSERT INTO WorkflowRuns (Id, ProjectId, WorkflowPackageId, WorkflowVersionId, State, StartedAtUtc, EvidenceRedactedJson)
                VALUES ('run-legacy', 'project-1', 'package-1', 'version-2', 'Running', '2026-09-28T00:00:00Z',
                        '{"currentStageId":"stage-task-specification","currentRole":"Coordinator"}');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var report = await new DatabaseMigrator(factory).MigrateAsync();
        Assert.Equal(4, report.PreviousSchemaVersion);
        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Max(m => m.Version), report.CurrentSchemaVersion);

        var services = new ServiceCollection();
        services.AddSingleton<ISqliteConnectionFactory>(factory);
        services.AddSingleton(new WorkflowBlobStore(directory.Root));
        services.AddLogging();
        services.AddWorkflowServices();
        await using var provider = services.BuildServiceProvider();

        var run = await provider.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync("run-legacy");

        Assert.NotNull(run);
        Assert.False(run!.IsTemplateBacked);
        Assert.Equal("stage-task-specification", run.CurrentStageId);

        // The standard scheme is artifact-gated, so the run that predates template pinning records its
        // document before it moves on. It is still the same legacy run: no template, the standard chain.
        run = await provider.GetRequiredService<IWorkflowRunService>()
            .RecordStageArtifactAsync(
                run.Id,
                WorkflowScheme.TaskSpecificationStageId,
                "TaskSpecificationDocument",
                new MemoryStream("legacy task specification"u8.ToArray(), writable: false),
                DataClassification.PrivateSource);

        var advanced = await provider.GetRequiredService<IWorkflowRunService>()
            .AdvanceStageAsync("run-legacy", "legacy after upgrade");

        Assert.Equal(WorkflowScheme.ArchitectureStageId, advanced.CurrentStageId);
    }

    [Fact]
    public async Task TheDatabaseRefusesToRewriteOrToHalfPinARunIdentity()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await SaveAndAssignAsync(templateStore, 1);
        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            "UPDATE WorkflowRuns SET TemplateId = 'other-template' WHERE Id = $id;",
            ("$id", run.Id)));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            "UPDATE WorkflowRuns SET TemplateVersion = 2 WHERE Id = $id;",
            ("$id", run.Id)));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            "UPDATE WorkflowRuns SET TemplateSchemeSnapshotJson = '{\"initialStageId\":\"x\",\"stages\":[]}' "
                + "WHERE Id = $id;",
            ("$id", run.Id)));

        // A legacy run may not acquire an identity after the fact either.
        var legacy = await runService.StartLegacyRunAsync(ProjectId, PackageId, VersionTwoId);

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            "UPDATE WorkflowRuns SET TemplateId = 'linear-template' WHERE Id = $id;",
            ("$id", legacy.Id)));

        // Half an identity is not a run.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            """
            INSERT INTO WorkflowRuns (
                Id, ProjectId, WorkflowPackageId, WorkflowVersionId, State, StartedAtUtc, EvidenceRedactedJson,
                TemplateId, TemplateVersion, TemplateGraphSnapshotJson
            )
            VALUES (
                'run-half', 'project-1', 'package-1', 'version-2', 'Running', '2026-09-28T00:00:00Z',
                '{"currentStageId":"node-a","currentRole":"Role A"}',
                'linear-template', 1, '{}'
            );
            """));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            """
            INSERT INTO WorkflowRuns (
                Id, ProjectId, WorkflowPackageId, WorkflowVersionId, State, StartedAtUtc, EvidenceRedactedJson,
                TemplateId, TemplateVersion, TemplateGraphSnapshotJson, TemplateSchemeSnapshotJson
            )
            VALUES (
                'run-unreadable', 'project-1', 'package-1', 'version-2', 'Running', '2026-09-28T00:00:00Z',
                '{"currentStageId":"node-a","currentRole":"Role A"}',
                'linear-template', 0, '{}', '{}'
            );
            """));

        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns WHERE Id = 'run-half';"));
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns WHERE Id = 'run-unreadable';"));
    }

    [Fact]
    public async Task TheImportedSourceBlobsAreNotTouched()
    {
        var blobStore = new WorkflowBlobStore(_database.Root);
        var bytes = System.Text.Encoding.UTF8.GetBytes(ImportedBytes);
        var blobId = WorkflowBlobStore.ComputeBlobId(bytes);

        await using (var content = new MemoryStream(bytes))
        {
            var blob = await blobStore.SaveBlobAsync(content);
            Assert.Equal(blobId, blob.BlobId);
        }

        await SeedImportedBlobRowAsync(blobId);

        var hashBefore = await ReadSingleAsync(
            "SELECT OriginalHash || '|' || OriginalBlobId FROM WorkflowPackages WHERE Id = $id;",
            ("$id", PackageId));
        var versionHashBefore = await ReadSingleAsync(
            "SELECT OriginalHash || '|' || BlobId FROM WorkflowVersions WHERE Id = $id;",
            ("$id", VersionTwoId));
        var fileHashBefore = await ComputeFileHashAsync(blobStore.GetBlobPath(blobId));

        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await SaveAndAssignAsync(templateStore, 1);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);
        await runService.AdvanceStageAsync(run.Id, "transition");
        await SaveAndAssignAsync(templateStore, 2);
        await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        Assert.Equal(
            hashBefore,
            await ReadSingleAsync(
                "SELECT OriginalHash || '|' || OriginalBlobId FROM WorkflowPackages WHERE Id = $id;",
                ("$id", PackageId)));
        Assert.Equal(
            versionHashBefore,
            await ReadSingleAsync(
                "SELECT OriginalHash || '|' || BlobId FROM WorkflowVersions WHERE Id = $id;",
                ("$id", VersionTwoId)));
        Assert.Equal(fileHashBefore, await ComputeFileHashAsync(blobStore.GetBlobPath(blobId)));
    }

    [Fact]
    public async Task NoAssignedTemplateLeavesNoRunAtAll()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => runService.StartRunAsync(ProjectId, PackageId, VersionTwoId));

        Assert.Equal(WorkflowTemplateExecutionBlockers.MissingAssignment, blocked.Blocker);
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns;"));
    }

    [Fact]
    public async Task TheBuiltInStandardTemplateStartsARunPinnedToItsOwnGraph()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();
        var runRepository = provider.GetRequiredService<IWorkflowRunRepository>();

        // The store seeds the shipped template on first use, so pointing the project at it pins the run to
        // the graph that seed actually wrote.
        await templateStore.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-built-in",
            ProjectId,
            WorkflowStudioService.StandardTemplateId,
            1,
            AssignedAt));

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionTwoId);

        Assert.Equal(WorkflowStudioService.StandardTemplateId, run.TemplateId);
        Assert.Equal(1, run.TemplateVersion);
        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, run.CurrentStageId);
        Assert.Equal(WorkflowScheme.CoordinatorRole, run.CurrentRole);
        Assert.Equal("1", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns;"));

        // The pinned graph is exactly the graph the shipped version holds, read back out of the run's own
        // row rather than out of the object the service just built.
        var stored = await runRepository.GetByIdAsync(run.Id);
        var pinned = WorkflowGraphSnapshot.Deserialize(stored!.TemplateGraphSnapshotJson!, run.Id);
        var shipped = await templateStore.GetAsync(WorkflowStudioService.StandardTemplateId, 1);

        Assert.NotNull(shipped);
        AssertGraphEquals(shipped!.Graph, pinned);
        Assert.Equal(0, pinned.GetRequiredNode(WorkflowScheme.CodeAndUiStageId).RetryBudget);
        Assert.Null(pinned.GetRequiredNode(WorkflowScheme.MultiLevelReviewStageId).FailureTargetNodeId);

        // Every stage the run is advanced against is derived from that graph, and the built-in's own gates,
        // failure targets and terminal stage survive a round trip through the pinned scheme snapshot.
        var scheme = WorkflowSchemeSnapshot.Deserialize(stored.TemplateSchemeSnapshotJson!, run.Id).Scheme;

        Assert.Equal(11, scheme.Stages.Count);
        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, scheme.InitialStageId);
        Assert.Null(scheme.GetRequiredStage(WorkflowScheme.MultiLevelReviewStageId).FailureStageId);
        Assert.Equal(
            new[] { WorkflowScheme.ReviewerRole, WorkflowScheme.ArchitectRole },
            scheme.GetRequiredStage(WorkflowScheme.DocumentReviewStageId).RequiredReviewerRoles);
        Assert.True(scheme.GetRequiredStage(WorkflowScheme.UserApprovalStageId).RequiresUserApproval);
        Assert.Null(scheme.GetRequiredStage(WorkflowScheme.FinalOutcomeStageId).NextStageId);
    }

    /// <summary>
    /// The built-in is pinned exactly as the studio ships it. A store that seeded a different graph under
    /// the built-in id - a half-applied edit, a restored row, a different build's seed - is refused by name
    /// before a run row exists, and the process-wide standard scheme is never substituted for it.
    /// </summary>
    [Fact]
    public async Task AChangedOrCorruptedBuiltInGraphLeavesNoRunAtAll()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<ISqliteConnectionFactory>(_database.Factory);
        services.AddSingleton(new WorkflowBlobStore(_database.Root));

        // The corrupted row is written by the store's own seed path, so it is a real persisted version and
        // not a definition that only ever existed in memory.
        services.AddSingleton<IWorkflowTemplateStore>(provider => new SqliteWorkflowTemplateStore(
            provider.GetRequiredService<ISqliteConnectionFactory>(),
            new WorkflowGraphValidator()));

        services.AddWorkflowServices();

        await using var provider = services.BuildServiceProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await templateStore.SaveAsync(CreateCorruptedBuiltInTemplate());
        await templateStore.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-built-in",
            ProjectId,
            WorkflowStudioService.StandardTemplateId,
            1,
            AssignedAt));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => runService.StartRunAsync(ProjectId, PackageId, VersionTwoId));

        Assert.Equal(
            WorkflowTemplateExecutionBlockers.BuiltInStandardTemplateGraphChanged,
            blocked.Blocker);
        Assert.Contains(WorkflowScheme.DocumentReviewStageId, blocked.Message, StringComparison.Ordinal);
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns;"));
    }

    [Theory]
    [MemberData(nameof(UnsupportedGraphs))]
    public async Task AnUnsupportedGraphLeavesNoRunAtAll(string shape, string expectedBlocker)
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await templateStore.SaveAsync(CreateUnsupportedTemplate(shape));
        await templateStore.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            $"assignment-{shape}",
            ProjectId,
            $"unsupported-{shape}",
            1,
            AssignedAt));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => runService.StartRunAsync(ProjectId, PackageId, VersionTwoId));

        Assert.Equal(expectedBlocker, blocked.Blocker);
        Assert.Contains(expectedBlocker, blocked.Message, StringComparison.Ordinal);
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns;"));
    }

    [Fact]
    public async Task AnUnconsumedPromptBudgetCycleIsRejectedBeforeItCanBeStoredOrPinned()
    {
        var provider = CreateProvider();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();
        var error = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            templateStore.SaveAsync(CreateUnsupportedTemplate("cycle")));

        Assert.Contains("unjustified cycle", error.Message, StringComparison.Ordinal);
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowTemplateVersions WHERE TemplateId='unsupported-cycle';"));
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns;"));
    }

    [Theory]
    [InlineData("terminal-retry-budget")]
    [InlineData("terminal-permission")]
    public async Task ASavedPromptToTerminalGraphWithAFieldOnItsTerminalNodeLeavesNoRunAtAll(string shape)
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await templateStore.SaveAsync(CreateUnsupportedTemplate(shape));
        await templateStore.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            $"assignment-{shape}",
            ProjectId,
            $"unsupported-{shape}",
            1,
            AssignedAt));

        // The store accepted the version and kept the declaration on the terminal node, so the refusal
        // comes from the run start reading a real field rather than from a field that was never stored.
        var stored = await templateStore.GetAsync($"unsupported-{shape}", 1);
        var terminal = stored!.Graph.GetRequiredNode("node-b");
        Assert.Equal(WorkflowNodeKind.TerminalOutcome, terminal.Kind);
        Assert.True(
            shape == "terminal-retry-budget" ? terminal.RetryBudget > 0 : terminal.PermissionIntent is not null,
            $"The terminal node of '{shape}' did not keep its declaration.");

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => runService.StartRunAsync(ProjectId, PackageId, VersionTwoId));

        Assert.Equal(
            shape == "terminal-retry-budget"
                ? WorkflowTemplateExecutionBlockers.RetryBudget
                : WorkflowTemplateExecutionBlockers.PermissionIntent,
            blocked.Blocker);
        Assert.Contains("node-b", blocked.Message, StringComparison.Ordinal);
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns;"));
    }

    /// <summary>
    /// Every graph shape this slice refuses, with the blocker each one is refused by. All of them are
    /// storable graphs - the durable store accepts them - so each refusal comes from the run start itself
    /// and not from a save that never happened. The four kinds the shipped built-in graph uses are here
    /// because a node carrying one still has to declare the gate that kind names; a node that declares it
    /// is mapped, and the built-in itself is mapped in full.
    /// </summary>
    public static TheoryData<string, string> UnsupportedGraphs()
    {
        var data = new TheoryData<string, string>();

        foreach (var kind in new[]
                 {
                     WorkflowNodeKind.Review,
                     WorkflowNodeKind.ApprovalGate,
                     WorkflowNodeKind.UserDecision,
                     WorkflowNodeKind.ValidationCommand,
                     WorkflowNodeKind.Writer,
                     WorkflowNodeKind.Escalation,
                     WorkflowNodeKind.Condition,
                     WorkflowNodeKind.Retry
                 })
        {
            data.Add(kind.ToString(), WorkflowTemplateExecutionBlockers.UnsupportedNodeKind);
        }
        data.Add(WorkflowNodeKind.ArtifactCollection.ToString(), WorkflowTemplateExecutionBlockers.ArtifactContract);

        // Unbudgeted cycles are refused at the durable-store boundary, covered separately below.
        data.Add("condition", WorkflowTemplateExecutionBlockers.Condition);
        data.Add("fallback-route", WorkflowTemplateExecutionBlockers.FallbackRoute);
        data.Add("artifact", WorkflowTemplateExecutionBlockers.ArtifactContract);
        data.Add("permission", WorkflowTemplateExecutionBlockers.PermissionIntent);
        data.Add("terminal-retry-budget", WorkflowTemplateExecutionBlockers.RetryBudget);
        data.Add("terminal-condition", WorkflowTemplateExecutionBlockers.Condition);
        data.Add("terminal-fallback-route", WorkflowTemplateExecutionBlockers.FallbackRoute);
        data.Add("terminal-artifact", WorkflowTemplateExecutionBlockers.ArtifactContract);
        data.Add("terminal-permission", WorkflowTemplateExecutionBlockers.PermissionIntent);

        return data;
    }

    [Theory]
    [InlineData("detached")]
    [InlineData("dead-end")]
    [InlineData("dangling-failure")]
    public async Task AGraphTheStoreItselfRefusesCanNeverReachARun(string shape)
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        // A detached island, a dead-end prompt node and a failure target onto an undeclared node are not
        // valid workflow graphs at all, so the durable store refuses the version and the project can never
        // be pointed at it.
        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => templateStore.SaveAsync(CreateUnsupportedTemplate(shape)));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => runService.StartRunAsync(ProjectId, PackageId, VersionTwoId));

        Assert.Equal(WorkflowTemplateExecutionBlockers.MissingAssignment, blocked.Blocker);
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns;"));
    }

    private async Task SeedAsync()
    {
        await _database.InitializeAsync();

        await ExecuteAsync(
            """
            INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, 'Project', 'C:\project', 'PrivateSource', $now, $now);
            INSERT INTO WorkflowPackages (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($package, 'package-1.zip', 'Imported', 'hash-1', 'blob-1', $now, $now);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($version1, $package, 1, 'blob-1', 'hash-1', 'Imported', $now);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($version2, $package, 2, 'blob-1', 'hash-1', 'Imported', $now);
            """,
            ("$id", ProjectId),
            ("$package", PackageId),
            ("$version1", VersionOneId),
            ("$version2", VersionTwoId),
            ("$now", "2026-09-28T00:00:00Z"));
    }

    private async Task SeedImportedBlobRowAsync(string blobId) =>
        await ExecuteAsync(
            "UPDATE WorkflowPackages SET OriginalBlobId = $blob WHERE Id = $id;",
            ("$blob", blobId),
            ("$id", PackageId));

    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<ISqliteConnectionFactory>(_database.Factory);

        // A pinned run's derived stages are gate-free, so nothing here records an artifact, but the run
        // service still refuses to be composed without the store it re-verifies a recorded artifact through.
        services.AddSingleton(new WorkflowBlobStore(_database.Root));
        services.AddWorkflowServices();

        return services.BuildServiceProvider();
    }

    private static async Task SaveAndAssignAsync(IWorkflowTemplateStore store, int version)
    {
        await store.SaveAsync(CreateTemplate(version));
        await store.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            $"assignment-{version}",
            ProjectId,
            TemplateId,
            version,
            AssignedAt));
    }

    /// <summary>
    /// Version 1 and version 2 of the same template deliberately hold different chains, so a test can tell
    /// which version a run was pinned to from the run alone.
    /// </summary>
    private static WorkflowTemplateDefinition CreateTemplate(int version) =>
        CreateLinearTemplate(TemplateId, version, version == 1 ? new[] { "node-a", "node-b", "node-c" } : new[] { "node-a", "node-x", "node-y" });

    private static WorkflowTemplateDefinition CreateLinearTemplate(
        string templateId,
        int version,
        string[] nodeIds)
    {
        var nodes = new List<WorkflowNodeDefinition>(nodeIds.Length);

        for (var index = 0; index < nodeIds.Length; index++)
        {
            var isLast = index == nodeIds.Length - 1;

            nodes.Add(isLast
                ? new WorkflowNodeDefinition(
                    nodeIds[index],
                    WorkflowNodeKind.TerminalOutcome,
                    $"Node {nodeIds[index]}",
                    $"Role {nodeIds[index]}")
                : new WorkflowNodeDefinition(
                    nodeIds[index],
                    WorkflowNodeKind.Prompt,
                    $"Node {nodeIds[index]}",
                    $"Role {nodeIds[index]}",
                    successTargetNodeId: nodeIds[index + 1]));
        }

        return new WorkflowTemplateDefinition(
            templateId,
            version,
            $"Linear template v{version}",
            "A gate-free linear template.",
            new WorkflowGraph(nodeIds[0], nodes),
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            TemplateCreatedAt);
    }

    private static WorkflowTemplateDefinition CreateUnsupportedTemplate(string shape)
    {
        var graph = shape switch
        {
            // A gate or an execution node this slice cannot represent as a plain scheme stage. The four
            // kinds the shipped built-in graph uses appear here because a node carrying one still has to
            // declare the gate that kind names - and this chain declares none.
            nameof(WorkflowNodeKind.Review)
                or nameof(WorkflowNodeKind.ApprovalGate)
                or nameof(WorkflowNodeKind.UserDecision)
                or nameof(WorkflowNodeKind.ValidationCommand)
                or nameof(WorkflowNodeKind.Writer)
                or nameof(WorkflowNodeKind.Escalation)
                or nameof(WorkflowNodeKind.Condition)
                or nameof(WorkflowNodeKind.Retry)
                or nameof(WorkflowNodeKind.ArtifactCollection) => CreateGateFreeChain(Enum.Parse<WorkflowNodeKind>(shape)),
            // A failure target onto a node the graph does not declare names a route nothing could preserve.
            "dangling-failure" => new WorkflowGraph("node-a", new[]
            {
                Prompt("node-a", success: "node-b", failure: "node-d"),
                Terminal("node-b")
            }),
            // A Prompt budget is never consumed; this loop must be rejected before it can be stored.
            "cycle" => new WorkflowGraph("node-a", new[]
            {
                new WorkflowNodeDefinition(
                    "node-a",
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    retryBudget: 1,
                    successTargetNodeId: "node-a",
                    failureTargetNodeId: "node-b"),
                Terminal("node-b")
            }),
            "detached" => new WorkflowGraph("node-a", new[]
            {
                Prompt("node-a", success: "node-b"),
                Terminal("node-b"),
                Prompt("node-c", success: "node-d"),
                Terminal("node-d")
            }),
            "dead-end" => new WorkflowGraph("node-a", new[] { Prompt("node-a") }),
            "condition" => new WorkflowGraph("node-a", new[]
            {
                new WorkflowNodeDefinition(
                    "node-a",
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: "node-b",
                    conditionExpression: "always"),
                Terminal("node-b")
            }),
            "fallback-route" => new WorkflowGraph("node-a", new[]
            {
                new WorkflowNodeDefinition(
                    "node-a",
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    fallbackRouteIds: new[] { "route-b" },
                    successTargetNodeId: "node-b"),
                Terminal("node-b")
            }),
            "artifact" => new WorkflowGraph("node-a", new[]
            {
                new WorkflowNodeDefinition(
                    "node-a",
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: "node-b",
                    artifactContract: "report.md"),
                Terminal("node-b")
            }),
            "permission" => new WorkflowGraph("node-a", new[]
            {
                new WorkflowNodeDefinition(
                    "node-a",
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: "node-b",
                    permissionIntent: "write"),
                Terminal("node-b")
            }),
            // A gate-free Prompt to Terminal chain whose terminal outcome node carries the declaration
            // itself. A terminal node with a retry budget, a condition, a fallback route, an artifact
            // contract or a permission intent is a storable graph, and the run start still refuses it:
            // the derived terminal stage honours none of them, so accepting the run would silently drop
            // what the template asked for.
            "terminal-retry-budget" => new WorkflowGraph("node-a", new[]
            {
                Prompt("node-a", success: "node-b"),
                new WorkflowNodeDefinition(
                    "node-b",
                    WorkflowNodeKind.TerminalOutcome,
                    "Node B",
                    "Role B",
                    retryBudget: 2)
            }),
            "terminal-condition" => new WorkflowGraph("node-a", new[]
            {
                Prompt("node-a", success: "node-b"),
                new WorkflowNodeDefinition(
                    "node-b",
                    WorkflowNodeKind.TerminalOutcome,
                    "Node B",
                    "Role B",
                    conditionExpression: "always")
            }),
            "terminal-fallback-route" => new WorkflowGraph("node-a", new[]
            {
                Prompt("node-a", success: "node-b"),
                new WorkflowNodeDefinition(
                    "node-b",
                    WorkflowNodeKind.TerminalOutcome,
                    "Node B",
                    "Role B",
                    fallbackRouteIds: new[] { "route-b" })
            }),
            "terminal-artifact" => new WorkflowGraph("node-a", new[]
            {
                Prompt("node-a", success: "node-b"),
                new WorkflowNodeDefinition(
                    "node-b",
                    WorkflowNodeKind.TerminalOutcome,
                    "Node B",
                    "Role B",
                    artifactContract: "report.md")
            }),
            "terminal-permission" => new WorkflowGraph("node-a", new[]
            {
                Prompt("node-a", success: "node-b"),
                new WorkflowNodeDefinition(
                    "node-b",
                    WorkflowNodeKind.TerminalOutcome,
                    "Node B",
                    "Role B",
                    permissionIntent: "write")
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown unsupported shape.")
        };

        return new WorkflowTemplateDefinition(
            $"unsupported-{shape}",
            1,
            $"Unsupported {shape}",
            "A graph this slice refuses to pin.",
            graph,
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            TemplateCreatedAt);
    }

    private static WorkflowGraph CreateGateFreeChain(WorkflowNodeKind kind)
    {
        // A retry node and a condition node only validate as a graph when they declare the budget and the
        // expression their kind requires, so the version is storable and the refusal comes from the run
        // start rather than from the save.
        var middle = kind switch
        {
            WorkflowNodeKind.Retry => new WorkflowNodeDefinition(
                "node-b",
                kind,
                "Node B",
                "Role B",
                retryBudget: 1,
                successTargetNodeId: "node-c"),
            WorkflowNodeKind.Condition => new WorkflowNodeDefinition(
                "node-b",
                kind,
                "Node B",
                "Role B",
                successTargetNodeId: "node-c",
                conditionExpression: "always"),
            _ => new WorkflowNodeDefinition(
                "node-b",
                kind,
                "Node B",
                "Role B",
                successTargetNodeId: "node-c")
        };

        return new WorkflowGraph(
            "node-a",
            new[]
            {
                Prompt("node-a", success: "node-b"),
                middle,
                Terminal("node-c")
            });
    }

    private static WorkflowNodeDefinition Prompt(string nodeId, string? success = null, string? failure = null) =>
        new(
            nodeId,
            WorkflowNodeKind.Prompt,
            $"Node {nodeId}",
            $"Role {nodeId}",
            successTargetNodeId: success,
            failureTargetNodeId: failure);

    private static WorkflowNodeDefinition Terminal(string nodeId) =>
        new(nodeId, WorkflowNodeKind.TerminalOutcome, $"Node {nodeId}", $"Role {nodeId}");

    /// <summary>
    /// The shipped built-in template with one reviewer removed from one gate. The graph is still valid and
    /// the gate is still enforceable, so nothing else about it would be refused: only the proof that the
    /// built-in is the graph the studio produces can catch this, which is exactly what it is for.
    /// </summary>
    private static WorkflowTemplateDefinition CreateCorruptedBuiltInTemplate()
    {
        var canonical = WorkflowStudioService.CreateStandardTemplate();
        var nodes = canonical.Graph.Nodes
            .Select(node => string.Equals(node.NodeId, WorkflowScheme.DocumentReviewStageId, StringComparison.Ordinal)
                ? WithSingleReviewer(node)
                : node)
            .ToArray();

        return new WorkflowTemplateDefinition(
            canonical.TemplateId,
            canonical.Version,
            canonical.DisplayName,
            canonical.Description,
            new WorkflowGraph(canonical.Graph.EntryNodeId, nodes),
            canonical.RoleBindings,
            canonical.RequiredDocumentTemplates,
            canonical.IsBuiltIn,
            canonical.CreatedAtUtc);

        static WorkflowNodeDefinition WithSingleReviewer(WorkflowNodeDefinition node) =>
            new(
                node.NodeId,
                node.Kind,
                node.DisplayName,
                node.RoleBinding,
                node.RequiredCapabilities,
                node.PrimaryRouteId,
                node.FallbackRouteIds,
                node.Timeout,
                node.RetryBudget,
                node.SuccessTargetNodeId,
                node.FailureTargetNodeId,
                node.ConditionExpression,
                node.ArtifactContract,
                node.PermissionIntent,
                new WorkflowNodeGateMetadata(
                    WorkflowStageKind.DocumentReview,
                    new[] { WorkflowScheme.ReviewerRole },
                    requiresUserApproval: false,
                    artifactRequirement: "DocumentBundle"));
    }

    private static void AssertGraphEquals(WorkflowGraph expected, WorkflowGraph actual)
    {
        Assert.Equal(expected.EntryNodeId, actual.EntryNodeId);
        Assert.Equal(expected.Nodes.Count, actual.Nodes.Count);

        foreach (var node in expected.Nodes)
        {
            var counterpart = actual.GetRequiredNode(node.NodeId);

            Assert.Equal(node.Kind, counterpart.Kind);
            Assert.Equal(node.DisplayName, counterpart.DisplayName);
            Assert.Equal(node.RoleBinding, counterpart.RoleBinding);
            Assert.Equal(node.SuccessTargetNodeId, counterpart.SuccessTargetNodeId);
            Assert.Equal(node.FailureTargetNodeId, counterpart.FailureTargetNodeId);
            Assert.Equal(node.RetryBudget, counterpart.RetryBudget);
            Assert.Equal(node.ConditionExpression, counterpart.ConditionExpression);
            Assert.Equal(node.ArtifactContract, counterpart.ArtifactContract);
            Assert.Equal(node.PermissionIntent, counterpart.PermissionIntent);
            Assert.Equal(node.FallbackRouteIds, counterpart.FallbackRouteIds);
            Assert.Equal(node.GateMetadata?.StageKind, counterpart.GateMetadata?.StageKind);
            Assert.Equal(
                node.GateMetadata?.RequiredReviewerRoles,
                counterpart.GateMetadata?.RequiredReviewerRoles);
            Assert.Equal(
                node.GateMetadata?.RequiresUserApproval,
                counterpart.GateMetadata?.RequiresUserApproval);
            Assert.Equal(
                node.GateMetadata?.ArtifactRequirement,
                counterpart.GateMetadata?.ArtifactRequirement);
        }
    }

    private async Task<string> ReadTemplateColumnCountsAsync(string runId) =>
        await ReadSingleAsync(
            "SELECT COUNT(TemplateId) || '|' || COUNT(TemplateVersion) || '|' "
            + "|| COUNT(TemplateGraphSnapshotJson) || '|' || COUNT(TemplateSchemeSnapshotJson) "
            + "FROM WorkflowRuns WHERE Id = $id;",
            ("$id", runId));

    private async Task<string> ReadSingleAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ComputeFileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
