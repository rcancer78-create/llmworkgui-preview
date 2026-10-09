using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Observability;

public sealed class WorkflowRunTimelineTests
{
    // The stage under review is gated by a stored artifact, so the verdicts and the approval of that stage
    // pin the recorded content hash rather than an arbitrary document label.
    private const string DocumentHash = "sha256:" + "1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly DateTimeOffset BaseTime = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly WorkflowRunTimelineService _service = new();

    [Fact]
    public void BuildTimeline_OrdersTransitionsAndExecutionsChronologically()
    {
        var run = CreateRunWithTransition();
        var session = CreateSession();

        var early = CreateExecution("execution-early", BaseTime.AddMinutes(1), BaseTime.AddMinutes(5));
        var late = CreateExecution("execution-late", BaseTime.AddMinutes(20), BaseTime.AddMinutes(25));

        var timeline = _service.BuildTimeline(run, new[] { late, early }, new[] { session });

        Assert.Equal(3, timeline.Items.Count);
        Assert.Equal(new[] { 0, 1, 2 }, timeline.Items.Select(item => item.Sequence));
        Assert.Equal(
            new[] { "execution-early", "execution-late" },
            timeline.Items
                .Where(item => item.Kind == WorkflowRunTimelineItemKind.Execution)
                .Select(item => item.ExecutionId)
                .ToArray());
        Assert.Equal(
            new[] { BaseTime.AddMinutes(5), BaseTime.AddMinutes(10), BaseTime.AddMinutes(25) },
            timeline.Items.Select(item => item.OccurredAtUtc).ToArray());
        Assert.Equal(WorkflowRunTimelineItemKind.Transition, timeline.Items[1].Kind);
    }

