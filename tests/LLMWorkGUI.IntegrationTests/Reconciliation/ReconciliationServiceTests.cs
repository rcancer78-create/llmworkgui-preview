using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Reconciliation;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;
using ReconciliationOutcome = LLMWorkGUI.Application.Reconciliation.ReconciliationOutcome;

namespace LLMWorkGUI.IntegrationTests.Reconciliation;

public sealed class ReconciliationServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteExecutionRepository _executions;
    private readonly SqliteProjectLockRepository _locks;

    public ReconciliationServiceTests()
    {
        _sessions = new SqliteSessionRepository(_database.Factory);
        _executions = new SqliteExecutionRepository(_database.Factory, new SensitiveDataFilter());
        _locks = new SqliteProjectLockRepository(_database.Factory);
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenProcessAliveAndEvidenceMatches_ReattachesSession()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateAliveResult(CreateBinding())
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Running");
        await _database.SeedExecutionAsync("execution-2", state: "Succeeded", endedAt: Now);

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Reattached, evidence.Outcome);
        Assert.True(evidence.ProcessAlive);
        Assert.True(evidence.BindingMatched);
        Assert.Equal("execution-1", evidence.ExecutionId);
        Assert.Equal("native-1", evidence.NativeSessionId);
        Assert.Equal(Now, evidence.ReconciledAtUtc);
        Assert.Equal(evidence.Details, evidence.EvidenceDetails);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.NotNull(session);
        Assert.Equal(SessionState.Active, session!.State);
        Assert.Equal(LLMWorkGUI.Domain.Enums.ReconciliationOutcome.Reattached, session.ReconciliationOutcome);
        Assert.Equal("execution-1", session.ActiveExecutionId);
        Assert.Equal(CreateBinding(), session.Binding);
        Assert.Equal(Now, session.LastEventAt);

        var execution = await _executions.GetByIdAsync("execution-1");

        Assert.NotNull(execution);
        Assert.Equal(ExecutionState.Running, execution!.State);
        Assert.Single(await _executions.ListEventsAsync("execution-1"));
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenConsumedExecutionIsTerminal_ReattachesSessionAsIdle()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateAliveResult(CreateBinding())
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Succeeded", endedAt: Now);

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Reattached, evidence.Outcome);
        Assert.True(evidence.ProcessAlive);
        Assert.True(evidence.BindingMatched);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.NotNull(session);
        Assert.Equal(SessionState.Idle, session!.State);
        Assert.Equal(LLMWorkGUI.Domain.Enums.ReconciliationOutcome.Reattached, session.ReconciliationOutcome);
        Assert.Null(session.ActiveExecutionId);

        var execution = await _executions.GetByIdAsync("execution-1");

        Assert.Equal(ExecutionState.Succeeded, execution!.State);
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenConsumedExecutionIsTerminalAndProcessIsGone_OrphansSession()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateGoneResult()
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Succeeded", endedAt: Now);

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.NotEqual(ReconciliationOutcome.Reattached, evidence.Outcome);
        Assert.Equal(ReconciliationOutcome.Orphaned, evidence.Outcome);
        Assert.False(evidence.ProcessAlive);
        Assert.False(evidence.BindingMatched);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.NotNull(session);
        Assert.Equal(SessionState.Orphaned, session!.State);
        Assert.Equal(LLMWorkGUI.Domain.Enums.ReconciliationOutcome.Orphaned, session.ReconciliationOutcome);
        Assert.Null(session.ActiveExecutionId);

        var execution = await _executions.GetByIdAsync("execution-1");

        Assert.NotNull(execution);
        Assert.Equal(ExecutionState.Succeeded, execution!.State);
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenOrphanedSessionIsReconciledAgain_RemainsOrphaned()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateGoneResult()
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Starting");

        var first = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Orphaned, first.Outcome);

        var failedExecution = await _executions.GetByIdAsync("execution-1");

        Assert.NotNull(failedExecution);
        Assert.Equal(ExecutionState.Failed, failedExecution!.State);

        var second = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Orphaned, second.Outcome);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.NotNull(session);
        Assert.Equal(SessionState.Orphaned, session!.State);
        Assert.Equal(LLMWorkGUI.Domain.Enums.ReconciliationOutcome.Orphaned, session.ReconciliationOutcome);
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenRecoverySessionCanBeReattached_ReturnsToActive()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateAliveResult(CreateBinding())
        });

        await _database.SeedSessionAsync(
            state: "Ambiguous",
            nativeSessionId: "native-1",
            reconciliationOutcome: "Ambiguous");
        await _database.SeedExecutionAsync("execution-1", state: "Running");

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Reattached, evidence.Outcome);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.NotNull(session);
        Assert.Equal(SessionState.Active, session!.State);
        Assert.Equal(LLMWorkGUI.Domain.Enums.ReconciliationOutcome.Reattached, session.ReconciliationOutcome);

        var execution = await _executions.GetByIdAsync("execution-1");

        Assert.Equal(ExecutionState.Running, execution!.State);
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenProcessGoneAndDeliveryExcluded_OrphansSession()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateGoneResult()
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Starting");

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Orphaned, evidence.Outcome);
        Assert.False(evidence.ProcessAlive);
        Assert.False(evidence.BindingMatched);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.NotNull(session);
        Assert.Equal(SessionState.Orphaned, session!.State);
        Assert.Equal(LLMWorkGUI.Domain.Enums.ReconciliationOutcome.Orphaned, session.ReconciliationOutcome);

        var execution = await _executions.GetByIdAsync("execution-1");

        Assert.NotNull(execution);
        Assert.Equal(ExecutionState.Failed, execution!.State);
        Assert.Equal(ExecutionFailureReason.StartupFailure, execution.FailureReason);
        Assert.Equal(Now, execution.EndedAt);
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenPromptMayHaveBeenDeliveredAndTerminalEvidenceIsMissing_IsAmbiguous()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateGoneResult()
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Running");

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.NotNull(session);
        Assert.Equal(SessionState.Ambiguous, session!.State);
        Assert.Equal(LLMWorkGUI.Domain.Enums.ReconciliationOutcome.Ambiguous, session.ReconciliationOutcome);

        var execution = await _executions.GetByIdAsync("execution-1");

        Assert.NotNull(execution);
        Assert.Equal(ExecutionState.Ambiguous, execution!.State);
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenStartingSessionLostItsProcess_OrphansSession()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateGoneResult()
        });

        await _database.SeedSessionAsync(state: "Starting");
        await _database.SeedExecutionAsync("execution-1", state: "Starting");

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Orphaned, evidence.Outcome);

        var session = await _sessions.GetByIdAsync("session-1");
        var execution = await _executions.GetByIdAsync("execution-1");

        Assert.Equal(SessionState.Orphaned, session!.State);
        Assert.Equal(ExecutionState.Failed, execution!.State);
    }

    [Theory]
    [InlineData("SessionConfirmed")]
    [InlineData("Running")]
    [InlineData("WaitingApproval")]
    [InlineData("Cancelling")]
    public async Task ReconcileSessionAsync_WhenPromptMayHaveBeenDeliveredAndProcessIsGone_IsAmbiguous(
        string executionState)
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateGoneResult()
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: executionState);

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome);

        var session = await _sessions.GetByIdAsync("session-1");
        var execution = await _executions.GetByIdAsync("execution-1");

        Assert.Equal(SessionState.Ambiguous, session!.State);
        Assert.Equal(ExecutionState.Ambiguous, execution!.State);
    }

    [Fact]
    public async Task ReconcileSessionAsync_DoesNotReleaseStaleWriterLockAutomatically()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateGoneResult()
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Running");
        await _database.InsertProjectLockAsync("lock-1", _database.GetWorkspacePath(), "execution-1");

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => _locks.ReleaseAsync("lock-1", Now, "manual release"));
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenBackendExecutableIsMissing_ReportsBackendMissing()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = new ReconciliationProbeResult
            {
                BackendAvailable = false,
                ProcessAlive = false,
                ObservedNativeSessionId = null,
                ObservedBinding = null,
                Details = "backend missing"
            }
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Running");

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.BackendMissing, evidence.Outcome);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.NotNull(session);
        Assert.Equal(SessionState.Orphaned, session!.State);
        Assert.Equal(LLMWorkGUI.Domain.Enums.ReconciliationOutcome.BackendMissing, session.ReconciliationOutcome);

        var execution = await _executions.GetByIdAsync("execution-1");

        Assert.NotNull(execution);
        Assert.Equal(ExecutionState.Ambiguous, execution!.State);
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenNativeSessionIdDiffers_DoesNotReattach()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateAliveResult(CreateBinding(), nativeSessionId: "native-other")
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Running");

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.Equal(SessionState.Ambiguous, session!.State);
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenImmutableBindingDiffers_DoesNotReattach()
    {
        var changedBinding = new SessionBinding(
            BackendType.OpenCode,
            "provider-1",
            "account-1",
            "model-other",
            reasoningEffort: null,
            speedMode: null,
            executionMode: null);

        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateAliveResult(changedBinding)
        });

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Running");

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome);
        Assert.False(evidence.BindingMatched);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.Equal(SessionState.Ambiguous, session!.State);
        Assert.Equal(CreateBinding(), session.Binding);
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenProcessAliveWithoutNativeSession_OrphansSession()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateAliveResult(CreateBinding())
        });

        await _database.SeedSessionAsync();
        await _database.SeedExecutionAsync("execution-1", state: "Starting");

        var evidence = await service.ReconcileSessionAsync("session-1");

        Assert.Equal(ReconciliationOutcome.Orphaned, evidence.Outcome);

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.Equal(SessionState.Orphaned, session!.State);
    }

    [Fact]
    public async Task MissingNativeIdDoesNotProveRunningPromptWasNeverDelivered()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe { Result = CreateGoneResult() });
        await _database.SeedSessionAsync();
        await _database.SeedExecutionAsync("execution-1", state: "Running");
        await _database.InsertProjectLockAsync("lock-1", _database.GetWorkspacePath(), "execution-1");
        var evidence = await service.ReconcileSessionAsync("session-1");
        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome);
        Assert.Equal(ExecutionState.Ambiguous, (await _executions.GetByIdAsync("execution-1"))!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession));
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
    }

    [Fact]
    public async Task MatchingProcessIdentityCannotConsumeAmbiguousTurnAsTerminal()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe { Result = CreateAliveResult(CreateBinding()) });
        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Ambiguous");
        var evidence = await service.ReconcileSessionAsync("session-1");
        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome);
        var session = await _sessions.GetByIdAsync("session-1");
        Assert.Equal(SessionState.Ambiguous, session!.State);
        Assert.Equal("execution-1", session.ActiveExecutionId);
        Assert.Equal(ExecutionState.Ambiguous, (await _executions.GetByIdAsync("execution-1"))!.State);
    }

    [Fact]
    public async Task OlderAmbiguousExecutionCannotBeHiddenByNewerRunningExecution()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe { Result = CreateAliveResult(CreateBinding()) });
        await _database.SeedSessionAsync(nativeSessionId: "native-1", activeExecutionId: "newer-running");
        await _database.SeedExecutionAsync("older-ambiguous", state: "Ambiguous");
        await _database.SeedExecutionAsync("newer-running", state: "Running");
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Executions SET CreatedAtUtc='2026-09-22T11:00:00.0000000+00:00' WHERE Id='newer-running';";
            await command.ExecuteNonQueryAsync();
        }
        var evidence = await service.ReconcileSessionAsync("session-1");
        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome);
        Assert.Equal("older-ambiguous", evidence.ExecutionId);
        var session = await _sessions.GetByIdAsync("session-1");
        Assert.Equal(SessionState.Ambiguous, session!.State);
        Assert.Equal("older-ambiguous", session.ActiveExecutionId);
        Assert.Equal(ExecutionState.Running, (await _executions.GetByIdAsync("newer-running"))!.State);
    }

    [Fact]
    public async Task ReconcileAllActiveAsync_AuditsOnlyStartingAndActiveSessions()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe
        {
            Result = CreateAliveResult(CreateBinding())
        });

        await _database.SeedSessionAsync("session-active", state: "Active", nativeSessionId: "native-1");
        await _database.SeedSessionAsync("session-idle", state: "Idle", nativeSessionId: "native-2");
        await _database.SeedSessionAsync("session-closed", state: "Closed");
        await _database.SeedSessionAsync("session-draft", state: "Draft");
        await _database.SeedExecutionAsync("execution-1", sessionId: "session-active", state: "Running");

        var evidence = await service.ReconcileAllActiveAsync();

        var single = Assert.Single(evidence);
        Assert.Equal("session-active", single.LocalSessionId);
        Assert.Equal(ReconciliationOutcome.Reattached, single.Outcome);

        Assert.Equal(SessionState.Idle, (await _sessions.GetByIdAsync("session-idle"))!.State);
        Assert.Equal(SessionState.Closed, (await _sessions.GetByIdAsync("session-closed"))!.State);
        Assert.Equal(SessionState.Draft, (await _sessions.GetByIdAsync("session-draft"))!.State);
    }

    [Theory]
    [InlineData(RecoveryAction.ResetSession)]
    [InlineData(RecoveryAction.CloseSession)]
    [InlineData(RecoveryAction.AcknowledgeAmbiguous)]
    public async Task ApplyRecoveryActionAsync_UncertainWriterRetainsStateAndLock(RecoveryAction action)
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe());
        await _database.SeedSessionAsync(state: "Ambiguous", nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Ambiguous");
        await _database.InsertProjectLockAsync("lock-1", _database.GetWorkspacePath(), "execution-1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyRecoveryActionAsync("session-1", action));

        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Equal(SessionState.Ambiguous, (await _sessions.GetByIdAsync("session-1"))!.State);
        Assert.Equal(ExecutionState.Ambiguous, (await _executions.GetByIdAsync("execution-1"))!.State);
        Assert.Empty(await _executions.ListEventsAsync("execution-1"));
    }

    [Fact]
    public async Task ApplyRecoveryActionAsync_CloseSession_ClosesOrphanedSessionAndReleasesStaleLock()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe());

        await _database.SeedSessionAsync(
            state: "Orphaned",
            nativeSessionId: "native-1",
            reconciliationOutcome: "Orphaned");
        await _database.SeedExecutionAsync("execution-1", state: "Failed", failureReason: "StartupFailure", endedAt: Now);
        await _database.InsertProjectLockAsync("lock-1", _database.GetWorkspacePath(), "execution-1");

        await Assert.ThrowsAsync<ProjectLockConflictException>(
            () => _locks.ReleaseAsync("lock-1", Now, "manual release"));

        var result = await service.ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession);

        Assert.Equal("lock-1", result.ReleasedLockId);
        Assert.Equal(CloseReason.UserClose, result.CloseReason);
        Assert.Null(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));

        var session = await _sessions.GetByIdAsync("session-1");

        Assert.Equal(SessionState.Closed, session!.State);
        Assert.Equal(CloseReason.UserClose, session.CloseReason);
        Assert.Equal(LLMWorkGUI.Domain.Enums.ReconciliationOutcome.Orphaned, session.ReconciliationOutcome);
    }

    [Fact]
    public async Task ApplyRecoveryActionAsync_AcknowledgeAmbiguousOutsideAmbiguousStateIsRejected()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe());

        await _database.SeedSessionAsync(nativeSessionId: "native-1");
        await _database.SeedExecutionAsync("execution-1", state: "Running");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyRecoveryActionAsync("session-1", RecoveryAction.AcknowledgeAmbiguous));
    }

    [Fact]
    public async Task ReconcileSessionAsync_WhenSessionDoesNotExist_Throws()
    {
        var service = await CreateServiceAsync(new FakeReconciliationProbe());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.ReconcileSessionAsync("missing-session"));
    }

    private async Task<ReconciliationService> CreateServiceAsync(FakeReconciliationProbe probe)
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();

        return new ReconciliationService(
            _sessions,
            _executions,
            new SqliteProjectRepository(_database.Factory),
            _locks,
            probe,
            new RecoveryMatrix(),
            new FixedTimeProvider(Now), connectionFactory: _database.Factory, instanceGuard: new PrimaryReconciliationTestGuard());
    }

    private static SessionBinding CreateBinding()
    {
        return new SessionBinding(
            BackendType.OpenCode,
            "provider-1",
            "account-1",
            "model-1",
            reasoningEffort: null,
            speedMode: null,
            executionMode: null);
    }

    private static ReconciliationProbeResult CreateAliveResult(
        SessionBinding binding,
        string? nativeSessionId = "native-1")
    {
        return new ReconciliationProbeResult
        {
            BackendAvailable = true,
            ProcessAlive = true,
            ObservedNativeSessionId = nativeSessionId,
            ObservedBinding = binding,
            Details = "process alive"
        };
    }

    private static ReconciliationProbeResult CreateGoneResult()
    {
        return new ReconciliationProbeResult
        {
            BackendAvailable = true,
            ProcessAlive = false,
            ObservedNativeSessionId = null,
            ObservedBinding = null,
            Details = "process gone"
        };
    }

    private sealed class FakeReconciliationProbe : IReconciliationProbe
    {
        public ReconciliationProbeResult Result { get; set; } = CreateGoneResult();

        public int CallCount { get; private set; }

        public ReconciliationProbeRequest? LastRequest { get; private set; }

        public Task<ReconciliationProbeResult> ProbeAsync(
            ReconciliationProbeRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;

            return Task.FromResult(Result);
        }
    }
}
