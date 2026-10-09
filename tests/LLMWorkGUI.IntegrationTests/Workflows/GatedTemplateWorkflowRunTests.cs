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
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

/// <summary>
/// A linear template whose first stage declares real stage gates, executed through the product-composed
/// <see cref="IWorkflowRunService"/> against a real migrated SQLite database and the real blob store.
///
/// The gates here are the preserved <see cref="WorkflowNodeGateMetadata"/> of the template's own nodes, so
/// every refusal and every transition in this file is about what a template declared: nothing is inferred
/// from a node kind or a role name, and a gate that is lost anywhere between the template document, the
/// pinned snapshots, the durable artifact row and the re-verified bytes shows up as a failing expectation
/// rather than as a transition that happened to be allowed.
/// </summary>
public sealed class GatedTemplateWorkflowRunTests : IDisposable
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionId = "version-1";
    private const string TemplateId = "gated-template";
    private const string GatedStageId = "node-a";
    private const string TerminalStageId = "node-b";
    private const string ArtifactKind = "ReviewedDocument";
    private const string ReviewerRole = "Reviewer";
    private const string ArchitectRole = "Architect";
    private const string ForeignRole = "Implementer";

    private const string ForeignHash =
        "sha256:0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f";

    /// <summary>
    /// A real persisted route. A reviewer verdict on a pinned stage is only satisfied by an execution whose
    /// requested and observed routes are a <c>Routes</c> row, and a label such as <c>route-opencode</c> is
    /// not one - the executions table would refuse to store it.
    /// </summary>
    private const string ReviewerRouteId = "route-reviewer-1";
    private const string ProviderProfileId = "profile-1";
    private const string AccountId = "account-1";
    private const string ModelId = "model-1";

    private const string AbsentStage = "absent";
    private const string NoGateStage = "no-gate";
    private const string GatedStage = "gated";

    private static readonly DateTimeOffset AssignedAt = new(2026, 9, 29, 7, 0, 0, TimeSpan.Zero);

    private static readonly WorkflowNodeGateMetadata DeclaredGate = new(
        WorkflowStageKind.DocumentReview,
        new[] { ReviewerRole, ArchitectRole },
        requiresUserApproval: true,
        artifactRequirement: ArtifactKind);

    /// <summary>
    /// A clock the service reads through, so every recorded timestamp is strictly ordered and the "newest
    /// artifact" and "latest verdict" rules never depend on how fast the test machine is. The same instance
    /// is handed to every provider of a test, so a reopen does not rewind the clock.
    /// </summary>
    private readonly TickingTimeProvider _timeProvider = new(new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero));

    private readonly TestDatabase _database = new();

    /// <summary>
    /// The reviewer execution minted for one role and one artifact hash, reused when that same role signs the
    /// same bytes again. Keyed the way the storage layer keys its uniqueness constraint.
    /// </summary>
    private readonly Dictionary<string, string> _reviewerExecutions = new(StringComparer.Ordinal);

    public GatedTemplateWorkflowRunTests() =>
        SeedAsync().GetAwaiter().GetResult();

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task HistoricalGateClaimsRemainBlockedWithoutParsedResponseEvidence()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        await SaveAndAssignAsync(provider.GetRequiredService<IWorkflowTemplateStore>(), GatedStage);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);
        Assert.Equal(GatedStageId, run.CurrentStageId);

        // The gate the run is pinned to is the declared one, read back out of the stored document rather
        // than out of the object the service just built.
        var persisted = await ReloadAsync(provider, run.Id);
        var gated = PinnedStage(persisted!, GatedStageId);
        Assert.Equal(WorkflowStageKind.DocumentReview, gated.StageKind);
        Assert.Equal(new[] { ReviewerRole, ArchitectRole }, gated.RequiredReviewerRoles);
        Assert.True(gated.RequiresUserApproval);
        Assert.Equal(ArtifactKind, gated.ArtifactRequirement);

        // Nothing has been produced yet, so there is no stored content for the gate to decide on.
        var withoutArtifact = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the document was reviewed"));
        Assert.Contains(ArtifactKind, withoutArtifact.Message, StringComparison.Ordinal);

        var artifact = await RecordArtifactAsync(runService, run.Id, "the reviewed document");

        // A stored artifact is not a gate either: the first declared reviewer has decided nothing, and a
        // verdict from a role the stage never asked for cannot stand in for one it did.
        var withoutVerdicts = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the document was reviewed"));
        Assert.Contains(ReviewerRole, withoutVerdicts.Message, StringComparison.Ordinal);

        await RecordVerdictAsync(runService, provider, run.Id, ForeignRole, artifact);

        await RecordVerdictAsync(runService, provider, run.Id, ReviewerRole, artifact);

        var oneReviewerShort = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the document was reviewed"));
        Assert.Contains(ArchitectRole, oneReviewerShort.Message, StringComparison.Ordinal);

        await RecordVerdictAsync(runService, provider, run.Id, ArchitectRole, artifact);

        var withoutApproval = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the document was reviewed"));
        Assert.Contains("user approval", withoutApproval.Message, StringComparison.OrdinalIgnoreCase);

        // An approval of some other document is not an approval of this one, and it is refused outright at
        // the service boundary: the run service resolves the stage's current artifact, re-hashes its bytes
        // and compares the hash exactly, so a decision about a foreign document never reaches storage.
        var foreignApproval = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => RecordApprovalAsync(runService, run.Id, ForeignHash));
        Assert.Contains(artifact.HashSha256, foreignApproval.Message, StringComparison.Ordinal);
        Assert.Contains(ForeignHash, foreignApproval.Message, StringComparison.Ordinal);

        var wrongDocument = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the document was reviewed"));
        Assert.Contains(artifact.HashSha256, wrongDocument.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ForeignHash, wrongDocument.Message, StringComparison.Ordinal);

        await RecordApprovalAsync(runService, run.Id, artifact.HashSha256);

        var advanced = await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(provider, run.Id, "Historical aggregate fixture traversal after product refusal.");

        Assert.Equal(TerminalStageId, advanced.CurrentStageId);

        var transition = advanced.Transitions[^1];
        Assert.True(transition.HasAuthorizingArtifact);
        Assert.Equal(artifact.ArtifactId, transition.AuthorizingArtifactId);
        Assert.Equal(artifact.HashSha256, transition.AuthorizingArtifactHash);
        Assert.Equal(
            new[] { ReviewerRole, ArchitectRole },
            transition.ReviewerVerdicts.Select(verdict => verdict.ReviewerRole));
        Assert.NotNull(transition.UserApproval);
        Assert.Equal(GatedStageId, transition.UserApproval!.StageId);
        Assert.Equal(artifact.HashSha256, transition.UserApproval.ArtifactHash);
    }

    [Fact]
    public async Task ReopenedHistoricalGateClaimsRemainBlockedWithoutParsedResponseEvidence()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        await SaveAndAssignAsync(provider.GetRequiredService<IWorkflowTemplateStore>(), GatedStage);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);
        var artifact = await RecordArtifactAsync(runService, run.Id, "the reviewed document");
        await RecordVerdictAsync(runService, provider, run.Id, ReviewerRole, artifact);
        await RecordVerdictAsync(runService, provider, run.Id, ArchitectRole, artifact);
        await RecordApprovalAsync(runService, run.Id, artifact.HashSha256);

        // A full reopen: brand new repositories, store and service over the same database and the same blob
        // directory, with only the committed rows and the stored bytes left to go on.
        await using var reopened = CreateProvider();
        var reopenedService = reopened.GetRequiredService<IWorkflowRunService>();
        var reopenedRepository = reopened.GetRequiredService<IWorkflowRunRepository>();

        // The gate is still the run's own, and the process-wide standard scheme - whose first stage is a
        // different one entirely - is not what decides this transition.
        var beforeAdvance = await reopenedRepository.GetByIdAsync(run.Id);
        Assert.Equal(ArtifactKind, PinnedStage(beforeAdvance!, GatedStageId).ArtifactRequirement);

        var advanced = await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(reopened, run.Id, "Historical aggregate fixture traversal after product refusal.");

        Assert.Equal(TerminalStageId, advanced.CurrentStageId);

        var reloaded = await reopenedRepository.GetByIdAsync(run.Id);
        var transition = reloaded!.Transitions[0];

        Assert.Equal(artifact.ArtifactId, transition.AuthorizingArtifactId);
        Assert.Equal(artifact.HashSha256, transition.AuthorizingArtifactHash);
        Assert.Equal(
            new[] { ReviewerRole, ArchitectRole },
            transition.ReviewerVerdicts.Select(verdict => verdict.ReviewerRole));
        Assert.Equal(artifact.HashSha256, transition.UserApproval!.ArtifactHash);

        // The gate is still pinned with all four declared fields, and the terminal stage of the same chain
        // is still the gate-free stage the template declared it to be.
        var gated = PinnedStage(reloaded, GatedStageId);
        Assert.Equal(WorkflowStageKind.DocumentReview, gated.StageKind);
        Assert.Equal(new[] { ReviewerRole, ArchitectRole }, gated.RequiredReviewerRoles);
        Assert.True(gated.RequiresUserApproval);
        Assert.Equal(ArtifactKind, gated.ArtifactRequirement);

        var terminal = PinnedStage(reloaded, TerminalStageId);
        Assert.Null(terminal.ArtifactRequirement);
        Assert.Empty(terminal.RequiredReviewerRoles);
        Assert.False(terminal.RequiresUserApproval);
        Assert.Null(terminal.NextStageId);
    }

    [Fact]
    public async Task ANewerArtifactInvalidatesTheVerdictsAndTheApprovalOfTheOlderHash()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        await SaveAndAssignAsync(provider.GetRequiredService<IWorkflowTemplateStore>(), GatedStage);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);
        var first = await RecordArtifactAsync(runService, run.Id, "the first revision");
        await RecordVerdictAsync(runService, provider, run.Id, ReviewerRole, first);
        await RecordVerdictAsync(runService, provider, run.Id, ArchitectRole, first);
        await RecordApprovalAsync(runService, run.Id, first.HashSha256);

        var second = await RecordArtifactAsync(runService, run.Id, "the second revision");
        Assert.NotEqual(first.HashSha256, second.HashSha256);

        // Every verdict and the approval were given for bytes that are no longer the current ones.
        var superseded = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the first revision was approved"));
        Assert.Contains(second.HashSha256, superseded.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(first.HashSha256, superseded.Message, StringComparison.Ordinal);

        // A reviewer who signs the superseded hash again changes nothing - and is refused outright rather
        // than stored, because the run service resolves the stage's current artifact, re-hashes its bytes
        // and compares exactly, so a verdict about a document that is no longer the current one never
        // reaches storage in the first place.
        var staleHash = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => RecordVerdictAsync(runService, provider, run.Id, ReviewerRole, first));
        Assert.Contains(second.HashSha256, staleHash.Message, StringComparison.Ordinal);
        Assert.Contains(first.HashSha256, staleHash.Message, StringComparison.Ordinal);

        var staleArtifact = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => RecordVerdictAsync(runService, provider, run.Id, ArchitectRole, first));
        Assert.Contains(first.HashSha256, staleArtifact.Message, StringComparison.Ordinal);

        // And the run is still exactly where it was: no newer verdict for the current hash, so still blocked.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the first revision was approved"));

        await RecordVerdictAsync(runService, provider, run.Id, ReviewerRole, second);
        await RecordVerdictAsync(runService, provider, run.Id, ArchitectRole, second);

        var withoutApproval = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the second revision was reviewed"));
        Assert.Contains("user approval", withoutApproval.Message, StringComparison.OrdinalIgnoreCase);

        await RecordApprovalAsync(runService, run.Id, second.HashSha256);

        var advanced = await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(provider, run.Id, "Historical aggregate fixture traversal after product refusal.");

        Assert.Equal(second.ArtifactId, advanced.Transitions[^1].AuthorizingArtifactId);
        Assert.Equal(second.HashSha256, advanced.Transitions[^1].AuthorizingArtifactHash);
        Assert.Equal(TerminalStageId, advanced.CurrentStageId);
    }

    [Fact]
    public async Task ARejectBlocksTheGatedStageUntilThatSameReviewerApprovesTheCurrentHash()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        await SaveAndAssignAsync(provider.GetRequiredService<IWorkflowTemplateStore>(), GatedStage);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);
        var artifact = await RecordArtifactAsync(runService, run.Id, "the reviewed document");

        await RecordVerdictAsync(runService, provider, run.Id, ReviewerRole, artifact, WorkflowReviewVerdict.Reject);
        await RecordVerdictAsync(runService, provider, run.Id, ArchitectRole, artifact);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the document was reviewed"));
        Assert.Contains(ReviewerRole, rejected.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(WorkflowReviewVerdict.Reject), rejected.Message, StringComparison.Ordinal);

        // A unanimous approve on the same bytes replaces the reject, which is what "the latest verdict of
        // that role on that hash" has to mean for a re-review to be possible at all.
        await RecordVerdictAsync(runService, provider, run.Id, ReviewerRole, artifact);

        var withoutApproval = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the document was reviewed"));
        Assert.Contains("user approval", withoutApproval.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GatedStageId, (await ReloadAsync(provider, run.Id))!.CurrentStageId);
        Assert.Empty((await ReloadAsync(provider, run.Id))!.Transitions);
    }

    [Fact]
    public async Task ACommittedArtifactWhoseBytesAreGoneCannotAuthorizeTheGatedStage()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        await SaveAndAssignAsync(provider.GetRequiredService<IWorkflowTemplateStore>(), GatedStage);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);
        var artifact = await RecordArtifactAsync(runService, run.Id, "the reviewed document");
        await RecordVerdictAsync(runService, provider, run.Id, ReviewerRole, artifact);
        await RecordVerdictAsync(runService, provider, run.Id, ArchitectRole, artifact);
        await RecordApprovalAsync(runService, run.Id, artifact.HashSha256);

        // The row is complete in SQL and still names the same blob; only the bytes are gone.
        var blobStore = new WorkflowBlobStore(_database.Root);
        Assert.True(await blobStore.BlobExistsAsync(artifact.BlobId));
        File.Delete(blobStore.GetBlobPath(artifact.BlobId));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the reviewed document was approved"));
        Assert.Contains(artifact.ArtifactId, refused.Message, StringComparison.Ordinal);

        await using var reopened = CreateProvider();
        var reopenedService = reopened.GetRequiredService<IWorkflowRunService>();

        // And the same refusal survives the restart, with the run still at the gated stage and no transition.
        var reloaded = await reopened.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(run.Id);
        Assert.Equal(GatedStageId, reloaded!.CurrentStageId);
        Assert.Empty(reloaded.Transitions);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopenedService.AdvanceStageAsync(run.Id, "the reviewed document was approved"));

        Assert.Equal(GatedStageId, (await ReloadAsync(provider, run.Id))!.CurrentStageId);
    }

    /// <summary>
    /// A template edit and a moved assignment cannot reach a run that already exists, and the run still
    /// pinned to the gated version keeps refusing to move without its own evidence.
    ///
    /// The second half is the negative control for gate loss: on the same database, through the same
    /// composed service, a run pinned to the ungated version of the identical chain advances immediately
    /// with no artifact, no verdict and no approval. Any loss of the declared gate anywhere in the mapping,
    /// the snapshots or the durable rows would make the first run behave exactly like the second.
    /// </summary>
    [Fact]
    public async Task ANewerTemplateVersionDoesNotWeakenARunPinnedToTheGatedOne()
    {
        var provider = CreateProvider();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();
        var runService = provider.GetRequiredService<IWorkflowRunService>();

        await SaveAndAssignAsync(templateStore, GatedStage);

        var gatedRun = await runService.StartRunAsync(ProjectId, PackageId, VersionId);

        // The project now points at a version whose first node is the same node with no gate at all.
        await templateStore.SaveAsync(CreateTemplate(2, NoGateStage));
        await templateStore.SaveAssignmentAsync(
            new WorkflowTemplateAssignment("assignment-2", ProjectId, TemplateId, 2, AssignedAt));

        await using var reopened = CreateProvider();
        var reopenedService = reopened.GetRequiredService<IWorkflowRunService>();

        var stillBlocked = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reopenedService.AdvanceStageAsync(gatedRun.Id, "the template changed"));
        Assert.Contains(ArtifactKind, stillBlocked.Message, StringComparison.Ordinal);

        var ungatedRun = await reopenedService.StartRunAsync(ProjectId, PackageId, VersionId);
        var advanced = await reopenedService.AdvanceStageAsync(ungatedRun.Id, "nothing to satisfy");

        Assert.Equal(TerminalStageId, advanced.CurrentStageId);
        Assert.Empty(advanced.Artifacts);
        Assert.Empty(advanced.Verdicts);
        Assert.Empty(advanced.Approvals);
        Assert.Empty(advanced.Transitions[0].ReviewerVerdicts);
        Assert.Null(advanced.Transitions[0].UserApproval);
        Assert.False(advanced.Transitions[0].HasAuthorizingArtifact);
    }

    public static TheoryData<string> AcceptedGateFreeDeclarations()
    {
        var data = new TheoryData<string>();
        data.Add(AbsentStage);
        data.Add(NoGateStage);
        return data;
    }

    [Theory]
    [MemberData(nameof(AcceptedGateFreeDeclarations))]
    public async Task AGateFreeDeclarationIsStillAcceptedAndNeedsNoEvidence(string declaration)
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await SaveAndAssignAsync(templateStore, declaration);

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);
        var advanced = await runService.AdvanceStageAsync(run.Id, "no gate declared");

        Assert.Equal(TerminalStageId, advanced.CurrentStageId);

        // Absent metadata and the explicit no-gate tuple keep the prior mapping, and both are visibly
        // gate-free afterwards rather than a gate that happened to pass.
        var stage = PinnedStage((await ReloadAsync(provider, run.Id))!, GatedStageId);
        Assert.Equal(WorkflowStageKind.Custom, stage.StageKind);
        Assert.Empty(stage.RequiredReviewerRoles);
        Assert.False(stage.RequiresUserApproval);
        Assert.Null(stage.ArtifactRequirement);
    }

    public static TheoryData<WorkflowNodeGateMetadata, string> UnenforceableTerminalGates()
    {
        var data = new TheoryData<WorkflowNodeGateMetadata, string>();

        data.Add(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.FinalVerification,
                new[] { ReviewerRole },
                requiresUserApproval: false,
                artifactRequirement: "ApprovedDocument"),
            "1 required reviewer(s) (Reviewer)");

        data.Add(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.UserApproval,
                Array.Empty<string>(),
                requiresUserApproval: true,
                artifactRequirement: "ApprovedDocument"),
            "user approval required");

        data.Add(
            new WorkflowNodeGateMetadata(
                WorkflowStageKind.FinalVerification,
                Array.Empty<string>(),
                requiresUserApproval: false,
                artifactRequirement: "ApprovedDocument"),
            "artifact requirement 'ApprovedDocument'");

        return data;
    }

    [Theory]
    [MemberData(nameof(UnenforceableTerminalGates))]
    public async Task ATerminalGateNoRunCouldEvaluateLeavesNoRunAtAll(
        WorkflowNodeGateMetadata terminalGate,
        string expectedDeclaration)
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await templateStore.SaveAsync(CreateTemplate(1, AbsentStage, terminalGate: terminalGate));
        await templateStore.SaveAssignmentAsync(
            new WorkflowTemplateAssignment("assignment-1", ProjectId, TemplateId, 1, AssignedAt));

        // The store keeps the declaration, so the refusal comes from the run start and not from a save that
        // never happened.
        var stored = await templateStore.GetAsync(TemplateId, 1);
        Assert.Equal(
            terminalGate.StageKind,
            stored!.Graph.GetRequiredNode(TerminalStageId).GateMetadata!.StageKind);

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => runService.StartRunAsync(ProjectId, PackageId, VersionId));

        Assert.Equal(WorkflowTemplateExecutionBlockers.TerminalNodeGate, blocked.Blocker);
        Assert.Contains(TerminalStageId, blocked.Message, StringComparison.Ordinal);
        Assert.Contains(expectedDeclaration, blocked.Message, StringComparison.Ordinal);
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns;"));
    }

    public static TheoryData<WorkflowNodeGateMetadata> UnenforceablePromptGates()
    {
        var data = new TheoryData<WorkflowNodeGateMetadata>();

        data.Add(new WorkflowNodeGateMetadata(
            WorkflowStageKind.DocumentReview,
            new[] { ReviewerRole },
            requiresUserApproval: false,
            artifactRequirement: null));

        data.Add(new WorkflowNodeGateMetadata(
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: true,
            artifactRequirement: null));

        return data;
    }

    [Theory]
    [MemberData(nameof(UnenforceablePromptGates))]
    public async Task AReviewerOrApprovalGateWithNoArtifactLeavesNoRunAtAll(WorkflowNodeGateMetadata gate)
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await templateStore.SaveAsync(CreateTemplate(1, GatedStage, gateOverride: gate));
        await templateStore.SaveAssignmentAsync(
            new WorkflowTemplateAssignment("assignment-1", ProjectId, TemplateId, 1, AssignedAt));

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => runService.StartRunAsync(ProjectId, PackageId, VersionId));

        Assert.Equal(WorkflowTemplateExecutionBlockers.UnrepresentableNodeGate, blocked.Blocker);
        Assert.Contains(GatedStageId, blocked.Message, StringComparison.Ordinal);
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns;"));
    }

    public static TheoryData<string, string> RefusedGraphShapes()
    {
        var data = new TheoryData<string, string>();

        data.Add("condition", WorkflowTemplateExecutionBlockers.Condition);
        data.Add("permission-intent", WorkflowTemplateExecutionBlockers.PermissionIntent);

        return data;
    }

    /// <summary>
    /// Mapping gates exactly must not have widened what this slice accepts. Every graph feature that is
    /// still refused is still refused before a run row exists, even when the nodes also declare a gate that
    /// would itself have been perfectly enforceable.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedGraphShapes))]
    public async Task AGraphFeatureOutsideTheSliceIsStillRefusedBeforeAnyRunIsInserted(
        string shape,
        string expectedBlocker)
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await templateStore.SaveAsync(CreateRefusedTemplate(shape));
        await templateStore.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            $"assignment-{shape}",
            ProjectId,
            $"refused-{shape}",
            1,
            AssignedAt));

        // The version is storable, so the refusal comes from the run start and not from a save that never
        // happened, and the declared gate really is on the stored node.
        var stored = await templateStore.GetAsync($"refused-{shape}", 1);
        Assert.Equal(DeclaredGate.StageKind, stored!.Graph.GetRequiredNode(GatedStageId).GateMetadata!.StageKind);

        var blocked = await Assert.ThrowsAsync<WorkflowTemplateExecutionBlockedException>(
            () => runService.StartRunAsync(ProjectId, PackageId, VersionId));

        Assert.Equal(expectedBlocker, blocked.Blocker);
        Assert.Equal("0", await ReadSingleAsync("SELECT COUNT(*) FROM WorkflowRuns;"));
    }

    /// <summary>
    /// A review node that declares the reviewer its kind names, a declared failure target and a declared
    /// retry budget are all mapped now, and none of them adds anything the node did not declare. The gate
    /// that was on the node is the gate the stage enforces, the failure edge is carried onto the stage and
    /// is never followed, and the retry budget is preserved on the pinned graph without becoming a second
    /// chance at the transition.
    /// </summary>
    [Theory]
    [InlineData("review-kind")]
    [InlineData("failure-edge")]
    [InlineData("retry-budget")]
    public async Task ARepresentedGraphFeatureChangesNothingAboutTheGateItSitsOn(string shape)
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await templateStore.SaveAsync(CreateRefusedTemplate(shape));
        await templateStore.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            $"refused-{shape}",
            ProjectId,
            $"refused-{shape}",
            1,
            AssignedAt));

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);
        var stored = (await ReloadAsync(provider, run.Id))!;
        var stage = PinnedStage(stored, GatedStageId);

        // The declared gate is untouched by the feature, so the same three refusals still apply in the same
        // order: no artifact, no reviewer verdicts, no approval.
        Assert.Equal(DeclaredGate.StageKind, stage.StageKind);
        Assert.Equal(DeclaredGate.RequiredReviewerRoles, stage.RequiredReviewerRoles);
        Assert.Equal(DeclaredGate.RequiresUserApproval, stage.RequiresUserApproval);
        Assert.Equal(DeclaredGate.ArtifactRequirement, stage.ArtifactRequirement);

        var withoutArtifact = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the document was reviewed"));
        Assert.Contains(ArtifactKind, withoutArtifact.Message, StringComparison.Ordinal);

        // The failure edge is on the stage, on the pinned scheme and on the pinned graph, and the run has
        // not moved along it: the refused transition left it on the stage whose gate refused it.
        var node = WorkflowGraphSnapshot.Deserialize(stored.TemplateGraphSnapshotJson!, run.Id)
            .GetRequiredNode(GatedStageId);

        if (shape == "failure-edge")
        {
            Assert.Equal("node-c", stage.FailureStageId);
            Assert.Equal("node-c", node.FailureTargetNodeId);
            Assert.NotNull(PinnedStageOrNull(stored, "node-c"));
        }
        else
        {
            Assert.Null(stage.FailureStageId);
            Assert.Null(node.FailureTargetNodeId);
        }

        if (shape == "retry-budget")
        {
            // Preserved on the graph, where the node executor will read it, and nowhere else: the stage and
            // the scheme document carry no retry of any kind.
            Assert.Equal(2, node.RetryBudget);
            Assert.DoesNotContain("retry", stored.TemplateSchemeSnapshotJson!, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(GatedStageId, stored.CurrentStageId);
        Assert.Empty(stored.Transitions);
        Assert.Equal(WorkflowTerminalOutcome.None, stored.TerminalOutcome);
        Assert.Null(stored.EndedAtUtc);

        // And the gate is still a real gate once its evidence exists.
        var artifact = await RecordArtifactAsync(runService, run.Id, "the reviewed document");
        await RecordVerdictAsync(runService, provider, run.Id, ReviewerRole, artifact);
        await RecordVerdictAsync(runService, provider, run.Id, ArchitectRole, artifact);

        var withoutApproval = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the document was reviewed"));
        Assert.Contains("user approval", withoutApproval.Message, StringComparison.OrdinalIgnoreCase);

        await RecordApprovalAsync(runService, run.Id, artifact.HashSha256);

        var advanced = await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(provider, run.Id, "Historical aggregate fixture traversal after product refusal.");

        Assert.Equal(TerminalStageId, advanced.CurrentStageId);
    }

    /// <summary>
    /// A failure target the template declared is never taken, and the attempt to take it is refused by name
    /// rather than being answered with the success target or with a moved run. The run stays exactly where
    /// it is, with no transition recorded and no terminal outcome invented from the refusal.
    /// </summary>
    [Fact]
    public async Task ADeclaredFailureRouteIsRefusedByNameAndNeverTaken()
    {
        var provider = CreateProvider();
        var runService = provider.GetRequiredService<IWorkflowRunService>();
        var templateStore = provider.GetRequiredService<IWorkflowTemplateStore>();

        await templateStore.SaveAsync(CreateRefusedTemplate("failure-edge"));
        await templateStore.SaveAssignmentAsync(new WorkflowTemplateAssignment(
            "assignment-failure-edge",
            ProjectId,
            "refused-failure-edge",
            1,
            AssignedAt));

        var run = await runService.StartRunAsync(ProjectId, PackageId, VersionId);
        var artifact = await RecordArtifactAsync(runService, run.Id, "the reviewed document");
        await RecordVerdictAsync(
            runService,
            provider,
            run.Id,
            ReviewerRole,
            artifact,
            WorkflowReviewVerdict.RequestChanges);
        await RecordVerdictAsync(
            runService,
            provider,
            run.Id,
            ArchitectRole,
            artifact,
            WorkflowReviewVerdict.RequestChanges);

        // A rejecting reviewer never sends the run to the success target.
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runService.AdvanceStageAsync(run.Id, "the review rejected the document"));
        Assert.Contains(ReviewerRole, rejected.Message, StringComparison.Ordinal);

        // And the failure route the stage declares cannot be taken either: the attempt is refused, by name,
        // with the stage and its declared target, and the run does not move.
        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.AdvanceToDeclaredFailureStageAsync(run.Id, "the review rejected the document"));
        Assert.Contains(
            WorkflowRunFailureRouteRefusals.FailureRouteNotExecutable,
            refused.Message,
            StringComparison.Ordinal);
        Assert.Contains("node-c", refused.Message, StringComparison.Ordinal);
        Assert.Contains(GatedStageId, refused.Message, StringComparison.Ordinal);

        var afterRefusal = (await ReloadAsync(provider, run.Id))!;
        Assert.Equal(GatedStageId, afterRefusal.CurrentStageId);
        Assert.Empty(afterRefusal.Transitions);
        Assert.Equal(WorkflowRunState.Running, afterRefusal.State);
        Assert.Equal(WorkflowTerminalOutcome.None, afterRefusal.TerminalOutcome);
        Assert.Null(afterRefusal.EndedAtUtc);

        // The edge itself is still there, on the pinned scheme and on the pinned graph, because a refused
        // transition does not erase what the template declared.
        Assert.Equal("node-c", PinnedStage(afterRefusal, GatedStageId).FailureStageId);

        // The same run goes on to pass its gate: the failure route was refused, not substituted for. Both
        // reviewers approve the same hash, the approval is recorded, and only then does the transition
        // happen - to the success target, on the stage the pinned scheme names as next.
        await RecordVerdictAsync(runService, provider, run.Id, ReviewerRole, artifact);
        await RecordVerdictAsync(runService, provider, run.Id, ArchitectRole, artifact);
        await RecordApprovalAsync(runService, run.Id, artifact.HashSha256);

        var advanced = await HistoricalWorkflowReviewFixture.AdvanceAggregateAsync(provider, run.Id, "Historical aggregate fixture traversal after product refusal.");

        Assert.Equal(TerminalStageId, advanced.CurrentStageId);

        // A stage that declares no failure target says so instead, and is refused by the other name.
        var noRoute = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => runService.AdvanceToDeclaredFailureStageAsync(run.Id, "no route here"));
        Assert.Contains(
            WorkflowRunFailureRouteRefusals.NoDeclaredFailureRoute,
            noRoute.Message,
            StringComparison.Ordinal);
    }

    private static WorkflowStageDefinition? PinnedStageOrNull(WorkflowRun run, string stageId) =>
        WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson!, run.Id).Scheme.FindStage(stageId);

    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<ISqliteConnectionFactory>(_database.Factory);
        services.AddSingleton(new WorkflowBlobStore(_database.Root));
        services.AddSingleton<TimeProvider>(_timeProvider);

        // A pinned stage is authorized by a persisted reviewer execution, so the run service needs the stores
        // that read and write them. They are registered here from the same connection factory rather than
        // through the full infrastructure composition, so this file still tests one thing at a time. The
        // redaction filter is the execution store's own dependency and is registered with it.
        services.AddSingleton<SensitiveDataFilter>();
        services.AddSingleton<ISessionRepository, SqliteSessionRepository>();
        services.AddSingleton<IExecutionRepository, SqliteExecutionRepository>();
        services.AddSingleton<IProjectRepository, SqliteProjectRepository>();

        services.AddWorkflowServices();

        return services.BuildServiceProvider();
    }

    private async Task SaveAndAssignAsync(IWorkflowTemplateStore store, string declaration)
    {
        await store.SaveAsync(CreateTemplate(1, declaration));
        await store.SaveAssignmentAsync(
            new WorkflowTemplateAssignment("assignment-1", ProjectId, TemplateId, 1, AssignedAt));
    }

    /// <summary>
    /// One Prompt node and one terminal outcome node, differing only in what the first node declares:
    /// nothing at all, the explicit no-gate tuple, or a reviewer gate with an approval over a named
    /// artifact. The terminal node declares nothing, so the chain ends on a stage with nothing to decide.
    /// </summary>
    private static WorkflowTemplateDefinition CreateTemplate(
        int version,
        string declaration,
        WorkflowNodeGateMetadata? gateOverride = null,
        WorkflowNodeGateMetadata? terminalGate = null)
    {
        var gate = gateOverride ?? declaration switch
        {
            GatedStage => DeclaredGate,
            NoGateStage => new WorkflowNodeGateMetadata(
                WorkflowStageKind.Custom,
                Array.Empty<string>(),
                requiresUserApproval: false,
                artifactRequirement: null),
            _ => null
        };

        return new WorkflowTemplateDefinition(
            TemplateId,
            version,
            $"Gated template v{version}",
            "A linear template whose first stage may declare stage gates.",
            new WorkflowGraph(
                GatedStageId,
                new[]
                {
                    new WorkflowNodeDefinition(
                        GatedStageId,
                        WorkflowNodeKind.Prompt,
                        "Node A",
                        "Role A",
                        successTargetNodeId: TerminalStageId,
                        gateMetadata: gate),
                    new WorkflowNodeDefinition(
                        TerminalStageId,
                        WorkflowNodeKind.TerminalOutcome,
                        "Node B",
                        "Role B",
                        gateMetadata: terminalGate)
                }),
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            AssignedAt);
    }

    /// <summary>
    /// Graphs that are storable - the durable store accepts them - but carry one feature this slice does
    /// not execute, on a node that also declares a fully enforceable gate. The gate is the control: it is
    /// mapped exactly when nothing else in the graph is outside the slice, so a refusal here can only have
    /// come from the graph feature.
    /// </summary>
    private static WorkflowTemplateDefinition CreateRefusedTemplate(string shape)
    {
        var gated = DeclaredGate;
        var terminal = new WorkflowNodeDefinition(
            TerminalStageId,
            WorkflowNodeKind.TerminalOutcome,
            "Node B",
            "Role B");

        var graph = shape switch
        {
            "review-kind" => new WorkflowGraph(GatedStageId, new[]
            {
                new WorkflowNodeDefinition(
                    GatedStageId,
                    WorkflowNodeKind.Review,
                    "Node A",
                    "Role A",
                    successTargetNodeId: TerminalStageId,
                    gateMetadata: gated),
                terminal
            }),
            "failure-edge" => new WorkflowGraph(GatedStageId, new[]
            {
                new WorkflowNodeDefinition(
                    GatedStageId,
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: TerminalStageId,
                    failureTargetNodeId: "node-c",
                    gateMetadata: gated),
                terminal,
                new WorkflowNodeDefinition("node-c", WorkflowNodeKind.TerminalOutcome, "Node C", "Role C")
            }),
            "retry-budget" => new WorkflowGraph(GatedStageId, new[]
            {
                new WorkflowNodeDefinition(
                    GatedStageId,
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    retryBudget: 2,
                    successTargetNodeId: TerminalStageId,
                    gateMetadata: gated),
                terminal
            }),
            "condition" => new WorkflowGraph(GatedStageId, new[]
            {
                new WorkflowNodeDefinition(
                    GatedStageId,
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: TerminalStageId,
                    conditionExpression: "always",
                    gateMetadata: gated),
                terminal
            }),
            "permission-intent" => new WorkflowGraph(GatedStageId, new[]
            {
                new WorkflowNodeDefinition(
                    GatedStageId,
                    WorkflowNodeKind.Prompt,
                    "Node A",
                    "Role A",
                    successTargetNodeId: TerminalStageId,
                    permissionIntent: "write",
                    gateMetadata: gated),
                terminal
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown refused shape.")
        };

        return new WorkflowTemplateDefinition(
            $"refused-{shape}",
            1,
            $"Refused {shape}",
            "A linear template this slice refuses to pin.",
            graph,
            Array.Empty<RoleBindingDefinition>(),
            Array.Empty<DocumentTemplateKind>(),
            isBuiltIn: false,
            AssignedAt);
    }

    private static WorkflowStageDefinition PinnedStage(WorkflowRun run, string stageId) =>
        WorkflowSchemeSnapshot.Deserialize(run.TemplateSchemeSnapshotJson!, run.Id).Scheme.GetRequiredStage(stageId);

    private static Task<WorkflowRun?> ReloadAsync(ServiceProvider provider, string runId) =>
        provider.GetRequiredService<IWorkflowRunRepository>().GetByIdAsync(runId);

    private async Task<WorkflowArtifactEvidence> RecordArtifactAsync(
        IWorkflowRunService runService,
        string runId,
        string content)
    {
        var run = await runService.RecordStageArtifactAsync(
            runId,
            GatedStageId,
            ArtifactKind,
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content), writable: false),
            DataClassification.PrivateSource);

        return WorkflowArtifactEvidence.SelectCurrent(run.Artifacts, runId, GatedStageId, ArtifactKind)!;
    }

    private Task RecordVerdictAsync(
        IWorkflowRunService runService,
        ServiceProvider provider,
        string runId,
        string role,
        WorkflowArtifactEvidence artifact,
        WorkflowReviewVerdict verdict = WorkflowReviewVerdict.Approve) =>
        RecordVerdictAsync(runService, provider, runId, role, artifact.HashSha256, artifact, verdict);

    /// <summary>
    /// A verdict the pinned gate can actually be satisfied by: a persisted, read-only reviewer execution of
    /// this run, on this stage, for this role, on a real <c>Routes</c> row, whose observed route is the route
    /// that was requested, about the exact artifact row the verdict names.
    /// <para>
    /// One execution serves every verdict that role signs for the same artifact, which is what a re-review
    /// after a rejection looks like: the same reviewer turn, read again, rather than a second turn nobody
    /// can distinguish from a replay. A different artifact needs a different execution, and the store's
    /// uniqueness on run, stage, role and reviewed hash is what enforces that.
    /// </para>
    /// </summary>
    private async Task RecordVerdictAsync(
        IWorkflowRunService runService,
        ServiceProvider provider,
        string runId,
        string role,
        string documentHash,
        WorkflowArtifactEvidence artifact,
        WorkflowReviewVerdict verdict = WorkflowReviewVerdict.Approve)
    {
        var executionId = await EnsureReviewerExecutionAsync(provider, runId, role, artifact)
            .ConfigureAwait(false);

        await HistoricalWorkflowReviewFixture.SeedAsync(
            provider, runId,
            new ReviewerVerdictRecord(
                role,
                ReviewerRouteId,
                documentHash,
                verdict,
                "Historical caller-authored fixture claim; no parsed response exists.",
                _timeProvider.GetUtcNow(),
                executionId,
                GatedStageId,
                artifact.ArtifactId),
            CancellationToken.None);
    }

    /// <summary>
    /// The reviewer execution for one role and one artifact, written once and reused afterwards.
    /// <para>
    /// The rows are real: a session bound to the route's own account, profile and model, an execution whose
    /// requested and observed routes are the same real <c>Routes</c> row - the observed one written only
    /// because a backend reported it, which is what the deterministic channel in the model-review tests
    /// stands in for - and a binding of that execution to this run, stage, role and artifact.
    /// </para>
    /// </summary>
    private async Task<string> EnsureReviewerExecutionAsync(
        ServiceProvider provider,
        string runId,
        string role,
        WorkflowArtifactEvidence artifact)
    {
        var key = $"{runId}|{role}|{artifact.HashSha256}";

        if (_reviewerExecutions.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var now = _timeProvider.GetUtcNow();
        var sessionId = Guid.NewGuid().ToString("N");
        var executionId = Guid.NewGuid().ToString("N");

        await provider.GetRequiredService<ISessionRepository>()
            .UpsertAsync(
                new Session(
                    sessionId,
                    new SessionBinding(BackendType.OpenCode, ProviderProfileId, AccountId, ModelId, null, null, null),
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
                    ExecutionState.Succeeded,
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
            GatedStageId,
            ReviewerRouteId,
            observedRouteId: null,
            artifact.ArtifactId,
            artifact.HashSha256,
            isReadOnly: true,
            ExecutionState.Succeeded);

        await evidenceRepository.SaveAsync(evidence, CancellationToken.None);
        await evidenceRepository.UpdateObservedAsync(
            evidence.WithObservedOutcome(ReviewerRouteId, ExecutionState.Succeeded),
            CancellationToken.None);

        _reviewerExecutions[key] = executionId;

        return executionId;
    }

    private Task RecordApprovalAsync(IWorkflowRunService runService, string runId, string artifactHash) =>
        runService.RecordUserApprovalAsync(
            runId,
            new UserApprovalEvidence(
                Guid.NewGuid().ToString("N"),
                "user-1",
                GatedStageId,
                artifactHash,
                UserApprovalDecision.Approved,
                "approved",
                _timeProvider.GetUtcNow()));

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

    private async Task<string> ReadSingleAsync(string sql)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? string.Empty : Convert.ToString(value)!;
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
