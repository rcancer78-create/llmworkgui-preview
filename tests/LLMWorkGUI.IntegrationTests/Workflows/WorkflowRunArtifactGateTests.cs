using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
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
/// The durable half of the artifact gate: a stage transition is authorized by a committed artifact row whose
/// stored bytes still hash to what the row records.
///
/// Every durability case here starts from a previously committed row - the blob really exists in the blob
/// store and the row really is in SQLite - and then removes the thing the row is only a claim about. A failed
/// insert is not a substitute: it produces no row, so it cannot be the starting point of a test about what a
/// committed row can and cannot authorize after a restart.
/// </summary>
public sealed partial class WorkflowRunArtifactGateTests : IDisposable
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionId = "version-1";
    private const string StageId = "stage-document-review";
    private const string ArtifactKind = "DocumentBundle";

    private static readonly DateTimeOffset AssignedAt = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();

    public WorkflowRunArtifactGateTests()
    {
        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task ACommittedRowIsTheSourceOfTruthAndThePayloadOnlyMirrorsItForAudit()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var evidence = await RecordArtifactAsync(run.Id, "first revision");

        var stored = await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(run.Id);

        // The artifact comes back from the Artifacts rows, not from the redacted payload of the run.
        var loaded = SingleArtifact(stored!);
        Assert.Equal(evidence.ArtifactId, loaded.ArtifactId);
        Assert.Equal(evidence.BlobId, loaded.BlobId);
        Assert.Equal(evidence.HashSha256, loaded.HashSha256);
        Assert.Equal(StageId, loaded.StageId);
        Assert.Equal(ArtifactKind, loaded.Kind);
        Assert.Equal(run.Id, loaded.RunId);
        Assert.Equal(DataClassification.PrivateSource, loaded.Classification);
        Assert.Equal(evidence.SizeBytes, loaded.SizeBytes);

        // The row itself is complete and names the stage it belongs to.
        Assert.Equal(StageId, await ReadSingleAsync(
            "SELECT StageId FROM Artifacts WHERE Id = $id;",
            ("$id", evidence.ArtifactId)));
        Assert.Equal(
            $"{evidence.BlobId}|{evidence.HashSha256}",
            await ReadSingleAsync(
                "SELECT BlobId || '|' || HashSha256 FROM Artifacts WHERE Id = $id;",
                ("$id", evidence.ArtifactId)));

        // And no artifact content ever reaches the run's evidence payload.
        var payload = await ReadSingleAsync(
            "SELECT EvidenceRedactedJson FROM WorkflowRuns WHERE Id = $id;",
            ("$id", run.Id));
        Assert.DoesNotContain("first revision", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ATransitionIsAuthorizedByTheCommittedArtifactAndRecordsIt()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var evidence = await RecordArtifactAsync(run.Id, "first revision");
        var service = CreateProvider().GetRequiredService<IWorkflowRunService>();

        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ReviewerRole, evidence.HashSha256));
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ArchitectRole, evidence.HashSha256));

        var advanced = await service.AdvanceStageAsync(run.Id, "the bundle was reviewed");

        var transition = advanced.Transitions[^1];
        Assert.True(transition.HasAuthorizingArtifact);
        Assert.Equal(evidence.ArtifactId, transition.AuthorizingArtifactId);
        Assert.Equal(evidence.HashSha256, transition.AuthorizingArtifactHash);
        Assert.Equal(2, transition.ReviewerVerdicts.Count);

        // The recorded pair survives the round trip as audit, and the artifact itself survives as a row.
        var stored = await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(run.Id);
        Assert.Equal(evidence.ArtifactId, stored!.Transitions[^1].AuthorizingArtifactId);
        Assert.Equal(evidence.HashSha256, stored.Transitions[^1].AuthorizingArtifactHash);
        Assert.Single(StageArtifacts(stored));
    }

    [Fact]
    public async Task ACommittedRowWhoseBlobWasDeletedCannotAuthorizeATransition()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var evidence = await RecordArtifactAsync(run.Id, "first revision");
        var service = CreateProvider().GetRequiredService<IWorkflowRunService>();

        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ReviewerRole, evidence.HashSha256));
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ArchitectRole, evidence.HashSha256));

        var blobStore = new WorkflowBlobStore(_database.Root);
        Assert.True(await blobStore.BlobExistsAsync(evidence.BlobId));
        File.Delete(blobStore.GetBlobPath(evidence.BlobId));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AdvanceStageAsync(run.Id, "the bundle was reviewed"));

        Assert.Contains(evidence.ArtifactId, exception.Message, StringComparison.Ordinal);
        Assert.Equal(StageId, (await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(run.Id))!.CurrentStageId);
    }

    [Fact]
    public async Task ACommittedRowWhoseBytesWereAlteredCannotAuthorizeATransition()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var evidence = await RecordArtifactAsync(run.Id, "first revision");
        var service = CreateProvider().GetRequiredService<IWorkflowRunService>();

        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ReviewerRole, evidence.HashSha256));
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ArchitectRole, evidence.HashSha256));

        // The row still says the same hash, and the row is still perfectly complete in SQL.
        var repository = new SqliteWorkflowRunRepository(_database.Factory);
        var beforeRefusal = (await repository.GetByIdAsync(run.Id))!;
        var beforeLocks = await ReadSingleAsync(
            "SELECT group_concat(Id || ':' || ExecutionId || ':' || COALESCE(ReleasedAtUtc, ''), '|') "
            + "FROM (SELECT Id, ExecutionId, ReleasedAtUtc FROM ProjectLocks WHERE ProjectId = $project ORDER BY Id);",
            ("$project", ProjectId));
        var blobStore = new WorkflowBlobStore(_database.Root);
        File.WriteAllText(blobStore.GetBlobPath(evidence.BlobId), "a different document");
        Assert.False(await blobStore.VerifyBlobAsync(evidence.BlobId));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AdvanceStageAsync(run.Id, "the bundle was reviewed"));

        Assert.Contains(evidence.ArtifactId, exception.Message, StringComparison.Ordinal);
        var afterRefusal = (await repository.GetByIdAsync(run.Id))!;
        Assert.Equal(beforeRefusal.CurrentStageId, afterRefusal.CurrentStageId);
        Assert.Equal(beforeRefusal.State, afterRefusal.State);
        Assert.Equal(beforeRefusal.Transitions.Count, afterRefusal.Transitions.Count);
        Assert.Equal(beforeRefusal.EndedAtUtc, afterRefusal.EndedAtUtc);
        Assert.Equal(beforeRefusal.Artifacts.Count, afterRefusal.Artifacts.Count);
        var retainedArtifact = SingleArtifact(afterRefusal);
        Assert.Equal(evidence.ArtifactId, retainedArtifact.ArtifactId);
        Assert.Equal(evidence.BlobId, retainedArtifact.BlobId);
        Assert.Equal(evidence.HashSha256, retainedArtifact.HashSha256);
        Assert.Equal(beforeLocks, await ReadSingleAsync(
            "SELECT group_concat(Id || ':' || ExecutionId || ':' || COALESCE(ReleasedAtUtc, ''), '|') "
            + "FROM (SELECT Id, ExecutionId, ReleasedAtUtc FROM ProjectLocks WHERE ProjectId = $project ORDER BY Id);",
            ("$project", ProjectId)));
    }

    [Fact]
    public async Task ACommittedRowSurvivesDatabaseConnectionReopenAndStillAuthorizesTheSameTransition()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var evidence = await RecordArtifactAsync(run.Id, "first revision");

        // Clear this isolated database's physical connection pool, then construct a new factory
        // and repositories over the persisted database and blobs. This is not an OS/process restart.
        using var reopened = await ReopenDatabaseProviderAsync();
        var reopenedService = reopened.GetRequiredService<IWorkflowRunService>();
        var reopenedRepository = reopened.GetRequiredService<IWorkflowRunRepository>();

        var reread = await reopenedRepository.GetByIdAsync(run.Id);
        Assert.Equal(evidence.BlobId, SingleArtifact(reread!).BlobId);

        await reopenedService.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ReviewerRole, evidence.HashSha256));
        await reopenedService.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ArchitectRole, evidence.HashSha256));

        var advanced = await reopenedService.AdvanceStageAsync(run.Id, "after the restart");

        Assert.Equal(evidence.ArtifactId, advanced.Transitions[^1].AuthorizingArtifactId);
    }

    [Fact]
    public async Task ACommittedRowOfADeletedBlobIsStillLoadedButStopsAuthorizingAfterDatabaseConnectionReopen()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var evidence = await RecordArtifactAsync(run.Id, "first revision");

        // The row is read back from storage before anything else happens, which is the point: a SQL-complete
        // row survives a restart, and only the bytes can say it no longer means anything.
        var reread = await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(run.Id);
        Assert.Equal(evidence.ArtifactId, SingleArtifact(reread!).ArtifactId);

        var blobStore = new WorkflowBlobStore(_database.Root);
        File.Delete(blobStore.GetBlobPath(evidence.BlobId));

        using var reopened = await ReopenDatabaseProviderAsync();
        var reopenedService = reopened.GetRequiredService<IWorkflowRunService>();
        var reopenedRepository = reopened.GetRequiredService<IWorkflowRunRepository>();

        await reopenedService.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ReviewerRole, evidence.HashSha256));
        await reopenedService.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ArchitectRole, evidence.HashSha256));

        Assert.Equal(evidence.ArtifactId, SingleArtifact((await reopenedRepository.GetByIdAsync(run.Id))!).ArtifactId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopenedService.AdvanceStageAsync(run.Id, "after the restart"));
    }

    [Fact]
    public async Task ANewerArtifactSupersedesTheOlderOneAndTheOldHashStopsAuthorizing()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var first = await RecordArtifactAsync(run.Id, "first revision");
        var second = await RecordArtifactAsync(run.Id, "second revision");

        Assert.NotEqual(first.BlobId, second.BlobId);

        var service = CreateProvider().GetRequiredService<IWorkflowRunService>();
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ReviewerRole, first.HashSha256));
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ArchitectRole, first.HashSha256));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AdvanceStageAsync(run.Id, "the first revision was reviewed"));

        Assert.Contains(second.HashSha256, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(first.HashSha256, exception.Message, StringComparison.Ordinal);

        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ReviewerRole, second.HashSha256));
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ArchitectRole, second.HashSha256));

        var advanced = await service.AdvanceStageAsync(run.Id, "the second revision was reviewed");

        Assert.Equal(second.ArtifactId, advanced.Transitions[^1].AuthorizingArtifactId);
    }

    [Theory]
    [InlineData(StageId, "UiAcceptanceEvidence")]
    [InlineData("stage-architecture", ArtifactKind)]
    public async Task AnArtifactOfAnotherKindOrAnotherStageDoesNotAuthorizeThisStage(string stage, string kind)
    {
        var run = await StartRunAtDocumentReviewAsync();
        var service = CreateProvider().GetRequiredService<IWorkflowRunService>();

        // Each wrong-kind/wrong-stage case independently has both required verdicts;
        // a missing verdict cannot mask a faulty artifact selector.
        var artifact = await RecordForeignArtifactAsync(run.Id, stage, kind, "owned foreign artifact");
        Assert.True(await new WorkflowBlobStore(_database.Root).VerifyBlobAsync(artifact.BlobId));

        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ReviewerRole, artifact.HashSha256));
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ArchitectRole, artifact.HashSha256));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AdvanceStageAsync(run.Id, "documents reviewed"));

        Assert.Contains(ArtifactKind, exception.Message, StringComparison.Ordinal);
        Assert.Equal(StageId, (await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(run.Id))!.CurrentStageId);
    }

    [Fact]
    public async Task AnApprovalOnlyStageIsAuthorizedByItsOwnArtifact()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var service = CreateProvider().GetRequiredService<IWorkflowRunService>();
        var reviewed = await RecordArtifactAsync(run.Id, "reviewed bundle");

        await service.RecordLegacyUnlinkedReviewerVerdictAsync(run.Id, Verdict(WorkflowScheme.ReviewerRole, reviewed.HashSha256));
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(run.Id, Verdict(WorkflowScheme.ArchitectRole, reviewed.HashSha256));
        run = await service.AdvanceStageAsync(run.Id, "documents reviewed");

        Assert.Equal(WorkflowScheme.UserApprovalStageId, run.CurrentStageId);

        // The approval stage declares no reviewers, so the only thing that can authorize it is the artifact
        // it recorded and an approval that pins its hash.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AdvanceStageAsync(run.Id, "approved without an approval"));

        var evidence = await RecordArtifactAtAsync(
            run.Id,
            WorkflowScheme.UserApprovalStageId,
            "ApprovedDocument",
            "approved document");

        await service.RecordUserApprovalAsync(run.Id, Approval(evidence.HashSha256, run.StartedAtUtc.AddMinutes(1)));

        var advanced = await service.AdvanceStageAsync(run.Id, "approved");

        var transition = advanced.Transitions[^1];
        Assert.Empty(transition.ReviewerVerdicts);
        Assert.Equal(evidence.ArtifactId, transition.AuthorizingArtifactId);
        Assert.Equal(WorkflowScheme.ImplementationPackagesStageId, advanced.CurrentStageId);
    }

    [Fact]
    public async Task ADuplicateArtifactInsertPreservesThePreviouslyCommittedAuthorizingEvidence()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);
        var service = CreateProvider().GetRequiredService<IWorkflowRunService>();

        // The duplicate insert must fail without replacing the artifact that was already committed.
        // Real changed-run rollback coverage is in WorkflowRunRepositoryTests.AFailedArtifactInsertRollsBackTheRunRowWithIt.
        var committed = await RecordArtifactAsync(run.Id, "first revision");

        var before = await ReadSingleAsync("SELECT COUNT(*) FROM Artifacts WHERE WorkflowRunId = $id;", ("$id", run.Id));

        await Assert.ThrowsAsync<SqliteException>(() => repository.SaveArtifactAsync(
            run,
            new WorkflowArtifactEvidence(
                committed.ArtifactId,
                run.Id,
                StageId,
                ArtifactKind,
                committed.BlobId,
                committed.BlobId,
                AssignedAt.AddHours(1),
                committed.SizeBytes,
                DataClassification.PrivateSource)));

        Assert.Equal(
            before,
            await ReadSingleAsync("SELECT COUNT(*) FROM Artifacts WHERE WorkflowRunId = $id;", ("$id", run.Id)));

        // The persisted run still has its original evidence; later verdicts may legitimately authorize it.
        var persisted = await repository.GetByIdAsync(run.Id);
        Assert.Equal(committed.ArtifactId, SingleArtifact(persisted!).ArtifactId);
        Assert.Equal(StageId, persisted!.CurrentStageId);

        await service.RecordLegacyUnlinkedReviewerVerdictAsync(run.Id, Verdict(WorkflowScheme.ReviewerRole, committed.HashSha256));
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(run.Id, Verdict(WorkflowScheme.ArchitectRole, committed.HashSha256));

        var advanced = await service.AdvanceStageAsync(run.Id, "the bundle was reviewed");

        Assert.Equal(committed.ArtifactId, advanced.Transitions[^1].AuthorizingArtifactId);
    }

    [Fact]
    public async Task ASchemaRejectedArtifactStatementLeavesExistingRunUnchanged()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var repository = new SqliteWorkflowRunRepository(_database.Factory);

        // A row the schema trigger refuses: the recorded hash is not the recorded blob id, so it names
        // content that cannot be found. It cannot be constructed as evidence, so the refusal is provoked on
        // the raw statement instead - which is exactly what a compromised or buggy writer would produce.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            """
            INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
            VALUES ('artifact-mismatch', $run, $stage, $kind, $blob, $other, 12, 'PrivateSource', $now);
            """,
            ("$run", run.Id),
            ("$stage", StageId),
            ("$kind", ArtifactKind),
            ("$blob", "sha256:" + new string('a', 64)),
            ("$other", "sha256:" + new string('b', 64)),
            ("$now", "2026-09-29T00:00:00Z")));

        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM Artifacts WHERE Id = 'artifact-mismatch';"));

        // Only the rejected artifact statement was executed; the existing run stage and transitions remain untouched.
        var persisted = await repository.GetByIdAsync(run.Id);
        Assert.Equal(StageId, persisted!.CurrentStageId);
        Assert.Equal(4, persisted.Transitions.Count);
    }

    [Fact]
    public async Task AGateFreePinnedRunStillAdvancesWithoutRecordingAnyArtifact()
    {
        var provider = CreateProvider();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        await templateStore.SaveAsync(CreateLinearTemplate(1));
        await templateStore.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-1",
            ProjectId,
            "linear-template",
            1,
            AssignedAt));

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);
        var advanced = await runService.AdvanceStageAsync(run.Id, "gate-free transition");

        Assert.Equal("node-b", advanced.CurrentStageId);
        Assert.Empty(advanced.Artifacts);

        var transition = Assert.Single(advanced.Transitions);
        Assert.False(transition.HasAuthorizingArtifact);
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM Artifacts WHERE WorkflowRunId = $id;", ("$id", run.Id)));
    }

    /// <summary>
    /// The newest-artifact rule is only worth anything if an unreadable newest row cannot be stepped over.
    ///
    /// 006 validated the stage, the kind, the blob and the hash of a new run-scoped row, and not the row's
    /// own identifier, so a writer could commit a row of this run, this stage and this kind whose bytes are
    /// real and whose id is whitespace. Skipping such a row does not make the stage unauthorized: it makes
    /// the *previous* artifact the newest one, and a verdict already recorded against the previous bytes
    /// then authorizes a transition over a document that was superseded and never looked at.
    ///
    /// The row is committed under the 006 schema, before 007 exists, so the failure cannot be attributed to
    /// the new trigger. What the upgrade may do is leave that row exactly where it is - the read path is what
    /// has to refuse the run.
    /// </summary>
    [Fact]
    public async Task ANewerUnnamedRunScopedRowRefusesTheRunInsteadOfPromotingTheOlderArtifact()
    {
        using var pre007 = new TestDatabase();

        await new DatabaseMigrator(
                pre007.Factory,
                DatabaseMigrator.LoadEmbeddedMigrations().Where(migration => migration.Version < 7).ToArray())
            .MigrateAsync();
        await SeedAsync(pre007);

        var provider = CreateProvider(pre007.Factory, pre007.Root);
        var service = provider.GetRequiredService<IWorkflowRunService>();
        var blobStore = new WorkflowBlobStore(pre007.Root);

        var run = await StartRunAtDocumentReviewAsync(provider);
        var approved = await RecordArtifactAtAsync(
            provider,
            run.Id,
            StageId,
            ArtifactKind,
            "the revision both reviewers signed off");

        // The control. With only this row in the table it is the current artifact of the stage and its bytes
        // verify, so the verdicts recorded below really are an authorization that the gate would honour.
        var before = await new SqliteWorkflowRunRepository(pre007.Factory).GetByIdAsync(run.Id);
        Assert.Equal(
            approved.ArtifactId,
            WorkflowArtifactEvidence.SelectCurrent(before!.Artifacts, run.Id, StageId, ArtifactKind)!.ArtifactId);
        Assert.True(await blobStore.VerifyBlobAsync(approved.BlobId));

        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ReviewerRole, approved.HashSha256));
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            Verdict(WorkflowScheme.ArchitectRole, approved.HashSha256));

        // The superseding revision. Its bytes are stored and its hash is the hash of those bytes, so the row is
        // complete in every respect 006 validates; only its own identifier is missing, and it is newer than
        // the approved row.
        var superseded = await StoreUnnamedRunScopedArtifactAsync(
            pre007,
            run.Id,
            StageId,
            ArtifactKind,
            "the revision nobody reviewed",
            "2099-01-01T00:00:00Z");

        Assert.NotEqual(approved.BlobId, superseded);

        // The upgrade to 007 must not touch 006-era data. Refusing the load is the repository's job, and
        // refusing it by rewriting or deleting the row would destroy the evidence of what happened.
        var report = await new DatabaseMigrator(pre007.Factory).MigrateAsync();
        Assert.Equal(6, report.PreviousSchemaVersion);
        Assert.Equal(DatabaseMigrator.LoadEmbeddedMigrations().Max(m => m.Version), report.CurrentSchemaVersion);
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
            "ReviewerInsertObservationAuthority", "ProviderHeaders", "WorkflowReviewTerminalRetries", "ProviderSecretDeletionQueue", "ModelRouteHealthIdentity", "HealthAuthenticationFanout", "ArtifactExecutionAssociation", "ArtifactExecutionChronology", "workflow_review_responses", "workflow_material_policy", "workflow_adaptation_transport", "adaptation_runtime_ownership", "account_credential_ownership", "opencode_dispatch_decision", "model_capability_invalidation", "model_capability_context", "project_data_policy_audit"
            },
            report.AppliedMigrations.Select(migration => migration.Name));
        Assert.Equal(
            "1",
            await ReadSingleAsync(
                pre007.Factory,
                "SELECT COUNT(*) FROM Artifacts WHERE WorkflowRunId = $id AND Id = '   ';",
                ("$id", run.Id)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.AdvanceStageAsync(run.Id, "the signed-off revision was reviewed"));

        // The refusal names the row, the run, the stage and the kind, so the operator can find it - and names
        // neither the stored content nor the hash of it.
        Assert.Contains(run.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains(StageId, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ArtifactKind, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(approved.HashSha256, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(superseded, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nobody reviewed", exception.Message, StringComparison.Ordinal);

        // The same refusal reaches a caller that only loads the run, which is what makes it a fail-closed read
        // rather than a check that happens to sit in front of one entry point.
        await Assert.ThrowsAsync<InvalidDataException>(
            () => new SqliteWorkflowRunRepository(pre007.Factory).GetByIdAsync(run.Id));

        // And nothing was advanced: the run is still at the stage it was reviewed at, with the same four
        // transitions and no artifact of its own recorded against the transition.
        var persisted = await ReadSingleAsync(
            pre007.Factory,
            "SELECT EvidenceRedactedJson FROM WorkflowRuns WHERE Id = $id;",
            ("$id", run.Id));
        Assert.Contains($"\"currentStageId\":\"{StageId}\"", persisted, StringComparison.Ordinal);
        Assert.Equal("4", await ReadSingleAsync(
            pre007.Factory,
            "SELECT json_array_length(EvidenceRedactedJson, '$.transitions') FROM WorkflowRuns WHERE Id = $id;",
            ("$id", run.Id)));
    }

    /// <summary>
    /// Commits a run-scoped artifact row of exactly the shape a pre-007 writer could produce: real bytes in
    /// the blob store, a matching blob id and hash, the stage and kind the gate looks for - and an identifier
    /// that is only whitespace. It goes in as raw SQL because the value object refuses to be constructed this
    /// way, which is the whole point: the row can only reach the database from a writer that bypasses it.
    /// </summary>
    private static async Task<string> StoreUnnamedRunScopedArtifactAsync(
        TestDatabase database,
        string runId,
        string stageId,
        string kind,
        string content,
        string createdAtUtc)
    {
        var blobStore = new WorkflowBlobStore(database.Root);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);

        string blobId;

        await using (var stream = new MemoryStream(bytes, writable: false))
        {
            blobId = (await blobStore.SaveBlobAsync(stream)).BlobId;
        }

        Assert.True(await blobStore.VerifyBlobAsync(blobId));

        await ExecuteAsync(
            database.Factory,
            """
            INSERT INTO Artifacts (Id, WorkflowRunId, StageId, Kind, BlobId, HashSha256, SizeBytes, DataClassification, CreatedAtUtc)
            VALUES ('   ', $run, $stage, $kind, $blob, $blob, $size, 'PrivateSource', $now);
            """,
            ("$run", runId),
            ("$stage", stageId),
            ("$kind", kind),
            ("$blob", blobId),
            ("$size", bytes.Length),
            ("$now", createdAtUtc));

        return blobId;
    }

    private async Task SeedAsync()
    {
        await _database.InitializeAsync();
        await SeedAsync(_database);
    }

    /// <summary>
    /// The project, package and version every run here belongs to. A run is only loadable when the rows it
    /// names exist, so this is written against whichever database the caller is about to use.
    /// </summary>
    private static async Task SeedAsync(TestDatabase database)
    {
        await ExecuteAsync(
            database.Factory,
            """
            INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, 'Project', 'C:\project', 'PrivateSource', $now, $now);
            INSERT INTO WorkflowPackages (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($package, 'package-1.zip', 'Imported', 'hash-1', 'blob-1', $now, $now);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($version, $package, 1, 'blob-1', 'hash-1', 'Imported', $now);
            """,
            ("$id", ProjectId),
            ("$package", PackageId),
            ("$version", VersionId),
            ("$now", "2026-09-29T00:00:00Z"));
    }

    /// <summary>
    /// A legacy run walked to the document review stage of the standard scheme, which is a stage that declares
    /// two required reviewers and the artifact kind the review is about.
    /// </summary>
    private Task<WorkflowRun> StartRunAtDocumentReviewAsync() =>
        StartRunAtDocumentReviewAsync(CreateProvider());

    private static async Task<WorkflowRun> StartRunAtDocumentReviewAsync(ServiceProvider provider)
    {
        var service = provider.GetRequiredService<IWorkflowRunService>();
        var run = await service.StartLegacyRunAsync(ProjectId, PackageId, VersionId);

        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();

        foreach (var stageId in new[]
                 {
                     WorkflowScheme.TaskSpecificationStageId,
                     WorkflowScheme.ArchitectureStageId,
                     WorkflowScheme.TechnicalSpecificationStageId,
                     WorkflowScheme.RoadmapStageId
                 })
        {
            var stage = scheme.GetRequiredStage(stageId);

            run = await service.RecordStageArtifactAsync(
                run.Id,
                stage.StageId,
                stage.ArtifactRequirement!,
                Content($"artifact of '{stage.StageId}'"),
                DataClassification.PrivateSource);

            run = await service.AdvanceStageAsync(run.Id, $"left {stage.StageId}");
        }

        Assert.Equal(StageId, run.CurrentStageId);

        return run;
    }

    private Task<WorkflowArtifactEvidence> RecordArtifactAtAsync(
        string runId,
        string stageId,
        string kind,
        string content) =>
        RecordArtifactAtAsync(CreateProvider(), runId, stageId, kind, content);

    private static async Task<WorkflowArtifactEvidence> RecordArtifactAtAsync(
        ServiceProvider provider,
        string runId,
        string stageId,
        string kind,
        string content)
    {
        var service = provider.GetRequiredService<IWorkflowRunService>();
        var run = await service.RecordStageArtifactAsync(
            runId,
            stageId,
            kind,
            Content(content),
            DataClassification.PrivateSource);

        return WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, runId, stageId, kind)!;
    }

    /// <summary>Records another artifact for the document review stage and returns the newest one of them.</summary>
    private Task<WorkflowArtifactEvidence> RecordArtifactAsync(string runId, string content) =>
        RecordArtifactAtAsync(CreateProvider(), runId, StageId, ArtifactKind, content);

    private static IReadOnlyList<WorkflowArtifactEvidence> StageArtifacts(WorkflowRun run) =>
        run.Artifacts
            .Where(artifact => string.Equals(artifact.StageId, StageId, StringComparison.Ordinal))
            .ToArray();

    private static WorkflowArtifactEvidence SingleArtifact(WorkflowRun run) =>
        Assert.Single(StageArtifacts(run));

    /// <summary>
    /// A complete, committed and byte-verified artifact row of a kind or a stage the gate does not look at.
    /// It is written the way the repository writes one, so it is indistinguishable from real evidence - which
    /// is what makes the refusal of it meaningful.
    /// </summary>
    private async Task<WorkflowArtifactEvidence> RecordForeignArtifactAsync(
        string runId,
        string stageId,
        string kind,
        string content)
    {
        var blobStore = new WorkflowBlobStore(_database.Root);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);

        string blobId;

        await using (var stream = new MemoryStream(bytes, writable: false))
        {
            blobId = (await blobStore.SaveBlobAsync(stream)).BlobId;
        }

        var evidence = new WorkflowArtifactEvidence(
            $"foreign-{stageId}-{kind}",
            runId,
            stageId,
            kind,
            blobId,
            blobId,
            AssignedAt.AddHours(2),
            bytes.Length,
            DataClassification.PrivateSource);

        var loaded = await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(runId);
        await new SqliteWorkflowRunRepository(_database.Factory)
            .SaveArtifactAsync(loaded!, evidence);

        return evidence;
    }

    private ServiceProvider CreateProvider() => CreateProvider(_database.Factory, _database.Root);

    private async Task<ServiceProvider> ReopenDatabaseProviderAsync()
    {
        await using (var connection = await _database.Factory.OpenConnectionAsync())
            SqliteConnection.ClearPool(connection); // Only this test's unique connection string/pool.
        return CreateProvider(new SqliteConnectionFactory(_database.Factory.DatabasePath), _database.Root);
    }

    private static ServiceProvider CreateProvider(ISqliteConnectionFactory factory, string blobRoot)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<ISqliteConnectionFactory>(factory);
        services.AddSingleton(new WorkflowBlobStore(blobRoot));
        services.AddWorkflowServices();

        return services.BuildServiceProvider();
    }

    private static Stream Content(string text) =>
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text), writable: false);

    private static ReviewerVerdictRecord Verdict(string role, string documentHash) =>
        new(
            role,
            "route-opencode",
            documentHash,
            WorkflowReviewVerdict.Approve,
            "reviewed the stored artifact",
            AssignedAt.AddHours(3));

    private static UserApprovalEvidence Approval(string hash, DateTimeOffset decidedAt) =>
        new(
            "approval-1",
            "user-1",
            WorkflowScheme.UserApprovalStageId,
            hash,
            UserApprovalDecision.Approved,
            "approved",
            decidedAt);

    private static WorkflowTemplateDefinition CreateLinearTemplate(int version) =>
        new(
            "linear-template",
            version,
            "Linear template",
            "A gate-free linear template.",
            new WorkflowGraph(
                "node-a",
                new[]
                {
                    new WorkflowNodeDefinition(
                        "node-a",
                        WorkflowNodeKind.Prompt,
                        "Node A",
                        "Role A",
                        successTargetNodeId: "node-b"),
                    new WorkflowNodeDefinition(
                        "node-b",
                        WorkflowNodeKind.TerminalOutcome,
                        "Node B",
                        "Role B")
                }),
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            AssignedAt);

    private Task<string> ReadSingleAsync(string sql, params (string Name, object? Value)[] parameters) =>
        ReadSingleAsync(_database.Factory, sql, parameters);

    private static async Task<string> ReadSingleAsync(
        ISqliteConnectionFactory factory,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? string.Empty : Convert.ToString(value)!;
    }

    private Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters) =>
        ExecuteAsync(_database.Factory, sql, parameters);

    private static async Task ExecuteAsync(
        ISqliteConnectionFactory factory,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
