using System.Text;
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
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Storage;
using LLMWorkGUI.Infrastructure.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// The shipped built-in standard template, assigned to a project, walked through the product-composed
/// <see cref="IWorkflowRunService"/> against a real migrated SQLite database and the real blob store.
/// <para>
/// Every success stage and gate on these runs comes from
/// the stored graph of <c>workflow-standard-development-bounded@1</c> and from nothing else: the process-wide
/// standard scheme is never substituted for it, no gate is inferred from a node kind, and no reviewer
/// execution is invented by the production code. The walk therefore exercises what a user would really get
/// - an exact, immutable plan - and not a scenario this file wrote for itself.
/// </para>
/// <para>
/// <b>Test-only fakes.</b> Two collaborators are stand-ins and are declared as such:
/// <see cref="TestOnlyApproverIdentity"/>, which supplies the local approver name the run service stamps -
/// the production source reads the Windows logon, which no test can assert on - and
/// <see cref="TestOnlyReviewerExecutionRepository"/>, which mints the persisted reviewer-execution rows a
/// linked model-review verdict is authorized by. The second one stands in for the external model turn that
/// only a live provider could produce; it writes real rows through the real repositories against the real
/// database, and no test here is evidence that a model reviewed anything.
/// </para>
/// </summary>
public sealed class StandardTemplatePinnedRunTests : IDisposable
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionId = "version-1";
    private const string OtherTemplateId = "other-template";
    private const string TemplateId = WorkflowStudioService.StandardTemplateId;

    /// <summary>
    /// A real persisted route. A linked verdict is only satisfied by an execution whose requested and
    /// observed routes are a <c>Routes</c> row; a label such as <c>route-opencode</c> is not one, and the
    /// executions table would refuse to store it.
    /// </summary>
    private const string ReviewerRouteId = "route-reviewer-1";
    private const string ProviderProfileId = "profile-1";
    private const string AccountId = "account-1";
    private const string ModelId = "model-1";

    private static readonly DateTimeOffset AssignedAt = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The four document-producing stages before the first review, in the order the built-in chain walks
    /// them. Each is a Prompt node whose only requirement is a stored artifact of the named kind.
    /// </summary>
    private static readonly (string StageId, string ArtifactKind)[] DocumentStages =
    {
        (WorkflowScheme.TaskSpecificationStageId, "TaskSpecificationDocument"),
        (WorkflowScheme.ArchitectureStageId, "ArchitectureDocument"),
        (WorkflowScheme.TechnicalSpecificationStageId, "TechnicalSpecificationDocument"),
        (WorkflowScheme.RoadmapStageId, "RoadmapDocument")
    };

    private readonly TickingTimeProvider _timeProvider = new(new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
    private readonly TestDatabase _database = new();

    public StandardTemplatePinnedRunTests() => SeedAsync().GetAwaiter().GetResult();

    public void Dispose()
    {
        _database.Dispose();
    }

    /// <summary>
    /// The point of the task in one test: the assigned built-in template starts, pins its own graph and
    /// scheme, walks its four document stages and its two-reviewer document review, and does all of it
    /// with a full SQLite reopen. Review traversal uses a historical aggregate fixture only after the
    /// production service refuses missing parsed response evidence.
    /// </summary>
    [Fact]
    public async Task BuiltInGraphFixtureWalkPreservesGatesWhileProductReviewRefusesMissingResponse()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        var run = await StartBuiltInRunAsync(provider);
        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, run.CurrentStageId);

        foreach (var (stageId, artifactKind) in DocumentStages)
        {
            Assert.Equal(stageId, run.CurrentStageId);

            // A stored document is the whole of a stage that declares no human gate.
            await RecordArtifactAsync(runService, run.Id, stageId, artifactKind, $"bytes for {artifactKind}");

            run = await runService.AdvanceStageAsync(run.Id, $"{artifactKind} is stored");
        }

        Assert.Equal(WorkflowScheme.DocumentReviewStageId, run.CurrentStageId);

        // A full reopen: brand new repositories, store and service over the same database and blob
        // directory, with only the committed rows and the stored bytes left to go on.
        await using var reopened = CreateProvider();
        var reopenedService = reopened.GetRequiredService<IWorkflowRunService>();
        var reopenedReviewer = reopened.GetRequiredService<TestOnlyReviewerExecutionRepository>();

        var reopenedRun = (await ReloadAsync(reopened, run.Id))!;
        var reviewStage = PinnedStage(reopenedRun, WorkflowScheme.DocumentReviewStageId);

        Assert.Equal(WorkflowStageKind.DocumentReview, reviewStage.StageKind);
        Assert.Equal(
            new[] { WorkflowScheme.ReviewerRole, WorkflowScheme.ArchitectRole },
            reviewStage.RequiredReviewerRoles);
        Assert.Equal("DocumentBundle", reviewStage.ArtifactRequirement);

        var bundle = await RecordArtifactAsync(
            reopenedService,
            run.Id,
            WorkflowScheme.DocumentReviewStageId,
            "DocumentBundle",
            "the document bundle");

        // Two required reviewers. A verdict from a role the stage never asked for is recorded - it is a real
        // review of a real artifact - but it authorizes nothing, so the gate still names the one it is
        // waiting for.
        await reopenedReviewer.RecordAsync(
            reopened,
            run.Id,
            WorkflowScheme.DocumentReviewStageId,
            WorkflowScheme.ImplementerRole,
            bundle);

        await reopenedReviewer.RecordAsync(
            reopened,
            run.Id,
            WorkflowScheme.DocumentReviewStageId,
            WorkflowScheme.ReviewerRole,
            bundle);

        var oneShort = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopenedService.AdvanceStageAsync(run.Id, "the bundle was reviewed"));
        Assert.Contains(WorkflowScheme.ArchitectRole, oneShort.Message, StringComparison.Ordinal);

        await reopenedReviewer.RecordAsync(
            reopened,
            run.Id,
            WorkflowScheme.DocumentReviewStageId,
            WorkflowScheme.ArchitectRole,
            bundle);

        var advanced = await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(reopened, run.Id, "Historical domain fixture traversal after product refusal.");

        Assert.Equal(WorkflowScheme.UserApprovalStageId, advanced.CurrentStageId);
        Assert.Equal(WorkflowScheme.ApproverRole, advanced.CurrentRole);

        var transition = advanced.Transitions[^1];
        Assert.Equal(WorkflowScheme.DocumentReviewStageId, transition.FromStageId);
        Assert.Equal(WorkflowScheme.UserApprovalStageId, transition.ToStageId);
        Assert.Equal(bundle.ArtifactId, transition.AuthorizingArtifactId);
        Assert.Equal(bundle.HashSha256, transition.AuthorizingArtifactHash);
        Assert.Equal(
            new[] { WorkflowScheme.ReviewerRole, WorkflowScheme.ArchitectRole },
            transition.ReviewerVerdicts.Select(verdict => verdict.ReviewerRole));

        // The verdict of the role the stage never asked for is in the run's history and is in none of the
        // transition's authorizing verdicts, which is what a role that was not required looks like.
        Assert.Contains(
            advanced.Verdicts,
            verdict => verdict.ReviewerRole == WorkflowScheme.ImplementerRole);
        Assert.DoesNotContain(
            transition.ReviewerVerdicts,
            verdict => verdict.ReviewerRole == WorkflowScheme.ImplementerRole);

        // The whole walk happened on the run's own pinned chain, and the pinned identity never moved.
        Assert.Equal(TemplateId, advanced.TemplateId);
        Assert.Equal(1, advanced.TemplateVersion);
        Assert.Equal(WorkflowRunState.Running, advanced.State);
        Assert.Equal(WorkflowTerminalOutcome.None, advanced.TerminalOutcome);
    }

    /// <summary>
    /// The approval stage the built-in declares is a real gate: the local approver has to sign the hash the
    /// stage is about, and the approver recorded is the one the service resolved - never a name that
    /// arrived with the evidence.
    /// </summary>
    [Fact]
    public async Task TheBuiltInUserApprovalStageIsBlockedUntilTheLocalApproverSignsTheCurrentHash()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        var run = await WalkToUserApprovalAsync(provider);
        var approvalStage = PinnedStage(run, WorkflowScheme.UserApprovalStageId);

        Assert.True(approvalStage.RequiresUserApproval);
        Assert.Equal("ApprovedDocument", approvalStage.ArtifactRequirement);

        var document = await RecordArtifactAsync(
            runService,
            run.Id,
            WorkflowScheme.UserApprovalStageId,
            "ApprovedDocument",
            "the approved document");

        // The stage the run is on declares no failure target, so the attempt to take one is refused by the
        // other name and says so - rather than borrowing another stage's route.
        var noRoute = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.AdvanceToDeclaredFailureStageAsync(run.Id, "no route here"));
        Assert.Contains(
            WorkflowRunFailureRouteRefusals.NoDeclaredFailureRoute,
            noRoute.Message,
            StringComparison.Ordinal);
        Assert.Contains(WorkflowScheme.UserApprovalStageId, noRoute.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            WorkflowRunFailureRouteRefusals.FailureRouteNotExecutable,
            noRoute.Message,
            StringComparison.Ordinal);

        var withoutApproval = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the document is ready"));
        Assert.Contains("user approval", withoutApproval.Message, StringComparison.OrdinalIgnoreCase);

        await runService.RecordUserApprovalAsync(
            run.Id,
            new UserApprovalEvidence(
                Guid.NewGuid().ToString("N"),
                "a name the caller supplied and the service must ignore",
                WorkflowScheme.UserApprovalStageId,
                document.HashSha256,
                UserApprovalDecision.Approved,
                "approved",
                _timeProvider.GetUtcNow()));

        var advanced = await runService.AdvanceStageAsync(run.Id, "the approver signed the document");

        Assert.Equal(WorkflowScheme.ImplementationPackagesStageId, advanced.CurrentStageId);
        Assert.Equal(TestOnlyApproverIdentity.Name, advanced.Approvals[^1].ApprovedBy);
    }

    /// <summary>
    /// A run pinned to the built-in version keeps its own plan after the project's assignment moves to a
    /// completely different template, and a new run then pins the new assignment. Nothing about the first
    /// run's stages, gates or identity is rewritten by the move.
    /// </summary>
    [Fact]
    public async Task AMovedAssignmentNeverRePinsARunAlreadyWalkingTheBuiltInChain()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        var run = await StartBuiltInRunAsync(provider);
        var pinnedGraph = run.TemplateGraphSnapshotJson;
        var pinnedScheme = run.TemplateSchemeSnapshotJson;

        await templateStore.SaveAsync(CreateOtherTemplate());
        await templateStore.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-moved",
            ProjectId,
            OtherTemplateId,
            1,
            AssignedAt));

        // A full reopen after the move: the first run is still decided by its own pinned scheme.
        await using var reopened = CreateProvider();
        var reopenedService = reopened.GetRequiredService<IWorkflowRunService>();

        await RecordArtifactAsync(
            reopenedService,
            run.Id,
            WorkflowScheme.TaskSpecificationStageId,
            "TaskSpecificationDocument",
            "the task");
        var advanced = await reopenedService.AdvanceStageAsync(run.Id, "the task specification is stored");

        Assert.Equal(WorkflowScheme.ArchitectureStageId, advanced.CurrentStageId);
        Assert.Equal(pinnedGraph, advanced.TemplateGraphSnapshotJson);
        Assert.Equal(pinnedScheme, advanced.TemplateSchemeSnapshotJson);

        var reread = (await ReloadAsync(reopened, run.Id))!;
        Assert.Equal(pinnedGraph, reread.TemplateGraphSnapshotJson);
        Assert.Equal(pinnedScheme, reread.TemplateSchemeSnapshotJson);
        Assert.Equal(TemplateId, reread.TemplateId);
        Assert.Equal(1, reread.TemplateVersion);

        // The other template is a two-stage chain, so the next run for the same project has a different
        // number of stages and a different first stage - which is how the move is proved to have happened.
        var second = await reopenedService.StartRunAsync(ProjectId, PackageId, VersionId);

        Assert.Equal(OtherTemplateId, second.TemplateId);
        Assert.Equal(
            2,
            WorkflowSchemeSnapshot.Deserialize(second.TemplateSchemeSnapshotJson!, second.Id).Scheme.Stages.Count);
        Assert.Equal("other-node-a", second.CurrentStageId);
    }

    /// <summary>
    /// The gates on the built-in chain are the real ones. A stage with no stored artifact, an artifact of
    /// the wrong kind, an approval of another document, a verdict from a role the stage never asked for and
    /// a stored artifact whose bytes are gone each block the transition by name, and none of them is
    /// reported as anything else.
    /// </summary>
    [Fact]
    public async Task MissingWrongAndLostArtifactBytesEachBlockTheBuiltInGateByName()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var reviewer = provider.GetRequiredService<TestOnlyReviewerExecutionRepository>();

        var run = await WalkToUserApprovalAsync(provider);

        // Nothing produced yet.
        var nothing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "ready"));
        Assert.Contains("ApprovedDocument", nothing.Message, StringComparison.Ordinal);

        // The wrong kind is refused at the service boundary, and records nothing.
        var wrongKind = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.RecordStageArtifactAsync(
                run.Id,
                WorkflowScheme.UserApprovalStageId,
                "SomethingElse",
                new MemoryStream(Encoding.UTF8.GetBytes("nope"), writable: false),
                DataClassification.PrivateSource));
        Assert.Contains("ApprovedDocument", wrongKind.Message, StringComparison.Ordinal);

        var document = await RecordArtifactAsync(
            runService,
            run.Id,
            WorkflowScheme.UserApprovalStageId,
            "ApprovedDocument",
            "the approved document");

        // An approval of a hash that is not the current one.
        var foreignHash = "sha256:" + new string('c', 64);
        var stale = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.RecordUserApprovalAsync(
                run.Id,
                new UserApprovalEvidence(
                    Guid.NewGuid().ToString("N"),
                    TestOnlyApproverIdentity.Name,
                    WorkflowScheme.UserApprovalStageId,
                    foreignHash,
                    UserApprovalDecision.Approved,
                    "approved something else",
                    _timeProvider.GetUtcNow())));
        Assert.Contains(document.HashSha256, stale.Message, StringComparison.Ordinal);
        Assert.Contains(foreignHash, stale.Message, StringComparison.Ordinal);

        // A verdict about a stage the run is not sitting on is refused outright, whatever role it names.
        var staleStage = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => reviewer.RecordAsync(
                provider,
                run.Id,
                WorkflowScheme.MultiLevelReviewStageId,
                WorkflowScheme.ImplementerRole,
                document));
        Assert.Contains(WorkflowScheme.UserApprovalStageId, staleStage.Message, StringComparison.Ordinal);
        Assert.Contains(WorkflowScheme.MultiLevelReviewStageId, staleStage.Message, StringComparison.Ordinal);

        // The stored bytes are the evidence, so a row whose blob is gone authorizes nothing.
        await runService.RecordUserApprovalAsync(
            run.Id,
            new UserApprovalEvidence(
                Guid.NewGuid().ToString("N"),
                TestOnlyApproverIdentity.Name,
                WorkflowScheme.UserApprovalStageId,
                document.HashSha256,
                UserApprovalDecision.Approved,
                "approved",
                _timeProvider.GetUtcNow()));

        DeleteCommittedBytes(document.BlobId);

        var lost = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the approval is stored"));
        Assert.Contains(document.ArtifactId, lost.Message, StringComparison.Ordinal);

        // The run is still on the stage whose gate refused it, with no transition out of it and no outcome.
        var afterRefusal = (await ReloadAsync(provider, run.Id))!;
        Assert.Equal(WorkflowScheme.UserApprovalStageId, afterRefusal.CurrentStageId);
        Assert.Empty(
            afterRefusal.Transitions.Where(
                transition => transition.FromStageId == WorkflowScheme.UserApprovalStageId));
        Assert.Equal(WorkflowTerminalOutcome.None, afterRefusal.TerminalOutcome);
        Assert.Null(afterRefusal.EndedAtUtc);
    }

    /// <summary>
    /// A reviewer that does not approve blocks the transition, and the run is sent neither to the stage's
    /// success target nor to the failure target the built-in declares. The declared route stays on the
    /// pinned scheme, taking it is refused by name, and the run then goes on to the success route once the
    /// same reviewer approves the same hash.
    /// </summary>
    [Fact]
    public async Task ARejectingReviewerNeitherAdvancesTheRunNorCreatesAnAutomaticReworkRoute()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var reviewer = provider.GetRequiredService<TestOnlyReviewerExecutionRepository>();

        var run = await WalkToImplementationAsync(provider);

        var multiLevelStage = PinnedStage(run, WorkflowScheme.MultiLevelReviewStageId);
        Assert.Null(multiLevelStage.FailureStageId);
        Assert.Equal(WorkflowScheme.TestsAndUiAcceptanceStageId, multiLevelStage.NextStageId);
        Assert.Equal(
            new[] { WorkflowScheme.ReviewerRole, WorkflowScheme.UiReviewerRole },
            multiLevelStage.RequiredReviewerRoles);

        var diff = await RecordArtifactAsync(
            runService,
            run.Id,
            WorkflowScheme.MultiLevelReviewStageId,
            "ReviewedImplementationDiff",
            "the diff under review");

        await reviewer.RecordAsync(
            provider,
            run.Id,
            WorkflowScheme.MultiLevelReviewStageId,
            WorkflowScheme.ReviewerRole,
            diff,
            WorkflowReviewVerdict.RequestChanges);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the review requested changes"));
        Assert.Contains(
            $"The reviewer '{WorkflowScheme.ReviewerRole}' returned 'RequestChanges'",
            rejected.Message,
            StringComparison.Ordinal);

        // The refusal does not invent a route: neither the success target nor automatic rework is taken.
        Assert.DoesNotContain(WorkflowScheme.TestsAndUiAcceptanceStageId, rejected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkflowScheme.CodeAndUiStageId, rejected.Message, StringComparison.Ordinal);

        // No failure route is invented when the gate rejects.
        var refusedRoute = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.AdvanceToDeclaredFailureStageAsync(run.Id, "the review requested changes"));
        Assert.Contains(
            WorkflowRunFailureRouteRefusals.NoDeclaredFailureRoute,
            refusedRoute.Message,
            StringComparison.Ordinal);
        Assert.Contains(WorkflowScheme.MultiLevelReviewStageId, refusedRoute.Message, StringComparison.Ordinal);

        var afterRefusal = (await ReloadAsync(provider, run.Id))!;
        Assert.Equal(WorkflowScheme.MultiLevelReviewStageId, afterRefusal.CurrentStageId);
        Assert.Empty(
            afterRefusal.Transitions.Where(
                transition => transition.FromStageId == WorkflowScheme.MultiLevelReviewStageId));
        Assert.Equal(WorkflowRunState.Running, afterRefusal.State);
        Assert.Equal(WorkflowTerminalOutcome.None, afterRefusal.TerminalOutcome);
        Assert.Null(afterRefusal.EndedAtUtc);

        // The pinned scheme and graph consistently declare no automatic rework edge.
        Assert.Null(PinnedStage(afterRefusal, WorkflowScheme.MultiLevelReviewStageId).FailureStageId);
        Assert.Null(WorkflowGraphSnapshot.Deserialize(afterRefusal.TemplateGraphSnapshotJson!, afterRefusal.Id)
            .GetRequiredNode(WorkflowScheme.MultiLevelReviewStageId).FailureTargetNodeId);

        // And once that same reviewer approves the same hash, the success route is the one that is taken.
        await reviewer.RecordAsync(
            provider,
            run.Id,
            WorkflowScheme.MultiLevelReviewStageId,
            WorkflowScheme.ReviewerRole,
            diff);
        await reviewer.RecordAsync(
            provider,
            run.Id,
            WorkflowScheme.MultiLevelReviewStageId,
            WorkflowScheme.UiReviewerRole,
            diff);

        var atAcceptance = await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(provider, run.Id, "Historical domain fixture implementation review traversal.");

        Assert.Equal(WorkflowScheme.TestsAndUiAcceptanceStageId, atAcceptance.CurrentStageId);
        Assert.Equal(
            1,
            atAcceptance.Transitions.Count(
                transition => transition.FromStageId == WorkflowScheme.MultiLevelReviewStageId));
        Assert.Equal(
            WorkflowScheme.TestsAndUiAcceptanceStageId,
            atAcceptance.Transitions[^1].ToStageId);
    }

    /// <summary>
    /// The supported linear built-in declares no automatic retry budget. A blocked gate stays blocked
    /// when it is asked again, and a passed gate is taken exactly once.
    /// </summary>
    [Fact]
    public async Task TheDefaultDeclaresNoUnusedRetryBudgetAndNeverGrantsASecondChanceAtTheTransition()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        var run = await WalkToUserApprovalAsync(provider);
        var pinned = WorkflowGraphSnapshot.Deserialize(run.TemplateGraphSnapshotJson!, run.Id);

        // The supported linear default has no budget that its stage runner cannot consume.
        Assert.Empty(pinned.Nodes.Where(node => node.RetryBudget != 0));

        // The pinned scheme document carries no retry of any kind, so nothing that reads the plan back can
        // find one to perform.
        Assert.DoesNotContain("retry", run.TemplateSchemeSnapshotJson!, StringComparison.OrdinalIgnoreCase);

        var document = await RecordArtifactAsync(
            runService,
            run.Id,
            WorkflowScheme.UserApprovalStageId,
            "ApprovedDocument",
            "the approved document");

        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "ready"));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "ready again"));

        Assert.Equal(first.Message, second.Message);

        // And the approval makes it pass exactly once.
        await runService.RecordUserApprovalAsync(
            run.Id,
            new UserApprovalEvidence(
                Guid.NewGuid().ToString("N"),
                TestOnlyApproverIdentity.Name,
                WorkflowScheme.UserApprovalStageId,
                document.HashSha256,
                UserApprovalDecision.Approved,
                "approved",
                _timeProvider.GetUtcNow()));

        var advanced = await runService.AdvanceStageAsync(run.Id, "the approver signed the document");

        Assert.Equal(WorkflowScheme.ImplementationPackagesStageId, advanced.CurrentStageId);

        // Exactly one transition was taken out of the approval stage, and it is
        // the one whose authorizing artifact is the document the approver signed.
        Assert.Equal(
            1,
            advanced.Transitions.Count(
                transition => transition.FromStageId == WorkflowScheme.UserApprovalStageId));
        Assert.Equal(document.ArtifactId, advanced.Transitions[^1].AuthorizingArtifactId);
    }

    /// <summary>
    /// A linked verdict is authorized only by a persisted, succeeded, read-only reviewer execution of this
    /// run on this stage for this role over the exact artifact the verdict names. The test-only repository
    /// that mints those rows can mint an unsound one on purpose, and every unsound shape is still refused.
    /// </summary>
    [Fact]
    public async Task AVerdictWithoutASucceededReadOnlyExecutionOfTheCurrentArtifactIsRefused()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var reviewer = provider.GetRequiredService<TestOnlyReviewerExecutionRepository>();

        var run = await WalkToImplementationAsync(provider);

        var firstDiff = await RecordArtifactAsync(
            runService,
            run.Id,
            WorkflowScheme.MultiLevelReviewStageId,
            "ReviewedImplementationDiff",
            "the diff under review");

        // A verdict that names no execution at all.
        var unlinked = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.RecordReviewerVerdictAsync(
                run.Id,
                new ReviewerVerdictRecord(
                    WorkflowScheme.ReviewerRole,
                    ReviewerRouteId,
                    firstDiff.HashSha256,
                    WorkflowReviewVerdict.Approve,
                    "read out of thin air",
                    _timeProvider.GetUtcNow())));
        Assert.Contains("no reviewer execution", unlinked.Message, StringComparison.OrdinalIgnoreCase);

        // A verdict naming an execution this run does not have.
        var unknownExecution = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.RecordReviewerVerdictAsync(
                run.Id,
                new ReviewerVerdictRecord(
                    WorkflowScheme.ReviewerRole,
                    ReviewerRouteId,
                    firstDiff.HashSha256,
                    WorkflowReviewVerdict.Approve,
                    "read out of an execution that never happened",
                    _timeProvider.GetUtcNow(),
                    "execution-that-does-not-exist",
                    WorkflowScheme.MultiLevelReviewStageId,
                    firstDiff.ArtifactId)));
        Assert.Contains("execution-that-does-not-exist", unknownExecution.Message, StringComparison.Ordinal);

        // A cancelled reviewer turn authorizes nothing, even though the row is a real one.
        await reviewer.MintAsync(
            provider,
            run.Id,
            WorkflowScheme.MultiLevelReviewStageId,
            WorkflowScheme.UiReviewerRole,
            firstDiff,
            ExecutionState.Cancelled);

        var cancelled = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the review finished"));
        Assert.Contains(
            $"The required reviewer '{WorkflowScheme.ReviewerRole}'",
            cancelled.Message,
            StringComparison.Ordinal);
        Assert.Equal(WorkflowScheme.MultiLevelReviewStageId, (await ReloadAsync(provider, run.Id))!.CurrentStageId);
        // A verdict about an artifact that has been replaced.
        var revised = await RecordArtifactAsync(
            runService,
            run.Id,
            WorkflowScheme.MultiLevelReviewStageId,
            "ReviewedImplementationDiff",
            "the diff under review, revised");
        Assert.NotEqual(firstDiff.ArtifactId, revised.ArtifactId);

        var stale = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => reviewer.RecordAsync(
                provider,
                run.Id,
                WorkflowScheme.MultiLevelReviewStageId,
                WorkflowScheme.ReviewerRole,
                firstDiff));
        Assert.Contains(revised.HashSha256, stale.Message, StringComparison.Ordinal);

        // With both reviewers approving the current hash the gate opens - through the same rows.
        await reviewer.RecordAsync(
            provider,
            run.Id,
            WorkflowScheme.MultiLevelReviewStageId,
            WorkflowScheme.ReviewerRole,
            revised);
        await reviewer.RecordAsync(
            provider,
            run.Id,
            WorkflowScheme.MultiLevelReviewStageId,
            WorkflowScheme.UiReviewerRole,
            revised);

        var advanced = await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(provider, run.Id, "Historical aggregate fixture traversal of revised diff after product refusal.");

        Assert.Equal(WorkflowScheme.TestsAndUiAcceptanceStageId, advanced.CurrentStageId);
    }

    /// <summary>
    /// Aggregate-only fixture walk across model-review gates, after asserting product refusal. Non-review
    /// stages still use the production service. This is not end-to-end model evidence. The last stage is a
    /// stage, not a workflow outcome: the run stops there, keeps <c>Running</c> with no terminal outcome,
    /// and only an explicit terminal command can set one.
    /// </summary>
    [Fact]
    public async Task DomainFixtureWalkOfShippedChainDoesNotClaimProductionReviewOrWorkflowOutcome()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var reviewer = provider.GetRequiredService<TestOnlyReviewerExecutionRepository>();

        var run = await StartBuiltInRunAsync(provider);

        foreach (var (stageId, artifactKind) in DocumentStages)
        {
            await RecordArtifactAsync(runService, run.Id, stageId, artifactKind, $"bytes for {artifactKind}");
            await runService.AdvanceStageAsync(run.Id, $"{artifactKind} is stored");
        }

        var bundle = await RecordArtifactAsync(
            runService,
            run.Id,
            WorkflowScheme.DocumentReviewStageId,
            "DocumentBundle",
            "the document bundle");
        await ApproveEveryRequiredAsync(provider, reviewer, run.Id, WorkflowScheme.DocumentReviewStageId, bundle);
        await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(provider, run.Id, "Historical domain fixture review traversal.");

        await ApproveStageAsync(provider, runService, reviewer, run.Id, WorkflowScheme.UserApprovalStageId, "ApprovedDocument", "the approved document");
        await runService.AdvanceStageAsync(run.Id, "the approver signed the document");

        await RecordArtifactAsync(runService, run.Id, WorkflowScheme.ImplementationPackagesStageId, "TaskPacketBundle", "the task packets");
        await runService.AdvanceStageAsync(run.Id, "the task packets are stored");

        await RecordArtifactAsync(runService, run.Id, WorkflowScheme.CodeAndUiStageId, "ImplementationDiff", "the diff");
        await runService.AdvanceStageAsync(run.Id, "the diff is stored");

        var diff = await RecordArtifactAsync(
            runService,
            run.Id,
            WorkflowScheme.MultiLevelReviewStageId,
            "ReviewedImplementationDiff",
            "the diff under review");
        await ApproveEveryRequiredAsync(provider, reviewer, run.Id, WorkflowScheme.MultiLevelReviewStageId, diff);
        await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(provider, run.Id, "Historical domain fixture implementation review traversal.");

        await ApproveStageAsync(provider, runService, reviewer, run.Id, WorkflowScheme.TestsAndUiAcceptanceStageId, "UiAcceptanceEvidence", "the acceptance evidence");
        await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(provider, run.Id, "Historical domain fixture acceptance traversal.");

        var atFinal = (await ReloadAsync(provider, run.Id))!;

        Assert.Equal(WorkflowScheme.FinalOutcomeStageId, atFinal.CurrentStageId);
        Assert.Equal(WorkflowRunState.Running, atFinal.State);
        Assert.Equal(WorkflowTerminalOutcome.None, atFinal.TerminalOutcome);
        Assert.Null(atFinal.EndedAtUtc);

        // Every stage of the shipped chain was entered exactly once, in the order its own graph declares,
        // and the last of them is where the run is standing.
        var expectedChain = new[]
        {
            WorkflowScheme.TaskSpecificationStageId,
            WorkflowScheme.ArchitectureStageId,
            WorkflowScheme.TechnicalSpecificationStageId,
            WorkflowScheme.RoadmapStageId,
            WorkflowScheme.DocumentReviewStageId,
            WorkflowScheme.UserApprovalStageId,
            WorkflowScheme.ImplementationPackagesStageId,
            WorkflowScheme.CodeAndUiStageId,
            WorkflowScheme.MultiLevelReviewStageId,
            WorkflowScheme.TestsAndUiAcceptanceStageId,
            WorkflowScheme.FinalOutcomeStageId
        };

        Assert.Equal(
            expectedChain,
            new[] { WorkflowScheme.TaskSpecificationStageId }
                .Concat(atFinal.Transitions.Select(transition => transition.ToStageId)));
        Assert.Equal(
            expectedChain,
            WorkflowSchemeSnapshot.Deserialize(atFinal.TemplateSchemeSnapshotJson!, atFinal.Id)
                .Scheme
                .Stages
                .Select(stage => stage.StageId));

        var finalStage = PinnedStage(atFinal, WorkflowScheme.FinalOutcomeStageId);
        Assert.Null(finalStage.NextStageId);
        Assert.Null(finalStage.FailureStageId);
        Assert.Empty(finalStage.RequiredReviewerRoles);
        Assert.False(finalStage.RequiresUserApproval);
        Assert.Null(finalStage.ArtifactRequirement);

        var noNextStage = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.AdvanceStageAsync(atFinal.Id, "there is nowhere left to go"));
        Assert.Contains("terminal stage", noNextStage.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<WorkflowRun> StartBuiltInRunAsync(ServiceProvider provider)
    {
        await provider.GetRequiredService<IWorkflowTemplateStore>()
            .SaveAssignmentAsync(new WorkflowTemplateAssignment(
                "assignment-built-in",
                ProjectId,
                TemplateId,
                1,
                AssignedAt));

        return await provider.GetRequiredService<IWorkflowRunService>()
            .StartRunAsync(ProjectId, PackageId, VersionId);
    }

    /// <summary>
    /// Walks the run onto the built-in's user approval stage, which is the first stage of the chain whose
    /// gate needs more than a stored document.
    /// </summary>
    private async Task<WorkflowRun> WalkToUserApprovalAsync(ServiceProvider provider)
    {
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var reviewer = provider.GetRequiredService<TestOnlyReviewerExecutionRepository>();

        var run = await StartBuiltInRunAsync(provider);

        foreach (var (stageId, artifactKind) in DocumentStages)
        {
            await RecordArtifactAsync(runService, run.Id, stageId, artifactKind, $"bytes for {artifactKind}");
            await runService.AdvanceStageAsync(run.Id, $"{artifactKind} is stored");
        }

        var bundle = await RecordArtifactAsync(
            runService,
            run.Id,
            WorkflowScheme.DocumentReviewStageId,
            "DocumentBundle",
            "the document bundle");
        await ApproveEveryRequiredAsync(provider, reviewer, run.Id, WorkflowScheme.DocumentReviewStageId, bundle);
        await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(provider, run.Id, "Historical domain fixture review traversal.");

        return (await ReloadAsync(provider, run.Id))!;
    }

    /// <summary>
    /// Walks the run onto the built-in's multi-level review stage, the first one that declares a failure
    /// target, and returns the stored run so the refusal can be asserted against real rows.
    /// </summary>
    private async Task<WorkflowRun> WalkToImplementationAsync(ServiceProvider provider)
    {
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        var atApproval = await WalkToUserApprovalAsync(provider);
        await ApproveStageAsync(
            provider,
            runService,
            provider.GetRequiredService<TestOnlyReviewerExecutionRepository>(),
            atApproval.Id,
            WorkflowScheme.UserApprovalStageId,
            "ApprovedDocument",
            "the approved document");
        await runService.AdvanceStageAsync(atApproval.Id, "the approver signed the document");

        await RecordArtifactAsync(
            runService,
            atApproval.Id,
            WorkflowScheme.ImplementationPackagesStageId,
            "TaskPacketBundle",
            "the task packets");
        await runService.AdvanceStageAsync(atApproval.Id, "the task packets are stored");

        await RecordArtifactAsync(
            runService,
            atApproval.Id,
            WorkflowScheme.CodeAndUiStageId,
            "ImplementationDiff",
            "the diff");
        await runService.AdvanceStageAsync(atApproval.Id, "the diff is stored");

        return (await ReloadAsync(provider, atApproval.Id))!;
    }

    /// <summary>
    /// Records a document for a stage and, when that stage declares reviewers and a user approval, the
    /// verdicts and the approval that go with it.
    /// </summary>
    private async Task<WorkflowArtifactEvidence> ApproveStageAsync(
        ServiceProvider provider,
        IWorkflowRunService runService,
        TestOnlyReviewerExecutionRepository reviewer,
        string runId,
        string stageId,
        string artifactKind,
        string content)
    {
        var artifact = await RecordArtifactAsync(runService, runId, stageId, artifactKind, content);
        var stage = PinnedStage((await ReloadAsync(provider, runId))!, stageId);

        await ApproveEveryRequiredAsync(provider, reviewer, runId, stageId, artifact);

        if (!stage.RequiresUserApproval)
        {
            return artifact;
        }

        await runService.RecordUserApprovalAsync(
            runId,
            new UserApprovalEvidence(
                Guid.NewGuid().ToString("N"),
                TestOnlyApproverIdentity.Name,
                stageId,
                artifact.HashSha256,
                UserApprovalDecision.Approved,
                "approved",
                _timeProvider.GetUtcNow()));

        return artifact;
    }

    private async Task ApproveEveryRequiredAsync(
        ServiceProvider provider,
        TestOnlyReviewerExecutionRepository reviewer,
        string runId,
        string stageId,
        WorkflowArtifactEvidence artifact)
    {
        var stage = PinnedStage((await ReloadAsync(provider, runId))!, stageId);

        foreach (var role in stage.RequiredReviewerRoles)
        {
            await reviewer.RecordAsync(provider, runId, stageId, role, artifact);
        }
    }

    private async Task<WorkflowArtifactEvidence> RecordArtifactAsync(
        IWorkflowRunService runService,
        string runId,
        string stageId,
        string kind,
        string content)
    {
        var run = await runService.RecordStageArtifactAsync(
            runId,
            stageId,
            kind,
            new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
            DataClassification.PrivateSource);

        return WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, runId, stageId, kind)!;
    }

    /// <summary>
    /// Deletes the committed bytes behind a blob id, the way a lost file or a rewritten one would. The store
    /// keeps them sharded under <c>sha256/&lt;prefix&gt;/&lt;hex&gt;</c>, so the file is addressed through the
    /// store's own path rather than guessed at.
    /// </summary>
    private void DeleteCommittedBytes(string blobId) =>
        File.Delete(new WorkflowBlobStore(_database.Root).GetBlobPath(blobId));

    private static Task<WorkflowRun?> ReloadAsync(ServiceProvider provider, string runId) =>
        provider.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);

    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<ISqliteConnectionFactory>(_database.Factory);
        services.AddSingleton(new WorkflowBlobStore(_database.Root));
        services.AddSingleton<TimeProvider>(_timeProvider);

        // The stores a linked model-review verdict is authorized by. They are registered here from the same
        // connection factory rather than through the full infrastructure composition, so this file still
        // tests one thing at a time. The redaction filter is the execution store's own dependency.
        services.AddSingleton<SensitiveDataFilter>();
        services.AddSingleton<ISessionRepository, SqliteSessionRepository>();
        services.AddSingleton<IExecutionRepository, SqliteExecutionRepository>();
        services.AddSingleton<IProjectRepository, SqliteProjectRepository>();

        // Test-only stand-ins, registered before the production composition so the container keeps them.
        services.AddSingleton<IUserApprovalIdentity, TestOnlyApproverIdentity>();
        services.AddSingleton<TestOnlyReviewerExecutionRepository>();

        services.AddWorkflowServices();

        return services.BuildServiceProvider();
    }

    private static WorkflowStageDefinition PinnedStage(WorkflowRun run, string stageId) =>
        WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson!, run.Id).Scheme.GetRequiredStage(stageId);

    private static WorkflowTemplateDefinition CreateOtherTemplate() =>
        new(
            OtherTemplateId,
            1,
            "Other template",
            "A gate-free two-stage chain, so a moved assignment is visible in the stage count.",
            new WorkflowGraph("other-node-a", new[]
            {
                new WorkflowNodeDefinition(
                    "other-node-a",
                    WorkflowNodeKind.Prompt,
                    "Other A",
                    "Other Role A",
                    successTargetNodeId: "other-node-b"),
                new WorkflowNodeDefinition(
                    "other-node-b",
                    WorkflowNodeKind.TerminalOutcome,
                    "Other B",
                    "Other Role B")
            }),
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            AssignedAt);

    private async Task SeedAsync()
    {
        await _database.InitializeAsync();

        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO Projects (Id, DisplayName, RootPath, DataClassification, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, 'Project', 'C:\project', 'PrivateSource', $now, $now);
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
            VALUES ($route, 'OpenCode', $profile, $account, $model, 'PrivateSource', 'Unknown', $now, $now);
            """;
        seed.Parameters.AddWithValue("$id", ProjectId);
        seed.Parameters.AddWithValue("$package", PackageId);
        seed.Parameters.AddWithValue("$version", VersionId);
        seed.Parameters.AddWithValue("$profile", ProviderProfileId);
        seed.Parameters.AddWithValue("$account", AccountId);
        seed.Parameters.AddWithValue("$model", ModelId);
        seed.Parameters.AddWithValue("$route", ReviewerRouteId);
        seed.Parameters.AddWithValue("$now", "2026-09-29T00:00:00Z");

        await seed.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Test-only stand-in for the local Windows logon identity the production approver source reads. The run
    /// service stamps every approval with whatever this returns, so a fixed name is what makes the stored
    /// approver assertable; nothing about it is production behavior.
    /// </summary>
    private sealed class TestOnlyApproverIdentity : IUserApprovalIdentity
    {
        public const string Name = "test-only-approver";

        public string? GetCurrentApproverIdentity() => Name;
    }

    /// <summary>
    /// Test-only stand-in for the external model reviewer.
    /// <para>
    /// Nothing in the product writes a reviewer execution without a real backend turn behind it, and no test
    /// can produce a real one, so this mints the rows a real turn would leave: a session bound to the
    /// route's own account, profile and model, an execution whose requested and observed routes are the same
    /// real <c>Routes</c> row, and the evidence binding that execution to this run, stage, role and artifact.
    /// The rows go through the real repositories against the real database.
    /// </para>
    /// <para>
    /// It is not a reviewer and not a channel, and no test in this file is evidence that a model reviewed
    /// anything. What it proves is that the production gate accepts a verdict only when a succeeded,
    /// read-only, correctly routed execution of the exact artifact stands behind it - and refuses it
    /// everywhere else. A cancelled turn deliberately gets no observed route at all, so the evidence stays
    /// fail-closed about a turn that never reported one.
    /// </para>
    /// </summary>
    private sealed class TestOnlyReviewerExecutionRepository
    {
        /// <summary>
        /// The execution minted for one role over one artifact, reused whenever that same role signs the
        /// same bytes again. That is what a re-review after a rejection looks like: the same reviewer turn,
        /// read again, rather than a second turn nobody could distinguish from a replay. The store's
        /// uniqueness on run, stage, role, reviewed hash and read-only is what enforces it.
        /// </summary>
        private readonly Dictionary<string, string> _executions = new(StringComparer.Ordinal);

        /// <summary>Mints the execution rows for a role and an artifact without recording any verdict.</summary>
        public async Task MintAsync(
            ServiceProvider provider,
            string runId,
            string stageId,
            string role,
            WorkflowArtifactEvidence artifact,
            ExecutionState executionState)
        {
            await WriteTurnAsync(
                provider,
                runId,
                stageId,
                role,
                artifact,
                executionState,
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"),
                _timeProviderOf(provider).GetUtcNow());
        }

        /// <summary>
        /// Seeds historical caller-authored claims after asserting the product rejects missing response
        /// evidence. Execution metadata in this fixture is not proof that a model reviewed anything.
        /// </summary>
        public async Task RecordAsync(
            ServiceProvider provider,
            string runId,
            string stageId,
            string role,
            WorkflowArtifactEvidence artifact,
            WorkflowReviewVerdict verdict = WorkflowReviewVerdict.Approve)
        {
            var key = $"{runId}|{stageId}|{role}|{artifact.HashSha256}";

            if (!_executions.TryGetValue(key, out var executionId))
            {
                executionId = Guid.NewGuid().ToString("N");

                await WriteTurnAsync(
                    provider,
                    runId,
                    stageId,
                    role,
                    artifact,
                    ExecutionState.Succeeded,
                    Guid.NewGuid().ToString("N"),
                    executionId,
                    _timeProviderOf(provider).GetUtcNow());

                _executions[key] = executionId;
            }

            await HistoricalWorkflowReviewFixture.SeedAsync(
                    provider, runId,
                    new ReviewerVerdictRecord(
                        role,
                        ReviewerRouteId,
                        artifact.HashSha256,
                        verdict,
                        "Historical caller-authored fixture claim; no parsed response exists.",
                        _timeProviderOf(provider).GetUtcNow(),
                        executionId,
                        stageId,
                        artifact.ArtifactId),
                    CancellationToken.None);
        }

        private static async Task WriteTurnAsync(
            ServiceProvider provider,
            string runId,
            string stageId,
            string role,
            WorkflowArtifactEvidence artifact,
            ExecutionState executionState,
            string sessionId,
            string executionId,
            DateTimeOffset now)
        {
            await provider.GetRequiredService<ISessionRepository>()
                .UpsertAsync(
                    new Session(
                        sessionId,
                        new SessionBinding(
                            BackendType.OpenCode,
                            ProviderProfileId,
                            AccountId,
                            ModelId,
                            null,
                            null,
                            null),
                        ProjectId,
                        @"C:\project",
                        nativeSessionId: null,
                        SessionState.Active,
                        ReconciliationOutcome.None,
                        CloseReason.None,
                        continuationOfSessionId: null,
                        forkedFromSessionId: null,
                        workflowRunId: runId,
                        role,
                        executionId,
                        createdAt: now,
                        lastEventAt: now),
                    CancellationToken.None);

            await provider.GetRequiredService<IExecutionRepository>()
                .UpsertAsync(
                    new Execution(
                        executionId,
                        sessionId,
                        executionId,
                        executionState,
                        ExecutionFailureReason.None,
                        ReviewerRouteId,
                        ReviewerRouteId,
                        retryOfExecutionId: null,
                        processState: null,
                        exitCode: 0,
                        terminationReason: null,
                        Array.Empty<string>(),
                        artifact.HashSha256,
                        artifact.HashSha256,
                        now,
                        now,
                        now),
                    CancellationToken.None);

            var evidenceRepository = provider.GetRequiredService<IWorkflowReviewEvidenceRepository>();
            var evidence = new ReviewerExecutionEvidence(
                executionId,
                sessionId,
                runId,
                role,
                stageId,
                ReviewerRouteId,
                observedRouteId: null,
                artifact.ArtifactId,
                artifact.HashSha256,
                isReadOnly: true,
                executionState);

            await evidenceRepository.SaveAsync(evidence, CancellationToken.None);

            if (executionState == ExecutionState.Succeeded)
            {
                await evidenceRepository.UpdateObservedAsync(
                    evidence.WithObservedOutcome(ReviewerRouteId, executionState),
                    CancellationToken.None);
            }
        }

        private static TimeProvider _timeProviderOf(ServiceProvider provider) =>
            provider.GetRequiredService<TimeProvider>();
    }

    /// <summary>
    /// A clock that hands out a strictly later instant on every read, so a recorded artifact, verdict or
    /// approval can never share a timestamp with the one it has to supersede.
    /// </summary>
    private sealed class TickingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public TickingTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            var now = _now;
            _now = now.AddSeconds(1);
            return now;
        }
    }
}
