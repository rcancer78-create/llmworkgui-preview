using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests.Workflows;

/// <summary>
/// The durable artifact gate of <see cref="WorkflowRun.AdvanceTo"/>: a transition is authorized by a stored
/// run artifact of the exact kind the stage requires, addressed to this run and this stage, and by nothing
/// else. Every case below starts from a run that already recorded the artifact, because the point of each one
/// is what the gate does with evidence that exists - not with a row that was never written.
/// </summary>
public sealed class WorkflowRunArtifactGateTests
{
    private const string DocumentKind = "DocumentBundle";
    private const string ReviewerRole = "Reviewer";
    private const string ArchitectRole = "Architect";

    private static readonly string DocumentHash = Hash('a');
    private static readonly string OtherHash = Hash('b');
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheCurrentArtifactIsTheNewestOfThisRunThisStageAndThisKind()
    {
        var artifacts = new[]
        {
            Artifact("a", "stage-1", DocumentKind, StartedAt.AddMinutes(1)),
            Artifact("b", "stage-1", DocumentKind, StartedAt.AddMinutes(3)),
            Artifact("c", "stage-1", DocumentKind, StartedAt.AddMinutes(2))
        };

        var current = WorkflowArtifactEvidence.SelectCurrent(artifacts, "run-1", "stage-1", DocumentKind);

        Assert.NotNull(current);
        Assert.Equal("b", current!.ArtifactId);
    }

    [Fact]
    public void TheCurrentArtifactBreaksATieByItsOwnId()
    {
        var at = StartedAt.AddMinutes(1);
        var artifacts = new[]
        {
            Artifact("a", "stage-1", DocumentKind, at),
            Artifact("b", "stage-1", DocumentKind, at)
        };

        Assert.Equal("b", WorkflowArtifactEvidence.SelectCurrent(artifacts, "run-1", "stage-1", DocumentKind)!.ArtifactId);
    }

    [Fact]
    public void AnArtifactOfAnotherStageIsNotTheCurrentOneOfThisStage()
    {
        var artifacts = new[]
        {
            Artifact("early", "stage-1", DocumentKind, StartedAt.AddMinutes(1)),
            // Newer, of the same kind, but recorded for a different stage - which is what a failure loop
            // produces when it sends the run through another stage that declares the same artifact kind.
            Artifact("late", "stage-2", DocumentKind, StartedAt.AddMinutes(9))
        };

        Assert.Equal(
            "early",
            WorkflowArtifactEvidence.SelectCurrent(artifacts, "run-1", "stage-1", DocumentKind)!.ArtifactId);
    }

    [Fact]
    public void AnArtifactOfAnotherRunOrAnotherKindIsNotTheCurrentOne()
    {
        var foreignRun = new WorkflowArtifactEvidence(
            "foreign-run",
            "run-2",
            "stage-1",
            DocumentKind,
            DocumentHash,
            DocumentHash,
            Minute(9),
            12,
            DataClassification.PrivateSource);

        var artifacts = new[]
        {
            Artifact("own", "stage-1", DocumentKind, Minute(1)),
            foreignRun,
            Artifact("other-kind", "stage-1", "UiAcceptanceEvidence", Minute(9))
        };

        Assert.Equal(
            "own",
            WorkflowArtifactEvidence.SelectCurrent(artifacts, "run-1", "stage-1", DocumentKind)!.ArtifactId);
        Assert.Equal(
            "other-kind",
            WorkflowArtifactEvidence.SelectCurrent(artifacts, "run-1", "stage-1", "UiAcceptanceEvidence")!.ArtifactId);
    }

    [Fact]
    public void AdvanceTo_WithoutTheRequiredArtifactIsBlocked()
    {
        var run = CreateRun();

        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(Stage("stage-1", "stage-2", DocumentKind), Stage("stage-2"), "reason", Minute(1)));

        Assert.Contains(DocumentKind, exception.Message, StringComparison.Ordinal);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void AdvanceTo_WithAnArtifactOfTheWrongKindIsBlocked()
    {
        var run = WithArtifact(CreateRun(), Artifact("a", "stage-1", "UiAcceptanceEvidence", Minute(1)));

        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(Stage("stage-1", "stage-2", DocumentKind), Stage("stage-2"), "reason", Minute(2)));

