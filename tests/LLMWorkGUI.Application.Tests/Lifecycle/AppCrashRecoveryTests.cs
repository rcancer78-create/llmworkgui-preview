using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Tests.TestSupport;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Lifecycle;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Lifecycle;

public sealed class AppCrashRecoveryTests : IDisposable
{
    private readonly SqliteTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Recover_QuarantinesInterruptedExecutionsAndPreservesUnresolvedSessionIdentity()
    {
        await _host.InitializeAsync();
        await _host.SeedRouteChainAsync();
        await _host.SeedSessionAsync(state: "Active", activeExecutionId: "execution-1");
        await _host.SeedSessionAsync(sessionId: "session-closed", state: "Closed");
        await _host.SeedExecutionAsync("execution-1", state: "Running");
        await _host.SeedExecutionAsync(
            "execution-waiting",
            state: "WaitingApproval",
            clientRequestId: "client-request-2");

        var service = CreateService();

        var report = await service.RecoverAsync();

        Assert.Equal(
            new[] { "execution-1", "execution-waiting" },
            report.InterruptedExecutionIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Contains("session-1", report.InterruptedSessionIds);
        Assert.DoesNotContain("session-closed", report.InterruptedSessionIds);

        var filter = new SensitiveDataFilter();
        var executionRepository = new SqliteExecutionRepository(_host.Factory, filter);

        var execution = await executionRepository.GetByIdAsync("execution-1");

        Assert.NotNull(execution);
        Assert.Equal(ExecutionState.Ambiguous, execution!.State);
        Assert.Equal(ExecutionFailureReason.None, execution.FailureReason);
        Assert.Equal(AppCrashRecoveryService.ExecutionUncertaintyReason, execution.TerminationReason);
        Assert.Null(execution.EndedAt);

        var audit = await executionRepository.ListEventsAsync("execution-1");

        Assert.Contains(audit, entry => entry.EventKind == AppCrashRecoveryService.AuditEventKind);

        var sessionRepository = new SqliteSessionRepository(_host.Factory);
        var session = await sessionRepository.GetByIdAsync("session-1");

        Assert.NotNull(session);
        Assert.Equal(SessionState.Ambiguous, session!.State);
        Assert.Equal(ReconciliationOutcome.Ambiguous, session.ReconciliationOutcome);
        Assert.Equal("execution-1", session.ActiveExecutionId);

        var closed = await sessionRepository.GetByIdAsync("session-closed");

        Assert.Equal(SessionState.Closed, closed!.State);
    }

    [Fact]
    public async Task Recover_FailsActiveWorkflowRunsAndLeavesTerminalRunsUntouched()
    {
        await _host.InitializeAsync();
        await _host.SeedRouteChainAsync();
        await _host.SeedWorkflowAsync(runId: "run-active", state: "Running");
        await _host.SeedWorkflowAsync(
            packageId: "package-2",
            versionId: "version-2",
            runId: "run-completed",
            state: "Completed");

        var service = CreateService();

        var report = await service.RecoverAsync();

        Assert.Equal(new[] { "run-active" }, report.InterruptedWorkflowRunIds);

        var runRepository = new SqliteWorkflowRunRepository(_host.Factory);

        var active = await runRepository.GetByIdAsync("run-active");

        Assert.NotNull(active);
        Assert.Equal(WorkflowRunState.Failed, active!.State);
        Assert.Equal(WorkflowTerminalOutcome.Failed, active.TerminalOutcome);
        Assert.NotNull(active.EndedAtUtc);
        Assert.Equal(AppCrashRecoveryService.WorkflowRunFailureReason, active.TerminalReason);

        var completed = await runRepository.GetByIdAsync("run-completed");

        Assert.Equal(WorkflowRunState.Completed, completed!.State);
    }

    [Fact]
    public async Task Recover_ReleasesStaleCheckoutLocksWithConfirmedTerminalEvidence()
    {
        await _host.InitializeAsync();
        await _host.SeedRouteChainAsync();
        await _host.SeedSessionAsync(state: "Active", activeExecutionId: "execution-1");
        await _host.SeedExecutionAsync("execution-1", state: "Succeeded", endedAt: DateTimeOffset.UtcNow);
        await _host.InsertProjectLockAsync("lock-1", _host.GetWorkspacePath(), "execution-1");

        var service = CreateService();

        var report = await service.RecoverAsync();

        Assert.Equal(new[] { "lock-1" }, report.ReleasedLockIds);
        Assert.Empty(report.RetainedLockIds);

        var lockRepository = new SqliteProjectLockRepository(_host.Factory);
        var held = await lockRepository.GetActiveByRootPathAsync(_host.GetWorkspacePath());

        Assert.Null(held);
    }

    [Fact]
    public async Task Recover_RetainsLocksWhoseExecutionIsStillAmbiguous()
    {
        await _host.InitializeAsync();
        await _host.SeedRouteChainAsync();
        await _host.SeedSessionAsync(state: "Active", activeExecutionId: "execution-ambiguous");
        await _host.SeedExecutionAsync("execution-ambiguous", state: "Ambiguous");
        await _host.InsertProjectLockAsync(
            "lock-ambiguous",
            _host.GetWorkspacePath(),
            "execution-ambiguous");

        var service = CreateService();

        var report = await service.RecoverAsync();

        Assert.Empty(report.ReleasedLockIds);
        Assert.Equal(new[] { "lock-ambiguous" }, report.RetainedLockIds);
        Assert.NotEmpty(report.Warnings);

        var lockRepository = new SqliteProjectLockRepository(_host.Factory);
        var held = await lockRepository.GetActiveByRootPathAsync(_host.GetWorkspacePath());

        Assert.NotNull(held);
    }

    [Fact]
    public async Task Recover_SkipsEverythingWhenTheInstanceIsViewOnly()
    {
        await _host.InitializeAsync();
        await _host.SeedRouteChainAsync();
        await _host.SeedSessionAsync(state: "Active", activeExecutionId: "execution-1");
        await _host.SeedExecutionAsync("execution-1", state: "Running");

        var service = CreateService(new FakeInstanceGuard { IsViewOnly = true });

        var report = await service.RecoverAsync();

        Assert.True(report.SkippedAsViewOnly);
        Assert.Empty(report.InterruptedExecutionIds);
        Assert.NotEmpty(report.Warnings);

        var executionRepository = new SqliteExecutionRepository(_host.Factory, new SensitiveDataFilter());
        var execution = await executionRepository.GetByIdAsync("execution-1");

        Assert.Equal(ExecutionState.Running, execution!.State);
    }

    [Fact]
    public async Task Recover_RehydratesDistinctHealthScopesFromTheAppendOnlyAudit()
    {
        await _host.InitializeAsync();
        await _host.InsertHealthEventAsync("health-event-1", "account", "account-1");
        await _host.InsertHealthEventAsync("health-event-2", "route", "account-1:model-1");
        await _host.InsertHealthEventAsync("health-event-3", "account", "account-1");

        var service = CreateService();

        var report = await service.RecoverAsync();

        Assert.Equal(2, report.RehydratedHealthScopeCount);
    }

    private AppCrashRecoveryService CreateService(IApplicationInstanceGuard? instanceGuard = null)
    {
        return new AppCrashRecoveryService(
            _host.Factory,
            new SqliteExecutionRepository(_host.Factory, new SensitiveDataFilter()),
            new SqliteSessionRepository(_host.Factory),
            new SqliteProjectRepository(_host.Factory),
            new SqliteWorkflowRunRepository(_host.Factory),
            new SqliteProjectLockRepository(_host.Factory),
            instanceGuard,
            new SqliteHealthEventRepository(_host.Factory),
            new HealthCenterService(
                new SqliteHealthStateRepository(_host.Factory),
                new SqliteHealthEventRepository(_host.Factory)));
    }

    private sealed class FakeInstanceGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "test-instance";

        public bool IsPrimarySupervisor => !IsViewOnly;

        public bool IsViewOnly { get; init; }

        public void EnsureSupervisorPermitted()
        {
            if (IsViewOnly)
            {
                throw new SecondaryInstanceReadOnlyException("The test instance is view-only.");
            }
        }

        public void Dispose()
        {
        }
    }
}
