using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;
using Xunit;

namespace LLMWorkGUI.Application.Tests;

public sealed class ActivityTimelineServiceTests
{
    private readonly ActivityTimelineService _service = new();
    private readonly SyntheticRunSequenceFixture _fixture = new();

    [Fact]
    public void BuildTimeline_OrdersChronologicallyAndAssignsSequenceNumbers()
    {
        var later = CreateProjection("execution-a", RunTestData.BaseTime.AddMinutes(2));
        var earlier = CreateProjection("execution-b", RunTestData.BaseTime.AddMinutes(1));
        var sameTime = CreateProjection("execution-c", RunTestData.BaseTime.AddMinutes(2));

        var timeline = _service.BuildTimeline(new[] { later, earlier, sameTime });

        Assert.Equal(
            new[] { "execution-b", "execution-a", "execution-c" },
            timeline.Items.Select(item => item.ExecutionId));
        Assert.Equal(new[] { 0, 1, 2 }, timeline.Items.Select(item => item.Sequence));

        var timestamps = timeline.Items.Select(item => item.LastActivityAtUtc).ToArray();
        Assert.Equal(timestamps.OrderBy(timestamp => timestamp), timestamps);
    }

    [Fact]
    public void BuildTimeline_OfCompletedCanonicalSequence_HasNoActiveRole()
    {
        var timeline = _service.BuildTimeline(_fixture.Canonical.Projections);

        Assert.Equal(5, timeline.Items.Count);
        Assert.Null(timeline.CurrentItem);
        Assert.Equal(WorkflowRole.Unknown, timeline.ActiveRole);
        Assert.DoesNotContain(timeline.Items, item => item.IsActive);
    }

    [Fact]
    public void BuildTimeline_ResolvesActiveRoleFromLatestNonTerminalRun()
    {
        var projections = _fixture.Canonical.Projections
            .Concat(_fixture.WaitingApprovalPending.Projections)
            .ToArray();

        var timeline = _service.BuildTimeline(projections);

        Assert.NotNull(timeline.CurrentItem);
        Assert.Equal(WorkflowRole.Executor, timeline.ActiveRole);
        Assert.Equal(ExecutionState.WaitingApproval, timeline.CurrentItem!.State);
        Assert.Equal(
            _fixture.WaitingApprovalPending.Steps[0].Execution.Id,
            timeline.CurrentItem.ExecutionId);
        Assert.True(timeline.CurrentItem.IsActive);
    }

    [Fact]
    public void BuildTimeline_EmptyInput_ReturnsEmptyTimeline()
    {
        var timeline = _service.BuildTimeline(Array.Empty<ObservableRunProjection>());

        Assert.True(timeline.IsEmpty);
        Assert.Empty(timeline.Items);
        Assert.Null(timeline.CurrentItem);
        Assert.Equal(WorkflowRole.Unknown, timeline.ActiveRole);
        Assert.False(timeline.HasSyntheticItems);
    }

