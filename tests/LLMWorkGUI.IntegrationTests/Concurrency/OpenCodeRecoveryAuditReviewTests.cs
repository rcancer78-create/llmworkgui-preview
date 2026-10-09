using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.OpenCode;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class OpenCodeRecoveryAuditReviewTests
{
    [Fact]
    public async Task AcknowledgementAuditMustDescribeRetainedAmbiguousStateInsteadOfClosure()
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        using var guard = new ApplicationInstanceGuard(Path.Combine(database.Root, "instance"));
        var journal = new SqliteOpenCodeExecutionJournal(database.Factory, TimeProvider.System, guard);
        var entry = await journal.BeginAsync("project-1", database.GetWorkspacePath(), "native-session",
            Assert.Single(await journal.ListRoutesAsync()), "request-1", "fixture-hash", 17);
        await journal.CompleteAsync(entry, new TurnResult
            { SessionId = entry.NativeSessionId, Status = TurnResult.CompletedStatus, OutputText = string.Empty });
        await using (var connection = await database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Sessions SET State='Ambiguous',ReconciliationOutcome='Ambiguous' WHERE Backend='OpenCode'";
            await command.ExecuteNonQueryAsync();
        }
        var result = await new SqliteOpenCodeJournalRecoveryService(database.Factory, guard, TimeProvider.System)
            .TryApplyActionAsync(entry.SessionId, RecoveryAction.AcknowledgeAmbiguous);

        Assert.NotNull(result);
        Assert.Equal(SessionState.Ambiguous, result.SessionState);
        Assert.Null(result.ReleasedLockId);
        Assert.DoesNotContain("Закрыта", result.AuditDetails, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("неопредел", result.AuditDetails, StringComparison.OrdinalIgnoreCase);
        await using var verify = await database.Factory.OpenConnectionAsync();
        await using var state = verify.CreateCommand();
        state.CommandText = "SELECT State FROM Sessions WHERE Id=$id";
        state.Parameters.AddWithValue("$id", entry.SessionId);
        Assert.Equal("Ambiguous", await state.ExecuteScalarAsync());
    }
}