        Assert.Contains(DocumentKind, exception.Message, StringComparison.Ordinal);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void AdvanceTo_WithAnArtifactOfAnotherStageIsBlocked()
    {
        var run = WithArtifact(CreateRun(), Artifact("a", "stage-9", DocumentKind, Minute(1)));

        Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(Stage("stage-1", "stage-2", DocumentKind), Stage("stage-2"), "reason", Minute(2)));

        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void AdvanceTo_AfterTheCurrentArtifactIsReplacedRequiresTheNewHash()
    {
        var run = WithArtifact(CreateRun(), Artifact("old", "stage-1", DocumentKind, Minute(1), DocumentHash));
        run = WithArtifact(run, Artifact("new", "stage-1", DocumentKind, Minute(2), OtherHash));
        run.RecordReviewerVerdict(Verdict(ReviewerRole, DocumentHash, WorkflowReviewVerdict.Approve));

        // The approval of the document that was superseded stays in the history and authorizes nothing.
        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(
                Stage("stage-1", "stage-2", DocumentKind, ReviewerRole),
                Stage("stage-2"),
                "reason",
                Minute(3)));

        Assert.Contains(OtherHash, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(DocumentHash, exception.Message, StringComparison.Ordinal);
        Assert.Empty(run.Transitions);

        run.RecordReviewerVerdict(Verdict(ReviewerRole, OtherHash, WorkflowReviewVerdict.Approve));

        var transition = run.AdvanceTo(
            Stage("stage-1", "stage-2", DocumentKind, ReviewerRole),
            Stage("stage-2"),
            "reason",
            Minute(3));

        Assert.Equal("new", transition.AuthorizingArtifactId);
        Assert.Equal(OtherHash, transition.AuthorizingArtifactHash);
    }

    [Fact]
    public void AdvanceTo_AfterAFailureLoopUsesTheArtifactOfTheStageItIsLeaving()
    {
        // The run is back at stage-1 after a failure further along. It produced a new document there, and
        // stage-2 recorded an artifact of the same kind while the run was there. The newer one is stage-2's
        // evidence, not this stage's.
        var run = WithArtifact(CreateRun(), Artifact("first-pass", "stage-1", DocumentKind, Minute(1), DocumentHash));
        run = WithArtifact(run, Artifact("other-stage", "stage-2", DocumentKind, Minute(3), OtherHash));
        run = WithArtifact(run, Artifact("second-pass", "stage-1", DocumentKind, Minute(4), OtherHash));
        run.RecordReviewerVerdict(Verdict(ReviewerRole, DocumentHash, WorkflowReviewVerdict.Approve, 2));
        run.RecordReviewerVerdict(Verdict(ReviewerRole, OtherHash, WorkflowReviewVerdict.Approve, 5));

        var transition = run.AdvanceTo(
            Stage("stage-1", "stage-2", DocumentKind, ReviewerRole),
            Stage("stage-2"),
            "second pass",
            Minute(6));

        Assert.Equal("second-pass", transition.AuthorizingArtifactId);
        Assert.Equal(OtherHash, transition.AuthorizingArtifactHash);
    }

    [Fact]
    public void AdvanceTo_OnAnApprovalOnlyStageUsesTheArtifactHash()
    {
        var run = WithArtifact(CreateRun(), Artifact("a", "stage-1", DocumentKind, Minute(1)));
        run.RecordUserApproval(Approval(DocumentHash));

        var stage = Stage("stage-1", "stage-2", DocumentKind, requiresUserApproval: true);
        var transition = run.AdvanceTo(stage, Stage("stage-2"), "approved", Minute(2));

        // There is no reviewer at all, and the approval still had to pin the stored content hash.
        Assert.Empty(transition.ReviewerVerdicts);
        Assert.Equal(DocumentHash, transition.AuthorizingArtifactHash);
        Assert.NotNull(transition.UserApproval);
    }

    [Fact]
    public void AdvanceTo_OnAnApprovalOnlyStageBlocksAnApprovalOfAnotherDocument()
    {
        var run = WithArtifact(CreateRun(), Artifact("a", "stage-1", DocumentKind, Minute(1)));

        // The aggregate refuses an approval that contradicts its own current artifact, so a decision about a
        // document that has already been replaced cannot even be written - by a screen, a recovery scenario
        // or any other caller of the aggregate.
        var refused = Assert.Throws<ArgumentException>(() => run.RecordUserApproval(Approval(OtherHash)));

        Assert.Contains(DocumentHash, refused.Message, StringComparison.Ordinal);
        Assert.Empty(run.Approvals);

        // And with no approval standing, the transition stays blocked.
        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(
                Stage("stage-1", "stage-2", DocumentKind, requiresUserApproval: true),
                Stage("stage-2"),
                "approved",
                Minute(2)));

