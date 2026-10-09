using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

/// <summary>
/// The service boundary of a product user approval, on top of a run repository that hands out a fresh run
/// instance on every read - which is what a durable store does, and what makes "the evidence is resolved and
/// re-verified immediately before the decision is stored" observable at all.
/// <para>
/// Every case asserts two things at once: what the service refused or recorded, and that the stored run did
/// not move. An approval that names somebody else, a hash of a document that has been replaced, a stage the
/// run has already left, a blob whose bytes are gone and an identity that cannot be established are all
/// refused here, so no caller - a screen or anything else - can get past them by skipping its own checks.
/// </para>
/// </summary>
public sealed class WorkflowRunUserApprovalServiceTests
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionId = "version-1";
    private const string ApprovalStageId = WorkflowScheme.UserApprovalStageId;
    private const string Kind = "ApprovedDocument";
    private const string Approver = @"WORKGROUP\local-operator";
    private const string ForgedApprover = "studio-user";

    /// <summary>A well-formed hash that is the hash of nothing this run has ever stored.</summary>
    private static readonly string ForeignHash = "sha256:" + new string('b', 64);

    private static readonly DateTimeOffset BaseTime = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly CopyingWorkflowRunRepository _repository = new();
    private readonly InMemoryWorkflowArtifactBlobStore _blobStore = new();
    private readonly SteppingTimeProvider _timeProvider = new(BaseTime);
    private FixedUserApprovalIdentity _identity = new(Approver);
    private WorkflowRunService _service = null!;

    public WorkflowRunUserApprovalServiceTests()
    {
        _service = CreateService();
    }

    [Fact]
    public async Task AnApprovalOfTheExactCurrentHashIsStoredUnderTheResolvedIdentity()
    {
        var run = await StartRunAtUserApprovalAsync();
        var artifact = await RecordAsync(run, "the approved document");
        var before = await _repository.StoredApprovalCountAsync(run.Id);

        var recorded = await _service.RecordUserApprovalAsync(run.Id, CreateApproval(artifact.HashSha256));

        var approval = Assert.Single(recorded.Approvals);
        Assert.Equal(ApprovalStageId, approval.StageId);
        Assert.Equal(artifact.HashSha256, approval.ArtifactHash);
        Assert.Equal(UserApprovalDecision.Approved, approval.Decision);
        Assert.Equal(Approver, approval.ApprovedBy);
        Assert.Equal(before + 1, await _repository.StoredApprovalCountAsync(run.Id));
    }

    [Fact]
    public async Task ACallerSuppliedApproverIsReplacedByTheResolvedIdentity()
    {
        var run = await StartRunAtUserApprovalAsync();
        var artifact = await RecordAsync(run, "the approved document");

        var recorded = await _service.RecordUserApprovalAsync(run.Id, CreateApproval(artifact.HashSha256));

        // The name that arrived with the evidence is not a name anybody is approved under, and it is not in
        // the record afterwards either.
        Assert.Equal(ForgedApprover, CreateApproval(artifact.HashSha256).ApprovedBy);
        Assert.DoesNotContain(
            recorded.Approvals,
            approval => string.Equals(approval.ApprovedBy, ForgedApprover, StringComparison.Ordinal));
        Assert.Equal(Approver, Assert.Single(recorded.Approvals).ApprovedBy);
    }

    [Fact]
    public async Task ARejectionIsStoredAndEndsTheRunWithTheRejectedOutcome()
    {
        var run = await StartRunAtUserApprovalAsync();
        var artifact = await RecordAsync(run, "the approved document");

        var recorded = await _service.RecordUserApprovalAsync(
            run.Id,
            CreateApproval(
                artifact.HashSha256,
                UserApprovalDecision.Rejected,
                comment: "The document is not acceptable."));

        Assert.Equal(UserApprovalDecision.Rejected, Assert.Single(recorded.Approvals).Decision);
        Assert.Equal(WorkflowRunState.Failed, recorded.State);
        Assert.Equal(WorkflowTerminalOutcome.Rejected, recorded.TerminalOutcome);
        Assert.Equal("The document is not acceptable.", recorded.TerminalReason);

        // And a terminal run accepts nothing afterwards, in either direction.
        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync(run.Id, CreateApproval(artifact.HashSha256)));
        Assert.Contains("terminal", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnApprovalOfAnotherDocumentIsRefused()
    {
        var run = await StartRunAtUserApprovalAsync();
        var artifact = await RecordAsync(run, "the approved document");
        var before = await _repository.StoredApprovalCountAsync(run.Id);

        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync(run.Id, CreateApproval(ForeignHash)));

        Assert.Contains(artifact.HashSha256, refused.Message, StringComparison.Ordinal);
        Assert.Contains(ForeignHash, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, await _repository.StoredApprovalCountAsync(run.Id));
    }

    [Fact]
    public async Task AnApprovalOfAStageTheRunHasLeftIsRefused()
    {
        var run = await StartRunAtUserApprovalAsync();
        var artifact = await RecordAsync(run, "the approved document");
        var before = await _repository.StoredApprovalCountAsync(run.Id);

        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync(
                run.Id,
                CreateApproval(artifact.HashSha256, stageId: WorkflowScheme.RoadmapStageId)));

        Assert.Contains(ApprovalStageId, refused.Message, StringComparison.Ordinal);
        Assert.Contains(WorkflowScheme.RoadmapStageId, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, await _repository.StoredApprovalCountAsync(run.Id));
    }

    [Fact]
    public async Task AnApprovalWithoutAStoredArtifactIsRefused()
    {
        var run = await StartRunAtUserApprovalAsync();
        var before = await _repository.StoredApprovalCountAsync(run.Id);

        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync(run.Id, CreateApproval(ForeignHash)));

        Assert.Contains(Kind, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, await _repository.StoredApprovalCountAsync(run.Id));
    }

    [Fact]
    public async Task AnApprovalWithoutAStoredArtifactOfTheStagesKindIsRefused()
    {
        var run = await StartRunAtUserApprovalAsync();
        var before = await _repository.StoredApprovalCountAsync(run.Id);

        // The stage's own artifact requirement is the only kind the service looks for, so a decision with no
        // stored artifact of that kind names nothing anybody could have approved.
        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync(run.Id, CreateApproval(ForeignHash)));

        Assert.Contains(Kind, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, await _repository.StoredApprovalCountAsync(run.Id));
    }

    [Fact]
    public async Task AnApprovalOfAnArtifactWhoseBytesAreGoneIsRefused()
    {
        var run = await StartRunAtUserApprovalAsync();
        var artifact = await RecordAsync(run, "the approved document");

        // The row survives the bytes: the decision is refused because the content cannot be re-hashed, not
        // because the row disappeared.
        _blobStore.Delete(artifact.BlobId);

        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync(run.Id, CreateApproval(artifact.HashSha256)));

        Assert.Contains(artifact.ArtifactId, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApprovalOfAnArtifactWhoseBytesWereAlteredIsRefused()
    {
        var run = await StartRunAtUserApprovalAsync();
        var artifact = await RecordAsync(run, "the approved document");

        _blobStore.Alter(artifact.BlobId, System.Text.Encoding.UTF8.GetBytes("a different document"));

        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync(run.Id, CreateApproval(artifact.HashSha256)));

        Assert.Contains(artifact.ArtifactId, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterAReplacementTheOlderHashStopsAuthorizingAndTheNewerOneDoesNot()
    {
        var run = await StartRunAtUserApprovalAsync();
        var first = await RecordAsync(run, "the first approved document");

        await _service.RecordUserApprovalAsync(run.Id, CreateApproval(first.HashSha256));

        // The artifact is replaced after the decision was taken. The recorded decision stays in the history
        // - it happened - but it no longer names the current content.
        var second = await RecordAsync(run, "the second approved document");

        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync(run.Id, CreateApproval(first.HashSha256)));
        Assert.Contains(second.HashSha256, refused.Message, StringComparison.Ordinal);

        // Re-deciding on the new hash is what authorizes the transition, and the transition itself refuses
        // while only the older decision stands.
        var stillBlocked = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(run.Id, "the first document was approved"));
        Assert.Contains(second.HashSha256, stillBlocked.Message, StringComparison.Ordinal);

        var approved = await _service.RecordUserApprovalAsync(run.Id, CreateApproval(second.HashSha256));
        var advanced = await _service.AdvanceStageAsync(run.Id, "the second document was approved");

        Assert.Equal(2, approved.Approvals.Count);
        Assert.Equal(WorkflowScheme.ImplementationPackagesStageId, advanced.CurrentStageId);
        Assert.Equal(second.HashSha256, advanced.Transitions[^1].UserApproval!.ArtifactHash);
    }

    [Fact]
    public async Task AnApprovalWithoutAnEstablishableIdentityIsRefused()
    {
        var run = await StartRunAtUserApprovalAsync();
        var artifact = await RecordAsync(run, "the approved document");
        var before = await _repository.StoredApprovalCountAsync(run.Id);

        _identity = FixedUserApprovalIdentity.Unavailable;
        _service = CreateService();

        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync(run.Id, CreateApproval(artifact.HashSha256)));

        Assert.Contains("Windows", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await _repository.StoredApprovalCountAsync(run.Id));
    }

    [Fact]
    public async Task AnApprovalOfARunThatDoesNotExistIsRefused()
    {
        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync("missing-run", CreateApproval(ForeignHash)));

        Assert.Contains("missing-run", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServiceRequiresItsIdentitySource()
    {
        Assert.Throws<ArgumentNullException>(() => new WorkflowRunService(
            _repository,
            WorkflowScheme.CreateStandardDevelopmentScheme(),
            new InMemoryWorkflowTemplateStore(),
            _blobStore,
            userApprovalIdentity: null!));

        // A composition that could not name an approver has to fail here rather than at the first decision.
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.RecordUserApprovalAsync("any-run", null!));
    }

    private WorkflowRunService CreateService() =>
        new(
            _repository,
            WorkflowScheme.CreateStandardDevelopmentScheme(),
            new InMemoryWorkflowTemplateStore(),
            _blobStore,
            _identity,
            _timeProvider);

    /// <summary>
    /// Walks the standard scheme to the stage that requires an explicit user approval, recording the
    /// artifact each stage it leaves produced, so the stage under test really is its own current stage.
    /// </summary>
    private async Task<WorkflowRun> StartRunAtUserApprovalAsync()
    {
        var run = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId);
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();

        foreach (var stageId in new[]
                 {
                     WorkflowScheme.TaskSpecificationStageId,
                     WorkflowScheme.ArchitectureStageId,
                     WorkflowScheme.TechnicalSpecificationStageId,
                     WorkflowScheme.RoadmapStageId,
                     WorkflowScheme.DocumentReviewStageId
                 })
        {
            var stage = scheme.GetRequiredStage(stageId);
            var artifact = await RecordArtifactAtAsync(
                run.Id,
                stage.StageId,
                stage.ArtifactRequirement!,
                $"artifact of '{stage.StageId}'");

            foreach (var reviewerRole in stage.RequiredReviewerRoles)
            {
                await RecordVerdictAsync(run.Id, reviewerRole, artifact.HashSha256);
            }

            run = await _service.AdvanceStageAsync(run.Id, $"left {stage.StageId}");
        }

        Assert.Equal(ApprovalStageId, run.CurrentStageId);
        Assert.Equal(Kind, scheme.GetRequiredStage(run.CurrentStageId).ArtifactRequirement);

        return run;
    }

    private async Task<WorkflowArtifactEvidence> RecordAsync(WorkflowRun run, string content) =>
        await RecordArtifactAtAsync(run.Id, ApprovalStageId, Kind, content);

    private async Task<WorkflowArtifactEvidence> RecordArtifactAtAsync(
        string runId,
        string stageId,
        string kind,
        string content)
    {
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        var recorded = await _service.RecordStageArtifactAsync(
            runId,
            stageId,
            kind,
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content), writable: false),
            DataClassification.PrivateSource);

        return WorkflowArtifactEvidence.SelectCurrent(recorded.Artifacts, runId, stageId, kind)!;
    }

    private async Task RecordVerdictAsync(string runId, string role, string documentHash)
    {
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        await _service.RecordLegacyUnlinkedReviewerVerdictAsync(
            runId,
            new ReviewerVerdictRecord(
                role,
                "route-1",
                documentHash,
                WorkflowReviewVerdict.Approve,
                "reviewed",
                _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// The evidence a caller hands in. The approver in it is deliberately a name nobody is approved under:
    /// the service is required to replace it.
    /// </summary>
    private UserApprovalEvidence CreateApproval(
        string artifactHash,
        UserApprovalDecision decision = UserApprovalDecision.Approved,
        string? stageId = null,
        string? comment = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            ForgedApprover,
            stageId ?? ApprovalStageId,
            artifactHash,
            decision,
            comment ?? "Reviewed and decided by the operator.",
            _timeProvider.GetUtcNow());

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public SteppingTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>
    /// A repository that rebuilds the run on every read, the way a durable one does, so an in-memory
    /// reference a test still holds can never be what a later command sees.
    /// </summary>
    private sealed class CopyingWorkflowRunRepository : IWorkflowRunRepository
    {
        private readonly Dictionary<string, WorkflowRun> _runs = new(StringComparer.Ordinal);

        /// <summary>How many decisions storage actually holds for a run, read without a copy in between.</summary>
        public Task<int> StoredApprovalCountAsync(string runId) =>
            Task.FromResult(_runs.TryGetValue(runId, out var run) ? run.Approvals.Count : 0);

        public Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken = default)
        {
            _runs[run.Id] = run;

            return Task.CompletedTask;
        }

        public Task SaveArtifactAsync(
            WorkflowRun run,
            WorkflowArtifactEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            _runs[run.Id] = run;

            return Task.CompletedTask;
        }

        public Task<WorkflowRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Copy(_runs.GetValueOrDefault(id)));

        public Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRun>>(_runs.Values
                .Where(run => run.ProjectId == projectId)
                .Select(run => Copy(run)!)
                .ToArray());

        public Task<WorkflowRun?> GetActiveByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Copy(_runs.Values
                .Where(run => run.ProjectId == projectId && !run.IsTerminal)
                .FirstOrDefault()));

        private static WorkflowRun? Copy(WorkflowRun? run) =>
            run is null
                ? null
                : new WorkflowRun(
                    run.Id,
                    run.ProjectId,
                    run.WorkflowPackageId,
                    run.WorkflowVersionId,
                    run.SessionId,
                    run.State,
                    run.CurrentStageId,
                    run.CurrentRole,
                    run.StartedAtUtc,
                    run.EndedAtUtc,
                    run.TerminalOutcome,
                    run.TerminalReason,
                    run.Transitions,
                    run.Verdicts,
                    run.Approvals,
                    run.TemplateId,
                    run.TemplateVersion,
                    run.TemplateGraphSnapshotJson,
                    run.TemplateSchemeSnapshotJson,
                    run.Artifacts);
    }
}
