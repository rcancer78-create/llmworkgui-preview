using System;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using Xunit;

namespace LLMWorkGUI.Application.Tests;

public sealed class ObservableRunProjectionTests
{
    [Fact]
    public void NotReportedPlaceholder_MatchesUiContract()
    {
        Assert.Equal("Not reported", ObservableRunProjection.NotReportedPlaceholder);
    }

    [Fact]
    public void FromExecution_MapsExecutionAndSessionEvidence()
    {
        var startedAt = RunTestData.BaseTime.AddMinutes(1);
        var endedAt = startedAt.AddMinutes(2);
        var session = RunTestData.CreateSession();
        var execution = RunTestData.CreateExecution(
            state: ExecutionState.Succeeded,
            startedAt: startedAt,
            endedAt: endedAt);

        var projection = ObservableRunProjection.FromExecution(
            execution,
            session,
            WorkflowRole.Reviewer,
            "Reviewer");

        Assert.Equal("execution-1", projection.ExecutionId);
        Assert.Equal("session-1", projection.SessionId);
        Assert.Equal(WorkflowRole.Reviewer, projection.Role);
        Assert.Equal("Reviewer", projection.DisplayLabel);
        Assert.Equal(ExecutionState.Succeeded, projection.State);
        Assert.Equal("route-requested", projection.RequestedRouteId);
        Assert.Equal("route-observed", projection.ObservedRouteId);
        Assert.Equal("native-session-1", projection.NativeSessionId);
        Assert.Equal(startedAt, projection.StartedAtUtc);
        Assert.Equal(endedAt, projection.EndedAtUtc);
        Assert.Equal(endedAt, projection.LastActivityAtUtc);
        Assert.Equal(EvidenceSourceKind.NotReported, projection.EvidenceSource);
        Assert.False(projection.IsSynthetic);
        Assert.False(projection.IsActive);
    }

    [Fact]
    public void FromExecution_FormatsUnconfirmedBackendEvidenceAsNotReported()
    {
        var session = RunTestData.CreateSession(nativeSessionId: null);
        var execution = RunTestData.CreateExecution(
            state: ExecutionState.Queued,
            observedRouteId: null,
            startedAt: null,
            endedAt: null);

        var projection = ObservableRunProjection.FromExecution(execution, session, WorkflowRole.Unknown);

        Assert.Null(projection.ObservedRouteId);
        Assert.Null(projection.NativeSessionId);
        Assert.Equal("Not reported", projection.ObservedRouteIdDisplay);
        Assert.Equal("Not reported", projection.NativeSessionIdDisplay);
        Assert.Equal("Unknown", projection.DisplayLabel);
        Assert.Equal(RunTestData.BaseTime, projection.LastActivityAtUtc);
        Assert.True(projection.IsActive);
    }

    [Fact]
    public void FromExecution_UsesSessionLastEventForActiveExecution()
    {
        var startedAt = RunTestData.BaseTime.AddMinutes(1);
        var lastEventAt = RunTestData.BaseTime.AddMinutes(9);
        var session = RunTestData.CreateSession(activeExecutionId: "execution-1", lastEventAt: lastEventAt);
        var execution = RunTestData.CreateExecution(
            state: ExecutionState.Running,
            startedAt: startedAt,
            endedAt: null);

        var projection = ObservableRunProjection.FromExecution(execution, session, WorkflowRole.Executor);

        Assert.Equal(lastEventAt, projection.LastActivityAtUtc);
        Assert.True(projection.IsActive);
    }

    [Fact]
    public void FromExecution_PrefersEndedAtForTerminalExecution()
    {
        var startedAt = RunTestData.BaseTime.AddMinutes(1);
        var endedAt = RunTestData.BaseTime.AddMinutes(2);
        var session = RunTestData.CreateSession(lastEventAt: RunTestData.BaseTime.AddMinutes(30));
        var execution = RunTestData.CreateExecution(startedAt: startedAt, endedAt: endedAt);

        var projection = ObservableRunProjection.FromExecution(execution, session, WorkflowRole.Executor);

        Assert.Equal(endedAt, projection.LastActivityAtUtc);
    }

    [Fact]
    public void FromExecution_FallsBackToRoleNameForDisplayLabel()
    {
        var session = RunTestData.CreateSession();
        var execution = RunTestData.CreateExecution();

        var projection = ObservableRunProjection.FromExecution(execution, session, WorkflowRole.Escalation);

        Assert.Equal("Escalation", projection.DisplayLabel);
    }

