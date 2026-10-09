using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Reconciliation;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Reconciliation;

public sealed class ReconciliationRecoveryProofTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteExecutionRepository _executions;
    private readonly SqliteProjectLockRepository _locks;

    public ReconciliationRecoveryProofTests()
    {
        _sessions = new(_database.Factory);
        _executions = new(_database.Factory, new SensitiveDataFilter());
        _locks = new(_database.Factory);
    }

    public void Dispose() => _database.Dispose();

    [Theory]
    [InlineData("Starting", null)]
    [InlineData("Queued", null)]
    [InlineData("Failed", null)]
    [InlineData("Succeeded", null)]
    [InlineData("TimedOut", "2025-12-31T23:59:00.0000000+00:00")]
    public async Task CloseAndResetRequireConfirmedTerminalProofBeforeAnyMutation(string state, string? ended)
    {
        await SeedAsync(state, ended);
        foreach (var action in new[] { RecoveryAction.CloseSession, RecoveryAction.ResetSession })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ApplyRecoveryActionAsync("session-1", action));
            Assert.Equal(state, (await _executions.GetByIdAsync("execution-1"))!.State.ToString());
            Assert.Equal(SessionState.Ambiguous, (await _sessions.GetByIdAsync("session-1"))!.State);
            Assert.Equal("execution-1", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
            Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
            Assert.Empty(await _executions.ListEventsAsync("execution-1"));
        }
    }

    [Fact]
    public async Task TerminalEndBeforeActualStartDoesNotAuthorizeLocalClose()
    {
        await SeedAsync("Succeeded", "2026-01-01T00:01:00.0000000+00:00");
        await ExecuteAsync("UPDATE Executions SET StartedAtUtc='2026-01-01T00:02:00.0000000+00:00';");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession));

        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Equal(SessionState.Ambiguous, (await _sessions.GetByIdAsync("session-1"))!.State);
    }

    [Fact]
    public async Task UnresolvedActivePointerCannotBeDiscardedByTerminalHistory()
    {
        await SeedAsync("Succeeded", "2026-01-01T00:01:00.0000000+00:00");
        await ExecuteAsync("UPDATE Sessions SET ActiveExecutionId='unknown-execution';");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession));

        Assert.Equal("unknown-execution", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
    }

    [Fact]
    public async Task OpenCodeJournalRefusalCannotFallThroughToGenericMutation()
    {
        await SeedAsync("Succeeded", "2026-01-01T00:01:00.0000000+00:00");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(new UnownedOpenCodeRecovery()).ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession));

        Assert.Equal(SessionState.Ambiguous, (await _sessions.GetByIdAsync("session-1"))!.State);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Empty(await _executions.ListEventsAsync("execution-1"));
    }

    [Fact]
    public async Task CursorReset_DoesNotReleaseAnOpenCodeWriterLock()
    {
        await SeedAsync("Running", null);
        await ExecuteAsync("""
            INSERT INTO Sessions (Id,ProjectId,Backend,ProviderProfileId,AccountId,ModelId,WorkspaceRootPath,
                NativeSessionId,State,ReconciliationOutcome,CloseReason,ActiveExecutionId,CreatedAtUtc,LastEventAtUtc)
            SELECT 'session-cursor',ProjectId,'CursorAcp',ProviderProfileId,AccountId,ModelId,WorkspaceRootPath,
                'cursor-native','Active','None','None','execution-cursor',CreatedAtUtc,LastEventAtUtc
            FROM Sessions WHERE Id='session-1';
            INSERT INTO Executions (Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,CreatedAtUtc,StartedAtUtc,EndedAtUtc)
            SELECT 'execution-cursor','session-cursor','request-cursor','Succeeded','None',RequestedRouteId,
                CreatedAtUtc,StartedAtUtc,'2026-01-01T00:01:00.0000000+00:00'
            FROM Executions WHERE Id='execution-1';
            """);

        var failure = await Record.ExceptionAsync(() => Service().ApplyRecoveryActionAsync("session-cursor", RecoveryAction.ResetSession));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(ExecutionState.Running, (await _executions.GetByIdAsync("execution-1"))!.State);
        Assert.Null((await _executions.GetByIdAsync("execution-1"))!.EndedAt);
        Assert.Equal(SessionState.Ambiguous, (await _sessions.GetByIdAsync("session-1"))!.State);
        Assert.Equal(SessionState.Active, (await _sessions.GetByIdAsync("session-cursor"))!.State);
        var held = await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath());
        Assert.Equal("execution-1", held!.ExecutionId);
        Assert.True(held.IsHeld);
        var checkout = new CheckoutLockService(_locks, new PrimaryReconciliationTestGuard(), TimeProvider.System);
        await Assert.ThrowsAsync<ProjectLockConflictException>(() =>
            checkout.AcquireWriterLockAsync(held.ProjectId, held.CanonicalRootPath, "execution-cursor", 0));
        Assert.Equal("execution-1", (await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()))!.ExecutionId);
        Assert.Equal(ExecutionState.Running, (await _executions.GetByIdAsync("execution-1"))!.State);
    }

    [Theory]
    [InlineData(RecoveryAction.CloseSession)]
    [InlineData(RecoveryAction.ResetSession)]
    public async Task ConfirmedTerminalControlClosesWithoutRewritingExecution(RecoveryAction action)
    {
        await SeedAsync("Succeeded", "2026-01-01T00:01:00.0000000+00:00");

        var result = await Service().ApplyRecoveryActionAsync("session-1", action);

        Assert.Equal("lock-1", result.ReleasedLockId);
        Assert.Equal(SessionState.Closed, (await _sessions.GetByIdAsync("session-1"))!.State);
        Assert.Equal(ExecutionState.Succeeded, (await _executions.GetByIdAsync("execution-1"))!.State);
        Assert.NotNull((await _executions.GetByIdAsync("execution-1"))!.EndedAt);
        Assert.Null(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Single(await _executions.ListEventsAsync("execution-1"));
    }

    [Theory]
    [InlineData("Failed", null, false)]
    [InlineData("Succeeded", null, false)]
    [InlineData("Succeeded", "2026-01-01T00:01:00.0000000+00:00", true)]
    public async Task ReattachmentDoesNotConsumeMissingTerminalEvidenceAsAnIdleTurn(string state, string? ended, bool confirmed)
    {
        await SeedAsync(state, ended);

        await Service(probe: new MatchingFixtureProbe()).ReconcileSessionAsync("session-1");

        var session = (await _sessions.GetByIdAsync("session-1"))!;
        Assert.Equal(confirmed ? SessionState.Idle : SessionState.Active, session.State);
        Assert.Equal(confirmed ? null : "execution-1", session.ActiveExecutionId);
        Assert.Equal(state, (await _executions.GetByIdAsync("execution-1"))!.State.ToString());
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
    }

    [Fact]
    public async Task ExplicitStartingDeliveryUncertaintyRemainsAmbiguousDuringAutomaticReconciliation()
    {
        await SeedAsync("Starting", null);

        var evidence = await Service(probe: new GoneFixtureProbe()).ReconcileSessionAsync("session-1");

        Assert.Equal(LLMWorkGUI.Application.Reconciliation.ReconciliationOutcome.Ambiguous, evidence.Outcome);
        var execution = (await _executions.GetByIdAsync("execution-1"))!;
        Assert.Equal(ExecutionState.Ambiguous, execution.State);
        Assert.Null(execution.EndedAt);
        Assert.Equal("execution-1", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
    }

    [Fact]
    public async Task LegitimatePreDispatchStartingControlCanBeCancelledLocally()
    {
        await SeedAsync("Starting", null);
        await ExecuteAsync("UPDATE Executions SET StartedAtUtc=NULL,ProcessState=NULL;");

        var result = await Service().ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession);

        Assert.Equal("lock-1", result.ReleasedLockId);
        Assert.Equal(ExecutionState.Failed, (await _executions.GetByIdAsync("execution-1"))!.State);
        Assert.Equal(ExecutionFailureReason.UserCancelled, (await _executions.GetByIdAsync("execution-1"))!.FailureReason);
        Assert.NotNull((await _executions.GetByIdAsync("execution-1"))!.EndedAt);
        Assert.Null(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
    }

    [Fact]
    public async Task LateNativeUncertaintyAfterPreflightCannotBeClosedOrReleased()
    {
        await SeedAsync("Succeeded", "2026-01-01T00:01:00.0000000+00:00");
        var locks = new MutatingLockRead(_locks, () => ExecuteAsync("""
            UPDATE Executions SET State='Running',EndedAtUtc=NULL WHERE Id='execution-1';
            UPDATE Sessions SET State='Active' WHERE Id='session-1';
            """));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(lockRepository: locks).ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession));

        Assert.True(locks.Mutated);
        Assert.Equal(SessionState.Active, (await _sessions.GetByIdAsync("session-1"))!.State);
        Assert.Equal("execution-1", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
        Assert.Equal(ExecutionState.Running, (await _executions.GetByIdAsync("execution-1"))!.State);
        Assert.Null((await _executions.GetByIdAsync("execution-1"))!.EndedAt);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Empty(await _executions.ListEventsAsync("execution-1"));
    }

    [Fact]
    public async Task RecoveryAuditFailureRollsBackSessionAndLockTogether()
    {
        await SeedAsync("Succeeded", "2026-01-01T00:01:00.0000000+00:00");
        await ExecuteAsync("""
            CREATE TRIGGER deny_manual_recovery_audit BEFORE INSERT ON ExecutionEvents WHEN NEW.EventKind='RecoveryAction'
            BEGIN SELECT RAISE(ABORT,'synthetic recovery audit storage fault'); END;
            """);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            Service().ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession));

        Assert.Equal(SessionState.Ambiguous, (await _sessions.GetByIdAsync("session-1"))!.State);
        Assert.Equal("execution-1", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Empty(await _executions.ListEventsAsync("execution-1"));
    }

    [Fact]
    public async Task AutomaticReattachmentPreservesUnknownPointerInsteadOfConsumingIt()
    {
        await SeedAsync("Succeeded", "2026-01-01T00:01:00.0000000+00:00");
        await ExecuteAsync("DELETE FROM ProjectLocks; DELETE FROM Executions; UPDATE Sessions SET ActiveExecutionId='unknown-execution';");

        await Service(probe: new MatchingFixtureProbe()).ReconcileSessionAsync("session-1");

        Assert.Equal(SessionState.Active, (await _sessions.GetByIdAsync("session-1"))!.State);
        Assert.Equal("unknown-execution", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
    }

    [Fact]
    public async Task MissingAtomicStorageCannotFallBackToSeparateRepositoryWrites()
    {
        await SeedAsync("Succeeded", "2026-01-01T00:01:00.0000000+00:00");
        var service = new ReconciliationService(_sessions, _executions,
            new SqliteProjectRepository(_database.Factory), _locks, new UnusedProbe(), new RecoveryMatrix(),
            instanceGuard: new PrimaryReconciliationTestGuard());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyRecoveryActionAsync("session-1", RecoveryAction.CloseSession));

        Assert.Equal(SessionState.Ambiguous, (await _sessions.GetByIdAsync("session-1"))!.State);
        Assert.Equal("execution-1", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Empty(await _executions.ListEventsAsync("execution-1"));
    }

    [Fact]
    public async Task AutomaticReconciliationCannotOverwriteNativeCompletionCommittedDuringProbe()
    {
        await SeedGenericActiveAsync();
        var probe = new MutatingProbe(() => ExecuteAsync("""
            BEGIN IMMEDIATE;
            UPDATE Executions SET State='Succeeded',EndedAtUtc='2026-01-01T00:01:00.0000000+00:00',
                ObservedRouteId='route-1',ExitCode=0,TerminationReason='SyntheticNativeTerminal',
                SourceHashAfter='new-native-hash',ProcessState='SyntheticNativeCompleted' WHERE Id='execution-1';
            UPDATE Sessions SET State='Idle',ActiveExecutionId=NULL,LastEventAtUtc='2026-01-01T00:01:00.0000000+00:00'
                WHERE Id='session-1';
            UPDATE ProjectLocks SET ReleasedAtUtc='2026-01-01T00:01:00.0000000+00:00',ReleaseReason='SyntheticNativeTerminal'
                WHERE Id='lock-1';
            INSERT INTO ExecutionEvents(Id,ExecutionId,Sequence,EventKind,OccurredAtUtc)
                VALUES('native-fixture-event','execution-1',0,'SyntheticNativeTerminal','2026-01-01T00:01:00.0000000+00:00');
            COMMIT;
            """));

        var error = await Record.ExceptionAsync(() => Service(probe: probe).ReconcileSessionAsync("session-1"));

        Assert.True(probe.Mutated);
        var execution = (await _executions.GetByIdAsync("execution-1"))!;
        Assert.Equal(ExecutionState.Succeeded, execution.State);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero), execution.EndedAt);
        Assert.Equal("route-1", execution.ObservedRouteId);
        Assert.Equal(0, execution.ExitCode);
        Assert.Equal("SyntheticNativeTerminal", execution.TerminationReason);
        Assert.Equal("new-native-hash", execution.SourceHashAfter);
        Assert.Equal("SyntheticNativeCompleted", execution.ProcessState);
        var session = (await _sessions.GetByIdAsync("session-1"))!;
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Null(session.ActiveExecutionId);
        Assert.Null(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Equal("SyntheticNativeTerminal", Assert.Single(await _executions.ListEventsAsync("execution-1")).EventKind);
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public async Task AutomaticReconciliationCannotRestoreOldNativeIdentityOrPointerAfterNewTurnAdmission()
    {
        await SeedGenericActiveAsync();
        var probe = new MutatingProbe(() => ExecuteAsync("""
            BEGIN IMMEDIATE;
            UPDATE Executions SET State='Succeeded',EndedAtUtc='2026-01-01T00:01:00.0000000+00:00'
                WHERE Id='execution-1';
            INSERT INTO Executions(Id,SessionId,ClientRequestId,State,FailureReason,RequestedRouteId,ProcessState,CreatedAtUtc,StartedAtUtc)
                VALUES('execution-2','session-1','request-2','Running','None','route-1','SyntheticNewTurn',
                    '2026-01-01T00:02:00.0000000+00:00','2026-01-01T00:02:00.0000000+00:00');
            UPDATE Sessions SET NativeSessionId='new-native-session',ActiveExecutionId='execution-2',
                LastEventAtUtc='2026-01-01T00:02:00.0000000+00:00' WHERE Id='session-1';
            UPDATE ProjectLocks SET ExecutionId='execution-2' WHERE Id='lock-1';
            COMMIT;
            """));

        var error = await Record.ExceptionAsync(() => Service(probe: probe).ReconcileSessionAsync("session-1"));

        Assert.True(probe.Mutated);
        var session = (await _sessions.GetByIdAsync("session-1"))!;
        Assert.Equal("new-native-session", session.NativeSessionId);
        Assert.Equal("execution-2", session.ActiveExecutionId);
        Assert.Equal(SessionState.Active, session.State);
        Assert.Equal(ExecutionState.Succeeded, (await _executions.GetByIdAsync("execution-1"))!.State);
        Assert.NotNull((await _executions.GetByIdAsync("execution-1"))!.EndedAt);
        Assert.Equal(ExecutionState.Running, (await _executions.GetByIdAsync("execution-2"))!.State);
        Assert.Equal("execution-2", (await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()))!.ExecutionId);
        Assert.Empty(await _executions.ListEventsAsync("execution-1"));
        Assert.Empty(await _executions.ListEventsAsync("execution-2"));
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public async Task AutomaticReconciliationAuditFailureRollsBackExecutionSessionAndPointer()
    {
        await SeedGenericActiveAsync();
        await ExecuteAsync("""
            CREATE TRIGGER deny_automatic_reconciliation_audit BEFORE INSERT ON ExecutionEvents WHEN NEW.EventKind='ReconciliationOutcome'
            BEGIN SELECT RAISE(ABORT,'synthetic automatic reconciliation audit storage fault'); END;
            """);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            Service(probe: new GoneFixtureProbe()).ReconcileSessionAsync("session-1"));

        var execution = (await _executions.GetByIdAsync("execution-1"))!;
        Assert.Equal(ExecutionState.Running, execution.State);
        Assert.Null(execution.EndedAt);
        Assert.Equal("NativePromptDeliveryUnconfirmed", execution.ProcessState);
        var session = (await _sessions.GetByIdAsync("session-1"))!;
        Assert.Equal(SessionState.Active, session.State);
        Assert.Equal("execution-1", session.ActiveExecutionId);
        Assert.Equal(LLMWorkGUI.Domain.Enums.ReconciliationOutcome.None, session.ReconciliationOutcome);
        Assert.Equal("execution-1", (await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()))!.ExecutionId);
        Assert.Empty(await _executions.ListEventsAsync("execution-1"));
    }

    private async Task SeedGenericActiveAsync()
    {
        await SeedAsync("Running", null);
        await ExecuteAsync("""
            UPDATE ProviderProfiles SET Backend='CursorAcp'; UPDATE Models SET Backend='CursorAcp';
            UPDATE Routes SET Backend='CursorAcp'; UPDATE Sessions SET Backend='CursorAcp',State='Active';
            """);
    }

    private async Task SeedAsync(string state, string? ended)
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync(state: "Ambiguous", nativeSessionId: "native-1", activeExecutionId: "execution-1");
        await _database.SeedExecutionAsync("execution-1", state: state, processState: "NativePromptDeliveryUnconfirmed",
            endedAt: ended is null ? null : DateTimeOffset.Parse(ended, System.Globalization.CultureInfo.InvariantCulture));
        await ExecuteAsync("UPDATE Executions SET StartedAtUtc=CreatedAtUtc;");
        await _database.InsertProjectLockAsync("lock-1", _database.GetWorkspacePath(), "execution-1");
    }

    private ReconciliationService Service(IOpenCodeJournalRecoveryService? recovery = null, IReconciliationProbe? probe = null,
        IProjectLockRepository? lockRepository = null) => new(_sessions, _executions,
        new SqliteProjectRepository(_database.Factory), lockRepository ?? _locks, probe ?? new UnusedProbe(), new RecoveryMatrix(),
        openCodeRecovery: recovery, connectionFactory: _database.Factory, instanceGuard: new PrimaryReconciliationTestGuard());

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class UnusedProbe : IReconciliationProbe
    {
        public Task<ReconciliationProbeResult> ProbeAsync(ReconciliationProbeRequest request, CancellationToken token = default)
            => throw new InvalidOperationException("A manual recovery action cannot substitute a liveness probe for terminal proof.");
    }

    private sealed class MatchingFixtureProbe : IReconciliationProbe
    {
        public Task<ReconciliationProbeResult> ProbeAsync(ReconciliationProbeRequest request, CancellationToken token = default)
            => Task.FromResult(new ReconciliationProbeResult { BackendAvailable = true, ProcessAlive = true,
                ObservedNativeSessionId = request.Session.NativeSessionId, ObservedBinding = request.Session.Binding,
                Details = "synthetic independently matching identity fixture; no native call" });
    }

    private sealed class GoneFixtureProbe : IReconciliationProbe
    {
        public Task<ReconciliationProbeResult> ProbeAsync(ReconciliationProbeRequest request, CancellationToken token = default)
            => Task.FromResult(new ReconciliationProbeResult { BackendAvailable = true, ProcessAlive = false,
                Details = "synthetic unavailable process fixture; delivery remains unconfirmed" });
    }

    private sealed class MutatingProbe(Func<Task> mutation) : IReconciliationProbe
    {
        public bool Mutated { get; private set; }
        public async Task<ReconciliationProbeResult> ProbeAsync(ReconciliationProbeRequest request, CancellationToken token = default)
        {
            await mutation();
            Mutated = true;
            return new ReconciliationProbeResult { BackendAvailable = true, ProcessAlive = false,
                Details = "Synthetic committed concurrent native-state fixture; no native process or model call." };
        }
    }

    private sealed class UnownedOpenCodeRecovery : IOpenCodeJournalRecoveryService
    {
        public Task<IReadOnlyList<ReconciliationEvidence>> QuarantineInterruptedAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ReconciliationEvidence>>([]);
        public Task<ReconciliationEvidence?> TryReconcileAsync(string id, CancellationToken token = default) => Task.FromResult<ReconciliationEvidence?>(null);
        public Task<ReconciliationRecoveryResult?> TryApplyActionAsync(string id, RecoveryAction action, CancellationToken token = default) => Task.FromResult<ReconciliationRecoveryResult?>(null);
        public Task<int> ReplayActivityAsync(CancellationToken token = default) => Task.FromResult(0);
    }

    private sealed class MutatingLockRead(IProjectLockRepository inner, Func<Task> mutation) : IProjectLockRepository
    {
        public bool Mutated { get; private set; }
        public async Task<ProjectLock?> GetActiveByRootPathAsync(string root, CancellationToken token = default)
        {
            if (!Mutated) { await mutation(); Mutated = true; }
            return await inner.GetActiveByRootPathAsync(root, token);
        }
        public Task<bool> TryAcquireAsync(ProjectLock value, CancellationToken token = default) => inner.TryAcquireAsync(value, token);
        public Task<bool> ReleaseAsync(string id, DateTimeOffset at, string reason, CancellationToken token = default) => inner.ReleaseAsync(id, at, reason, token);
    }
}