        Assert.Contains("user approval", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void RecordUserApproval_AfterAMismatchedApprovalIsRefusedTheValidOneStillAuthorizes()
    {
        var run = WithArtifact(CreateRun(), Artifact("a", "stage-1", DocumentKind, Minute(1)));

        // The removed null-hash bypass: an approval whose hash is not the current one is not approval, and
        // the aggregate now says so at the moment of recording instead of leaving a second decision to
        // override the first one at the transition.
        run.RecordUserApproval(Approval(DocumentHash));
        Assert.Throws<ArgumentException>(
            () => run.RecordUserApproval(Approval(OtherHash, decision: UserApprovalDecision.Approved, minutes: 3)));

        // The refused decision left nothing behind, so the one that pins the current content is the one that
        // authorizes - and it authorizes exactly that hash.
        var transition = run.AdvanceTo(
            Stage("stage-1", "stage-2", DocumentKind, requiresUserApproval: true),
            Stage("stage-2"),
            "approved",
            Minute(4));

        Assert.Equal(DocumentHash, transition.UserApproval!.ArtifactHash);
        Assert.Equal(DocumentHash, transition.AuthorizingArtifactHash);
    }

    [Fact]
    public void AdvanceTo_UsesTheLatestVerdictOfEveryRequiredRoleAndBlocksAConflict()
    {
        var run = WithArtifact(CreateRun(), Artifact("a", "stage-1", DocumentKind, Minute(1)));
        run.RecordReviewerVerdict(Verdict(ReviewerRole, DocumentHash, WorkflowReviewVerdict.Approve, 2));
        run.RecordReviewerVerdict(Verdict(ReviewerRole, DocumentHash, WorkflowReviewVerdict.RequestChanges, 3));
        run.RecordReviewerVerdict(Verdict(ArchitectRole, DocumentHash, WorkflowReviewVerdict.Approve, 4));

        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(
                Stage("stage-1", "stage-2", DocumentKind, ReviewerRole, ArchitectRole),
                Stage("stage-2"),
                "reason",
                Minute(5)));

        Assert.Contains("RequestChanges", exception.Message, StringComparison.Ordinal);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void AdvanceTo_AllowsTheStageOnceTheConflictingRoleApprovesAgain()
    {
        var run = WithArtifact(CreateRun(), Artifact("a", "stage-1", DocumentKind, Minute(1)));
        run.RecordReviewerVerdict(Verdict(ReviewerRole, DocumentHash, WorkflowReviewVerdict.Approve, 2));
        run.RecordReviewerVerdict(Verdict(ReviewerRole, DocumentHash, WorkflowReviewVerdict.RequestChanges, 3));
        run.RecordReviewerVerdict(Verdict(ReviewerRole, DocumentHash, WorkflowReviewVerdict.Approve, 4));
        run.RecordReviewerVerdict(Verdict(ArchitectRole, DocumentHash, WorkflowReviewVerdict.Approve, 5));

        var transition = run.AdvanceTo(
            Stage("stage-1", "stage-2", DocumentKind, ReviewerRole, ArchitectRole),
            Stage("stage-2"),
            "reason",
            Minute(6));

        Assert.Equal(2, transition.ReviewerVerdicts.Count);
    }

    [Fact]
    public void AdvanceTo_BlocksAReviewerRoleThatNeverReviewedTheCurrentDocument()
    {
        var run = WithArtifact(CreateRun(), Artifact("a", "stage-1", DocumentKind, Minute(1)));
        run.RecordReviewerVerdict(Verdict(ReviewerRole, DocumentHash, WorkflowReviewVerdict.Approve, 2));

        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(
                Stage("stage-1", "stage-2", DocumentKind, ReviewerRole, ArchitectRole),
                Stage("stage-2"),
                "reason",
                Minute(3)));

        Assert.Contains(ArchitectRole, exception.Message, StringComparison.Ordinal);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void AdvanceTo_BlocksAStageThatDeclaresReviewersWithoutAnArtifactRequirement()
    {
        var run = WithArtifact(CreateRun(), Artifact("a", "stage-1", DocumentKind, Minute(1)));
        run.RecordReviewerVerdict(Verdict(ReviewerRole, DocumentHash, WorkflowReviewVerdict.Approve, 2));

        var exception = Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(
                Stage("stage-1", "stage-2", artifactRequirement: null, ReviewerRole),
                Stage("stage-2"),
                "reason",
                Minute(3)));

        Assert.Contains("artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void AdvanceTo_BlocksAStageThatDeclaresAUserApprovalWithoutAnArtifactRequirement()
    {
        var run = WithArtifact(CreateRun(), Artifact("a", "stage-1", DocumentKind, Minute(1)));
        run.RecordUserApproval(Approval(DocumentHash));

        Assert.Throws<InvalidOperationException>(
            () => run.AdvanceTo(
                Stage("stage-1", "stage-2", artifactRequirement: null, requiresUserApproval: true),
                Stage("stage-2"),
                "reason",
                Minute(2)));
    }

    [Fact]
    public void AdvanceTo_OnAStageWithoutAnyGateNeedsNoArtifact()
    {
        var run = CreateRun();

        var transition = run.AdvanceTo(
            Stage("stage-1", "stage-2", artifactRequirement: null),
            Stage("stage-2"),
            "reason",
            Minute(1));

        Assert.False(transition.HasAuthorizingArtifact);
        Assert.Null(transition.AuthorizingArtifactId);
        Assert.Null(transition.AuthorizingArtifactHash);
    }

    [Fact]
    public void WithArtifact_LeavesTheOriginalRunUntouched()
    {
        var run = CreateRun();

        var staged = run.WithArtifact(Artifact("a", "stage-1", DocumentKind, Minute(1)));

        Assert.Empty(run.Artifacts);
        Assert.Single(staged.Artifacts);
    }

    [Fact]
    public void WithArtifact_RefusesAnArtifactOfAnotherRun()
    {
        var run = CreateRun();
        var foreign = Artifact("a", "stage-1", DocumentKind, Minute(1));

        Assert.Throws<ArgumentException>(() => run.WithArtifact(new WorkflowArtifactEvidence(
            foreign.ArtifactId,
            "run-2",
            foreign.StageId,
            foreign.Kind,
            foreign.BlobId,
            foreign.HashSha256,
            foreign.CreatedAtUtc,
            foreign.SizeBytes,
            foreign.Classification)));
    }

    [Fact]
    public void WithArtifact_RefusesATerminalRun()
    {
        var run = CreateRun();
        run.Complete("done", Minute(5));

        Assert.Throws<InvalidStateTransitionException>(
            () => run.WithArtifact(Artifact("a", "stage-1", DocumentKind, Minute(1))));
    }

    [Fact]
    public void TheConstructorRefusesAnArtifactOfAnotherRun()
    {
        var artifact = Artifact("a", "stage-1", DocumentKind, Minute(1));

        Assert.Throws<ArgumentException>(() => new WorkflowRun(
            "run-1",
            "project-1",
            "pkg-1",
            "ver-1",
            null,
            WorkflowRunState.Running,
            "stage-1",
            "Coordinator",
            StartedAt,
            null,
            WorkflowTerminalOutcome.None,
            null,
            Array.Empty<WorkflowTransitionRecord>(),
            Array.Empty<ReviewerVerdictRecord>(),
            Array.Empty<UserApprovalEvidence>(),
            artifacts: new[] { new WorkflowArtifactEvidence(
                artifact.ArtifactId,
                "run-2",
                artifact.StageId,
                artifact.Kind,
                artifact.BlobId,
                artifact.HashSha256,
                artifact.CreatedAtUtc,
                artifact.SizeBytes,
                artifact.Classification) }));
    }

    [Fact]
    public void ATransitionRecordRecordsItsAuthorizingArtifactAsAPairOrNotAtAll()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowTransitionRecord(
            "transition-1",
            "stage-1",
            "stage-2",
            Minute(1),
            "reason",
            Array.Empty<ReviewerVerdictRecord>(),
            userApproval: null,
            authorizingArtifactId: "artifact-1"));

        var paired = new WorkflowTransitionRecord(
            "transition-1",
            "stage-1",
            "stage-2",
            Minute(1),
            "reason",
            Array.Empty<ReviewerVerdictRecord>(),
            userApproval: null,
            authorizingArtifactId: "artifact-1",
            authorizingArtifactHash: DocumentHash);

        Assert.True(paired.HasAuthorizingArtifact);
        Assert.Equal(DocumentHash, paired.AuthorizingArtifactHash);
    }

