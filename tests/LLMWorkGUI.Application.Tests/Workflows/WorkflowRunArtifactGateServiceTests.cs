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
/// The service side of the artifact gate, on top of a run repository that hands out a fresh run instance on
/// every read - which is what a durable store does and what makes "the evidence is re-checked immediately
/// before the gate decides" observable at all.
/// </summary>
public sealed partial class WorkflowRunArtifactGateServiceTests
{
    private const string ProjectId = "project-1";
    private const string PackageId = "package-1";
    private const string VersionId = "version-1";
    private const string StageId = WorkflowScheme.DocumentReviewStageId;
    private const string Kind = "DocumentBundle";

    private static readonly DateTimeOffset BaseTime = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly CopyingWorkflowRunRepository _repository = new();
    private readonly InMemoryWorkflowArtifactBlobStore _blobStore = new();
    private readonly SteppingTimeProvider _timeProvider = new(BaseTime);
    private readonly WorkflowRunService _service;

    public WorkflowRunArtifactGateServiceTests()
    {
        _service = new WorkflowRunService(
            _repository,
            WorkflowScheme.CreateStandardDevelopmentScheme(),
            new InMemoryWorkflowTemplateStore(),
            _blobStore,
            new FixedUserApprovalIdentity("DOMAIN\\operator"),
            _timeProvider);
    }

    [Fact]
    public async Task AdvanceStageAsync_AfterTheStoredBytesWereAltered_BlocksTheTransition()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var evidence = await RecordAsync(run, "reviewed bundle");

        _blobStore.Alter(evidence.BlobId, System.Text.Encoding.UTF8.GetBytes("a different bundle"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(run.Id, "the bundle was reviewed"));

        Assert.Contains(evidence.ArtifactId, exception.Message, StringComparison.Ordinal);
        Assert.Equal(StageId, (await _repository.GetByIdAsync(run.Id))!.CurrentStageId);
    }

    [Fact]
    public async Task ARefusedRecordingNeverPutsTheContentIntoAMessage()
    {
        const string secret = "api-key=super-secret-value";
        var run = await StartRunAtDocumentReviewAsync();
        var storedBlobs = _blobStore.BlobIds.Count;

        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordStageArtifactAsync(
                run.Id,
                StageId,
                "UiAcceptanceEvidence",
                Content(secret),
                DataClassification.Restricted));

        Assert.DoesNotContain(secret, refused.Message, StringComparison.Ordinal);