    [Fact]
    public void FromExecution_MarksSyntheticFixtureEvidence()
    {
        var session = RunTestData.CreateSession();
        var execution = RunTestData.CreateExecution();

        var projection = ObservableRunProjection.FromExecution(
            execution,
            session,
            WorkflowRole.Coordinator,
            "Coordinator",
            EvidenceSourceKind.SyntheticFixture);

        Assert.True(projection.IsSynthetic);
        Assert.Equal(EvidenceSourceKind.SyntheticFixture, projection.EvidenceSource);
    }

    [Fact]
    public void FromExecution_RejectsExecutionFromAnotherSession()
    {
        var session = RunTestData.CreateSession(id: "session-1");
        var execution = RunTestData.CreateExecution(sessionId: "session-2");

        Assert.Throws<ArgumentException>(() =>
            ObservableRunProjection.FromExecution(execution, session, WorkflowRole.Executor));
    }

    [Fact]
    public void Constructor_AcceptsSyntheticFixtureEvidence()
    {
        var projection = CreateProjection(
            evidenceSource: EvidenceSourceKind.SyntheticFixture,
            isSynthetic: true);

        Assert.True(projection.IsSynthetic);
        Assert.Equal(EvidenceSourceKind.SyntheticFixture, projection.EvidenceSource);
    }

    [Theory]
    [InlineData(EvidenceSourceKind.NativeProtocolEvent)]
    [InlineData(EvidenceSourceKind.NotReported)]
    public void Constructor_RejectsSyntheticRunClaimingNonSyntheticEvidence(EvidenceSourceKind evidenceSource)
    {
        Assert.Throws<ArgumentException>(() =>
            CreateProjection(evidenceSource: evidenceSource, isSynthetic: true));
    }

    [Fact]
    public void Constructor_RejectsSyntheticFixtureEvidenceWithoutSyntheticFlag()
    {
        Assert.Throws<ArgumentException>(() =>
            CreateProjection(evidenceSource: EvidenceSourceKind.SyntheticFixture, isSynthetic: false));
    }

    [Fact]
    public void Constructor_RejectsEndBeforeStart()
    {
        var startedAt = RunTestData.BaseTime.AddMinutes(5);
        var endedAt = RunTestData.BaseTime.AddMinutes(1);

        Assert.Throws<ArgumentException>(() => CreateProjection(
            startedAtUtc: startedAt,
            lastActivityAtUtc: endedAt,
            endedAtUtc: endedAt));
    }

    [Fact]
    public void Constructor_RejectsLastActivityBeforeStart()
    {
        var startedAt = RunTestData.BaseTime.AddMinutes(5);

        Assert.Throws<ArgumentException>(() => CreateProjection(
            startedAtUtc: startedAt,
            lastActivityAtUtc: RunTestData.BaseTime));
    }

