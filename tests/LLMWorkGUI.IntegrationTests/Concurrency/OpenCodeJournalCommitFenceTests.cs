using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.OpenCode;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class OpenCodeJournalCommitFenceTests
{
    [Theory]
    [InlineData("confirm")]
    [InlineData("admit")]
    [InlineData("running")]
    [InlineData("approval")]
    [InlineData("terminal")]
    [InlineData("reconcile")]
    [InlineData("close")]
    [InlineData("acknowledge")]
    public async Task DisposalDuringSqliteMutationMustRollbackBeforeCommit(string phase)
    {
        using var database = new TestDatabase();
        await database.InitializeAsync();
        await database.SeedRouteChainAsync();
        using var guard = new ApplicationInstanceGuard(Path.Combine(database.Root, "instance"));
        Assert.True(guard.IsPrimarySupervisor);
        var factory = new DisposingFactory(database.Factory, guard);
        var journal = new SqliteOpenCodeExecutionJournal(factory, TimeProvider.System, guard);
        var route = Assert.Single(await journal.ListRoutesAsync());
        OpenCodeJournalEntry? entry = null;
        if (phase is not ("confirm" or "admit"))
        {
            entry = await journal.BeginAsync("project-1", database.GetWorkspacePath(), "native-session", route,
                "request-1", "fixture-hash", 17);
            if (phase is "running" or "approval")
                await database.InsertProjectLockAsync("lock-1", database.GetWorkspacePath(), entry.ExecutionId, applicationInstanceId: guard.InstanceId, processGeneration: 17);
            if (phase == "approval") await journal.MarkRunningAsync(entry);
            if (phase is "close" or "acknowledge")
            {
                await journal.CompleteAsync(entry, Completed(entry));
                await Execute(factory, "UPDATE Sessions SET State='Ambiguous',ReconciliationOutcome='Ambiguous' WHERE Backend='OpenCode'");
            }
        }
        var before = await Snapshot(factory);
        var mutation = phase == "confirm" ? "AFTER INSERT ON Sessions WHEN NEW.Backend='OpenCode'"
            : "AFTER INSERT ON ExecutionEvents WHEN NEW.EventKind='" + (phase switch
            {
                "admit" => "OpenCodeAdmission", "running" => "OpenCodeDispatch", "approval" => "OpenCodeApproval",
                "terminal" => "OpenCodeTerminal", "reconcile" => "OpenCodeInterrupted", _ => "OpenCodeRecoveryAction"
            }) + "'";
        // A real SQLite callback disposes the real named-mutex owner after a mutation, before
        // the caller can commit. There is no fake guard counter or database reentry in the callback.
        await Execute(factory, "CREATE TRIGGER DisposeSupervisor " + mutation +
            " BEGIN SELECT dispose_supervisor(); END;");
        var recovery = new SqliteOpenCodeJournalRecoveryService(factory, guard, TimeProvider.System);
        Func<Task> operation = phase switch
        {
            "confirm" => async () => { await journal.ConfirmSessionAsync("project-1", database.GetWorkspacePath(), "native-session", route); },
            "admit" => async () => { await journal.BeginAsync("project-1", database.GetWorkspacePath(), "native-session", route, "request-1", "fixture-hash", 17); },
            "running" => () => journal.MarkRunningAsync(entry!),
            "approval" => () => journal.SetWaitingApprovalAsync(entry!, true),
            "terminal" => () => journal.CompleteAsync(entry!, Completed(entry!)),
            "reconcile" => async () => { await recovery.TryReconcileAsync(entry!.SessionId); },
            _ => async () => { await recovery.TryApplyActionAsync(entry!.SessionId,
                phase == "close" ? RecoveryAction.CloseSession : RecoveryAction.AcknowledgeAmbiguous); }
        };

        await Assert.ThrowsAsync<ObjectDisposedException>(operation);

        Assert.Equal(1, factory.Disposals);
        Assert.Equal(before, await Snapshot(factory));
        Assert.Throws<ObjectDisposedException>(guard.EnsureSupervisorPermitted);
    }

    private static TurnResult Completed(OpenCodeJournalEntry entry) => new()
        { SessionId = entry.NativeSessionId, Status = TurnResult.CompletedStatus, OutputText = string.Empty };

    private static async Task<string> Snapshot(ISqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*)||':'||COALESCE(MAX(State),'')||':'||COALESCE(MAX(ActiveExecutionId),'') FROM Sessions WHERE Backend='OpenCode')
                ||'/'||(SELECT COUNT(*)||':'||COALESCE(MAX(State),'')||':'||COALESCE(MAX(EndedAtUtc),'') FROM Executions)
                ||'/'||(SELECT COUNT(*) FROM ClientRequests)||'/'||(SELECT COUNT(*) FROM ExecutionEvents);
            """;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task Execute(ISqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class DisposingFactory(ISqliteConnectionFactory inner, ApplicationInstanceGuard guard) : ISqliteConnectionFactory
    {
        public int Disposals { get; private set; }
        public string DatabasePath => inner.DatabasePath;
        public string ConnectionString => inner.ConnectionString;
        public SqliteConnection CreateConnection()
        {
            var connection = inner.CreateConnection();
            connection.CreateFunction("dispose_supervisor", () => { Disposals++; guard.Dispose(); return 1; });
            return connection;
        }
        public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = CreateConnection();
            try { await connection.OpenAsync(cancellationToken); return connection; }
            catch { await connection.DisposeAsync(); throw; }
        }
    }
}
