using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Infrastructure.Concurrency;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class NativeGatewayTurnTests
{
    [Theory]
    [InlineData("BEFORE UPDATE OF EndedAtUtc ON Executions WHEN NEW.EndedAtUtc IS NOT NULL")]
    [InlineData("BEFORE INSERT ON ExecutionEvents WHEN NEW.EventKind='NativeGatewayLifecycle' AND json_extract(NEW.NormalizedRedactedPayloadJson,'$.state')='Succeeded'")]
    public async Task TerminalCommitFailure_RetainsPhysicalAndDurableOwnership(string trigger)
    {
        await Ready();
        await Sql($"CREATE TRIGGER fail_terminal {trigger} BEGIN SELECT RAISE(ABORT,'fixture'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => _service!.ExecuteAsync(_request));

        Assert.True(Assert.Single(_tokens).IsHeld);
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal("Running", await Sql("SELECT State FROM Executions"));
        Assert.Equal(DBNull.Value, await Sql("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal("Active", await Sql("SELECT State FROM Sessions WHERE Backend='NativeGateway'"));
        Assert.Equal(3L, await Sql("SELECT COUNT(*) FROM ExecutionEvents"));
        Assert.Equal(1, _adapter.Calls);
        Assert.Null(NamedMutexScope.TryAcquire(NamedMutexNames.ForCheckout(_request.RootPath)));
        await Assert.ThrowsAsync<ProjectLockConflictException>(() =>
            _locks.AcquireWriterLockAsync(_request.ProjectId, _request.RootPath, "another-writer", 0));
    }

    [Fact]
    public async Task TerminalCommit_PrecedesDurableLockRelease()
    {
        await Ready();
        await Sql("""
            CREATE TRIGGER require_terminal BEFORE UPDATE OF ReleasedAtUtc ON ProjectLocks
            WHEN NEW.ReleasedAtUtc IS NOT NULL AND EXISTS
                (SELECT 1 FROM Executions WHERE Id=NEW.ExecutionId AND EndedAtUtc IS NULL)
            BEGIN SELECT RAISE(ABORT,'terminal result must commit first'); END;
            """);

        try { await _service!.ExecuteAsync(_request); }
        finally { await Sql("DROP TRIGGER require_terminal"); }

        Assert.False(Assert.Single(_tokens).IsHeld);
        Assert.Equal("Succeeded", await Sql("SELECT State FROM Executions"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        using var next = NamedMutexScope.TryAcquire(NamedMutexNames.ForCheckout(_request.RootPath));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task LockReleaseFailure_AfterTerminalCommit_RetainsOwnershipAndCanRetryRelease()
    {
        await Ready();
        await Sql("""
            CREATE TRIGGER fail_lock_release BEFORE UPDATE OF ReleasedAtUtc ON ProjectLocks
            BEGIN SELECT RAISE(ABORT,'fixture release failure'); END;
            """);
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => _service!.ExecuteAsync(_request));
            Assert.Equal("Succeeded", await Sql("SELECT State FROM Executions"));
            Assert.NotEqual(DBNull.Value, await Sql("SELECT EndedAtUtc FROM Executions"));
            Assert.Equal("Closed", await Sql("SELECT State FROM Sessions WHERE Backend='NativeGateway'"));
            Assert.Equal(4L, await Sql("SELECT COUNT(*) FROM ExecutionEvents"));
            Assert.True(Assert.Single(_tokens).IsHeld);
            Assert.Null(NamedMutexScope.TryAcquire(NamedMutexNames.ForCheckout(_request.RootPath)));
        }
        finally { await Sql("DROP TRIGGER fail_lock_release"); }

        await Assert.Single(_tokens).ReleaseAsync("retry after fixture fault");
        Assert.False(Assert.Single(_tokens).IsHeld);
        using var next = NamedMutexScope.TryAcquire(NamedMutexNames.ForCheckout(_request.RootPath));
        Assert.NotNull(next);
        Assert.Equal(1, _adapter.Calls);
    }
}