    [Fact]
    public void Constructor_RejectsBlankIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => CreateProjection(executionId: " "));
        Assert.Throws<ArgumentException>(() => CreateProjection(sessionId: ""));
        Assert.Throws<ArgumentException>(() => CreateProjection(displayLabel: " "));
        Assert.Throws<ArgumentException>(() => CreateProjection(requestedRouteId: " "));
        Assert.Throws<ArgumentException>(() => CreateProjection(observedRouteId: " "));
        Assert.Throws<ArgumentException>(() => CreateProjection(nativeSessionId: " "));
    }

    [Theory]
    [InlineData(ExecutionState.Queued, true)]
    [InlineData(ExecutionState.Starting, true)]
    [InlineData(ExecutionState.SessionConfirmed, true)]
    [InlineData(ExecutionState.Running, true)]
    [InlineData(ExecutionState.WaitingApproval, true)]
    [InlineData(ExecutionState.Cancelling, true)]
    [InlineData(ExecutionState.Succeeded, false)]
    [InlineData(ExecutionState.Failed, false)]
    [InlineData(ExecutionState.TimedOut, false)]
    [InlineData(ExecutionState.Cancelled, false)]
    [InlineData(ExecutionState.Ambiguous, false)]
    [InlineData(ExecutionState.RouteMismatch, false)]
    public void IsActive_TracksExecutionStateMachine(ExecutionState state, bool expected)
    {
        var projection = CreateProjection(state: state);

        Assert.Equal(expected, projection.IsActive);
    }

    [Fact]
    public void FromExecution_ProjectsModelAndAccountFromTheObservedSessionBinding()
    {
        var session = RunTestData.CreateSession(
            binding: RunTestData.CreateBinding(accountId: "account-observed", modelId: "model-observed"));
        var execution = RunTestData.CreateExecution(state: ExecutionState.Running);

        var projection = ObservableRunProjection.FromExecution(
            execution,
            session,
            WorkflowRole.Reviewer,
            "Reviewer");

        Assert.Equal("model-observed", projection.ObservedModelId);
        Assert.Equal("account-observed", projection.ObservedAccountId);
        Assert.True(projection.HasObservedModel);
        Assert.True(projection.HasObservedAccount);
        Assert.Equal("model-observed", projection.ObservedModelIdDisplay);
        Assert.Equal("account-observed", projection.ObservedAccountIdDisplay);
    }

    [Theory]
    [InlineData("plan", true)]
    [InlineData("ask", true)]
    [InlineData("diff", true)]
    [InlineData("review", true)]
    [InlineData("apply", false)]
    [InlineData("interactive", false)]
    [InlineData(null, false)]
    public void FromExecution_ProvesAReadOnlyTurnOnlyThroughTheWriterLockPolicy(
        string? executionMode,
        bool expected)
    {
        var session = RunTestData.CreateSession(
            binding: RunTestData.CreateBinding(executionMode: executionMode));
        var execution = RunTestData.CreateExecution(state: ExecutionState.Running);

        var projection = ObservableRunProjection.FromExecution(
            execution,
            session,
            WorkflowRole.Reviewer,
            "Reviewer",
            EvidenceSourceKind.NativeProtocolEvent,
            lastActivityAtUtc: null,
            CreateWriterLockPolicy());

        Assert.Equal(expected, projection.IsReadOnlyTurn);
        Assert.Equal(executionMode, projection.ObservedExecutionMode);
        Assert.Equal(executionMode ?? "Not reported", projection.ObservedExecutionModeDisplay);
    }

    [Fact]
    public void FromExecution_WithoutTheWriterLockPolicyNeverClaimsAReadOnlyTurn()
    {
        // The read-only proof needs the policy. Without it the turn stays unproven, even when the observed
        // execution mode is a read-only one, so no writer lock decision is ever invented.
        var session = RunTestData.CreateSession(
            binding: RunTestData.CreateBinding(executionMode: "review"));
        var execution = RunTestData.CreateExecution(state: ExecutionState.Running);

        var projection = ObservableRunProjection.FromExecution(
            execution,
            session,
            WorkflowRole.Reviewer,
            "Reviewer");

        Assert.False(projection.IsReadOnlyTurn);
        Assert.Equal("review", projection.ObservedExecutionMode);
    }

    private static ObservableRunProjection CreateProjection(
        string executionId = "execution-1",
        string sessionId = "session-1",
        WorkflowRole role = WorkflowRole.Executor,
        string displayLabel = "Executor",
        ExecutionState state = ExecutionState.Running,
        string requestedRouteId = "route-requested",
        string? observedRouteId = "route-observed",
        string? nativeSessionId = "native-session-1",
        DateTimeOffset? startedAtUtc = null,
        DateTimeOffset? lastActivityAtUtc = null,
        DateTimeOffset? endedAtUtc = null,
        EvidenceSourceKind evidenceSource = EvidenceSourceKind.NotReported,
        bool isSynthetic = false)
    {
        return new ObservableRunProjection(
            executionId,
            sessionId,
            role,
            displayLabel,
            state,
            requestedRouteId,
            observedRouteId,
            nativeSessionId,
            startedAtUtc,
            lastActivityAtUtc ?? RunTestData.BaseTime,
            endedAtUtc,
            evidenceSource,
            isSynthetic);
    }

    /// <summary>
    /// The production writer-lock policy, so the read-only proof is exercised against the shipped rule
    /// (<c>plan</c>/<c>ask</c>/<c>diff</c>/<c>review</c> are read-only, a null mode is a writer turn)
    /// instead of a test-local approximation of it.
    /// </summary>
    private static ICheckoutLockService CreateWriterLockPolicy() =>
        new CheckoutLockService(
            new UnusedProjectLockRepository(),
            new PermissiveInstanceGuard(),
            TimeProvider.System);

    private sealed class PermissiveInstanceGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "projection-test-instance";

        public bool IsPrimarySupervisor => true;

        public bool IsViewOnly => false;

        public void EnsureSupervisorPermitted()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class UnusedProjectLockRepository : IProjectLockRepository
    {
        public Task<ProjectLock?> GetActiveByRootPathAsync(
            string rootPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The read-only policy never reads a project lock.");

        public Task<bool> TryAcquireAsync(ProjectLock projectLock, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The read-only policy never acquires a writer lock.");

        public Task<bool> ReleaseAsync(
            string lockId,
            DateTimeOffset releasedAt,
            string reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The read-only policy never releases a writer lock.");
    }
}
