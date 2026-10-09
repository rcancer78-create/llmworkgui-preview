using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.StateMachines;
using Xunit;

namespace LLMWorkGUI.Application.Tests;

public sealed class SyntheticRunSequenceFixtureTests
{
    private readonly SyntheticRunSequenceFixture _fixture = new();

    [Fact]
    public void CanonicalSequence_FollowsCoordinatorExecutorReviewerFixAcceptanceOrder()
    {
        Assert.Equal(
            SyntheticRunSequenceFixture.CanonicalStageOrder,
            _fixture.Canonical.Steps.Select(step => step.StageLabel));

        Assert.Equal(
            new[]
            {
                WorkflowRole.Coordinator,
                WorkflowRole.Executor,
                WorkflowRole.Reviewer,
                WorkflowRole.Executor,
                WorkflowRole.Coordinator
            },
            _fixture.Canonical.Steps.Select(step => step.Role));
    }

    [Fact]
    public void CanonicalSequence_CompletesEachStepThroughExecutionStateMachine()
    {
        Assert.All(_fixture.Canonical.Steps, step =>
        {
            Assert.Equal(ExecutionState.Succeeded, step.Execution.State);
            Assert.True(ExecutionStateMachine.IsTerminal(step.Execution.State));
            Assert.NotNull(step.Execution.StartedAt);
            Assert.NotNull(step.Execution.EndedAt);
            Assert.Equal(ExecutionFailureReason.None, step.Execution.FailureReason);
            Assert.False(step.IsActive);
        });
    }

    [Fact]
    public void CanonicalSequence_UsesDeterministicIncreasingTimestamps()
    {
        var startedAt = _fixture.Canonical.Steps.Select(step => step.Execution.StartedAt!.Value).ToArray();

        Assert.Equal(startedAt.OrderBy(timestamp => timestamp), startedAt);
        Assert.Equal(startedAt.Distinct().Count(), startedAt.Length);

        var second = new SyntheticRunSequenceFixture();

        Assert.Equal(
            _fixture.Canonical.Steps.Select(step => step.Execution.StartedAt),
            second.Canonical.Steps.Select(step => step.Execution.StartedAt));
    }

    [Fact]
    public void AllGeneratedEvidence_IsExplicitlySynthetic()
    {
        Assert.True(_fixture.IsSynthetic);
        Assert.All(_fixture.AllScenarios, scenario => Assert.True(scenario.IsSynthetic));

        Assert.All(_fixture.AllSteps, step =>
        {
            Assert.True(step.IsSynthetic);
            Assert.True(step.Projection.IsSynthetic);
            Assert.Equal(EvidenceSourceKind.SyntheticFixture, step.Projection.EvidenceSource);
            Assert.NotEqual(EvidenceSourceKind.NativeProtocolEvent, step.Projection.EvidenceSource);
        });
    }

    [Fact]
    public void AllScenarios_AreNamedUniquelyAndContainRuns()
    {
        Assert.NotEmpty(_fixture.AllScenarios);
        Assert.Equal(
            _fixture.AllScenarios.Select(scenario => scenario.Name).Distinct().Count(),
            _fixture.AllScenarios.Count);
        Assert.All(_fixture.AllScenarios, scenario => Assert.NotEmpty(scenario.Steps));
        Assert.Equal(_fixture.AllSteps.Count, _fixture.AllExecutions.Count);
        Assert.Equal(_fixture.AllSteps.Count, _fixture.AllProjections.Count);
    }

    [Fact]
    public void RouteMismatchScenario_EndsInTerminalRouteMismatchWithDivergentRoutes()
    {
        var step = Assert.Single(_fixture.RouteMismatch.Steps);

        Assert.Equal(ExecutionState.RouteMismatch, step.Execution.State);
        Assert.True(ExecutionStateMachine.IsTerminal(step.Execution.State));
        Assert.NotNull(step.Execution.ObservedRouteId);
        Assert.NotEqual(step.Execution.RequestedRouteId, step.Execution.ObservedRouteId);
        Assert.False(step.IsActive);
    }