    [Fact]
    public void AnArtifactRefusesABlobIdThatIsNotTheHashOfItsOwnBytes()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowArtifactEvidence(
            "artifact-1",
            "run-1",
            "stage-1",
            DocumentKind,
            DocumentHash,
            OtherHash,
            Minute(1),
            12,
            DataClassification.PrivateSource));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("abc")]
    [InlineData("sha256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("sha512:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void AContentHashIsAcceptedOnlyInItsExactForm(string candidate)
    {
        Assert.False(WorkflowArtifactEvidence.IsContentHash(candidate));

        Assert.Throws<ArgumentException>(() => new WorkflowArtifactEvidence(
            "artifact-1",
            "run-1",
            "stage-1",
            DocumentKind,
            candidate,
            candidate,
            Minute(1),
            12,
            DataClassification.PrivateSource));
    }

    [Fact]
    public void AnArtifactRefusesBlankIdentifiersAndAnUnknownClassification()
    {
        Assert.Throws<ArgumentException>(() => Artifact(" ", "stage-1", DocumentKind, Minute(1)));
        Assert.Throws<ArgumentException>(() => Artifact("a", " ", DocumentKind, Minute(1)));
        Assert.Throws<ArgumentException>(() => Artifact("a", "stage-1", " ", Minute(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowArtifactEvidence(
            "artifact-1",
            "run-1",
            "stage-1",
            DocumentKind,
            DocumentHash,
            DocumentHash,
            Minute(1),
            -1,
            DataClassification.PrivateSource));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowArtifactEvidence(
            "artifact-1",
            "run-1",
            "stage-1",
            DocumentKind,
            DocumentHash,
            DocumentHash,
            Minute(1),
            12,
            (DataClassification)42));
    }

    private static WorkflowRun CreateRun() =>
        WorkflowRun.Start(
            "run-1",
            "project-1",
            "pkg-1",
            "ver-1",
            "session-1",
            Stage("stage-1", "stage-2", DocumentKind),
            StartedAt);

    private static WorkflowRun WithArtifact(WorkflowRun run, WorkflowArtifactEvidence artifact) =>
        run.WithArtifact(artifact);

    private static WorkflowStageDefinition Stage(
        string stageId,
        string? nextStageId = null,
        string? artifactRequirement = DocumentKind,
        params string[] reviewerRoles) =>
        Stage(stageId, nextStageId, artifactRequirement, requiresUserApproval: false, reviewerRoles);

    private static WorkflowStageDefinition Stage(
        string stageId,
        string? nextStageId,
        string? artifactRequirement,
        bool requiresUserApproval,
        params string[] reviewerRoles) =>
        new(
            stageId,
            $"Display {stageId}",
            "Coordinator",
            WorkflowStageKind.Custom,
            reviewerRoles,
            requiresUserApproval,
            artifactRequirement,
            nextStageId,
            failureStageId: null);

    private static WorkflowArtifactEvidence Artifact(
        string artifactId,
        string stageId,
        string kind,
        DateTimeOffset createdAtUtc,
        string? hash = null) =>
        new(
            artifactId,
            "run-1",
            stageId,
            kind,
            hash ?? DocumentHash,
            hash ?? DocumentHash,
            createdAtUtc,
            12,
            DataClassification.PrivateSource);

    /// <summary>
    /// A linked verdict: it names the reviewer execution, the stage and the artifact row it was read out
    /// of, so it is a real model-review record rather than a bare claim.
    /// <para>
    /// The runs in this file are legacy runs - <c>WorkflowRun.Start</c> pins no template - so the gate
    /// keeps the historical rule where a role, a route and a hash were the whole of the evidence. Recording
    /// the linkage anyway means these tests would also pass on a pinned run, and keeps a single verdict
    /// shape in the suite.
    /// </para>
    /// </summary>
    private static ReviewerVerdictRecord Verdict(
        string role,
        string documentHash,
        WorkflowReviewVerdict verdict = WorkflowReviewVerdict.Approve,
        int minutes = 1) =>
        new(
            role,
            "route-1",
            documentHash,
            verdict,
            "evidence",
            Minute(minutes),
            $"execution-{role}-{minutes}",
            "stage-1",
            $"artifact-{role}-{minutes}");

    private static UserApprovalEvidence Approval(
        string hash,
        UserApprovalDecision decision = UserApprovalDecision.Approved,
        int minutes = 1) =>
        new("approval-1", "user-1", "stage-1", hash, decision, "comment", Minute(minutes));

    private static DateTimeOffset Minute(int minutes) => StartedAt.AddMinutes(minutes);

    private static string Hash(char character) =>
        WorkflowArtifactEvidence.ContentHashPrefix + new string(character, WorkflowArtifactEvidence.Sha256HexLength);
}
