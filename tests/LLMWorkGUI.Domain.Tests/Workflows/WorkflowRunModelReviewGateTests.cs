using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Domain.Tests.Workflows;

/// <summary>
/// The transition gate of a run pinned to a template version, and the reviewer-execution evidence it now
/// requires.
/// <para>
/// A pinned stage's reviewer verdict used to be a role, a route string, a hash and a verdict value, and any
/// caller could write one. That is a claim, not evidence: it cannot distinguish a model that read the
/// artifact from a screen that typed a route name, and it cannot tell an <c>Approve</c> of one revision from
/// an <c>Approve</c> of another. Every test here is about the single rule that closes that gap - a verdict is
/// only an approval when a persisted, succeeded, read-only execution of this run, this stage and this role
/// observed the assigned route while reviewing the very artifact the gate is about.
/// </para>
/// </summary>
public sealed partial class WorkflowRunModelReviewGateTests
{
    private const string RunId = "run-1";
    private const string StageId = "stage-1";
    private const string NextStageId = "stage-2";
    private const string DocumentKind = "ReviewedDocument";
    private const string ReviewerRole = "Reviewer";
    private const string RouteId = "route-1";
    private const string ArtifactId = "artifact-1";
    private const string ExecutionId = "execution-1";

    private const string GraphSnapshot = """{"entryNodeId":"stage-1","nodes":[]}""";
    private static string SchemeSnapshot => System.Text.Json.JsonSerializer.Serialize(
        new { InitialStageId = StageId, Stages = new[] { CurrentStage(), NextStage() } },
        new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

    private static readonly DateTimeOffset StartedAt = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    private static readonly string DocumentHash = Hash('a');

    [Fact]
    public void ValidateGate_RefusesWeakenedPinnedStageWithoutChangingTheRun()
    {
        var run = PinnedRunWithArtifact();
        var weakened = new WorkflowStageDefinition(StageId, "Review", "Coordinator",
            WorkflowStageKind.DocumentReview, Array.Empty<string>(), false, DocumentKind, NextStageId, null);
        Assert.Throws<InvalidOperationException>(() => run.ValidateTransitionGate(weakened));
        Assert.Equal(StageId, run.CurrentStageId);
        Assert.Empty(run.Transitions);
    }

    [Fact]
    public void ValidateGate_RefusesAnotherPinnedStage()
    {
        var run = PinnedRunWithArtifact();
        Assert.Throws<InvalidOperationException>(() => run.ValidateTransitionGate(NextStage()));
    }


    [Fact]
    public void APinnedRunRefusesAnUnlinkedApproveAndSaysWhy()
    {
        // The shape every historical unlinked approval has: a real row with a real role, a real route string
        // and a real hash. It cannot be written through the aggregate any more, so it is materialized the way
        // a database written before this rule existed materializes it - through the constructor, on the way
        // out of the repository. The gate has to refuse it, and refusing it is not the same as deleting it.
        var run = PinnedRunWithPersistedUnlinkedApproval();

        var refusal = Assert.Throws<InvalidOperationException>(() => Advance(run));

        Assert.Contains("names no reviewer execution", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(StageId, run.CurrentStageId);
        Assert.Empty(run.Transitions);
        Assert.Equal(WorkflowReviewVerdict.Approve, Assert.Single(run.Verdicts).Verdict);
    }

    [Fact]
    public void APinnedRunRefusesAVerdictWhoseExecutionDoesNotExist()
    {
        // A verdict that names an execution id is still only a claim until the execution is there. An id
        // that resolves to nothing is the same as no execution at all.
        var run = PinnedRunWithArtifact();
        run.RecordReviewerVerdict(Verdict());

        var refusal = Assert.Throws<InvalidOperationException>(() => Advance(run, evidence: Array.Empty<ReviewerExecutionEvidence>()));

        Assert.Contains(ExecutionId, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("no persisted reviewer execution", refusal.Message, StringComparison.Ordinal);
    }

    public static TheoryData<ExecutionState> UnsatisfiedExecutionStates() => new()
    {
        ExecutionState.Queued,
        ExecutionState.Starting,
        ExecutionState.Running,
        ExecutionState.WaitingApproval,
        ExecutionState.Cancelling,
        ExecutionState.Cancelled,
        ExecutionState.Ambiguous,
        ExecutionState.Failed,
        ExecutionState.TimedOut,
        ExecutionState.RouteMismatch
    };

    [Theory]
    [MemberData(nameof(UnsatisfiedExecutionStates))]
    public void APinnedRunRefusesAnExecutionThatDidNotSucceed(ExecutionState state)
    {
        // Cancellation and an ambiguous delivery are the two that matter most: both may or may not have
        // reached the model, and both must stay unsatisfied. Neither is ever turned into a success here.
        var run = PinnedRunWithArtifact();
        run.RecordReviewerVerdict(Verdict());

        var refusal = Assert.Throws<InvalidOperationException>(
            () => Advance(run, Only(Evidence(state, observedRouteId: RouteId))));

        Assert.Contains($"ended in state '{state}'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedRunRefusesAnExecutionThatWasNotReadOnly()
    {
        var run = PinnedRunWithArtifact();
        run.RecordReviewerVerdict(Verdict());

        var refusal = Assert.Throws<InvalidOperationException>(
            () => Advance(run, Only(Evidence(ExecutionState.Succeeded, RouteId, isReadOnly: false))));

        Assert.Contains("not recorded as read-only", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedRunRefusesAnExecutionWhoseRouteWasNeverObserved()
    {
        // The requested route alone is written before dispatch. Promoting it into the observed column is
        // exactly the fabrication this gate exists to refuse, so a null observation is "no evidence", never
        // "the same route".
        var run = PinnedRunWithArtifact();
        run.RecordReviewerVerdict(Verdict());

        var refusal = Assert.Throws<InvalidOperationException>(
            () => Advance(run, Only(Evidence(ExecutionState.Succeeded, observedRouteId: null))));

        Assert.Contains("no backend reported an observed route", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(RouteId, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedRunRefusesAnExecutionWhoseObservedRouteIsAnotherOne()
    {
        var run = PinnedRunWithArtifact();
        run.RecordReviewerVerdict(Verdict());

        var refusal = Assert.Throws<InvalidOperationException>(
            () => Advance(run, Only(Evidence(ExecutionState.Succeeded, "route-other"))));

        Assert.Contains("observed route 'route-other'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedRunRefusesAVerdictWhoseRouteIsNotTheOneTheExecutionRequested()
    {
        var run = PinnedRunWithArtifact();
        run.RecordReviewerVerdict(Verdict(routeId: "route-other"));

        var refusal = Assert.Throws<InvalidOperationException>(
            () => Advance(run, Only(Evidence(ExecutionState.Succeeded, RouteId))));

        Assert.Contains("the verdict names route 'route-other'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedRunRefusesAnExecutionOfAnotherRunStageOrRole()
    {
        var run = PinnedRunWithArtifact();
        run.RecordReviewerVerdict(Verdict());

        var otherRun = Assert.Throws<InvalidOperationException>(
            () => Advance(run, Only(Evidence(ExecutionState.Succeeded, RouteId, runId: "run-other"))));
        Assert.Contains("belongs to run 'run-other'", otherRun.Message, StringComparison.Ordinal);

        var otherStage = Assert.Throws<InvalidOperationException>(
            () => Advance(run, Only(Evidence(ExecutionState.Succeeded, RouteId, stageId: "stage-other"))));
        Assert.Contains("recorded on stage 'stage-other'", otherStage.Message, StringComparison.Ordinal);

        var otherRole = Assert.Throws<InvalidOperationException>(
            () => Advance(run, Only(Evidence(ExecutionState.Succeeded, RouteId, role: "Architect"))));
        Assert.Contains("recorded for role 'Architect'", otherRole.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedRunRefusesAnExecutionThatReviewedAnotherArtifactRowOrHash()
    {
        var run = PinnedRunWithArtifact();
        run.RecordReviewerVerdict(Verdict());

        var otherRow = Assert.Throws<InvalidOperationException>(
            () => Advance(run, Only(Evidence(ExecutionState.Succeeded, RouteId, artifactId: "artifact-other"))));
        Assert.Contains("reviewed artifact 'artifact-other'", otherRow.Message, StringComparison.Ordinal);

        var otherHash = Assert.Throws<InvalidOperationException>(
            () => Advance(run, Only(Evidence(ExecutionState.Succeeded, RouteId, hash: Hash('b')))));
        Assert.Contains($"reviewed hash '{Hash('b')}'", otherHash.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedRunAdvancesOnASucceededReadOnlyExecutionOfTheCurrentArtifact()
    {
        var run = PinnedRunWithArtifact();
        run.RecordReviewerVerdict(Verdict());

        var transition = run.AdvanceTo(
            CurrentStage(),
            NextStage(),
            "reviewed and observed",
            StartedAt.AddMinutes(5),
            new[] { Evidence(ExecutionState.Succeeded, RouteId) });

        Assert.Equal(NextStageId, run.CurrentStageId);
        Assert.Equal(ReviewerRole, Assert.Single(transition.ReviewerVerdicts).ReviewerRole);
    }

    [Fact]
    public void ALegacyRunStillAcceptsItsUnlinkedVerdicts()
    {
        // The documented compatibility for a run that predates template pinning. It is deliberately not
        // extended to anything a pinned template governs, and a legacy run never consults the evidence.
        var run = LegacyRunWithArtifact();
        run.RecordLegacyUnlinkedReviewerVerdict(
            new ReviewerVerdictRecord(
                ReviewerRole,
                RouteId,
                DocumentHash,
                WorkflowReviewVerdict.Approve,
                "the original rule",
                StartedAt.AddMinutes(1)));

        var transition = run.AdvanceTo(
            CurrentStage(),
            NextStage(),
            "legacy advance",
            StartedAt.AddMinutes(5));

        Assert.Equal(NextStageId, run.CurrentStageId);
        Assert.Equal(ReviewerRole, Assert.Single(transition.ReviewerVerdicts).ReviewerRole);
    }

    [Fact]
    public void APinnedRunRefusesTheLegacyUnlinkedVerdictAtTheAggregateToo()
    {
        // The service refuses this case as well, and the aggregate refuses it independently, so the rule
        // does not depend on there being only one door into it.
        var run = PinnedRunWithArtifact();

        Assert.Throws<InvalidStateTransitionException>(
            () => run.RecordLegacyUnlinkedReviewerVerdict(
                new ReviewerVerdictRecord(
                    ReviewerRole,
                    RouteId,
                    DocumentHash,
                    WorkflowReviewVerdict.Approve,
                    "typed by a caller",
                    StartedAt.AddMinutes(1))));

        Assert.Empty(run.Verdicts);
    }

    [Fact]
    public void ALegacyRunRefusesALinkedVerdictOnTheUnlinkedPath()
    {
        // A record that names an execution is not an unlinked one. The two paths cannot be crossed, so
        // "legacy" can never be used to smuggle a linked record past the legacy method's own refusals.
        var run = LegacyRunWithArtifact();

        Assert.Throws<ArgumentException>(() => run.RecordLegacyUnlinkedReviewerVerdict(Verdict()));
    }

    [Fact]
    public void TheCheckedPathRefusesAnUnlinkedVerdictAndTheUnlinkedPathRefusesALegacyRun()
    {
        var pinned = PinnedRunWithArtifact();
        Assert.Throws<ArgumentException>(() => pinned.RecordReviewerVerdict(
            new ReviewerVerdictRecord(
                ReviewerRole,
                RouteId,
                DocumentHash,
                WorkflowReviewVerdict.Approve,
                "typed by a caller",
                StartedAt.AddMinutes(1))));

        Assert.Empty(pinned.Verdicts);
    }

    [Fact]
    public void AVerdictThatNamesAnExecutionButNoStageOrArtifactIsRefused()
    {
        // The linkage is one value. A record that claims a model review and cannot say which stage or which
        // artifact row it was about is completed by nobody: the read is refused at construction.
        Assert.Throws<ArgumentException>(() => new ReviewerVerdictRecord(
            ReviewerRole,
            RouteId,
            DocumentHash,
            WorkflowReviewVerdict.Approve,
            "half linked",
            StartedAt.AddMinutes(1),
            ExecutionId));

        Assert.Throws<ArgumentException>(() => new ReviewerVerdictRecord(
            ReviewerRole,
            RouteId,
            DocumentHash,
            WorkflowReviewVerdict.Approve,
            "half linked",
            StartedAt.AddMinutes(1),
            ExecutionId,
            StageId,
            "   "));
    }

    private static WorkflowRun PinnedRunWithArtifact() =>
        WithArtifact(CreatePinnedRun());

    private static WorkflowRun LegacyRunWithArtifact() =>
        WithArtifact(CreateLegacyRun());

    /// <summary>
    /// A pinned run that already carries the unlinked approval a database written before this rule would
    /// hold. It goes through the aggregate's constructor rather than through a mutator, which is exactly the
    /// path the repository takes when it reads such a row back.
    /// </summary>
    private static WorkflowRun PinnedRunWithPersistedUnlinkedApproval() =>
        new(
            RunId,
            "project-1",
            "pkg-1",
            "ver-1",
            sessionId: null,
            WorkflowRunState.Running,
            StageId,
            "Coordinator",
            StartedAt,
            endedAtUtc: null,
            WorkflowTerminalOutcome.None,
            terminalReason: null,
            Array.Empty<WorkflowTransitionRecord>(),
            new[]
            {
                new ReviewerVerdictRecord(
                    ReviewerRole,
                    RouteId,
                    DocumentHash,
                    WorkflowReviewVerdict.Approve,
                    "an approval written before executions were required",
                    StartedAt.AddMinutes(2))
            },
            Array.Empty<UserApprovalEvidence>(),
            "template-1",
            1,
            GraphSnapshot,
            SchemeSnapshot,
            new[]
            {
                new WorkflowArtifactEvidence(
                    ArtifactId,
                    RunId,
                    StageId,
                    DocumentKind,
                    DocumentHash,
                    DocumentHash,
                    StartedAt.AddMinutes(1),
                    64,
                    DataClassification.PrivateSource)
            });

    private static WorkflowRun WithArtifact(WorkflowRun run) =>
        run.WithArtifact(new WorkflowArtifactEvidence(
            ArtifactId,
            RunId,
            StageId,
            DocumentKind,
            DocumentHash,
            DocumentHash,
            StartedAt.AddMinutes(1),
            64,
            DataClassification.PrivateSource));

    private static WorkflowRun CreatePinnedRun() =>
        WorkflowRun.StartPinnedToTemplate(
            RunId,
            "project-1",
            "pkg-1",
            "ver-1",
            sessionId: null,
            CurrentStage(),
            StartedAt,
            "template-1",
            1,
            GraphSnapshot,
            SchemeSnapshot);

    private static WorkflowRun CreateLegacyRun() =>
        WorkflowRun.Start(
            RunId,
            "project-1",
            "pkg-1",
            "ver-1",
            sessionId: null,
            CurrentStage(),
            StartedAt);

    private static WorkflowStageDefinition CurrentStage() => new(
        StageId,
        "Review",
        "Coordinator",
        WorkflowStageKind.DocumentReview,
        new[] { ReviewerRole },
        requiresUserApproval: false,
        DocumentKind,
        NextStageId,
        failureStageId: null);

    private static WorkflowStageDefinition NextStage() => new(
        NextStageId,
        "Done",
        "Coordinator",
        WorkflowStageKind.FinalVerification,
        Array.Empty<string>(),
        requiresUserApproval: false,
        artifactRequirement: null,
        nextStageId: null,
        failureStageId: null);

    private static ReviewerVerdictRecord Verdict(string routeId = RouteId) =>
        new(
            ReviewerRole,
            routeId,
            DocumentHash,
            WorkflowReviewVerdict.Approve,
            "the assigned model approved these bytes",
            StartedAt.AddMinutes(2),
            ExecutionId,
            StageId,
            ArtifactId);

    private static ReviewerExecutionEvidence Evidence(
        ExecutionState state,
        string? observedRouteId,
        bool isReadOnly = true,
        string runId = RunId,
        string stageId = StageId,
        string role = ReviewerRole,
        string artifactId = ArtifactId,
        string? hash = null) =>
        new(
            ExecutionId,
            "session-1",
            runId,
            role,
            stageId,
            RouteId,
            observedRouteId,
            artifactId,
            hash ?? DocumentHash,
            isReadOnly,
            state);

    private static IReadOnlyList<ReviewerExecutionEvidence> Only(ReviewerExecutionEvidence evidence) =>
        new[] { evidence };

    private static void Advance(WorkflowRun run) =>
        Advance(run, new[] { Evidence(ExecutionState.Succeeded, RouteId) });

    private static void Advance(WorkflowRun run, IReadOnlyList<ReviewerExecutionEvidence> evidence) =>
        run.AdvanceTo(CurrentStage(), NextStage(), "advance", StartedAt.AddMinutes(5), evidence);

    private static string Hash(char character) =>
        WorkflowArtifactEvidence.ContentHashPrefix + new string(character, WorkflowArtifactEvidence.Sha256HexLength);
}