    [Fact]
    public void AmbiguousScenario_ReportsUnreportedEvidenceAsNotReported()
    {
        var step = Assert.Single(_fixture.Ambiguous.Steps);

        Assert.Equal(ExecutionState.Ambiguous, step.Execution.State);
        Assert.True(ExecutionStateMachine.IsTerminal(step.Execution.State));
        Assert.Null(step.Session.NativeSessionId);
        Assert.Null(step.Projection.NativeSessionId);
        Assert.Null(step.Projection.ObservedRouteId);
        Assert.Equal("Not reported", step.Projection.NativeSessionIdDisplay);
        Assert.Equal("Not reported", step.Projection.ObservedRouteIdDisplay);
        Assert.Equal(SessionState.Ambiguous, step.Session.State);
    }

    [Fact]
    public void WaitingApprovalPending_KeepsExecutionActive()
    {
        var step = Assert.Single(_fixture.WaitingApprovalPending.Steps);

        Assert.Equal(ExecutionState.WaitingApproval, step.Execution.State);
        Assert.True(step.IsActive);
        Assert.Null(step.Execution.EndedAt);
        Assert.Equal(step.Execution.Id, step.Session.ActiveExecutionId);
        Assert.Equal(SessionState.Active, step.Session.State);
    }

    [Fact]
    public void WaitingApprovalApproved_CompletesAfterApproval()
    {
        var step = Assert.Single(_fixture.WaitingApprovalApproved.Steps);

        Assert.Equal(ExecutionState.Succeeded, step.Execution.State);
        Assert.True(ExecutionStateMachine.IsTerminal(step.Execution.State));
        Assert.False(step.IsActive);
    }

    [Fact]
    public void WaitingApprovalDenied_EndsCancelledWithoutFailureReason()
    {
        var step = Assert.Single(_fixture.WaitingApprovalDenied.Steps);

        Assert.Equal(ExecutionState.Cancelled, step.Execution.State);
        Assert.True(ExecutionStateMachine.IsTerminal(step.Execution.State));
        Assert.Equal(ExecutionFailureReason.None, step.Execution.FailureReason);
        Assert.Equal("approval denied by the operator", step.Execution.TerminationReason);
        Assert.False(step.IsActive);
    }

    [Fact]
    public void CustomBaseTimestamp_ShiftsAllRunsWithoutChangingOrder()
    {
        var customBase = new DateTimeOffset(2030, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var fixture = new SyntheticRunSequenceFixture(customBase);

        Assert.Equal(customBase, fixture.BaseTimestampUtc);
        Assert.All(fixture.AllSteps, step => Assert.True(step.Execution.StartedAt!.Value >= customBase));

        var timeline = new ActivityTimelineService().BuildTimeline(fixture.Canonical.Projections);

        Assert.Equal(
            SyntheticRunSequenceFixture.CanonicalStageOrder,
            timeline.Items.Select(item => item.DisplayLabel));
        Assert.Equal(WorkflowRole.Unknown, timeline.ActiveRole);
    }

    [Fact]
    public void EveryRunBelongsToSyntheticProjectAndWorkflowRun()
    {
        Assert.All(_fixture.AllSessions, session =>
        {
            Assert.Equal(SyntheticRunSequenceFixture.SyntheticProjectId, session.ProjectId);
            Assert.Equal(SyntheticRunSequenceFixture.SyntheticWorkflowRunId, session.WorkflowRunId);
        });

        Assert.All(_fixture.AllExecutions, execution =>
            Assert.Contains(_fixture.AllSessions, session => session.Id == execution.SessionId));
    }

    [Fact]
    public void CanonicalTimeline_ContainsAllFiveStagesInOrder()
    {
        var timeline = new ActivityTimelineService().BuildTimeline(_fixture.Canonical.Projections);

        Assert.Equal(SyntheticRunSequenceFixture.CanonicalStageOrder, timeline.Items.Select(item => item.DisplayLabel));
        Assert.Equal(
            new[] { 0, 1, 2, 3, 4 },
            timeline.Items.Select(item => item.Sequence));
        Assert.True(timeline.HasSyntheticItems);
    }
}