    [Fact]
    public void BuildTimeline_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _service.BuildTimeline(null!));
    }

    [Fact]
    public void BuildTimeline_ProjectsUnreportedEvidenceAsNotReported()
    {
        var timeline = _service.BuildTimeline(_fixture.Ambiguous.Projections);

        var item = Assert.Single(timeline.Items);

        Assert.Null(item.Run.ObservedRouteId);
        Assert.Null(item.Run.NativeSessionId);
        Assert.Equal("Not reported", item.ObservedRouteIdDisplay);
        Assert.Equal("Not reported", item.NativeSessionIdDisplay);
    }

    [Fact]
    public void BuildTimeline_PreservesSyntheticMarkerAndEvidenceSource()
    {
        var timeline = _service.BuildTimeline(_fixture.AllProjections);

        Assert.True(timeline.HasSyntheticItems);
        Assert.All(timeline.Items, item =>
        {
            Assert.True(item.IsSynthetic);
            Assert.Equal(EvidenceSourceKind.SyntheticFixture, item.EvidenceSource);
        });
    }

    [Fact]
    public void BuildTimeline_MixedEvidence_FlagsOnlySyntheticItems()
    {
        var native = CreateProjection("execution-native", RunTestData.BaseTime);
        var synthetic = _fixture.Canonical.Projections[0];

        var timeline = _service.BuildTimeline(new[] { native, synthetic });

        Assert.True(timeline.HasSyntheticItems);
        Assert.Single(timeline.Items, item => item.IsSynthetic);
        Assert.Single(timeline.Items, item => !item.IsSynthetic);
    }

    [Fact]
    public void BuildProjectTimeline_JoinsProjectSessionsAndOrdersChronologically()
    {
        var timeline = _service.BuildProjectTimeline(
            SyntheticRunSequenceFixture.SyntheticProjectId,
            _fixture.AllExecutions,
            _fixture.AllSessions,
            EvidenceSourceKind.SyntheticFixture);

        Assert.Equal(_fixture.AllExecutions.Count, timeline.Items.Count);
        Assert.All(timeline.Items, item => Assert.True(item.IsSynthetic));

        var timestamps = timeline.Items.Select(item => item.LastActivityAtUtc).ToArray();
        Assert.Equal(timestamps.OrderBy(timestamp => timestamp), timestamps);
        Assert.Equal(
            Enumerable.Range(0, timeline.Items.Count),
            timeline.Items.Select(item => item.Sequence));
    }

    [Fact]
    public void BuildProjectTimeline_ExcludesExecutionsFromOtherProjects()
    {
        var foreignSession = RunTestData.CreateSession(
            id: "foreign-session",
            projectId: "other-project",
            role: "Reviewer",
            activeExecutionId: "foreign-execution");

        var foreignExecution = RunTestData.CreateExecution(
            id: "foreign-execution",
            sessionId: "foreign-session",
            state: ExecutionState.Running,
            startedAt: RunTestData.BaseTime,
            endedAt: null);

        var executions = _fixture.AllExecutions.Concat(new[] { foreignExecution }).ToArray();
        var sessions = _fixture.AllSessions.Concat(new[] { foreignSession }).ToArray();

        var timeline = _service.BuildProjectTimeline(
            SyntheticRunSequenceFixture.SyntheticProjectId,
            executions,
            sessions,
            EvidenceSourceKind.SyntheticFixture);

        Assert.Equal(_fixture.AllExecutions.Count, timeline.Items.Count);
        Assert.DoesNotContain(timeline.Items, item => item.ExecutionId == "foreign-execution");
    }

    [Fact]
    public void BuildProjectTimeline_ParsesSessionRoleIntoWorkflowRole()
    {
        var coordinatorStep = _fixture.Canonical.Steps[0];

        var timeline = _service.BuildProjectTimeline(
            SyntheticRunSequenceFixture.SyntheticProjectId,
            new[] { coordinatorStep.Execution },
            _fixture.AllSessions,
            EvidenceSourceKind.SyntheticFixture);

        var item = Assert.Single(timeline.Items);

        Assert.Equal(WorkflowRole.Coordinator, item.Role);
        Assert.Equal("Coordinator", item.DisplayLabel);
    }

    [Fact]
    public void BuildProjectTimeline_RequiresProjectId()
    {
        Assert.Throws<ArgumentException>(() => _service.BuildProjectTimeline(
            " ",
            _fixture.AllExecutions,
            _fixture.AllSessions));

        Assert.Throws<ArgumentNullException>(() => _service.BuildProjectTimeline(
            SyntheticRunSequenceFixture.SyntheticProjectId,
            null!,
            _fixture.AllSessions));
    }

    [Fact]
    public void BuildSessionTimeline_FiltersToRequestedSession()
    {
        var reviewerStep = _fixture.Canonical.Steps[2];

        var timeline = _service.BuildSessionTimeline(
            reviewerStep.Session.Id,
            _fixture.AllExecutions,
            _fixture.AllSessions,
            EvidenceSourceKind.SyntheticFixture);

        var item = Assert.Single(timeline.Items);

        Assert.Equal(WorkflowRole.Reviewer, item.Role);
        Assert.Equal("Reviewer", item.DisplayLabel);
        Assert.Equal(reviewerStep.Execution.Id, item.ExecutionId);
    }

    [Fact]
    public void BuildSessionTimeline_WithPendingApproval_ResolvesActiveRole()
    {
        var pendingStep = _fixture.WaitingApprovalPending.Steps[0];

        var timeline = _service.BuildSessionTimeline(
            pendingStep.Session.Id,
            _fixture.AllExecutions,
            _fixture.AllSessions,
            EvidenceSourceKind.SyntheticFixture);

        Assert.Equal(WorkflowRole.Executor, timeline.ActiveRole);
        Assert.Equal(ExecutionState.WaitingApproval, timeline.CurrentItem!.State);
    }

    [Fact]
    public void BuildSessionTimeline_MissingSession_ReturnsEmptyTimeline()
    {
        var timeline = _service.BuildSessionTimeline(
            "missing-session",
            _fixture.AllExecutions,
            _fixture.AllSessions);

        Assert.True(timeline.IsEmpty);
        Assert.Equal(WorkflowRole.Unknown, timeline.ActiveRole);
    }

    [Fact]
    public void BuildSessionTimeline_UnknownRoleString_ProjectsUnknownWithRawLabel()
    {
        var session = RunTestData.CreateSession(id: "session-mystery", role: "Mystery", activeExecutionId: "execution-mystery");

        var execution = RunTestData.CreateExecution(
            id: "execution-mystery",
            sessionId: "session-mystery",
            state: ExecutionState.Running,
            startedAt: RunTestData.BaseTime.AddMinutes(1),
            endedAt: null);

        var timeline = _service.BuildSessionTimeline(
            "session-mystery",
            new[] { execution },
            new[] { session });

        var item = Assert.Single(timeline.Items);

        Assert.Equal(WorkflowRole.Unknown, item.Role);
        Assert.Equal("Mystery", item.DisplayLabel);
        Assert.Equal(EvidenceSourceKind.NotReported, item.EvidenceSource);
        Assert.False(item.IsSynthetic);
        Assert.Equal(WorkflowRole.Unknown, timeline.ActiveRole);
    }

    [Fact]
    public void BuildSessionTimeline_RequiresSessionId()
    {
        Assert.Throws<ArgumentException>(() => _service.BuildSessionTimeline(
            " ",
            _fixture.AllExecutions,
            _fixture.AllSessions));

        Assert.Throws<ArgumentNullException>(() => _service.BuildSessionTimeline(
            "session-1",
            _fixture.AllExecutions,
            null!));
    }

    [Theory]
    [InlineData("Coordinator", WorkflowRole.Coordinator)]
    [InlineData("executor", WorkflowRole.Executor)]
    [InlineData("  Reviewer ", WorkflowRole.Reviewer)]
    [InlineData("ESCALATION", WorkflowRole.Escalation)]
    [InlineData("Unknown", WorkflowRole.Unknown)]
    public void WorkflowRoleParser_ParsesKnownRolesCaseInsensitively(string value, WorkflowRole expected)
    {
        Assert.Equal(expected, WorkflowRoleParser.Parse(value));
        Assert.True(WorkflowRoleParser.TryParse(value, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Mystery")]
    [InlineData("2")]
    public void WorkflowRoleParser_UnknownRolesFallBackToUnknown(string? value)
    {
        Assert.Equal(WorkflowRole.Unknown, WorkflowRoleParser.Parse(value));
        Assert.False(WorkflowRoleParser.TryParse(value, out var parsed));
        Assert.Equal(WorkflowRole.Unknown, parsed);
    }

    private static ObservableRunProjection CreateProjection(
        string executionId,
        DateTimeOffset lastActivityAtUtc,
        ExecutionState state = ExecutionState.Succeeded,
        WorkflowRole role = WorkflowRole.Executor,
        string? observedRouteId = "route-observed",
        string? nativeSessionId = "native-session-1",
        bool isSynthetic = false)
    {
        return new ObservableRunProjection(
            executionId,
            "session-1",
            role,
            role.ToString(),
            state,
            "route-requested",
            observedRouteId,
            nativeSessionId,
            startedAtUtc: null,
            lastActivityAtUtc,
            endedAtUtc: null,
            isSynthetic ? EvidenceSourceKind.SyntheticFixture : EvidenceSourceKind.NotReported,
            isSynthetic);
    }
}
