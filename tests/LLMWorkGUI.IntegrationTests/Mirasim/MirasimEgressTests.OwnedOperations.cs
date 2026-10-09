using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Backends.Abstractions.Mirasim;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Mirasim;

public sealed partial class MirasimEgressTests
{
    [Theory]
    [InlineData("watch", false)]
    [InlineData("watch", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("reconcile", false)]
    [InlineData("reconcile", true)]
    public async Task ForeignSessionOrTurnCannotReadOrCancelAnOwnedExecution(string operation, bool foreignSession)
    {
        using var f = await RunningFixture();
        await AssertOwnedOperationRefused(f, operation, foreignSession ? "foreign-session" : "session-fixture",
            foreignSession ? "turn-fixture" : "foreign-turn");
    }

    [Theory]
    [InlineData("watch", "session")]
    [InlineData("cancel", "session")]
    [InlineData("reconcile", "session")]
    [InlineData("watch", "root")]
    [InlineData("cancel", "root")]
    [InlineData("reconcile", "root")]
    [InlineData("watch", "prompt")]
    [InlineData("cancel", "prompt")]
    [InlineData("reconcile", "prompt")]
    [InlineData("watch", "writer")]
    [InlineData("cancel", "writer")]
    [InlineData("reconcile", "writer")]
    public async Task StoredOwnershipMutationRefusesBeforeControlTransport(string operation, string changed)
    {
        using var f = await RunningFixture();
        await f.Sql(changed switch
        {
            "session" => "UPDATE Sessions SET NativeSessionId='foreign-session'",
            "root" => "UPDATE Sessions SET WorkspaceRootPath='D:/foreign-workspace'",
            "prompt" => "UPDATE ClientRequests SET PromptHash='foreign-request'",
            _ => "UPDATE ProjectLocks SET ProcessGeneration=ProcessGeneration+1 WHERE ReleasedAtUtc IS NULL"
        });
        await AssertOwnedOperationRefused(f, operation, "session-fixture", "turn-fixture");
    }

    [Theory]
    [InlineData("watch")]
    [InlineData("cancel")]
    [InlineData("reconcile")]
    public async Task ReplacementCredentialCannotRedirectAnOwnedControlOperation(string operation)
    {
        using var f = await RunningFixture("synthetic-original-token");
        await AssertOwnedOperationRefused(f, operation, "session-fixture", "turn-fixture", "synthetic-replacement-token");
    }

    [Theory]
    [InlineData("watch")]
    [InlineData("cancel")]
    [InlineData("reconcile")]
    public async Task DisposedPrimaryAuthorityRefusesControlTransportWithoutReleasingOwnership(string operation)
    {
        using var f = await RunningFixture();
        f.Guard.Dispose();
        await AssertOwnedOperationRefused(f, operation, "session-fixture", "turn-fixture");
    }

    [Theory]
    [InlineData("cancel", "class")]
    [InlineData("reconcile", "class")]
    [InlineData("cancel", "disabled")]
    [InlineData("reconcile", "disabled")]
    [InlineData("cancel", "route")]
    [InlineData("reconcile", "route")]
    public async Task OwnedCleanupRemainsAvailableAfterNewTurnPolicyIsRevoked(string operation, string change)
    {
        using var f = await RunningFixture("synthetic-original-token");
        await f.Change(change);
        var result = operation == "cancel"
            ? await f.Service.CancelTurnAsync("session-fixture", "turn-fixture", f.Request.AuthToken)
            : await f.Service.ReconcileTurnAsync("session-fixture", "turn-fixture", f.Request.AuthToken);
        Assert.True(result.IsTerminal);
        Assert.Single(f.Handler.Requests);
        Assert.Equal("Bearer synthetic-original-token", Assert.Single(f.Handler.AuthorizationHeaders));
        Assert.DoesNotContain(f.Request.Prompt, f.Handler.Requests[0]);
        Assert.Equal("Succeeded", await f.Sql("SELECT State FROM Executions"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(0L, await f.Sql("SELECT COUNT(*) FROM Sessions WHERE ActiveExecutionId IS NOT NULL"));
    }

    [Fact]
    public async Task OwnedWatchReadsOnlyItsStreamAndDoesNotReleaseWriterOwnership()
    {
        using var f = await RunningFixture("synthetic-original-token");
        var events = new List<MirasimStreamEvent>();
        await foreach (var item in f.Service.WatchTurnAsync("session-fixture", "turn-fixture", f.Request.AuthToken)) events.Add(item);
        Assert.True(Assert.Single(events).IsTerminal);
        Assert.Single(f.Handler.Requests);
        Assert.Equal("Bearer synthetic-original-token", Assert.Single(f.Handler.AuthorizationHeaders));
        Assert.Equal("Running", await f.Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task StoredIdentityChangedAfterTerminalResponseCannotCommitOrReleaseTheWriter()
    {
        using var f = await RunningFixture();
        // The response is terminal, but the durable tuple no longer identifies this admission.
        f.Handler.BeforeResponse = () => f.Sql("UPDATE Sessions SET NativeSessionId='foreign-session'");
        var result = await f.Service.ReconcileTurnAsync("session-fixture", "turn-fixture");
        Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
        Assert.Single(f.Handler.Requests);
        Assert.Equal("Running", await f.Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    private static async Task<Fixture> RunningFixture(string? authToken = null)
    {
        var f = await Fixture.Create();
        try
        {
            f.Request = f.Request with { AuthToken = authToken };
            await f.CreateSession(); f.Handler.KeepTurnRunning = true;
            Assert.Equal(MirasimTurnStatus.Running, (await f.Service.ExecuteTurnAsync(f.Request)).Status);
            f.Handler.Requests.Clear(); f.Handler.AuthorizationHeaders.Clear(); return f;
        }
        catch { f.Dispose(); throw; }
    }

    private static async Task AssertOwnedOperationRefused(Fixture f, string operation, string session, string turn, string? authToken = null)
    {
        if (operation == "watch")
        {
            await Assert.ThrowsAsync<MirasimEgressPolicyException>(async () =>
            {
                await foreach (var _ in f.Service.WatchTurnAsync(session, turn, authToken)) { }
            });
        }
        else
        {
            var result = operation == "cancel" ? await f.Service.CancelTurnAsync(session, turn, authToken)
                : await f.Service.ReconcileTurnAsync(session, turn, authToken);
            Assert.Equal(MirasimTurnStatus.Ambiguous, result.Status);
            Assert.False(result.IsTerminal);
        }
        Assert.Empty(f.Handler.Requests);
        Assert.Equal("Running", await f.Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await f.Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }
}
