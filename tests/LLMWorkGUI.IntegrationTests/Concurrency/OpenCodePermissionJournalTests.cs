using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.OpenCode;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class OpenCodePermissionJournalTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private ApplicationInstanceGuard? _guard;
    public void Dispose() { _guard?.Dispose(); _db.Dispose(); }
    private async Task<(SqliteOpenCodeExecutionJournal Journal, OpenCodeJournalEntry Entry, ICheckoutLockToken Lock)> Ready()
    {
        await _db.InitializeAsync(); await _db.SeedRouteChainAsync();
        await Execute("UPDATE Models SET ProviderModelId='provider/native-model'");
        _guard = new ApplicationInstanceGuard(Path.Combine(_db.Root, "instance"));
        var journal = new SqliteOpenCodeExecutionJournal(_db.Factory, TimeProvider.System, _guard);
        var entry = await journal.BeginAsync("project-1", _db.GetWorkspacePath(), "native-owned",
            Assert.Single(await journal.ListRoutesAsync()), "request-owned", "fixture-prompt-hash", 17);
        var locks = new CheckoutLockService(new SqliteProjectLockRepository(_db.Factory), _guard, TimeProvider.System);
        var token = await locks.AcquireWriterLockAsync("project-1", _db.GetWorkspacePath(), entry.ExecutionId, 17);
        await journal.MarkRunningAsync(entry);
        return (journal, entry, token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingApproval_KeepsOwnershipAndCompletesFromEitherProjection(bool resume)
    {
        var (journal, entry, token) = await Ready();
        await journal.SetWaitingApprovalAsync(entry, true);
        Assert.Equal("WaitingApproval", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("Active", await Scalar("SELECT State FROM Sessions"));
        Assert.Equal(entry.ExecutionId, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Null(await Scalar("SELECT EndedAtUtc FROM Executions"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetWaitingApprovalAsync(entry, true));
        Assert.Equal("3", await Scalar("SELECT COUNT(*) FROM ExecutionEvents"));
        if (resume) await journal.SetWaitingApprovalAsync(entry, false);
        await token.ReleaseAsync("synthetic confirmed terminal");
        await journal.CompleteAsync(entry, new TurnResult { SessionId = entry.NativeSessionId, Status = TurnResult.CompletedStatus, OutputText = "" });
        Assert.Equal("Succeeded", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("Idle", await Scalar("SELECT State FROM Sessions"));
        Assert.Null(await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.NotNull(await Scalar("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal(resume ? "2" : "1", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeApproval'"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE DataClassification<>'PrivateSource'"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeApproval' AND NormalizedRedactedPayloadJson NOT IN ('{\"state\":\"WaitingApproval\",\"reason\":\"NativePermissionWaiting\"}','{\"state\":\"Running\",\"reason\":\"NativePermissionResolved\"}')"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetWaitingApprovalAsync(entry, true));
    }

    [Theory]
    [InlineData("UPDATE Sessions SET NativeSessionId='changed'")]
    [InlineData("UPDATE Sessions SET ActiveExecutionId=NULL")]
    [InlineData("UPDATE Sessions SET State='Ambiguous'")]
    [InlineData("UPDATE Executions SET EndedAtUtc='2026-10-03T00:00:00Z'")]
    [InlineData("UPDATE Executions SET State='Ambiguous'")]
    [InlineData("UPDATE ProjectLocks SET ReleasedAtUtc='2026-10-03T00:00:00Z'")]
    public async Task ChangedOwnership_RefusesWaitingProjectionWithoutAnEvent(string mutation)
    {
        var (journal, entry, _) = await Ready(); await Execute(mutation);
        var state = await Scalar("SELECT State FROM Executions");
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetWaitingApprovalAsync(entry, true));
        Assert.Equal(state, await Scalar("SELECT State FROM Executions"));
        Assert.Equal("2", await Scalar("SELECT COUNT(*) FROM ExecutionEvents"));
    }

    [Fact]
    public async Task StaleTicket_CannotChangeAnotherExecution()
    {
        var (journal, entry, _) = await Ready();
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetWaitingApprovalAsync(entry with { ClientRequestId = "foreign-request" }, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SetWaitingApprovalAsync(entry with { NativeSessionId = "foreign-session" }, true));
        Assert.Equal("Running", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("2", await Scalar("SELECT COUNT(*) FROM ExecutionEvents"));
    }

    [Fact]
    public async Task UncertainPendingOutcome_RetainsWriterAndAccountSlot()
    {
        var (journal, entry, token) = await Ready();
        await journal.SetWaitingApprovalAsync(entry, true);
        await journal.CompleteAsync(entry, new TurnResult { SessionId = entry.NativeSessionId,
            Status = TurnResult.FailedStatus, OutputText = "", IsDeliveryUncertain = true });
        Assert.Equal("Ambiguous", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("Ambiguous", await Scalar("SELECT State FROM Sessions"));
        Assert.Null(await Scalar("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal(entry.ExecutionId, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        await Assert.ThrowsAsync<LLMWorkGUI.Domain.Entities.ProjectLockConflictException>(() => token.ReleaseAsync("unsafe release"));
        // Explicit synthetic teardown only; production ambiguous state never auto-releases.
        await Execute("UPDATE Executions SET State='Failed'; UPDATE Sessions SET State='Idle',ActiveExecutionId=NULL");
        await token.ReleaseAsync("fixture reconciled");
    }
    private async Task Execute(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private async Task<string?> Scalar(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(); return value is null or DBNull ? null : Convert.ToString(value);
    }
}