    [Fact]
    public void BuildTimeline_ProjectsUnconfirmedFieldsAsNotReported()
    {
        var run = CreateRunWithTransition(sessionId: null);
        var session = CreateSession(role: null, nativeSessionId: null, workflowRunId: run.Id);
        var execution = CreateExecution(
            "execution-unknown",
            BaseTime.AddMinutes(1),
            BaseTime.AddMinutes(2),
            observedRouteId: null);

        var timeline = _service.BuildTimeline(run, new[] { execution }, new[] { session });

        var item = Assert.Single(timeline.Items, candidate => candidate.Kind == WorkflowRunTimelineItemKind.Execution);

        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, item.StageDisplay);
        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, item.RoleDisplay);
        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, item.ObservedRouteDisplay);
        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, item.NativeSessionDisplay);
        Assert.Equal(ExecutionState.Succeeded, item.ExecutionState);
    }

    [Fact]
    public void BuildTimeline_TransitionWithoutRoleOrSessionProjectsNotReported()
    {
        var run = CreateRunWithTransition();

        var timeline = _service.BuildTimeline(
            run,
            Array.Empty<Execution>(),
            Array.Empty<Session>());

        var item = Assert.Single(timeline.Items);

        Assert.Equal(WorkflowRunTimelineItemKind.Transition, item.Kind);
        Assert.Equal("stage-2", item.StageDisplay);
        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, item.RoleDisplay);
        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, item.NativeSessionDisplay);
        Assert.Equal("route-review", item.ObservedRouteDisplay);
        Assert.Equal("Review passed.", item.Description);
    }

    [Fact]
    public void BuildTimeline_TransitionCarriesReviewerVerdictsAndUserApproval()
    {
        var run = CreateRunWithTransition();

        var timeline = _service.BuildTimeline(
            run,
            Array.Empty<Execution>(),
            Array.Empty<Session>());

        var item = Assert.Single(timeline.Items);

        var verdict = Assert.Single(item.ReviewerVerdicts);
        Assert.Equal("Reviewer", verdict.ReviewerRole);
        Assert.Equal(WorkflowReviewVerdict.Approve, verdict.Verdict);
        Assert.NotNull(item.UserApproval);
        Assert.Equal(UserApprovalDecision.Approved, item.UserApproval!.Decision);
    }

    [Fact]
    public void BuildTimeline_FiltersExecutionsToSessionsOfTheRun()
    {
        var run = CreateRunWithTransition();

        var runSession = CreateSession(id: "session-1");
        var referencedSession = CreateSession(id: "session-referenced", workflowRunId: run.Id);
        var foreignSession = CreateSession(id: "session-foreign", workflowRunId: "other-run");

        var executions = new[]
        {
            CreateExecution("execution-run", BaseTime.AddMinutes(1), BaseTime.AddMinutes(2), sessionId: "session-1"),
            CreateExecution("execution-referenced", BaseTime.AddMinutes(2), BaseTime.AddMinutes(3), sessionId: "session-referenced"),
            CreateExecution("execution-foreign", BaseTime.AddMinutes(3), BaseTime.AddMinutes(4), sessionId: "session-foreign")
        };

        var timeline = _service.BuildTimeline(
            run,
            executions,
            new[] { runSession, referencedSession, foreignSession });

        Assert.Equal(
            new[] { "execution-run", "execution-referenced" },
            timeline.Items
                .Where(item => item.Kind == WorkflowRunTimelineItemKind.Execution)
                .Select(item => item.ExecutionId)
                .ToArray());
    }

    [Fact]
    public void BuildTimeline_ReportsTheRunCurrentStageRoleAndSession()
    {
        var run = CreateRunWithTransition();

        var timeline = _service.BuildTimeline(run, Array.Empty<Execution>(), Array.Empty<Session>());

        Assert.Equal(run.CurrentStageId, timeline.CurrentStageDisplay);
        Assert.Equal(run.CurrentRole, timeline.CurrentRoleDisplay);
        Assert.Equal("session-1", timeline.RunSessionDisplay);
        Assert.False(timeline.IsTerminal);
    }

    [Fact]
    public void BuildTimeline_WithoutARunSession_ReportsNotReported()
    {
        var run = CreateRunWithTransition(sessionId: null);

        var timeline = _service.BuildTimeline(run, Array.Empty<Execution>(), Array.Empty<Session>());

        Assert.Equal(ObservableRunProjection.NotReportedPlaceholder, timeline.RunSessionDisplay);
    }

    [Fact]
    public void BuildTimeline_WithProjections_UsesTheProvidedObservableProjectionEvidence()
    {
        var run = CreateRunWithTransition();
        var session = CreateSession();

        var projection = ObservableRunProjection.FromExecution(
            CreateExecution("execution-projected", BaseTime.AddMinutes(1), BaseTime.AddMinutes(2)),
            session,
            WorkflowRoleParser.Parse(session.Role),
            session.Role);

        var timeline = _service.BuildTimeline(run, new[] { projection });

        var item = Assert.Single(timeline.Items, candidate => candidate.Kind == WorkflowRunTimelineItemKind.Execution);

        Assert.Equal("execution-projected", item.ExecutionId);
        Assert.Equal("Executor", item.RoleDisplay);
        Assert.Equal("route-observed", item.ObservedRouteDisplay);
        Assert.Equal("native-session-1", item.NativeSessionDisplay);
        Assert.Equal(EvidenceSourceKind.NotReported, item.EvidenceSource);
    }

    [Fact]
    public void BuildTimeline_PreservesSyntheticEvidenceSource()
    {
        var run = CreateRunWithTransition();
        var session = CreateSession();
        var execution = CreateExecution("execution-synthetic", BaseTime.AddMinutes(1), BaseTime.AddMinutes(2));

        var timeline = _service.BuildTimeline(
            run,
            new[] { execution },
            new[] { session },
            EvidenceSourceKind.SyntheticFixture);

        var item = Assert.Single(timeline.Items, candidate => candidate.Kind == WorkflowRunTimelineItemKind.Execution);

        Assert.Equal(EvidenceSourceKind.SyntheticFixture, item.EvidenceSource);
        Assert.Single(timeline.Items, candidate => candidate.Kind == WorkflowRunTimelineItemKind.Transition);
    }

    [Fact]
    public void BuildTimeline_RequiresRunAndCollections()
    {
        var run = CreateRunWithTransition();

        Assert.Throws<ArgumentNullException>(
            () => _service.BuildTimeline(null!, Array.Empty<Execution>(), Array.Empty<Session>()));
        Assert.Throws<ArgumentNullException>(
            () => _service.BuildTimeline(run, null!, Array.Empty<Session>()));
        Assert.Throws<ArgumentNullException>(
            () => _service.BuildTimeline(run, Array.Empty<Execution>(), null!));
        Assert.Throws<ArgumentNullException>(
            () => _service.BuildTimeline(run, (IReadOnlyList<ObservableRunProjection>)null!));
    }

    private static WorkflowRun CreateRunWithTransition(string? sessionId = "session-1")
    {
        // The stage under review declares the artifact its gate is decided on, and the run recorded it before
        // the verdicts and the approval were pinned to its hash. The hash is the recorded one, so the timeline
        // below still sees a coherent chain of evidence.
        var initialStage = new WorkflowStageDefinition(
            "stage-1",
            "Stage 1",
            "Architect",
            WorkflowStageKind.Custom,
            new[] { "Reviewer" },
            requiresUserApproval: true,
            artifactRequirement: "DocumentBundle",
            nextStageId: "stage-2",
            failureStageId: null);

        var nextStage = new WorkflowStageDefinition(
            "stage-2",
            "Stage 2",
            "Implementer",
            WorkflowStageKind.Custom,
            Array.Empty<string>(),
            requiresUserApproval: false,
            artifactRequirement: null,
            nextStageId: null,
            failureStageId: null);

        var run = WorkflowRun.Start(
            "run-1",
            "project-1",
            "pkg-1",
            "ver-1",
            sessionId,
            initialStage,
            BaseTime);

        run = run.WithArtifact(new WorkflowArtifactEvidence(
            "artifact-1",
            "run-1",
            "stage-1",
            "DocumentBundle",
            DocumentHash,
            DocumentHash,
            BaseTime.AddMinutes(8),
            42,
            DataClassification.PrivateSource));

        run.RecordReviewerVerdict(new ReviewerVerdictRecord(
            "Reviewer",
            "route-review",
            DocumentHash,
            WorkflowReviewVerdict.Approve,
            "Approved by the reviewer.",
            BaseTime.AddMinutes(9),
            "execution-review",
            "stage-2",
            "artifact-stage-2"));

        run.RecordUserApproval(new UserApprovalEvidence(
            "approval-1",
            "user-1",
            "stage-1",
            DocumentHash,
            UserApprovalDecision.Approved,
            "Approved by the user.",
            BaseTime.AddMinutes(9).AddSeconds(30)));

        run.AdvanceTo(initialStage, nextStage, "Review passed.", BaseTime.AddMinutes(10));

        return run;
    }

    private static Session CreateSession(
        string id = "session-1",
        string? role = "Executor",
        string? nativeSessionId = "native-session-1",
        string? workflowRunId = "run-1")
    {
        return new Session(
            id,
            new SessionBinding(
                BackendType.OpenCode,
                "provider-1",
                "account-1",
                "model-1",
                "high",
                "fast",
                "interactive"),
            "project-1",
            @"C:\test\workspace",
            nativeSessionId,
            SessionState.Idle,
            ReconciliationOutcome.Reattached,
            CloseReason.None,
            continuationOfSessionId: null,
            forkedFromSessionId: null,
            workflowRunId,
            role,
            activeExecutionId: null,
            BaseTime,
            BaseTime.AddMinutes(30));
    }

    private static Execution CreateExecution(
        string id,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string sessionId = "session-1",
        string? observedRouteId = "route-observed")
    {
        return new Execution(
            id,
            sessionId,
            $"client-request-{id}",
            ExecutionState.Succeeded,
            ExecutionFailureReason.None,
            "route-requested",
            observedRouteId,
            retryOfExecutionId: null,
            processState: null,
            exitCode: null,
            terminationReason: null,
            Array.Empty<string>(),
            sourceHashBefore: null,
            sourceHashAfter: null,
            startedAt,
            startedAt,
            endedAt);
    }
}
