using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Lifecycle;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Lifecycle;

public sealed class CrashRecoveryOwnershipTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly SqliteExecutionRepository _executions;
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteProjectLockRepository _locks;

    public CrashRecoveryOwnershipTests()
    {
        _executions = new(_database.Factory, new SensitiveDataFilter());
        _sessions = new(_database.Factory);
        _locks = new(_database.Factory);
    }

    public void Dispose() => _database.Dispose();

    [Theory]
    [InlineData("CursorAcp", "SessionConfirmed")]
    [InlineData("CursorAcp", "Running")]
    [InlineData("CursorAcp", "WaitingApproval")]
    [InlineData("CursorAcp", "Cancelling")]
    [InlineData("OpenCode", "SessionConfirmed")]
    [InlineData("OpenCode", "Running")]
    [InlineData("OpenCode", "WaitingApproval")]
    [InlineData("OpenCode", "Cancelling")]
    public async Task DispatchedUncertaintyCannotBecomeTerminalOrLoseItsWriter(string backend, string state)
    {
        await SeedAsync(backend, state);
        var service = Service();

        var first = await service.RecoverAsync();
        var second = await service.RecoverAsync();

        var execution = Assert.IsType<LLMWorkGUI.Domain.Entities.Execution>(await _executions.GetByIdAsync("execution-1"));
        Assert.Equal(ExecutionState.Ambiguous, execution.State);
        Assert.Null(execution.EndedAt);
        Assert.Equal("NativePromptDeliveryUnconfirmed", execution.ProcessState);
        Assert.Null(execution.ExitCode);
        Assert.Equal(ExecutionFailureReason.None, execution.FailureReason);
        var session = Assert.IsType<LLMWorkGUI.Domain.Entities.Session>(await _sessions.GetByIdAsync("session-1"));
        Assert.Equal(SessionState.Ambiguous, session.State);
        Assert.Equal("execution-1", session.ActiveExecutionId);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Equal(new[] { "lock-1" }, first.RetainedLockIds);
        Assert.Empty(first.ReleasedLockIds);
        Assert.Equal(new[] { "lock-1" }, second.RetainedLockIds);
        Assert.Empty(second.InterruptedExecutionIds);
        var audit = Assert.Single(await _executions.ListEventsAsync("execution-1"));
        Assert.Contains("\"recoveryState\":\"Ambiguous\"", audit.NormalizedRedactedPayloadJson);
    }

    [Theory]
    [InlineData("CursorAcp", "Ambiguous")]
    [InlineData("OpenCode", "Ambiguous")]
    [InlineData("CursorAcp", "Failed")]
    [InlineData("OpenCode", "Succeeded")]
    public async Task MissingTerminalTimestampRetainsTheActivePointerAndLock(string backend, string state)
    {
        await SeedAsync(backend, state);

        var report = await Service().RecoverAsync();

        Assert.Null((await _executions.GetByIdAsync("execution-1"))!.EndedAt);
        Assert.Equal("execution-1", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Empty(report.ReleasedLockIds);
        Assert.Equal(new[] { "lock-1" }, report.RetainedLockIds);
    }

    [Theory]
    [InlineData("CursorAcp", "Succeeded")]
    [InlineData("CursorAcp", "Failed")]
    [InlineData("CursorAcp", "TimedOut")]
    [InlineData("CursorAcp", "Cancelled")]
    [InlineData("CursorAcp", "RouteMismatch")]
    [InlineData("OpenCode", "Succeeded")]
    public async Task ConfirmedTerminalExecutionRemainsUnchangedAndCanReleaseItsStaleLock(string backend, string state)
    {
        var ended = new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero);
        await SeedAsync(backend, state, ended);
        var original = (await _executions.GetByIdAsync("execution-1"))!;

        var report = await Service().RecoverAsync();

        var stored = (await _executions.GetByIdAsync("execution-1"))!;
        Assert.Equal(original.State, stored.State);
        Assert.Equal(ended, stored.EndedAt);
        Assert.Equal(original.FailureReason, stored.FailureReason);
        Assert.Equal(original.ProcessState, stored.ProcessState);
        Assert.Empty(await _executions.ListEventsAsync("execution-1"));
        Assert.Null((await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
        Assert.Null(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Equal(new[] { "lock-1" }, report.ReleasedLockIds);
        Assert.Empty(report.RetainedLockIds);
    }

    [Fact]
    public async Task TerminalActivePointerCannotHideAnotherUnresolvedExecutionInTheSameSession()
    {
        await SeedAsync("CursorAcp", "Succeeded", new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero));
        await _database.SeedExecutionAsync("execution-unresolved", state: "Running", clientRequestId: "other-request");

        var report = await Service().RecoverAsync();

        Assert.Equal("execution-unresolved", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
        Assert.Equal(ExecutionState.Ambiguous, (await _executions.GetByIdAsync("execution-unresolved"))!.State);
        Assert.Empty(report.ReleasedLockIds);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
    }

    [Fact]
    public async Task AuditWriteFailureRollsBackQuarantineAndRetainsUnresolvedOwnership()
    {
        await SeedAsync("CursorAcp", "Running");
        await ExecuteAsync("""
            CREATE TRIGGER deny_crash_audit BEFORE INSERT ON ExecutionEvents WHEN NEW.EventKind='CrashRecovery'
            BEGIN SELECT RAISE(ABORT,'synthetic audit storage failure'); END;
            """);

        var report = await Service().RecoverAsync();

        var execution = (await _executions.GetByIdAsync("execution-1"))!;
        Assert.Equal(ExecutionState.Running, execution.State);
        Assert.Null(execution.EndedAt);
        Assert.Null(execution.TerminationReason);
        Assert.Empty(await _executions.ListEventsAsync("execution-1"));
        Assert.Empty(report.InterruptedExecutionIds);
        Assert.Equal("execution-1", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Equal(new[] { "lock-1" }, report.RetainedLockIds);
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public async Task SuccessfulNativeCommitAfterInitialInventoryIsNotOverwrittenByQuarantine()
    {
        await SeedAsync("CursorAcp", "Running");
        var factory = new CommitBeforeQuarantineFactory(_database.Factory, () => ExecuteAsync("""
            UPDATE Executions SET State='Succeeded',EndedAtUtc='2026-01-01T00:01:00.0000000+00:00',
                TerminationReason='native-success' WHERE Id='execution-1';
            """));

        var report = await Service(factory).RecoverAsync();

        Assert.True(factory.CommitInjected);
        var execution = (await _executions.GetByIdAsync("execution-1"))!;
        Assert.Equal(ExecutionState.Succeeded, execution.State);
        Assert.NotNull(execution.EndedAt);
        Assert.Equal("native-success", execution.TerminationReason);
        Assert.Empty(await _executions.ListEventsAsync("execution-1"));
        Assert.Empty(report.InterruptedExecutionIds);
        Assert.Equal(new[] { "lock-1" }, report.ReleasedLockIds);
    }

    [Fact]
    public async Task ReleaseStorageFailurePreservesTheConfirmedTerminalAndReportsRetainedLock()
    {
        await SeedAsync("OpenCode", "Succeeded", new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero));
        await ExecuteAsync("""
            CREATE TRIGGER deny_crash_release BEFORE UPDATE OF ReleasedAtUtc ON ProjectLocks
            WHEN NEW.ReleasedAtUtc IS NOT NULL BEGIN SELECT RAISE(ABORT,'synthetic release storage failure'); END;
            """);

        var report = await Service().RecoverAsync();

        Assert.Equal(ExecutionState.Succeeded, (await _executions.GetByIdAsync("execution-1"))!.State);
        Assert.NotNull((await _executions.GetByIdAsync("execution-1"))!.EndedAt);
        Assert.Empty(report.ReleasedLockIds);
        Assert.Equal(new[] { "lock-1" }, report.RetainedLockIds);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
    }

    [Fact]
    public async Task StartTimestampBeforeCreationCannotProveTerminalOwnershipRelease()
    {
        await SeedAsync("CursorAcp", "Succeeded", new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero));
        await ExecuteAsync("UPDATE Executions SET StartedAtUtc='2025-12-31T23:59:00.0000000+00:00';");

        var report = await Service().RecoverAsync();

        Assert.Equal("execution-1", (await _sessions.GetByIdAsync("session-1"))!.ActiveExecutionId);
        Assert.NotNull(await _locks.GetActiveByRootPathAsync(_database.GetWorkspacePath()));
        Assert.Empty(report.ReleasedLockIds);
        Assert.Equal(new[] { "lock-1" }, report.RetainedLockIds);
    }

    private async Task SeedAsync(string backend, string state, DateTimeOffset? ended = null)
    {
        await _database.InitializeAsync();
        await _database.SeedRouteChainAsync();
        await _database.SeedSessionAsync(nativeSessionId: "native-session-1", activeExecutionId: "execution-1");
        await ExecuteAsync($"UPDATE Sessions SET Backend='{backend}' WHERE Id='session-1';");
        await _database.SeedExecutionAsync("execution-1", state: state, endedAt: ended,
            processState: "NativePromptDeliveryUnconfirmed");
        await _database.InsertProjectLockAsync("lock-1", _database.GetWorkspacePath(), "execution-1");
    }

    private AppCrashRecoveryService Service(ISqliteConnectionFactory? factory = null) => new(factory ?? _database.Factory, _executions, _sessions,
        new SqliteProjectRepository(_database.Factory), new SqliteWorkflowRunRepository(_database.Factory), _locks);

    private sealed class CommitBeforeQuarantineFactory(ISqliteConnectionFactory inner, Func<Task> commit) : ISqliteConnectionFactory
    {
        private int _opened;
        public bool CommitInjected { get; private set; }
        public string DatabasePath => inner.DatabasePath;
        public string ConnectionString => inner.ConnectionString;
        public SqliteConnection CreateConnection() => inner.CreateConnection();
        public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken token = default)
        {
            if (Interlocked.Increment(ref _opened) == 2)
            {
                await commit();
                CommitInjected = true;
            }
            return await inner.OpenConnectionAsync(token);
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _database.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
