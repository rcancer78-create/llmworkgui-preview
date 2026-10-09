using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Workflows;

public sealed class WorkflowRunServiceTests
{
    private const string ProjectId = "project-1";
    private const string PackageId = "pkg-1";
    private const string VersionId = "ver-1";
    private const string SessionId = "session-1";

    private static readonly string FirstDocumentHash = "sha256:" + new string('a', 64);
    private static readonly DateTimeOffset BaseTime = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeWorkflowRunRepository _repository = new();
    private readonly WorkflowScheme _scheme = WorkflowScheme.CreateStandardDevelopmentScheme();
    private readonly InMemoryWorkflowArtifactBlobStore _blobStore = new();
    private readonly FakeTimeProvider _timeProvider = new(BaseTime);
    private readonly WorkflowRunService _service;
    private readonly WorkflowRunTimelineService _timelineService = new();

    public WorkflowRunServiceTests()
    {
        // The standard-scheme run below is the legacy path: a run with no assigned template, driven by the
        // process-wide scheme. Every stage of that scheme is gated by a stored artifact, so the walk below
        // has to record real content before it can advance. Pinning a template to a run is covered by the
        // template execution plan tests and by the pinned-run integration suite.
        _service = new WorkflowRunService(
            _repository,
            _scheme,
            new InMemoryWorkflowTemplateStore(),
            _blobStore,
            new FixedUserApprovalIdentity("DOMAIN\\operator"),
            _timeProvider);
    }

