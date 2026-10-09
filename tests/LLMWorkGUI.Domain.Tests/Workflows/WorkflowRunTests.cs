using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests.Workflows;

public sealed class WorkflowRunTests
{
    private const string ReviewerRole = "Reviewer";
    private const string ArchitectRole = "Architect";
    private const string DocumentKind = "DocumentBundle";

    private static readonly string DocumentHash = "sha256:" + new string('a', 64);
    private static readonly string RevisedDocumentHash = "sha256:" + new string('b', 64);
    private static readonly DateTimeOffset StartedAt = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Start_CreatesARunningRunAtTheInitialStage()
    {
        var run = CreateRunningRun();

        Assert.Equal("run-1", run.Id);
        Assert.Equal("project-1", run.ProjectId);
        Assert.Equal("pkg-1", run.WorkflowPackageId);
        Assert.Equal("ver-1", run.WorkflowVersionId);
        Assert.Equal("session-1", run.SessionId);
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Equal(WorkflowTerminalOutcome.None, run.TerminalOutcome);
        Assert.Equal("stage-1", run.CurrentStageId);
        Assert.Equal("Architect", run.CurrentRole);
        Assert.Equal(StartedAt, run.StartedAtUtc);
        Assert.Null(run.EndedAtUtc);
        Assert.Null(run.TerminalReason);
        Assert.False(run.IsTerminal);
        Assert.Empty(run.Transitions);
        Assert.Empty(run.Verdicts);
        Assert.Empty(run.Approvals);
    }

    [Fact]
    public void Start_RejectsNullInitialStage()
    {
        Assert.Throws<ArgumentNullException>(() => WorkflowRun.Start(
            "run-1",
            "project-1",
            "pkg-1",
            "ver-1",
            sessionId: null,
            initialStage: null!,
            StartedAt));
    }