        // And the refused content was never stored either: the run service writes nothing before it knows
        // the stage and the kind are the ones this run may record.
        Assert.Equal(storedBlobs, _blobStore.BlobIds.Count);
    }

    [Fact]
    public async Task ASecondRecordingOfTheSameStageSupersedesTheFirst()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var first = await RecordAsync(run, "first revision");
        var second = await RecordAsync(run, "second revision");

        Assert.Equal(2, StageArtifacts((await _repository.GetByIdAsync(run.Id))!).Count);

        await RecordVerdictAsync(run, WorkflowScheme.ReviewerRole, first.HashSha256);
        await RecordVerdictAsync(run, WorkflowScheme.ArchitectRole, first.HashSha256);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(run.Id, "the first revision was reviewed"));

        await RecordVerdictAsync(run, WorkflowScheme.ReviewerRole, second.HashSha256);
        await RecordVerdictAsync(run, WorkflowScheme.ArchitectRole, second.HashSha256);

        var advanced = await _service.AdvanceStageAsync(run.Id, "the second revision was reviewed");

        Assert.Equal(second.ArtifactId, advanced.Transitions[^1].AuthorizingArtifactId);
    }

    [Fact]
    public async Task ARecordedArtifactIsAuthorizedOnTheNextReadOfTheRun()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var evidence = await RecordAsync(run, "reviewed bundle");

        // The repository hands out a new instance, so nothing the caller held before the recording can be
        // what authorizes this.
        Assert.NotSame(run, await _repository.GetByIdAsync(run.Id));

        await RecordVerdictAsync(run, WorkflowScheme.ReviewerRole, evidence.HashSha256);
        await RecordVerdictAsync(run, WorkflowScheme.ArchitectRole, evidence.HashSha256);

        var advanced = await _service.AdvanceStageAsync(run.Id, "the bundle was reviewed");

        Assert.Equal(evidence.ArtifactId, advanced.Transitions[^1].AuthorizingArtifactId);
    }

    [Fact]
    public async Task RecordingIsRefusedForAStageThatTheRunAlreadyLeft()
    {
        var run = await StartRunAtDocumentReviewAsync();
        var evidence = await RecordAsync(run, "reviewed bundle");
        await RecordVerdictAsync(run, WorkflowScheme.ReviewerRole, evidence.HashSha256);
        await RecordVerdictAsync(run, WorkflowScheme.ArchitectRole, evidence.HashSha256);

        run = await _service.AdvanceStageAsync(run.Id, "the bundle was reviewed");

        var refused = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordStageArtifactAsync(
                run.Id,
                StageId,
                Kind,
                Content("a late revision"),
                DataClassification.PrivateSource));

        Assert.Contains(WorkflowScheme.UserApprovalStageId, refused.Message, StringComparison.Ordinal);
        Assert.Contains(StageId, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordingRequiresIdentifiers()
    {
        var run = await StartRunAtDocumentReviewAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.RecordStageArtifactAsync(
                run.Id,
                " ",
                Kind,
                Content("content"),
                DataClassification.PrivateSource));
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.RecordStageArtifactAsync(
                run.Id,
                StageId,
                "   ",
                Content("content"),
                DataClassification.PrivateSource));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.RecordStageArtifactAsync(
                run.Id,
                StageId,
                Kind,
                content: null!,
                DataClassification.PrivateSource));
        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordStageArtifactAsync(
                "missing-run",
                StageId,
                Kind,
                Content("content"),
                DataClassification.PrivateSource));
    }

    private async Task<WorkflowRun> StartRunAtDocumentReviewAsync()
    {
        var run = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId);
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

            run = await _service.RecordStageArtifactAsync(
                run.Id,
                stage.StageId,
                stage.ArtifactRequirement!,
                Content($"artifact of '{stage.StageId}'"),
                DataClassification.PrivateSource);

            run = await _service.AdvanceStageAsync(run.Id, $"left {stage.StageId}");
        }

        Assert.Equal(StageId, run.CurrentStageId);

        return run;
    }

    private async Task<WorkflowArtifactEvidence> RecordAsync(WorkflowRun run, string content)
    {
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        var updated = await _service.RecordStageArtifactAsync(
            run.Id,
            StageId,
            Kind,
            Content(content),
            DataClassification.PrivateSource);

        return WorkflowArtifactEvidence.SelectCurrent(updated.Artifacts, run.Id, StageId, Kind)!;
    }

    private async Task RecordVerdictAsync(WorkflowRun run, string role, string documentHash)
    {
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        await _service.RecordLegacyUnlinkedReviewerVerdictAsync(
            run.Id,
            new ReviewerVerdictRecord(
                role,
                "route-1",
                documentHash,
                WorkflowReviewVerdict.Approve,
                "reviewed",
                _timeProvider.GetUtcNow()));
    }

    private static Stream Content(string text) =>
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text), writable: false);

    private static IReadOnlyList<WorkflowArtifactEvidence> StageArtifacts(WorkflowRun run) =>
        run.Artifacts
            .Where(artifact => string.Equals(artifact.StageId, StageId, StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// A repository that rebuilds the run on every read, the way a durable one does, so an in-memory
    /// reference a test still holds can never be what a later command sees.
    /// </summary>
    private sealed class CopyingWorkflowRunRepository : IWorkflowRunRepository
    {
        private readonly Dictionary<string, WorkflowRun> _runs = new(StringComparer.Ordinal);

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

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public SteppingTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