    [Fact]
    public async Task StartLegacyRunAsync_CreatesTheInitialStageAndPersistsTheRun()
    {
        var run = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId, SessionId);

        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, run.CurrentStageId);
        Assert.Equal(WorkflowScheme.CoordinatorRole, run.CurrentRole);
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Equal(WorkflowTerminalOutcome.None, run.TerminalOutcome);
        Assert.Equal(VersionId, run.WorkflowVersionId);
        Assert.Equal(SessionId, run.SessionId);
        Assert.Equal(BaseTime, run.StartedAtUtc);
        Assert.Equal(1, _repository.SaveCount);
        Assert.Same(run, await _repository.GetByIdAsync(run.Id));
    }

    [Theory]
    [InlineData("", VersionId)]
    [InlineData("   ", VersionId)]
    [InlineData(PackageId, "")]
    [InlineData(PackageId, "   ")]
    public async Task StartLegacyRunAsync_RequiresIdentifiers(string packageId, string versionId)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.StartLegacyRunAsync(ProjectId, packageId, versionId));
    }

    [Fact]
    public async Task StartLegacyRunAsync_RequiresTheProjectId()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.StartLegacyRunAsync(" ", PackageId, VersionId));
    }

    [Fact]
    public async Task FullStandardSchemeRun_WalksEveryDeclaredStage()
    {
        var run = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId, SessionId);

        run = await AdvanceAsync(run);
        Assert.Equal(WorkflowScheme.ArchitectureStageId, run.CurrentStageId);

        run = await AdvanceAsync(run);
        Assert.Equal(WorkflowScheme.TechnicalSpecificationStageId, run.CurrentStageId);

        run = await AdvanceAsync(run);
        Assert.Equal(WorkflowScheme.RoadmapStageId, run.CurrentStageId);

        run = await AdvanceAsync(run);
        Assert.Equal(WorkflowScheme.DocumentReviewStageId, run.CurrentStageId);
        Assert.Equal(WorkflowScheme.ReviewerRole, run.CurrentRole);

        // The reviewed document is the artifact the stage itself recorded, so both verdicts name its hash.
        var reviewedHash = await RecordArtifactAsync(run);
        await RecordVerdictAsync(run, WorkflowScheme.ReviewerRole, reviewedHash, WorkflowReviewVerdict.Approve);
        await RecordVerdictAsync(run, WorkflowScheme.ArchitectRole, reviewedHash, WorkflowReviewVerdict.Approve);
        run = await TransitionAsync(run);
        Assert.Equal(WorkflowScheme.UserApprovalStageId, run.CurrentStageId);

        var approvedHash = await RecordArtifactAsync(run);
        await RecordApprovalAsync(run, WorkflowScheme.UserApprovalStageId, approvedHash);
        run = await TransitionAsync(run);
        Assert.Equal(WorkflowScheme.ImplementationPackagesStageId, run.CurrentStageId);

        run = await AdvanceAsync(run);
        Assert.Equal(WorkflowScheme.CodeAndUiStageId, run.CurrentStageId);
        Assert.Equal(WorkflowScheme.ImplementerRole, run.CurrentRole);

        run = await AdvanceAsync(run);
        Assert.Equal(WorkflowScheme.MultiLevelReviewStageId, run.CurrentStageId);

        var implementationHash = await RecordArtifactAsync(run);
        await RecordVerdictAsync(run, WorkflowScheme.ReviewerRole, implementationHash, WorkflowReviewVerdict.Approve);
        await RecordVerdictAsync(run, WorkflowScheme.UiReviewerRole, implementationHash, WorkflowReviewVerdict.Approve);
        run = await TransitionAsync(run);
        Assert.Equal(WorkflowScheme.TestsAndUiAcceptanceStageId, run.CurrentStageId);

        var acceptanceHash = await RecordArtifactAsync(run);
        await RecordVerdictAsync(run, WorkflowScheme.TesterRole, acceptanceHash, WorkflowReviewVerdict.Approve);
        await RecordApprovalAsync(run, WorkflowScheme.TestsAndUiAcceptanceStageId, acceptanceHash);
        run = await TransitionAsync(run);
        Assert.Equal(WorkflowScheme.FinalOutcomeStageId, run.CurrentStageId);

        // Every transition of the walk was authorized by a recorded artifact, and the final stage declares
        // none, so it is a terminal transition instead.
        Assert.All(run.Transitions, transition => Assert.True(transition.HasAuthorizingArtifact));
        Assert.All(
            run.Transitions,
            transition => Assert.Contains(
                run.Artifacts,
                artifact => string.Equals(
                    artifact.ArtifactId,
                    transition.AuthorizingArtifactId,
                    StringComparison.Ordinal)
                    && string.Equals(artifact.HashSha256, transition.AuthorizingArtifactHash, StringComparison.Ordinal)));

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.AdvanceStageAsync(run.Id, "beyond the final stage"));

        run = await _service.CompleteRunAsync(run.Id, "The workflow outcome is verified.");

        Assert.Equal(WorkflowRunState.Completed, run.State);
        Assert.Equal(WorkflowTerminalOutcome.Completed, run.TerminalOutcome);
        Assert.Equal(WorkflowScheme.FinalOutcomeStageId, run.CurrentStageId);
        Assert.Equal(VersionId, run.WorkflowVersionId);
        Assert.Equal(10, run.Transitions.Count);
        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, run.Transitions[0].FromStageId);
        Assert.Equal(WorkflowScheme.FinalOutcomeStageId, run.Transitions[^1].ToStageId);
    }

    [Fact]
    public async Task AdvanceStageAsync_WithoutAStoredArtifact_BlocksTheTransition()
    {
        var run = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId, SessionId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(run.Id, "no document was produced"));

        Assert.Contains(WorkflowScheme.TaskSpecificationStageId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("TaskSpecificationDocument", exception.Message, StringComparison.Ordinal);
        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, run.CurrentStageId);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public async Task AdvanceStageAsync_AfterTheStoredBytesAreDeleted_BlocksTheTransition()
    {
        var run = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId, SessionId);

        var hash = await RecordArtifactAsync(run);
        var artifact = Assert.Single(
            (await _repository.GetByIdAsync(run.Id))!.Artifacts);

        _blobStore.Delete(artifact.BlobId);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(run.Id, "the document is gone"));

        Assert.Contains(artifact.ArtifactId, exception.Message, StringComparison.Ordinal);
        Assert.Equal(hash, artifact.HashSha256);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public async Task RecordStageArtifactAsync_RefusesAStaleStageAWrongKindAndATerminalRun()
    {
        var run = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId, SessionId);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordStageArtifactAsync(
                run.Id,
                WorkflowScheme.ArchitectureStageId,
                "ArchitectureDocument",
                Content("late"),
                DataClassification.PrivateSource));

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordStageArtifactAsync(
                run.Id,
                WorkflowScheme.TaskSpecificationStageId,
                "ArchitectureDocument",
                Content("wrong kind"),
                DataClassification.PrivateSource));

        await RecordArtifactAsync(run);
        var terminal = await _service.CompleteRunAsync(run.Id, "done");

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordStageArtifactAsync(
                terminal.Id,
                WorkflowScheme.TaskSpecificationStageId,
                "TaskSpecificationDocument",
                Content("after the end"),
                DataClassification.PrivateSource));
    }

    [Fact]
    public async Task AdvanceStageAsync_WithConflictingVerdicts_BlocksTheTransition()
    {
        var run = await StartRunAtDocumentReviewAsync();

        var hash = await RecordArtifactAsync(run);
        await RecordVerdictAsync(run, WorkflowScheme.ReviewerRole, hash, WorkflowReviewVerdict.Approve);
        await RecordVerdictAsync(run, WorkflowScheme.ArchitectRole, hash, WorkflowReviewVerdict.Reject);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(run.Id, "documents reviewed"));

        var reread = await _repository.GetByIdAsync(run.Id);
        Assert.Equal(WorkflowScheme.DocumentReviewStageId, reread!.CurrentStageId);
        Assert.Equal(4, reread.Transitions.Count);
    }

    [Fact]
    public async Task AdvanceStageAsync_WithIncompleteVerdicts_BlocksTheTransition()
    {
        var run = await StartRunAtDocumentReviewAsync();

        var hash = await RecordArtifactAsync(run);
        await RecordVerdictAsync(run, WorkflowScheme.ReviewerRole, hash, WorkflowReviewVerdict.Approve);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(run.Id, "documents reviewed"));

        Assert.Contains("incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkflowScheme.DocumentReviewStageId, run.CurrentStageId);
    }

    [Fact]
    public async Task AdvanceStageAsync_OnAStaleHash_BlocksTheTransition()
    {
        var run = await StartRunAtDocumentReviewAsync();

        await RecordArtifactAsync(run);

        // A verdict on some other document cannot stand in for the artifact the stage actually recorded.
        await RecordVerdictAsync(run, WorkflowScheme.ReviewerRole, FirstDocumentHash, WorkflowReviewVerdict.Approve);
        await RecordVerdictAsync(run, WorkflowScheme.ArchitectRole, FirstDocumentHash, WorkflowReviewVerdict.Approve);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(run.Id, "documents reviewed"));

        Assert.Contains("incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdvanceStageAsync_RequiresUserApprovalOnTheApprovalStage()
    {
        var run = await StartRunAtUserApprovalAsync();

        await RecordArtifactAsync(run);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.AdvanceStageAsync(run.Id, "approved"));

        Assert.Contains("user approval", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkflowScheme.UserApprovalStageId, run.CurrentStageId);
    }

    [Fact]
    public async Task RecordUserApprovalAsync_WithRejection_StopsTheRun()
    {
        var run = await StartRunAtUserApprovalAsync();
        var recorded = await RecordArtifactAsync(run);

        // The service works on the run it reads back from storage, which is the copy that carries the
        // artifact, so the terminal outcome is asserted on what it returned.
        var rejected = await _service.RecordUserApprovalAsync(
            run.Id,
            new UserApprovalEvidence(
                "approval-1",
                "user-1",
                WorkflowScheme.UserApprovalStageId,
                recorded,
                UserApprovalDecision.Rejected,
                "The documents are rejected.",
                _timeProvider.GetUtcNow()));

        Assert.Equal(WorkflowRunState.Failed, rejected.State);
        Assert.Equal(WorkflowTerminalOutcome.Rejected, rejected.TerminalOutcome);
        Assert.NotNull(rejected.EndedAtUtc);

        await Assert.ThrowsAsync<InvalidStateTransitionException>(
            () => _service.AdvanceStageAsync(run.Id, "continue"));
    }

    [Fact]
    public async Task TerminalCommands_SetDistinctWorkflowOutcomes()
    {
        var cancelled = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId);
        var failed = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId);
        var completed = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId);

        await _service.CancelRunAsync(cancelled.Id, "Cancelled by the user.");
        await _service.FailRunAsync(failed.Id, "A required backend is unavailable.");
        await _service.CompleteRunAsync(completed.Id, "Finished.");

        Assert.Equal(WorkflowRunState.Cancelled, cancelled.State);
        Assert.Equal(WorkflowTerminalOutcome.Cancelled, cancelled.TerminalOutcome);
        Assert.Equal(WorkflowRunState.Failed, failed.State);
        Assert.Equal(WorkflowTerminalOutcome.Failed, failed.TerminalOutcome);
        Assert.Equal(WorkflowRunState.Completed, completed.State);
        Assert.Equal(WorkflowTerminalOutcome.Completed, completed.TerminalOutcome);
    }

    [Fact]
    public async Task Commands_RequireAnExistingRun()
    {
        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.AdvanceStageAsync("missing-run", "reason"));
        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.CompleteRunAsync("missing-run", "reason"));
        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordReviewerVerdictAsync(
                "missing-run",
                CreateVerdict(WorkflowScheme.ReviewerRole, FirstDocumentHash, WorkflowReviewVerdict.Approve)));
        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => _service.RecordUserApprovalAsync(
                "missing-run",
                CreateApproval(WorkflowScheme.UserApprovalStageId, FirstDocumentHash)));
    }

    [Fact]
    public async Task WorkflowOutcome_DoesNotFollowASucceededExecution()
    {
        var run = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId, SessionId);
        run = await AdvanceAsync(run);

        var session = CreateSession();
        var execution = CreateExecution(ExecutionState.Succeeded);

        var timeline = _timelineService.BuildTimeline(
            run,
            new[] { execution },
            new[] { session },
            EvidenceSourceKind.NotReported);

        Assert.Single(timeline.Items, item => item.ExecutionId == execution.Id);
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Equal(WorkflowTerminalOutcome.None, run.TerminalOutcome);

        await _service.CompleteRunAsync(run.Id, "Explicitly completed.");

        Assert.Equal(WorkflowTerminalOutcome.Completed, run.TerminalOutcome);
    }

    [Fact]
    public void CreateStandardDevelopmentScheme_ExposesTheDeclaredDevelopmentChain()
    {
        var scheme = WorkflowScheme.CreateStandardDevelopmentScheme();

        scheme.Validate();

        Assert.Equal(WorkflowScheme.TaskSpecificationStageId, scheme.InitialStageId);

        var chain = new List<string>();
        var stage = scheme.GetRequiredStage(scheme.InitialStageId);

        while (true)
        {
            chain.Add(stage.StageId);

            if (stage.NextStageId is null)
            {
                break;
            }

            stage = scheme.GetRequiredStage(stage.NextStageId);
        }

        Assert.Equal(
            new[]
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
            },
            chain);

        Assert.Equal(11, scheme.Stages.Count);
        Assert.Single(scheme.GetTerminalStages());
        Assert.Equal(WorkflowScheme.FinalOutcomeStageId, scheme.GetTerminalStages()[0].StageId);
        Assert.Equal(
            WorkflowScheme.CodeAndUiStageId,
            scheme.GetRequiredStage(WorkflowScheme.MultiLevelReviewStageId).FailureStageId);
        Assert.True(scheme.GetRequiredStage(WorkflowScheme.UserApprovalStageId).RequiresUserApproval);
        Assert.True(scheme.GetRequiredStage(WorkflowScheme.TestsAndUiAcceptanceStageId).RequiresUserApproval);
    }

    [Fact]
    public void WorkflowScheme_RejectsAnUnknownInitialStage()
    {
        var stages = new[] { CreateStage("stage-1") };

        Assert.Throws<WorkflowValidationException>(() => new WorkflowScheme("stage-missing", stages));
    }

    [Fact]
    public void WorkflowScheme_RejectsADanglingTransitionTarget()
    {
        var stages = new[]
        {
            CreateStage("stage-1", nextStageId: "stage-missing")
        };

        Assert.Throws<WorkflowValidationException>(() => new WorkflowScheme("stage-1", stages));
    }

    [Fact]
    public void WorkflowScheme_RejectsADanglingFailureTarget()
    {
        var stages = new[]
        {
            CreateStage("stage-1", failureStageId: "stage-missing")
        };

        Assert.Throws<WorkflowValidationException>(() => new WorkflowScheme("stage-1", stages));
    }

    [Fact]
    public void WorkflowScheme_RejectsAnUnreachableTerminalOutcome()
    {
        var stages = new[]
        {
            CreateStage("stage-1", nextStageId: "stage-2"),
            CreateStage("stage-2", nextStageId: "stage-1"),
            CreateStage("stage-3")
        };

        Assert.Throws<WorkflowValidationException>(() => new WorkflowScheme("stage-1", stages));
    }

    [Fact]
    public void WorkflowScheme_RejectsDuplicateStageIds()
    {
        var stages = new[]
        {
            CreateStage("stage-1"),
            CreateStage("stage-1")
        };

        Assert.Throws<WorkflowValidationException>(() => new WorkflowScheme("stage-1", stages));
    }

    [Fact]
    public void WorkflowScheme_RejectsAnEmptyStageList()
    {
        Assert.Throws<WorkflowValidationException>(
            () => new WorkflowScheme("stage-1", Array.Empty<WorkflowStageDefinition>()));
    }

    [Fact]
    public void WorkflowScheme_RejectsNullStages()
    {
        Assert.Throws<ArgumentException>(
            () => new WorkflowScheme("stage-1", new WorkflowStageDefinition[] { null! }));
    }

    private async Task<WorkflowRun> StartRunAtDocumentReviewAsync()
    {
        var run = await _service.StartLegacyRunAsync(ProjectId, PackageId, VersionId, SessionId);

        for (var index = 0; index < 4; index++)
        {
            run = await AdvanceAsync(run);
        }

        return run;
    }

    private async Task<WorkflowRun> StartRunAtUserApprovalAsync()
    {
        var run = await StartRunAtDocumentReviewAsync();

        var hash = await RecordArtifactAsync(run);
        await RecordVerdictAsync(run, WorkflowScheme.ReviewerRole, hash, WorkflowReviewVerdict.Approve);
        await RecordVerdictAsync(run, WorkflowScheme.ArchitectRole, hash, WorkflowReviewVerdict.Approve);

        return await TransitionAsync(run);
    }

    /// <summary>
    /// Records the artifact of the run's current stage and returns the hash the verdicts and the approval of
    /// that stage have to pin. The content is derived from the stage, so the hash is a pure function of where
    /// the run is and does not depend on the order the walk happens to take.
    /// </summary>
    private async Task<string> RecordArtifactAsync(WorkflowRun run)
    {
        var stage = _scheme.GetRequiredStage(run.CurrentStageId);

        Assert.NotNull(stage.ArtifactRequirement);

        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        var updated = await _service.RecordStageArtifactAsync(
            run.Id,
            stage.StageId,
            stage.ArtifactRequirement!,
            Content($"artifact of '{stage.StageId}' ({stage.ArtifactRequirement})"),
            DataClassification.PrivateSource);

        return Assert.Single(
            updated.Artifacts.Where(artifact =>
                string.Equals(artifact.StageId, stage.StageId, StringComparison.Ordinal))).HashSha256;
    }

    private async Task<WorkflowRun> AdvanceAsync(WorkflowRun run)
    {
        await RecordArtifactAsync(run);

        return await TransitionAsync(run);
    }

    private async Task<WorkflowRun> TransitionAsync(WorkflowRun run)
    {
        _timeProvider.Advance(TimeSpan.FromMinutes(1));

        return await _service.AdvanceStageAsync(run.Id, "Stage gates passed.");
    }

    private async Task RecordVerdictAsync(
        WorkflowRun run,
        string reviewerRole,
        string documentHash,
        WorkflowReviewVerdict verdict)
    {
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        // These are legacy runs - started through StartLegacyRunAsync, following the process-wide standard
        // scheme and carrying no template identity - so their verdicts are written through the explicitly
        // legacy, unlinked path. A pinned run would refuse it, which is what keeps a typed role, route and
        // hash from ever standing in for a model review.
        await _service.RecordLegacyUnlinkedReviewerVerdictAsync(run.Id, CreateVerdict(reviewerRole, documentHash, verdict));
    }

    private async Task RecordApprovalAsync(WorkflowRun run, string stageId, string artifactHash)
    {
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        await _service.RecordUserApprovalAsync(run.Id, CreateApproval(stageId, artifactHash));
    }

    private static Stream Content(string text) =>
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text), writable: false);

    private ReviewerVerdictRecord CreateVerdict(
        string reviewerRole,
        string documentHash,
        WorkflowReviewVerdict verdict)
    {
        return new ReviewerVerdictRecord(
            reviewerRole,
            $"route-{reviewerRole}",
            documentHash,
            verdict,
            $"{reviewerRole} returned {verdict}.",
            _timeProvider.GetUtcNow());
    }

    private UserApprovalEvidence CreateApproval(string stageId, string artifactHash)
    {
        return new UserApprovalEvidence(
            Guid.NewGuid().ToString("N"),
            "user-1",
            stageId,
            artifactHash,
            UserApprovalDecision.Approved,
            "Approved by the project owner.",
            _timeProvider.GetUtcNow());
    }

    private static WorkflowStageDefinition CreateStage(
        string stageId,
        string? nextStageId = null,
        string? failureStageId = null)
    {
        return new WorkflowStageDefinition(
            stageId,
            $"Display {stageId}",
            WorkflowScheme.CoordinatorRole,
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null,
            nextStageId,
            failureStageId);
    }

    private static Session CreateSession()
    {
        return new Session(
            SessionId,
            new SessionBinding(
                BackendType.OpenCode,
                "provider-1",
                "account-1",
                "model-1",
                "high",
                "fast",
                "interactive"),
            ProjectId,
            @"C:\test\workspace",
            "native-session-1",
            SessionState.Idle,
            ReconciliationOutcome.Reattached,
            CloseReason.None,
            continuationOfSessionId: null,
            forkedFromSessionId: null,
            workflowRunId: null,
            role: "Executor",
            activeExecutionId: null,
            BaseTime,
            BaseTime.AddMinutes(5));
    }

    private static Execution CreateExecution(ExecutionState state)
    {
        return new Execution(
            "execution-1",
            SessionId,
            "client-request-1",
            state,
            ExecutionFailureReason.None,
            "route-requested",
            "route-observed",
            retryOfExecutionId: null,
            processState: null,
            exitCode: null,
            terminationReason: null,
            Array.Empty<string>(),
            sourceHashBefore: null,
            sourceHashAfter: null,
            BaseTime,
            BaseTime,
            BaseTime.AddMinutes(1));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private sealed class FakeWorkflowRunRepository : IWorkflowRunRepository
    {
        private readonly Dictionary<string, WorkflowRun> _runs = new(StringComparer.Ordinal);

        public int SaveCount { get; private set; }

        public Task SaveAsync(WorkflowRun run, CancellationToken cancellationToken = default)
        {
            _runs[run.Id] = run;
            SaveCount++;

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
            Task.FromResult(_runs.GetValueOrDefault(id));

        public Task<IReadOnlyList<WorkflowRun>> GetByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRun>>(
                _runs.Values
                    .Where(run => string.Equals(run.ProjectId, projectId, StringComparison.Ordinal))
                    .OrderBy(run => run.StartedAtUtc)
                    .ThenBy(run => run.Id, StringComparer.Ordinal)
                    .ToArray());

        public Task<WorkflowRun?> GetActiveByProjectIdAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _runs.Values
                    .Where(run => string.Equals(run.ProjectId, projectId, StringComparison.Ordinal)
                        && !run.IsTerminal)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .ThenBy(run => run.Id, StringComparer.Ordinal)
                    .FirstOrDefault());
    }
}
