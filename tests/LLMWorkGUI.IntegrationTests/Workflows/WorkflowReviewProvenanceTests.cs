using System.Text;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using LLMWorkGUI.Infrastructure.Workflows.Channels;
using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// The durable side of the model-review provenance: what migration 008 admits, what the reviewer-evidence
/// store reads back, and what the whole stack does when a reviewer execution is cancelled, ambiguous,
/// mismatched or lost.
/// <para>
/// Everything here runs against a real migrated SQLite database and the real blob store. That is the point:
/// the whole reason a verdict can no longer stand in for a model review is that the run, stage, role, route
/// and artifact it is about are bound by a row with real foreign keys, and a fake store would not prove
/// that the row can be written at all.
/// </para>
/// </summary>
public sealed partial class WorkflowReviewProvenanceTests : IDisposable
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionId = "version-1";
    private const string TemplateId = "review-template";
    private const string StageId = "node-a";
    private const string TerminalStageId = "node-b";
    private const string ArtifactKind = "ReviewedDocument";
    private const string ReviewerRole = "Reviewer";
    private const string RealRouteId = "route-real";
    private const string OtherRouteId = "route-other";
    private const string DuplicateRouteId = "route-duplicate-tuple";
    private const string DocumentedDefaultRouteId = "route-opencode";

    private const string ProfileId = "profile-1";
    private const string AccountId = "account-1";
    private const string ModelId = "model-1";

    private static readonly DateTimeOffset AssignedAt = new(2026, 9, 29, 7, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();

    public WorkflowReviewProvenanceTests() =>
        SeedAsync().GetAwaiter().GetResult();

    private WorkflowReviewProvenanceTests(bool legacy) =>
        SeedAsync(legacy).GetAwaiter().GetResult();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public void TheMigrationAddsTheBindingTableAndItsTwoTriggers()
    {
        var migration = DatabaseMigrator.LoadEmbeddedMigrations()
            .Single(candidate => candidate.Name == "WorkflowReviewExecutionProvenance");

        Assert.Equal(8, migration.Version);
        Assert.Contains("CREATE TABLE WorkflowReviewExecutions (", migration.Sql, StringComparison.Ordinal);
        Assert.Contains(
            "TR_WorkflowReviewExecutions_IdentityIsComplete",
            migration.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "TR_WorkflowReviewExecutions_BindingIsImmutable",
            migration.Sql,
            StringComparison.Ordinal);

        // Additive in the strict sense: a new table, two indexes and two triggers, and nothing that rewrites
        // or drops what an already-migrated database holds.
        Assert.DoesNotContain("DROP", migration.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", migration.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABindingRoundTripsWithItsObservedRouteAndItsExecutionState()
    {
        await StartRunAsync(CreateProvider(), RealRouteId);
        var evidenceRepository = CreateProvider().GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var executionId = await PersistExecutionAsync(evidenceRepository, ExecutionState.Succeeded, observedRouteId: RealRouteId);

        var evidence = await evidenceRepository.GetByExecutionIdAsync(executionId);

        Assert.NotNull(evidence);
        Assert.Equal(executionId, evidence!.ExecutionId);
        Assert.Equal(RealRouteId, evidence.ObservedRouteId);
        Assert.Equal(RealRouteId, evidence.RequestedRouteId);
        Assert.True(evidence.IsReadOnly);
        Assert.Equal(ExecutionState.Succeeded, evidence.ExecutionState);
        Assert.Equal(StageId, evidence.StageId);
        Assert.Equal(ReviewerRole, evidence.ReviewerRole);
        Assert.Equal(DocumentHash(await CurrentArtifactAsync()), evidence.ReviewedArtifactHash);
    }

    [Fact]
    public async Task ABindingIsWrittenBeforeTheTurnAndAGuessAboutItChangesNothing()
    {
        var provider = CreateProvider();
        await StartRunAsync(provider, RealRouteId);
        var evidenceRepository = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var executionId = await PersistExecutionAsync(evidenceRepository, ExecutionState.Queued, observedRouteId: null);

        // Persisted before dispatch: the requested route is there and the observation is not.
        var queued = await evidenceRepository.GetByExecutionIdAsync(executionId);
        Assert.Equal(RealRouteId, queued!.RequestedRouteId);
        Assert.Null(queued.ObservedRouteId);

        // The binding's run, stage, role, artifact, requested route and read-only flag are immutable, so a
        // call that tries to re-point one of them updates nothing at all - the immutability trigger refuses
        // the statement and the store's own UPDATE list does not mention those columns.
        await evidenceRepository.UpdateObservedAsync(
            queued.WithObservedOutcome(null, ExecutionState.Ambiguous).WithIdentity("another-run"));

        var unchanged = await evidenceRepository.GetByExecutionIdAsync(executionId);
        Assert.Equal(RunId, unchanged!.WorkflowRunId);
        Assert.Equal(StageId, unchanged.StageId);
        Assert.Equal(ReviewerRole, unchanged.ReviewerRole);
        Assert.Equal(RealRouteId, unchanged.RequestedRouteId);
        Assert.True(unchanged.IsReadOnly);
    }

    [Fact]
    public async Task AnAmbiguousDeliveryLeavesARealRowThatAuthorizesNothing()
    {
        // The state a gate reads is the state of the execution row, not a copy on the binding, so an
        // uncertain delivery is expressed by the execution being ambiguous and the observation being absent.
        var provider = CreateProvider();
        await StartRunAsync(provider, RealRouteId);
        var evidenceRepository = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var executionId = await PersistExecutionAsync(evidenceRepository, ExecutionState.Queued, observedRouteId: null);
        var artifact = await CurrentArtifactAsync();

        var session = (await provider.GetRequiredService<ISessionRepository>().GetByIdAsync(
            (await provider.GetRequiredService<IExecutionRepository>().GetByIdAsync(executionId))!.SessionId))!;

        await provider.GetRequiredService<IExecutionRepository>().UpsertAsync(
            new Execution(
                executionId,
                session.Id,
                executionId,
                ExecutionState.Ambiguous,
                ExecutionFailureReason.InternalError,
                RealRouteId,
                null,
                retryOfExecutionId: null,
                processState: null,
                exitCode: null,
                terminationReason: null,
                Array.Empty<string>(),
                artifact.HashSha256,
                artifact.HashSha256,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

        var evidence = await evidenceRepository.GetByExecutionIdAsync(executionId);

        Assert.NotNull(evidence);
        Assert.Equal(ExecutionState.Ambiguous, evidence!.ExecutionState);
        Assert.Null(evidence.ObservedRouteId);
        Assert.Equal(RealRouteId, evidence.RequestedRouteId);
    }

    [Fact]
    public async Task TheStoreRefusesASecondBindingForTheSameRunStageRoleAndHash()
    {
        await StartRunAsync(CreateProvider(), RealRouteId);
        var evidenceRepository = CreateProvider().GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var executionId = await PersistExecutionAsync(evidenceRepository, ExecutionState.Succeeded, observedRouteId: RealRouteId);
        var artifact = await CurrentArtifactAsync();

        var refusal = await Assert.ThrowsAnyAsync<Exception>(
            () => evidenceRepository.SaveAsync(
                new ReviewerExecutionEvidence(
                    Guid.NewGuid().ToString("N"),
                    "session-2",
                    RunId,
                    ReviewerRole,
                    StageId,
                    RealRouteId,
                    observedRouteId: null,
                    artifact.ArtifactId,
                    artifact.HashSha256,
                    isReadOnly: true,
                    ExecutionState.Queued)));

        Assert.NotNull(refusal);
        Assert.Equal(1L, await _database.CountAsync("WorkflowReviewExecutions"));
    }

    [Fact]
    public async Task TheProductCompositionRefusesTheDocumentedRouteOpenCodeLabel()
    {
        // The route the shipped Studio template binds to every role has never been a Routes row. The request
        // refuses it by name, and the refusals are proven by the row counts: an execution whose requested
        // route is that label could not be written at all.
        var provider = CreateProvider();
        var runId = await StartRunAsync(provider, DocumentedDefaultRouteId);

        var result = await provider.GetRequiredService<IWorkflowReviewRequestService>()
            .RequestAssignedReviewAsync(runId);

        Assert.True(result.IsRefusal);
        var role = Assert.Single(result.Roles);
        Assert.Equal(DocumentedDefaultRouteId, role.AssignedRouteId);
        Assert.Contains("is not a persisted route", role.Refusal!, StringComparison.Ordinal);

        Assert.Equal(0, await _database.CountAsync("Sessions"));
        Assert.Equal(0, await _database.CountAsync("Executions"));
        Assert.Equal(0, await _database.CountAsync("WorkflowReviewExecutions"));
    }

    [Fact]
    public async Task TheProductCompositionRefusesARealRouteBecauseNoProductionChannelObservesOne()
    {
        // The route is real, the identities resolve, the bytes verify - and the request still refuses, because
        // nothing in this build can identify which persisted route a gateway served. That is the honest
        // production state, and the refusal has to say which identity is missing rather than leaving an
        // operator to wonder whether a component is broken.
        var provider = CreateProvider();
        var runId = await StartRunAsync(provider, RealRouteId);

        var result = await provider.GetRequiredService<IWorkflowReviewRequestService>()
            .RequestAssignedReviewAsync(runId);

        Assert.True(result.IsRefusal);
        var role = Assert.Single(result.Roles);
        Assert.Equal(RealRouteId, role.AssignedRouteId);
        Assert.Null(role.ExecutionId);
        Assert.Null(role.ObservedRouteId);
        Assert.False(role.ObservedAssignedRoute);

        var refusal = role.Refusal!;
        Assert.Contains("No channel with proven capabilities", refusal, StringComparison.Ordinal);

        // Storage exists; the channel still needs native per-turn proof and terminal confirmation.
        Assert.Contains(StarCliProxyReviewReadOnlyChannel.ChannelIdValue, refusal, StringComparison.Ordinal);
        Assert.Contains("native identity storage and a strict route resolver exist", refusal, StringComparison.Ordinal);
        Assert.Contains("trusted per-turn response-origin proof", refusal, StringComparison.Ordinal);
        Assert.Contains("confirmed terminal outcome", refusal, StringComparison.Ordinal);

        // And the product line reports a refusal, not a dispatch.
        Assert.Equal("Отказ", result.StateDisplay);
        Assert.Equal(0, result.DispatchedRoleCount);
        Assert.Contains("nothing was dispatched and nothing was recorded", result.Detail, StringComparison.Ordinal);

        Assert.Equal(0, await _database.CountAsync("Sessions"));
        Assert.Equal(0, await _database.CountAsync("Executions"));
        Assert.Equal(0, await _database.CountAsync("WorkflowReviewExecutions"));
    }

    [Fact]
    public async Task TheProductCompositionRefusesARouteRowWhoseBackendThisBuildDoesNotKnow()
    {
        // A row that names a backend this build does not have used to be read as - and dispatched as - an
        // OpenCode route, with nothing anywhere recording the substitution. It now refuses, and the refusal
        // is a refusal: nothing is written and the route is never read as a different backend.
        var provider = CreateProvider();
        var runId = await StartRunAsync(provider, RealRouteId);

        await SetRouteBackendAsync(RealRouteId, "SomeFutureBackend");

        var routeRepository = provider.GetRequiredService<IRouteRepository>();
        var read = await Assert.ThrowsAsync<InvalidDataException>(
            () => routeRepository.GetAssignmentAsync(RealRouteId));
        Assert.Contains("SomeFutureBackend", read.Message, StringComparison.Ordinal);
        Assert.Contains("is not a backend this build knows", read.Message, StringComparison.Ordinal);

        // A label is still simply absent, so the two refusals stay distinguishable.
        Assert.Null(await routeRepository.GetAssignmentAsync(DocumentedDefaultRouteId));

        var result = await provider.GetRequiredService<IWorkflowReviewRequestService>()
            .RequestAssignedReviewAsync(runId);

        Assert.True(result.IsRefusal);
        var role = Assert.Single(result.Roles);
        Assert.Equal(RealRouteId, role.AssignedRouteId);
        Assert.Contains("cannot be read", role.Refusal!, StringComparison.Ordinal);
        Assert.Contains("nothing is dispatched", role.Refusal!, StringComparison.Ordinal);
        Assert.Null(role.ExecutionId);
        Assert.Null(role.ObservedRouteId);

        Assert.Equal(0, await _database.CountAsync("Sessions"));
        Assert.Equal(0, await _database.CountAsync("Executions"));
        Assert.Equal(0, await _database.CountAsync("WorkflowReviewExecutions"));
    }

    [Fact]
    public async Task TwoPersistedRoutesSharingOneTupleStillRefuseTheReview()
    {
        // The concrete shape of the missing uniqueness: two Routes rows with the same provider profile, the
        // same account and the same model, differing only in their mode dimensions. Neither can be proven to
        // be the route a gateway served, so a review over either is refused and nothing is written.
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Routes (
                    Id, Backend, ProviderProfileId, AccountId, ModelId, ReasoningEffort, SpeedMode, ExecutionMode,
                    MaxDataClass, Health, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($route, 'OpenCode', $profile, $account, $model, 'high', NULL, NULL,
                    'PrivateSource', 'Healthy', $now, $now);
                """;
            command.Parameters.AddWithValue("$route", DuplicateRouteId);
            command.Parameters.AddWithValue("$profile", ProfileId);
            command.Parameters.AddWithValue("$account", AccountId);
            command.Parameters.AddWithValue("$model", ModelId);
            command.Parameters.AddWithValue("$now", "2026-09-29T00:00:00Z");

            await command.ExecuteNonQueryAsync();
        }

        var provider = CreateProvider();
        var runId = await StartRunAsync(provider, RealRouteId);

        var result = await provider.GetRequiredService<IWorkflowReviewRequestService>()
            .RequestAssignedReviewAsync(runId);

        Assert.True(result.IsRefusal);
        var role = Assert.Single(result.Roles);
        Assert.Equal(RealRouteId, role.AssignedRouteId);

        // Both rows read as complete, real, enabled routes - and the reviewer is still refused, because a
        // complete row is not an observed route.
        var routeRepository = provider.GetRequiredService<IRouteRepository>();
        Assert.True((await routeRepository.GetAssignmentAsync(RealRouteId))!.HasEveryIdentity);
        Assert.True((await routeRepository.GetAssignmentAsync(DuplicateRouteId))!.HasEveryIdentity);

        Assert.Equal(0, await _database.CountAsync("Sessions"));
        Assert.Equal(0, await _database.CountAsync("Executions"));
        Assert.Equal(0, await _database.CountAsync("WorkflowReviewExecutions"));
    }

    [Fact]
    public async Task TheRouteRepositoryDistinguishesAnAbsentRowFromAMissingIdentity()

    {
        var provider = CreateProvider();
        var routeRepository = provider.GetRequiredService<IRouteRepository>();

        // A label is not a row, and the reader says so rather than inventing one.
        Assert.Null(await routeRepository.GetAssignmentAsync(DocumentedDefaultRouteId));

        var complete = await routeRepository.GetAssignmentAsync(RealRouteId);
        Assert.NotNull(complete);
        Assert.True(complete!.HasEveryIdentity);
        Assert.Equal(AccountId, complete.Route.Binding.AccountId);

        // A row whose model was removed outside this process is reported with the identity that is gone.
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF; DELETE FROM Models WHERE Id = $model;";
            command.Parameters.AddWithValue("$model", ModelId);
            await command.ExecuteNonQueryAsync();
        }

        var incomplete = await routeRepository.GetAssignmentAsync(RealRouteId);
        Assert.NotNull(incomplete);
        Assert.False(incomplete!.HasEveryIdentity);
        Assert.Equal(new[] { WorkflowRouteIdentity.Model }, incomplete.MissingIdentities);
    }

    [Fact]
    public void TheNinthMigrationMakesTheExecutionRowTheAuthorityForAnObservedRoute()
    {
        var migration = DatabaseMigrator.LoadEmbeddedMigrations()
            .Single(candidate => candidate.Name == "WorkflowReviewObservedRouteAuthority");

        Assert.Equal(9, migration.Version);
        Assert.Contains(
            "TR_WorkflowReviewExecutions_ObservedRouteIsObserved",
            migration.Sql,
            StringComparison.Ordinal);

        // Additive in the strict sense, so migration 008 keeps the exact text an already migrated database
        // recorded as its checksum: one trigger, and nothing that creates, alters, rewrites or drops.
        Assert.DoesNotContain("DROP", migration.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", migration.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", migration.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheBindingColumnCannotBeGivenAnObservedRouteItsExecutionDidNotRecord()
    {
        // The database-level half of the rule, proven against SQL rather than against a store method, because
        // the whole point is that the row is unreachable for anyone who writes the statement themselves.
        var provider = CreateProvider();
        await StartRunAsync(provider, RealRouteId);
        var evidenceRepository = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var executionId = await PersistExecutionAsync(evidenceRepository, ExecutionState.Succeeded, observedRouteId: null);

        Assert.Null((await evidenceRepository.GetByExecutionIdAsync(executionId))!.ObservedRouteId);

        await using (var connection = await _database.Factory.OpenConnectionAsync())
        {
            await Assert.ThrowsAsync<SqliteException>(
                () => SetBindingObservedRouteAsync(connection, executionId, RealRouteId));
        }

        // Refused is refused: the statement aborted and the column is exactly as it was.
        Assert.Null((await evidenceRepository.GetByExecutionIdAsync(executionId))!.ObservedRouteId);

        // The same statement is accepted for a route the execution did record, so what refuses the first one
        // is the disagreement with the execution row and not the column, the table or the foreign key.
        await SetExecutionObservationAsync(executionId, OtherRouteId);

        await using (var connection = await _database.Factory.OpenConnectionAsync())
        {
            await SetBindingObservedRouteAsync(connection, executionId, OtherRouteId);
        }

        Assert.Equal(OtherRouteId, await BindingObservedRouteAsync(executionId));

        // A binding column that is set back to nothing is still legal - the foreign key's ON DELETE SET NULL
        // has to be able to do it - and the read then reports nothing at all, so the rule cannot manufacture
        // an observation by refusing either direction.
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        {
            await SetBindingObservedRouteAsync(connection, executionId, observedRouteId: null);
        }

        Assert.Null(await BindingObservedRouteAsync(executionId));

        await SetExecutionObservationAsync(executionId, observedRouteId: null);

        Assert.Null((await evidenceRepository.GetByExecutionIdAsync(executionId))!.ObservedRouteId);
    }

    [Fact]
    public async Task TheStoreRefusesToPromoteAnObservedRouteTheExecutionDidNotRecord()
    {
        // The same rule one layer up, and the direction that matters: a succeeded execution that reported
        // nothing, and a caller that nonetheless hands the binding the requested route.
        var provider = CreateProvider();
        await StartRunAsync(provider, RealRouteId);
        var evidenceRepository = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var executionId = await PersistExecutionAsync(evidenceRepository, ExecutionState.Succeeded, observedRouteId: null);

        var binding = (await evidenceRepository.GetByExecutionIdAsync(executionId))!;

        await evidenceRepository.UpdateObservedAsync(
            binding.WithObservedOutcome(RealRouteId, ExecutionState.Succeeded));

        // A no-op, not a correction: the binding is left unobserved, which is the fail-closed answer, and the
        // read reports the execution's own column rather than anything the caller passed in.
        var unchanged = await evidenceRepository.GetByExecutionIdAsync(executionId);
        Assert.Null(unchanged!.ObservedRouteId);
        Assert.Equal(RealRouteId, unchanged.RequestedRouteId);
        Assert.Equal(ExecutionState.Succeeded, unchanged.ExecutionState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OtherRouteId)]
    public async Task AVerdictAndAnAdvanceBothRefuseWhenTheExecutionNeverObservedTheAssignedRoute(
        string? executionObservedRouteId)
    {
        // The tamper this whole rule exists for. A succeeded, read-only execution that the execution row says
        // never observed the assigned route - either because nothing was reported at all or because what was
        // reported was a different route - while a caller tries to make the binding say the assigned one
        // anyway. The binding column is what the caller can write; the gate must not be reading it.
        var provider = CreateProvider();
        await StartRunAsync(provider, RealRouteId);
        var evidenceRepository = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var executionId = await PersistExecutionAsync(
            evidenceRepository,
            ExecutionState.Succeeded,
            observedRouteId: executionObservedRouteId);

        var artifact = await CurrentArtifactAsync();

        // The caller's attempt, through the store and then through the database. Neither may promote it, and
        // neither may raise: a refused observation is written as nothing.
        var binding = (await evidenceRepository.GetByExecutionIdAsync(executionId))!;

        await evidenceRepository.UpdateObservedAsync(
            binding.WithObservedOutcome(RealRouteId, ExecutionState.Succeeded));

        await using (var connection = await _database.Factory.OpenConnectionAsync())
        {
            await Assert.ThrowsAsync<SqliteException>(
                () => SetBindingObservedRouteAsync(connection, executionId, RealRouteId));
        }

        // The verdict path refuses, and it refuses for the reason the evidence actually holds.
        var verdict = new ReviewerVerdictRecord(
            ReviewerRole,
            RealRouteId,
            artifact.HashSha256,
            WorkflowReviewVerdict.Approve,
            "the assigned model approved these bytes",
            AssignedAt.AddMinutes(5),
            executionId,
            StageId,
            artifact.ArtifactId);

        var refusal = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.RecordReviewerVerdictAsync(RunId, verdict));

        Assert.Contains(
            executionObservedRouteId is null
                ? "no backend reported an observed route"
                : $"the backend observed route '{OtherRouteId}'",
            refusal.Message,
            StringComparison.Ordinal);
        Assert.Empty((await CurrentRunAsync()).Verdicts);

        // The same read, taken from a fresh connection, is what the gate sees: it reports the execution's own
        // column, and the binding's own column still holds nothing but what the execution recorded - the
        // caller's promotion to the assigned route landed neither through the store nor through the database.
        var reloaded = (await evidenceRepository.GetByExecutionIdAsync(executionId))!;

        Assert.Equal(executionObservedRouteId, reloaded.ObservedRouteId);
        Assert.Equal(RealRouteId, reloaded.RequestedRouteId);
        Assert.Equal(executionObservedRouteId, await BindingObservedRouteAsync(executionId));

        // And the advance path is refused even for a historical caller-authored fixture recorded while the
        // execution was still coherent: the evidence stops authorizing the moment the execution stops being
        // an observed, read-only, correctly-routed turn of this artifact.
        await RestoreVerdictableEvidenceAsync(evidenceRepository, executionId, artifact);
        await HistoricalWorkflowReviewFixture.SeedAsync(provider, RunId, verdict);
        await SetExecutionObservationAsync(executionId, executionObservedRouteId);

        var advance = await Assert.ThrowsAnyAsync<Exception>(() => runService.AdvanceStageAsync(RunId, "advance"));

        Assert.Contains(
            executionObservedRouteId is null
                ? "no backend reported an observed route"
                : $"the backend observed route '{OtherRouteId}'",
            advance.Message,
            StringComparison.Ordinal);
        Assert.Equal(StageId, (await CurrentRunAsync()).CurrentStageId);
    }

    [Fact]
    public async Task AVerifiedArtifactCanBeReadBackThroughTheVerifyThenReadContractAndOnlyThroughIt()

    {
        var provider = CreateProvider();
        var runId = await StartRunAsync(provider, RealRouteId);
        var artifact = await CurrentArtifactAsync();
        var blobStore = provider.GetRequiredService<IWorkflowArtifactBlobStore>();

        // The exact bytes, re-hashed and opened under a bound.
        await using (var verified = await blobStore.OpenVerifiedAsync(artifact.BlobId, 4096))
        {
            Assert.NotNull(verified);
            using var reader = new StreamReader(verified!, Encoding.UTF8, leaveOpen: true);
            Assert.Equal("the document under review", await reader.ReadToEndAsync());
        }

        // The same call refuses an artifact that is bigger than the bound, rather than truncating it, because
        // a prefix is not the document the hash names.
        var tooLarge = await Assert.ThrowsAsync<WorkflowArtifactTooLargeException>(
            () => blobStore.OpenVerifiedAsync(artifact.BlobId, 4));
        Assert.Equal(artifact.BlobId, tooLarge.BlobId);

        // A non-positive bound is a caller error, not a zero-length answer.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => blobStore.OpenVerifiedAsync(artifact.BlobId, 0));

        // And a blob whose bytes are gone is a null handle rather than an exception whose text could carry a
        // local path into a durable record.
        var blobPath = provider.GetRequiredService<WorkflowBlobStore>().GetBlobPath(artifact.BlobId);
        Assert.True(File.Exists(blobPath), "The committed blob is expected to exist before it is deleted.");
        File.Delete(blobPath);
        Assert.Null(await blobStore.OpenVerifiedAsync(artifact.BlobId, 4096));
        Assert.Equal(runId, RunId);
    }

    private string RunId = "run-1";

    private static string DocumentHash(WorkflowArtifactEvidence artifact) => artifact.HashSha256;

    /// <summary>
    /// Rewrites a route's backend column to a name this build does not know, the way a row written by
    /// another or newer build would carry it. The column has no CHECK constraint, so the statement is
    /// accepted and it is the reader that has to refuse.
    /// </summary>
    private async Task SetRouteBackendAsync(string routeId, string backend)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Routes SET Backend = $backend WHERE Id = $route;";
        command.Parameters.AddWithValue("$backend", backend);
        command.Parameters.AddWithValue("$route", routeId);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Writes the binding's own observed-route column directly, the way anything that is not this
    /// application's store would. It is here so the guard can be proven against the statement itself.
    /// </summary>
    private static async Task SetBindingObservedRouteAsync(
        SqliteConnection connection,
        string executionId,
        string? observedRouteId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WorkflowReviewExecutions
            SET ObservedRouteId = $observedRouteId
            WHERE ExecutionId = $executionId;
            """;
        command.Parameters.AddWithValue("$observedRouteId", (object?)observedRouteId ?? DBNull.Value);
        command.Parameters.AddWithValue("$executionId", executionId);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// What the binding's own column holds, read without the join. The gate does not read this - that is the
    /// point - so a test can show what a caller wrote apart from what the gate is told.
    /// </summary>
    private async Task<string?> BindingObservedRouteAsync(string executionId)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ObservedRouteId FROM WorkflowReviewExecutions WHERE ExecutionId = $executionId;";
        command.Parameters.AddWithValue("$executionId", executionId);

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>
    /// Rewrites what the execution row itself says it observed, in either direction. Used to put a turn into
    /// the state a backend really reporting the assigned route would have left it in, and to stage the tamper
    /// the new authority has to survive: a turn whose observation is corrected downwards, or never existed,
    /// while the binding column still holds the assigned route a caller wrote there earlier.
    /// </summary>
    private async Task SetExecutionObservationAsync(string executionId, string? observedRouteId)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Executions SET ObservedRouteId = $observedRouteId WHERE Id = $executionId;";
        command.Parameters.AddWithValue("$observedRouteId", (object?)observedRouteId ?? DBNull.Value);
        command.Parameters.AddWithValue("$executionId", executionId);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Puts a turn back into the state a backend really reporting the assigned route would have left it in:
    /// the execution row records the route, and only then does the binding's copy of it follow. This is the
    /// proper path, and the direction of the two writes is the point - a binding never leads its execution.
    /// </summary>
    private async Task RestoreVerdictableEvidenceAsync(
        IWorkflowReviewEvidenceRepository evidenceRepository,
        string executionId,
        WorkflowArtifactEvidence artifact)
    {
        await SetExecutionObservationAsync(executionId, RealRouteId);

        var binding = (await evidenceRepository.GetByExecutionIdAsync(executionId))!;

        await evidenceRepository.UpdateObservedAsync(
            binding.WithObservedOutcome(RealRouteId, ExecutionState.Succeeded));

        var restored = await evidenceRepository.GetByExecutionIdAsync(executionId);
        Assert.Equal(RealRouteId, restored!.ObservedRouteId);
        Assert.Equal(artifact.HashSha256, restored.ReviewedArtifactHash);
    }


    private ServiceProvider CreateProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<ISqliteConnectionFactory>(_database.Factory);
        services.AddSingleton(new WorkflowBlobStore(_database.Root));
        services.AddSingleton(WorkflowScheme.CreateStandardDevelopmentScheme());
        services.AddSingleton<TimeProvider>(TimeProvider.System);

        // The run-scoped reviewer session, execution and project stores the review path writes through, plus
        // the redaction filter the execution store depends on. They are registered here from the same
        // connection factory rather than through the whole infrastructure composition, so this file still
        // tests one thing at a time.
        services.AddSingleton<SensitiveDataFilter>();
        services.AddSingleton<ISessionRepository, SqliteSessionRepository>();
        services.AddSingleton<IExecutionRepository, SqliteExecutionRepository>();
        services.AddSingleton<IProjectRepository, SqliteProjectRepository>();
        services.AddSingleton<IProviderProfileRepository, SqliteProviderProfileRepository>();
        services.AddSingleton<LLMWorkGUI.Application.Security.IDataClassificationGate,
            LLMWorkGUI.Application.Security.DataClassificationGate>();

        configure?.Invoke(services);
        services.AddWorkflowServices();

        return services.BuildServiceProvider();
    }

    private async Task<string> StartRunAsync(ServiceProvider provider, string primaryRouteId,
        DataClassification classification = DataClassification.PrivateSource)
    {
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        await templateStore.SaveAsync(CreateTemplate(primaryRouteId));
        await templateStore.SaveAssignmentAsync(
            new WorkflowTemplateAssignment("assignment-1", ProjectId, TemplateId, 1, AssignedAt));

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);
        RunId = run.Id;

        var recorded = await runService.RecordStageArtifactAsync(
            run.Id,
            StageId,
            ArtifactKind,
            new MemoryStream(Encoding.UTF8.GetBytes("the document under review"), writable: false),
            classification);

        return recorded.Id;
    }

    private async Task<WorkflowArtifactEvidence> CurrentArtifactAsync() =>
        WorkflowArtifactEvidence.SelectCurrent((await CurrentRunAsync()).Artifacts, RunId, StageId, ArtifactKind)!;

    private async Task<WorkflowRun> CurrentRunAsync() =>
        (await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(RunId))!;

    /// <summary>
    /// A real session and a real execution on a real route, plus the binding between them and this run,
    /// stage, role and artifact. The rows are written through the same stores the product uses.
    /// </summary>
    private async Task<string> PersistExecutionAsync(
        IWorkflowReviewEvidenceRepository evidenceRepository,
        ExecutionState state,
        string? observedRouteId)
    {
        var artifact = await CurrentArtifactAsync();
        var sessionId = Guid.NewGuid().ToString("N");
        var executionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        var provider = CreateProvider();

        await provider.GetRequiredService<ISessionRepository>().UpsertAsync(
            new Session(
                sessionId,
                new SessionBinding(BackendType.OpenCode, ProfileId, AccountId, ModelId, null, null, null),
                ProjectId,
                @"C:\project",
                nativeSessionId: null,
                SessionState.Active,
                ReconciliationOutcome.None,
                CloseReason.None,
                continuationOfSessionId: null,
                forkedFromSessionId: null,
                workflowRunId: RunId,
                ReviewerRole,
                executionId,
                now,
                now));

        await provider.GetRequiredService<IExecutionRepository>().UpsertAsync(
            new Execution(
                executionId,
                sessionId,
                executionId,
                state,
                state == ExecutionState.Succeeded ? ExecutionFailureReason.None : ExecutionFailureReason.InternalError,
                RealRouteId,
                observedRouteId,
                retryOfExecutionId: null,
                processState: null,
                exitCode: state == ExecutionState.Succeeded ? 0 : null,
                terminationReason: null,
                Array.Empty<string>(),
                artifact.HashSha256,
                artifact.HashSha256,
                now,
                now,
                state == ExecutionState.Queued ? null : now));

        var evidence = new ReviewerExecutionEvidence(
            executionId,
            sessionId,
            RunId,
            ReviewerRole,
            StageId,
            RealRouteId,
            observedRouteId: null,
            artifact.ArtifactId,
            artifact.HashSha256,
            isReadOnly: true,
            state);

        await evidenceRepository.SaveAsync(evidence);
        await evidenceRepository.UpdateObservedAsync(evidence.WithObservedOutcome(observedRouteId, state));

        return executionId;
    }

    private static WorkflowTemplateDefinition CreateTemplate(string primaryRouteId) =>
        new(
            TemplateId,
            1,
            "Review template",
            "A two-node chain whose first node is a reviewer gate.",
            new WorkflowGraph(
                StageId,
                new[]
                {
                    new WorkflowNodeDefinition(
                        StageId,
                        WorkflowNodeKind.Prompt,
                        "Node A",
                        "Reviewer",
                        successTargetNodeId: TerminalStageId,
                        gateMetadata: new WorkflowNodeGateMetadata(
                            WorkflowStageKind.DocumentReview,
                            new[] { ReviewerRole },
                            requiresUserApproval: false,
                            artifactRequirement: ArtifactKind)),
                    new WorkflowNodeDefinition(
                        TerminalStageId,
                        WorkflowNodeKind.TerminalOutcome,
                        "Node B",
                        "Coordinator")
                }),
            new[] { new RoleBindingDefinition(ReviewerRole, primaryRouteId, modelId: ModelId) },
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            AssignedAt);

    private async Task SeedAsync(bool legacy = false)
    {
        if (legacy)
            await new DatabaseMigrator(_database.Factory, DatabaseMigrator.LoadEmbeddedMigrations()
                .Where(m => m.Version <= 16).ToArray()).MigrateAsync();
        else
            await _database.InitializeAsync();

        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($project, 'Project', 'C:\project', 'PrivateSource', $now, $now);
            INSERT INTO WorkflowPackages (Id, Name, SourceType, OriginalHash, OriginalBlobId, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($package, 'package-1.zip', 'Imported', 'hash-1', 'blob-1', $now, $now);
            INSERT INTO WorkflowVersions (Id, WorkflowPackageId, VersionNumber, BlobId, OriginalHash, SourceType, CreatedAtUtc)
            VALUES ($version, $package, 1, 'blob-1', 'hash-1', 'Imported', $now);
            INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($profile, 'Profile', 'OpenCode', 'PrivateSource', $now, $now);
            INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($account, $profile, 'Account', 'Unverified', 'Unknown', $now, $now);
            INSERT INTO Models (
                Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState, Provenance,
                Health, DiscoveredAtUtc)
            VALUES ($model, 'OpenCode', $profile, 'model-1', 'Model', 'Unknown', 'Imported', 'Unknown', $now);
            INSERT INTO Routes (
                Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, Health,
                CreatedAtUtc, UpdatedAtUtc)
            VALUES ($route, 'OpenCode', $profile, $account, $model, 'PrivateSource', 'Healthy', $now, $now);
            INSERT INTO Routes (
                Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, Health,
                CreatedAtUtc, UpdatedAtUtc)
            VALUES ($otherRoute, 'OpenCode', $profile, $account, $model, 'PrivateSource', 'Healthy', $now, $now);
            """;
        seed.Parameters.AddWithValue("$project", ProjectId);
        seed.Parameters.AddWithValue("$package", PackageId);
        seed.Parameters.AddWithValue("$version", VersionId);
        seed.Parameters.AddWithValue("$profile", ProfileId);
        seed.Parameters.AddWithValue("$account", AccountId);
        seed.Parameters.AddWithValue("$model", ModelId);
        seed.Parameters.AddWithValue("$route", RealRouteId);
        seed.Parameters.AddWithValue("$otherRoute", OtherRouteId);
        seed.Parameters.AddWithValue("$now", "2026-09-29T00:00:00Z");

        await seed.ExecuteNonQueryAsync();
    }
}