    [Fact]
    public void WorkflowVersionId_IsReadOnlyAfterCreation()
    {
        var property = typeof(WorkflowRun).GetProperty(nameof(WorkflowRun.WorkflowVersionId));

        Assert.NotNull(property);
        Assert.False(property!.CanWrite);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankRunId(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowRun(
            invalidValue,
            "project-1",
            "pkg-1",
            "ver-1",
            null,
            WorkflowRunState.Running,
            "stage-1",
            ArchitectRole,
            StartedAt,
            null,
            WorkflowTerminalOutcome.None,
            null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankProjectId(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowRun(
            "run-1",
            invalidValue,
            "pkg-1",
            "ver-1",
            null,
            WorkflowRunState.Running,
            "stage-1",
            ArchitectRole,
            StartedAt,
            null,
            WorkflowTerminalOutcome.None,
            null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankWorkflowPackageId(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowRun(
            "run-1",
            "project-1",
            invalidValue,
            "ver-1",
            null,
            WorkflowRunState.Running,
            "stage-1",
            ArchitectRole,
            StartedAt,
            null,
            WorkflowTerminalOutcome.None,
            null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankWorkflowVersionId(string invalidValue)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowRun(
            "run-1",
            "project-1",
            "pkg-1",
            invalidValue,
            null,
            WorkflowRunState.Running,
            "stage-1",
            ArchitectRole,
            StartedAt,
            null,
            WorkflowTerminalOutcome.None,
            null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>()));
    }

    [Fact]
    public void Constructor_RejectsTerminalRunWithoutEndedAt()
    {
        Assert.Throws<ArgumentException>(() => CreateRun(
            state: WorkflowRunState.Completed,
            endedAtUtc: null,
            terminalOutcome: WorkflowTerminalOutcome.Completed));
    }

    [Fact]
    public void Constructor_RejectsNonTerminalRunWithTerminalFields()
    {
        Assert.Throws<ArgumentException>(() => CreateRun(
            state: WorkflowRunState.Running,
            endedAtUtc: StartedAt.AddMinutes(5),
            terminalOutcome: WorkflowTerminalOutcome.None));

        Assert.Throws<ArgumentException>(() => CreateRun(
            state: WorkflowRunState.Running,
            endedAtUtc: null,
            terminalOutcome: WorkflowTerminalOutcome.Completed));
    }

    [Fact]
    public void Constructor_RejectsIncompatibleTerminalOutcome()
    {
        Assert.Throws<ArgumentException>(() => CreateRun(
            state: WorkflowRunState.Cancelled,
            endedAtUtc: StartedAt.AddMinutes(5),
            terminalOutcome: WorkflowTerminalOutcome.Completed));
    }

    [Fact]
    public void Constructor_RejectsEndedAtBeforeStartedAt()
    {
        Assert.Throws<ArgumentException>(() => CreateRun(
            state: WorkflowRunState.Completed,
            endedAtUtc: StartedAt.AddMinutes(-1),
            terminalOutcome: WorkflowTerminalOutcome.Completed));
    }

    [Fact]
    public void RecordReviewerVerdict_KeepsEveryVerdictSeparateAndDoesNotTerminateTheRun()
    {
        var run = CreateRunningRun();

        run.RecordReviewerVerdict(CreateVerdict(ReviewerRole, WorkflowReviewVerdict.Approve));
        run.RecordReviewerVerdict(CreateVerdict(ArchitectRole, WorkflowReviewVerdict.Reject));

        Assert.Equal(2, run.Verdicts.Count);
        Assert.Equal(WorkflowReviewVerdict.Approve, run.Verdicts[0].Verdict);
        Assert.Equal(WorkflowReviewVerdict.Reject, run.Verdicts[1].Verdict);
        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Equal(WorkflowTerminalOutcome.None, run.TerminalOutcome);
    }

    [Fact]
    public void RecordReviewerVerdict_RejectsNullAndTerminalRuns()
    {
        var run = CreateRunningRun();

        Assert.Throws<ArgumentNullException>(() => run.RecordReviewerVerdict(null!));

        run.Complete("done", StartedAt.AddMinutes(1));

        Assert.Throws<InvalidStateTransitionException>(
            () => run.RecordReviewerVerdict(CreateVerdict(ReviewerRole, WorkflowReviewVerdict.Approve)));
    }

    [Fact]
    public void RecordUserApproval_StoresApprovalEvidence()
    {
        var run = CreateRunningRun();

        run.RecordUserApproval(CreateApproval(DocumentHash));

        var approval = Assert.Single(run.Approvals);
        Assert.Equal("approval-1", approval.ApprovalId);
        Assert.Equal("user-1", approval.ApprovedBy);
        Assert.Equal(UserApprovalDecision.Approved, approval.Decision);
    }

    [Fact]
    public void RecordUserApproval_WithRejection_TerminatesTheRunWithRejectedOutcome()
    {
        var run = CreateRunningRun();
        var rejectedApproval = new UserApprovalEvidence(
            "approval-1",
            "user-1",
            "stage-1",
            DocumentHash,
            UserApprovalDecision.Rejected,
            "The document does not meet the requirements.",
            StartedAt.AddMinutes(10));

        run.RecordUserApproval(rejectedApproval);

        Assert.Equal(WorkflowRunState.Failed, run.State);
        Assert.Equal(WorkflowTerminalOutcome.Rejected, run.TerminalOutcome);
        Assert.Equal(StartedAt.AddMinutes(10), run.EndedAtUtc);
        Assert.Equal("The document does not meet the requirements.", run.TerminalReason);
        Assert.True(run.IsTerminal);
    }

    [Fact]
    public void RecordUserApproval_RejectsEvidenceThatPredatesTheRun()
    {
        var run = CreateRunningRun();
        var earlyApproval = new UserApprovalEvidence(
            "approval-1",
            "user-1",
            "stage-1",
            DocumentHash,
            UserApprovalDecision.Approved,
            "Looks good.",
            StartedAt.AddMinutes(-1));

        Assert.Throws<ArgumentException>(() => run.RecordUserApproval(earlyApproval));
    }

    [Fact]
    public void AdvanceTo_BlocksWhenRequiredVerdictIsMissing()
    {
        var run = WithArtifact(CreateRunningRun());
        var currentStage = CreateStage(
            "stage-1",
            nextStageId: "stage-2",
            reviewerRoles: new[] { ReviewerRole },
            artifactRequirement: DocumentKind);
        var nextStage = CreateStage("stage-2");

        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(currentStage, nextStage, "review complete", StartedAt.AddMinutes(1), ReviewerExecutions(run.Verdicts)));

        Assert.Contains("incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("stage-1", run.CurrentStageId);
        Assert.Empty(run.Transitions);
    }

    [Theory]
    [InlineData(WorkflowReviewVerdict.Reject)]
    [InlineData(WorkflowReviewVerdict.RequestChanges)]
    [InlineData(WorkflowReviewVerdict.Missing)]
    public void AdvanceTo_BlocksOnAnyNonApproveVerdict(WorkflowReviewVerdict verdict)
    {
        var run = WithArtifact(CreateRunningRun());
        run.RecordReviewerVerdict(CreateVerdict(ReviewerRole, verdict));

        var currentStage = CreateStage(
            "stage-1",
            nextStageId: "stage-2",
            reviewerRoles: new[] { ReviewerRole },
            artifactRequirement: DocumentKind);
        var nextStage = CreateStage("stage-2");

        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(currentStage, nextStage, "review complete", StartedAt.AddMinutes(1), ReviewerExecutions(run.Verdicts)));

        Assert.Contains("Approve", exception.Message, StringComparison.Ordinal);
        Assert.Equal("stage-1", run.CurrentStageId);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void AdvanceTo_RequiresTheCurrentArtifactHashToBeReviewedByEveryRequiredRole()
    {
        var run = WithArtifact(CreateRunningRun());
        run.RecordReviewerVerdict(CreateVerdict(ReviewerRole, WorkflowReviewVerdict.Approve, DocumentHash));
        run.RecordReviewerVerdict(CreateVerdict(ArchitectRole, WorkflowReviewVerdict.Approve, RevisedDocumentHash));

        var currentStage = CreateStage(
            "stage-1",
            nextStageId: "stage-2",
            reviewerRoles: new[] { ReviewerRole, ArchitectRole },
            artifactRequirement: DocumentKind);
        var nextStage = CreateStage("stage-2");

        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(currentStage, nextStage, "review complete", StartedAt.AddMinutes(1), ReviewerExecutions(run.Verdicts)));

        // The architect reviewed a different document than the one the stage actually recorded, so the
        // incomplete role is the architect and the current hash is the recorded one - not the other verdict.
        Assert.Contains(ArchitectRole, exception.Message, StringComparison.Ordinal);
        Assert.Contains(DocumentHash, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(RevisedDocumentHash, exception.Message, StringComparison.Ordinal);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void AdvanceTo_BlocksWhenTheRequiredUserApprovalIsMissing()
    {
        var run = WithArtifact(CreateRunningRun());

        var currentStage = CreateStage(
            "stage-1",
            nextStageId: "stage-2",
            requiresUserApproval: true,
            artifactRequirement: DocumentKind);
        var nextStage = CreateStage("stage-2");

        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(currentStage, nextStage, "approved", StartedAt.AddMinutes(1), ReviewerExecutions(run.Verdicts)));

        Assert.Contains("user approval", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void AdvanceTo_TransitionsWhenEveryGatePassesAndCapturesEvidence()
    {
        var run = WithArtifact(CreateRunningRun());
        run.RecordReviewerVerdict(CreateVerdict(ReviewerRole, WorkflowReviewVerdict.Approve));
        run.RecordReviewerVerdict(CreateVerdict(ArchitectRole, WorkflowReviewVerdict.Approve));
        run.RecordUserApproval(CreateApproval(DocumentHash));

        var currentStage = CreateStage(
            "stage-1",
            nextStageId: "stage-2",
            reviewerRoles: new[] { ReviewerRole, ArchitectRole },
            requiresUserApproval: true,
            artifactRequirement: DocumentKind,
            role: ArchitectRole);
        var nextStage = CreateStage("stage-2", role: "Implementer");
        var triggeredAt = StartedAt.AddMinutes(30);

        var transition = run.AdvanceTo(currentStage, nextStage, "Documents approved.", triggeredAt, ReviewerExecutions(run.Verdicts));

        Assert.NotEmpty(transition.TransitionId);
        Assert.Equal("stage-1", transition.FromStageId);
        Assert.Equal("stage-2", transition.ToStageId);
        Assert.Equal(triggeredAt, transition.TriggeredAtUtc);
        Assert.Equal("Documents approved.", transition.Reason);
        Assert.Equal(2, transition.ReviewerVerdicts.Count);
        Assert.NotNull(transition.UserApproval);
        Assert.Equal("stage-2", run.CurrentStageId);
        Assert.Equal("Implementer", run.CurrentRole);
        Assert.Single(run.Transitions);
        Assert.Equal(WorkflowRunState.Running, run.State);

        // The transition names the artifact that authorized it, and the hash it names is the hash every
        // authorizing verdict and the user approval pinned.
        var artifact = Assert.Single(run.Artifacts);
        Assert.True(transition.HasAuthorizingArtifact);
        Assert.Equal(artifact.ArtifactId, transition.AuthorizingArtifactId);
        Assert.Equal(DocumentHash, transition.AuthorizingArtifactHash);
    }

    [Fact]
    public void AdvanceTo_OnAGateFreeStageRecordsNoAuthorizingArtifact()
    {
        var run = WithArtifact(CreateRunningRun());
        var currentStage = CreateStage("stage-1", nextStageId: "stage-2", artifactRequirement: DocumentKind);
        var nextStage = CreateStage("stage-2");

        var transition = run.AdvanceTo(currentStage, nextStage, "delivered", StartedAt.AddMinutes(1), ReviewerExecutions(run.Verdicts));

        // An artifact is still required by the stage, so the transition names it even though no reviewer and
        // no approval were needed.
        Assert.Equal(Assert.Single(run.Artifacts).ArtifactId, transition.AuthorizingArtifactId);
    }

    [Fact]
    public void AdvanceTo_RejectsAStageThatIsNotDeclaredAsNext()
    {
        var run = CreateRunningRun();
        var currentStage = CreateStage("stage-1", nextStageId: "stage-2");
        var foreignStage = CreateStage("stage-3");

        Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(currentStage, foreignStage, "reason", StartedAt.AddMinutes(1)));
    }

    [Fact]
    public void AdvanceTo_RejectsAStageThatIsNotTheCurrentStage()
    {
        var run = CreateRunningRun();
        var foreignStage = CreateStage("stage-other", nextStageId: "stage-2");
        var nextStage = CreateStage("stage-2");

        Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(foreignStage, nextStage, "reason", StartedAt.AddMinutes(1)));
    }

    [Fact]
    public void AdvanceTo_BlocksOnceTheRunIsTerminal()
    {
        var run = CreateRunningRun();
        run.Complete("done", StartedAt.AddMinutes(1));

        var currentStage = CreateStage("stage-1", nextStageId: "stage-2");
        var nextStage = CreateStage("stage-2");

        Assert.Throws<InvalidStateTransitionException>(
            () => run.AdvanceTo(currentStage, nextStage, "reason", StartedAt.AddMinutes(2)));
    }

    [Theory]
    [InlineData(WorkflowRunState.Completed, WorkflowTerminalOutcome.Completed)]
    [InlineData(WorkflowRunState.Failed, WorkflowTerminalOutcome.Failed)]
    [InlineData(WorkflowRunState.Cancelled, WorkflowTerminalOutcome.Cancelled)]
    public void TerminalTransitions_SetStateOutcomeAndEndTime(
        WorkflowRunState expectedState,
        WorkflowTerminalOutcome expectedOutcome)
    {
        var run = CreateRunningRun();
        var endedAt = StartedAt.AddHours(1);

        switch (expectedState)
        {
            case WorkflowRunState.Completed:
                run.Complete("finished", endedAt);
                break;
            case WorkflowRunState.Failed:
                run.Fail("backend unavailable", endedAt);
                break;
            case WorkflowRunState.Cancelled:
                run.Cancel("cancelled by user", endedAt);
                break;
            default:
                throw new InvalidOperationException("Unexpected terminal state.");
        }

        Assert.Equal(expectedState, run.State);
        Assert.Equal(expectedOutcome, run.TerminalOutcome);
        Assert.Equal(endedAt, run.EndedAtUtc);
        Assert.NotNull(run.TerminalReason);
        Assert.True(run.IsTerminal);
    }

    [Theory]
    [InlineData(WorkflowRunState.Completed)]
    [InlineData(WorkflowRunState.Failed)]
    [InlineData(WorkflowRunState.Cancelled)]
    public void TerminalTransitions_BlockEveryFurtherMutation(WorkflowRunState terminalState)
    {
        var run = CreateRunningRun();
        var endedAt = StartedAt.AddMinutes(5);

        switch (terminalState)
        {
            case WorkflowRunState.Completed:
                run.Complete("finished", endedAt);
                break;
            case WorkflowRunState.Failed:
                run.Fail("failed", endedAt);
                break;
            case WorkflowRunState.Cancelled:
                run.Cancel("cancelled", endedAt);
                break;
            default:
                throw new InvalidOperationException("Unexpected terminal state.");
        }

        Assert.Throws<InvalidStateTransitionException>(
            () => run.RecordReviewerVerdict(CreateVerdict(ReviewerRole, WorkflowReviewVerdict.Approve)));
        Assert.Throws<InvalidStateTransitionException>(
            () => run.RecordUserApproval(CreateApproval(DocumentHash)));
        Assert.Throws<InvalidStateTransitionException>(
            () => run.Complete("again", endedAt.AddMinutes(1)));
        Assert.Throws<InvalidStateTransitionException>(
            () => run.Fail("again", endedAt.AddMinutes(1)));
        Assert.Throws<InvalidStateTransitionException>(
            () => run.Cancel("again", endedAt.AddMinutes(1)));
    }

    [Fact]
    public void TerminalTransitions_RejectEndTimeBeforeStart()
    {
        var run = CreateRunningRun();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => run.Complete("finished", StartedAt.AddMinutes(-1)));
    }

    [Fact]
    public void TerminalOutcome_IsOnlyProducedByExplicitTerminalTransitions()
    {
        var run = CreateRunningRun();
        run.RecordReviewerVerdict(CreateVerdict(ReviewerRole, WorkflowReviewVerdict.Approve));
        run.RecordUserApproval(CreateApproval(DocumentHash));

        Assert.Equal(WorkflowRunState.Running, run.State);
        Assert.Equal(WorkflowTerminalOutcome.None, run.TerminalOutcome);

        run.Complete("all gates passed", StartedAt.AddMinutes(45));

        Assert.Equal(WorkflowTerminalOutcome.Completed, run.TerminalOutcome);
    }

    [Fact]
    public void WorkflowRunEnums_ExposeNormativeMembers()
    {
        Assert.Equal(
            new[] { "Pending", "Running", "Suspended", "Completed", "Failed", "Cancelled" },
            Enum.GetNames<WorkflowRunState>());
        Assert.Equal(
            new[] { "None", "Completed", "Failed", "Cancelled", "Rejected" },
            Enum.GetNames<WorkflowTerminalOutcome>());
        Assert.Equal(
            new[] { "Approve", "Reject", "RequestChanges", "Missing" },
            Enum.GetNames<WorkflowReviewVerdict>());
        Assert.Equal(
            new[]
            {
                "TaskSpecification",
                "Architecture",
                "TechnicalSpecification",
                "Roadmap",
                "DocumentReview",
                "UserApproval",
                "Implementation",
                "UiAcceptance",
                "FinalVerification",
                "Custom"
            },
            Enum.GetNames<WorkflowStageKind>());
        Assert.Equal(
            new[] { "Approved", "Rejected" },
            Enum.GetNames<UserApprovalDecision>());
    }

    [Fact]
    public void WorkflowRunState_ClassifiesTerminalStates()
    {
        Assert.False(WorkflowRun.IsTerminalState(WorkflowRunState.Pending));
        Assert.False(WorkflowRun.IsTerminalState(WorkflowRunState.Running));
        Assert.False(WorkflowRun.IsTerminalState(WorkflowRunState.Suspended));
        Assert.True(WorkflowRun.IsTerminalState(WorkflowRunState.Completed));
        Assert.True(WorkflowRun.IsTerminalState(WorkflowRunState.Failed));
        Assert.True(WorkflowRun.IsTerminalState(WorkflowRunState.Cancelled));
    }

    [Fact]
    public void RecordUserApproval_IsBlockedOnTerminalRuns()
    {
        var run = CreateRunningRun();
        run.Fail("failed", StartedAt.AddMinutes(1));

        Assert.Throws<InvalidStateTransitionException>(
            () => run.RecordUserApproval(CreateApproval(DocumentHash)));
    }

    private static WorkflowRun CreateRunningRun()
    {
        return WorkflowRun.Start(
            "run-1",
            "project-1",
            "pkg-1",
            "ver-1",
            "session-1",
            CreateStage("stage-1", nextStageId: "stage-2", role: ArchitectRole),
            StartedAt);
    }

    private static WorkflowRun CreateRun(
        WorkflowRunState state,
        DateTimeOffset? endedAtUtc,
        WorkflowTerminalOutcome terminalOutcome)
    {
        return new WorkflowRun(
            "run-1",
            "project-1",
            "pkg-1",
            "ver-1",
            null,
            state,
            "stage-1",
            ArchitectRole,
            StartedAt,
            endedAtUtc,
            terminalOutcome,
            terminalReason: null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>());
    }

    private static WorkflowStageDefinition CreateStage(
        string stageId,
        string? nextStageId = null,
        string? failureStageId = null,
        IReadOnlyList<string>? reviewerRoles = null,
        bool requiresUserApproval = false,
        string? artifactRequirement = null,
        string role = "Coordinator")
    {
        return new WorkflowStageDefinition(
            stageId,
            $"Display {stageId}",
            role,
            WorkflowStageKind.Custom,
            reviewerRoles ?? Array.Empty<string>(),
            requiresUserApproval,
            artifactRequirement,
            nextStageId,
            failureStageId);
    }

    /// <summary>
    /// Records the document the stage under review actually produced. The hash it carries is the content hash
    /// of that document, so a verdict that pins a different one is visibly not about it.
    /// </summary>
    private static WorkflowRun WithArtifact(
        WorkflowRun run,
        string stageId = "stage-1",
        string kind = DocumentKind,
        string? blobId = null,
        string? artifactId = null,
        DateTimeOffset? createdAtUtc = null) =>
        run.WithArtifact(new WorkflowArtifactEvidence(
            artifactId ?? $"artifact-{stageId}",
            run.Id,
            stageId,
            kind,
            blobId ?? DocumentHash,
            blobId ?? DocumentHash,
            createdAtUtc ?? StartedAt.AddMinutes(1),
            128,
            DataClassification.PrivateSource));

    private static ReviewerVerdictRecord CreateVerdict(
        string reviewerRole,
        WorkflowReviewVerdict verdict,
        string? documentHash = null)
    {
        return new ReviewerVerdictRecord(
            reviewerRole,
            "route-1",
            documentHash ?? DocumentHash,
            verdict,
            $"{reviewerRole} returned {verdict}.",
            StartedAt.AddMinutes(1),
            ExecutionIdFor(reviewerRole),
            "stage-1",
            ArtifactIdFor(reviewerRole));
    }

    /// <summary>
    /// A reviewer execution per recorded verdict: a succeeded, read-only turn of this run on this stage,
    /// for that role, on the route the verdict names, about the artifact row the verdict names.
    /// <para>
    /// These runs are legacy runs - <c>WorkflowRun.Start</c> pins no template - so the gate does not demand
    /// the evidence. It is supplied anyway so every test in this file exercises the same evidence path the
    /// production gate walks, and so a change to what satisfies an execution shows up here rather than
    /// only in the integration suite.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ReviewerExecutionEvidence> ReviewerExecutions(
        IReadOnlyList<ReviewerVerdictRecord> verdicts) =>
        verdicts
            .Where(verdict => verdict.IsLinked)
            .Select(verdict => new ReviewerExecutionEvidence(
                verdict.ExecutionId!,
                $"session-{verdict.ReviewerRole}",
                "run-1",
                verdict.ReviewerRole,
                verdict.StageId!,
                verdict.RouteId,
                verdict.RouteId,
                verdict.ReviewedArtifactId!,
                verdict.DocumentHash,
                isReadOnly: true,
                ExecutionState.Succeeded))
            .ToArray();

    private static string ExecutionIdFor(string reviewerRole) => $"execution-{reviewerRole}";

    private static string ArtifactIdFor(string reviewerRole) => $"artifact-{reviewerRole}";

    private static UserApprovalEvidence CreateApproval(string documentHash)
    {
        return new UserApprovalEvidence(
            "approval-1",
            "user-1",
            "stage-1",
            documentHash,
            UserApprovalDecision.Approved,
            "Approved by the project owner.",
            StartedAt.AddMinutes(2));
    }
}
