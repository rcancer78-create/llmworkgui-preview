using LLMWorkGUI.Application.Cli;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Reconciliation;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

public sealed partial class MirasimEgressTests
{
    [Fact]
    public async Task CompletedTurnClosesItsDurablePerExecutionSessionBeforeRestart()
    {
        using var f = await Fixture.Create();
        await f.CreateSession();
        var result = await f.Service.ExecuteTurnAsync(f.Request);
        Assert.Equal(MirasimTurnStatus.Completed, result.Status);
        Assert.Equal("Closed", await f.Sql("SELECT State FROM Sessions"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Sessions WHERE ActiveExecutionId IS NOT NULL"));
        Assert.Empty(await StartupReconciliation(f).ReconcileAllActiveAsync());
        Assert.Equal("Succeeded", await f.Sql("SELECT State FROM Executions"));
    }

    [Fact]
    public async Task LegacyCompletedActiveMirasimSessionDoesNotCrashStartupOrRewriteSuccess()
    {
        using var f = await Fixture.Create();
        await f.CreateSession();
        Assert.Equal(MirasimTurnStatus.Completed, (await f.Service.ExecuteTurnAsync(f.Request)).Status);
        await f.Sql("UPDATE Sessions SET State='Active'");
        Assert.Single(await StartupReconciliation(f).ReconcileAllActiveAsync());
        Assert.Equal("Succeeded", await f.Sql("SELECT State FROM Executions"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Sessions WHERE State='Active'"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task InterruptedMirasimTurnRestartRetainsUncertainDeliveryAndWriterOwnership()
    {
        using var f = await Fixture.Create();
        await f.CreateSession();
        f.Handler.KeepTurnRunning = true;
        var result = await f.Service.ExecuteTurnAsync(f.Request);
        Assert.False(result.IsTerminal);
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Single(await StartupReconciliation(f).ReconcileAllActiveAsync());
        Assert.Equal("Ambiguous", await f.Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM Sessions WHERE ActiveExecutionId IS NOT NULL"));
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(2, f.Handler.Requests.Count); // creation + original turn; no restart resend
        // The fixture's lock disposer deliberately refuses to release an uncertain real writer.
        // After asserting that invariant, supply terminal evidence only for owned fixture cleanup.
        await f.Sql("UPDATE Executions SET State='Failed'; UPDATE Sessions SET State='Closed',ActiveExecutionId=NULL");
    }

    private static ReconciliationService StartupReconciliation(Fixture f) => new(
        new SqliteSessionRepository(f.Factory), new SqliteExecutionRepository(f.Factory, new LLMWorkGUI.Infrastructure.Security.SensitiveDataFilter()),
        new SqliteProjectRepository(f.Factory), new SqliteProjectLockRepository(f.Factory),
        new ReconciliationProbe(new NoExecutableInspection()), new RecoveryMatrix(),
        connectionFactory: f.Factory, instanceGuard: f.Guard);

    private sealed class NoExecutableInspection : ICliExecutableLocator
    {
        public Task<string?> LocateAsync(string executableName, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Mirasim remote sessions have no CLI executable reattachment proof.");
    }
}
