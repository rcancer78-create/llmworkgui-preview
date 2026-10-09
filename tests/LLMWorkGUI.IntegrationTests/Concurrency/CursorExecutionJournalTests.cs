using LLMWorkGUI.Backends.Abstractions.CursorAcp;
using LLMWorkGUI.Backends.CursorAcp;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.CursorAcp;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class CursorExecutionJournalTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private ApplicationInstanceGuard? _guard;
    public void Dispose() { _guard?.Dispose(); _db.Dispose(); }
    private async Task<SqliteCursorAcpExecutionJournal> Ready()
    {
        await _db.InitializeAsync(); await _db.SeedRouteChainAsync();
        await Execute("UPDATE ProviderProfiles SET Backend='CursorAcp'; UPDATE Models SET Backend='CursorAcp',ProviderModelId='native-model'; UPDATE Routes SET Backend='CursorAcp';");
        _guard = new ApplicationInstanceGuard(Path.Combine(_db.Root, "instance"));
        return new(_db.Factory, TimeProvider.System, _guard);
    }
    private Task<CursorAcpJournalEntry> Begin(SqliteCursorAcpExecutionJournal journal, CursorAcpStoredRoute route, string request = "request-1", string native = "native-session") =>
        journal.BeginAsync("project-1", _db.GetWorkspacePath(), native, route, "ask", request, "fixture-prompt-hash");
    private async Task<string?> Scalar(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(); return result is null or DBNull ? null : Convert.ToString(result);
    }
    private async Task Execute(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static CursorAcpTurnResult Result(CursorAcpJournalEntry entry, CursorAcpTurnOutcome outcome, bool retained = false) =>
        new() { SessionId = entry.NativeSessionId, ClientRequestId = entry.ClientRequestId, Outcome = outcome, LockRetained = retained };

    [Fact]
    public async Task Admission_AllowsRealForeignKeyLock_AndCompletionLeavesObservedRouteUnknown()
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var entry = await Begin(journal, route);
        Assert.NotEqual(entry.NativeSessionId, entry.ExecutionId);
        var locks = new CheckoutLockService(new SqliteProjectLockRepository(_db.Factory), _guard!, TimeProvider.System);
        await using var token = await locks.AcquireWriterLockAsync("project-1", _db.GetWorkspacePath(), entry.ExecutionId, 0);
        Assert.True(token.IsHeld);
        await token.ReleaseAsync("confirmed terminal");
        await journal.CompleteAsync(entry, Result(entry, CursorAcpTurnOutcome.Succeeded));
        Assert.Equal("Succeeded", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("Idle", await Scalar("SELECT State FROM Sessions"));
        Assert.Null(await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Null(await Scalar("SELECT ObservedRouteId FROM Executions"));
        Assert.Null(await Scalar("SELECT ObservedRouteId FROM ClientRequests"));
        var next = await Begin(journal, route, "request-2");
        Assert.Equal(entry.SessionId, next.SessionId); Assert.NotEqual(entry.ExecutionId, next.ExecutionId);
    }

    [Fact]
    public async Task Admission_RecordsOnlyTheSelectedRouteDecision()
    {
        var journal = await Ready();
        var selected = Assert.Single(await journal.ListRoutesAsync());
        const string stamp = "2026-01-01T00:00:00.0000000Z";
        await Execute("INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ('provider-2', 'Other', 'CursorAcp', 'PrivateSource', 1, '" + stamp + "', '" + stamp + "')");
        await Execute("INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, CreatedAtUtc, UpdatedAtUtc) VALUES ('account-2', 'provider-2', 'Other account', 'Valid', 'Healthy', '" + stamp + "', '" + stamp + "')");
        await Execute("INSERT INTO Models (Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState, Provenance, IsEnabled, Health, DiscoveredAtUtc) VALUES ('model-2', 'CursorAcp', 'provider-2', 'native-other', 'Other model', 'Supported', 'ProviderReported', 1, 'Healthy', '" + stamp + "')");
        await Execute("INSERT INTO Routes (Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, IsEnabled, Health, ManualPriority, CreatedAtUtc, UpdatedAtUtc) VALUES ('route-2', 'CursorAcp', 'provider-2', 'account-2', 'model-2', 'PrivateSource', 1, 'Healthy', 1, '" + stamp + "', '" + stamp + "')");
        var other = Assert.Single(await journal.ListRoutesAsync(), route => route.Id == "route-2");
        Assert.Null(await Scalar("SELECT DispatchNativeModelId FROM Executions WHERE Id='not-admitted'"));
        var entry = await Begin(journal, selected);
        Assert.Equal(selected.Id, await Scalar("SELECT RequestedRouteId FROM Executions WHERE Id='" + entry.ExecutionId + "'"));
        Assert.Equal(selected.ProviderProfileId, await Scalar("SELECT DispatchProviderProfileId FROM Executions WHERE Id='" + entry.ExecutionId + "'"));
        Assert.Equal(selected.AccountId, await Scalar("SELECT DispatchAccountId FROM Executions WHERE Id='" + entry.ExecutionId + "'"));
        Assert.Equal(selected.NativeModelId, await Scalar("SELECT DispatchNativeModelId FROM Executions WHERE Id='" + entry.ExecutionId + "'"));
        Assert.NotEqual(other.Id, await Scalar("SELECT RequestedRouteId FROM Executions WHERE Id='" + entry.ExecutionId + "'"));
        Assert.NotEqual(other.ProviderProfileId, await Scalar("SELECT DispatchProviderProfileId FROM Executions WHERE Id='" + entry.ExecutionId + "'"));
        Assert.NotEqual(other.NativeModelId, await Scalar("SELECT DispatchNativeModelId FROM Executions WHERE Id='" + entry.ExecutionId + "'"));
        await Execute("UPDATE Models SET ProviderModelId='rewritten-after-admission'");
        Assert.Equal(selected.NativeModelId, await Scalar("SELECT DispatchNativeModelId FROM Executions WHERE Id='" + entry.ExecutionId + "'"));
        Assert.Null(await Scalar("SELECT ObservedRouteId FROM Executions WHERE Id='" + entry.ExecutionId + "'"));
    }

    [Theory]
    [InlineData("UPDATE Accounts SET IsEnabled=0")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'")]
    [InlineData("UPDATE Accounts SET CooldownUntilUtc='2999-01-01T00:00:00Z'")]
    [InlineData("UPDATE Routes SET ExecutionMode='agent'")]
    [InlineData("UPDATE Models SET ProviderModelId='changed'")]
    [InlineData("UPDATE Routes SET ReasoningEffort='high'")]
    [InlineData("UPDATE Projects SET DataClassification='Restricted'")]
    public async Task ChangedOrIneligibleRoute_RefusesBeforeAnyJournalRows(string mutation)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        await Execute(mutation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Begin(journal, route));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM Executions"));
    }

    [Fact]
    public async Task DuplicateClientRequest_RollsBackNewSessionAndExecution()
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var entry = await Begin(journal, route);
        await journal.CompleteAsync(entry, Result(entry, CursorAcpTurnOutcome.Succeeded));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => Begin(journal, route, native: "other-native"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Executions"));
    }

    [Theory]
    [InlineData(CursorAcpTurnOutcome.Ambiguous)]
    [InlineData(CursorAcpTurnOutcome.Orphaned)]
    public async Task UncertainOutcome_PreservesActiveExecutionAndBlocksAnotherTurn(CursorAcpTurnOutcome outcome)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var entry = await Begin(journal, route);
        await journal.CompleteAsync(entry, Result(entry, outcome, true));
        Assert.Equal("Ambiguous", await Scalar("SELECT State FROM Executions"));
        Assert.Equal(entry.ExecutionId, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Null(await Scalar("SELECT EndedAtUtc FROM Executions"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Begin(journal, route, "request-2"));
    }

    [Fact]
    public async Task BusySession_AndMismatchedCompletion_AreRefused()
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var entry = await Begin(journal, route);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Begin(journal, route, "request-2"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.CompleteAsync(entry,
            Result(entry, CursorAcpTurnOutcome.Succeeded) with { ClientRequestId = "wrong" }));
        Assert.Equal("SessionConfirmed", await Scalar("SELECT State FROM Executions"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAdmission_OnlyOneExecutionBecomesActive(bool existingIdleSession)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        // Leave account capacity for both contenders: this test must exercise the
        // session admission guard rather than pass because the account is full.
        await Execute("UPDATE Accounts SET MaxConcurrentExecutions=2");
        if (existingIdleSession)
        {
            var initial = await Begin(journal, route, "initial");
            await journal.CompleteAsync(initial, Result(initial, CursorAcpTurnOutcome.Succeeded));
        }

        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            await start.Task;
            try { return (Entry: await Begin(journal, route, $"concurrent-{index}"), Error: (Exception?)null); }
            catch (Exception error) { return (Entry: (CursorAcpJournalEntry?)null, Error: error); }
        })).ToArray();
        start.SetResult(true);
        var results = await Task.WhenAll(attempts);
        var winner = Assert.Single(results.Where(result => result.Entry is not null)).Entry!;
        Assert.IsType<InvalidOperationException>(Assert.Single(results.Where(result => result.Error is not null)).Error);
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(existingIdleSession ? "2" : "1", await Scalar("SELECT COUNT(*) FROM Executions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Executions WHERE State='SessionConfirmed'"));
        Assert.Equal(winner.ExecutionId, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Equal(winner.ClientRequestId, await Scalar("SELECT ClientRequestId FROM Executions WHERE State='SessionConfirmed'"));
        Assert.Equal(existingIdleSession ? "2" : "1", await Scalar("SELECT COUNT(*) FROM ClientRequests"));
    }

    [Fact]
    public async Task ConcurrentAdmission_AcrossProjectsAndRoutesHonorsAccountLimit()
    {
        var journal = await Ready();
        await Execute("""
            UPDATE Accounts SET MaxConcurrentExecutions=2;
            INSERT INTO Projects (Id,DisplayName,RootPath,DataClassification,CreatedAtUtc,UpdatedAtUtc)
                SELECT 'project-2','Second project',RootPath || '-second','PrivateSource',CreatedAtUtc,UpdatedAtUtc FROM Projects;
            INSERT INTO Routes (Id,Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,IsEnabled,Health,CreatedAtUtc,UpdatedAtUtc)
                SELECT 'route-2',Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,IsEnabled,Health,CreatedAtUtc,UpdatedAtUtc FROM Routes;
            """);
        var routes = await journal.ListRoutesAsync();
        Assert.Equal(2, routes.Count);
        var secondRoot = (await Scalar("SELECT RootPath FROM Projects WHERE Id='project-2'"))!;
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 6).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var second = index % 2 == 1;
            try
            {
                var entry = await journal.BeginAsync(second ? "project-2" : "project-1",
                    second ? secondRoot : _db.GetWorkspacePath(), $"native-{index}", routes[index % 2],
                    "ask", $"request-{index}", "fixture-hash");
                return (Entry: entry, Error: (Exception?)null);
            }
            catch (Exception error) { return (Entry: (CursorAcpJournalEntry?)null, Error: error); }
        })).ToArray();
        start.SetResult(true);
        var results = await Task.WhenAll(attempts);
        Assert.Equal(2, results.Count(result => result.Entry is not null));
        foreach (var result in results.Where(result => result.Error is not null))
            Assert.Contains("лимит", Assert.IsType<InvalidOperationException>(result.Error).Message);
        Assert.Equal("2", await Scalar("SELECT COUNT(*) FROM Executions"));
        Assert.Equal("2", await Scalar("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal("2", await Scalar("SELECT COUNT(*) FROM ClientRequests"));
    }

    [Theory]
    [InlineData(CursorAcpTurnOutcome.Succeeded)]
    [InlineData(CursorAcpTurnOutcome.Cancelled)]
    [InlineData(CursorAcpTurnOutcome.TimedOut)]
    [InlineData(CursorAcpTurnOutcome.Failed)]
    public async Task ConfirmedCompletion_ReleasesAccountSlot(CursorAcpTurnOutcome outcome)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var first = await Begin(journal, route);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Begin(journal, route, "request-2", "native-2"));
        Assert.Contains("лимит", refusal.Message);
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ClientRequests"));
        await journal.CompleteAsync(first, Result(first, outcome));
        var next = await Begin(journal, route, "request-2", "native-2");
        Assert.NotEqual(first.SessionId, next.SessionId);
        Assert.Equal("2", await Scalar("SELECT COUNT(*) FROM Executions"));
    }

    [Theory]
    [InlineData(CursorAcpTurnOutcome.Ambiguous)]
    [InlineData(CursorAcpTurnOutcome.Orphaned)]
    [InlineData(CursorAcpTurnOutcome.Succeeded)]
    public async Task UncertainOrRetainedOutcome_KeepsAccountSlotAcrossSessions(CursorAcpTurnOutcome outcome)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var first = await Begin(journal, route);
        await journal.CompleteAsync(first, Result(first, outcome, retained: true));
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Begin(journal, route, "request-2", "native-2"));
        Assert.Contains("лимит", refusal.Message);
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ClientRequests"));
    }

    [Fact]
    public async Task Admission_RechecksReducedLimitAfterRouteListing()
    {
        var journal = await Ready();
        await Execute("UPDATE Accounts SET MaxConcurrentExecutions=2");
        var route = Assert.Single(await journal.ListRoutesAsync());
        await Begin(journal, route);
        await Execute("UPDATE Accounts SET MaxConcurrentExecutions=1");
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Begin(journal, route, "request-2", "native-2"));
        Assert.Contains("лимит", refusal.Message);
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Executions"));
    }

    [Fact]
    public async Task Admission_OtherAccountHasIndependentCapacity()
    {
        var journal = await Ready();
        await Execute("""
            INSERT INTO Accounts (Id,ProviderProfileId,DisplayName,AuthState,Health,CreatedAtUtc,UpdatedAtUtc)
                SELECT 'account-2',ProviderProfileId,'Other account',AuthState,Health,CreatedAtUtc,UpdatedAtUtc FROM Accounts;
            INSERT INTO Routes (Id,Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,IsEnabled,Health,CreatedAtUtc,UpdatedAtUtc)
                SELECT 'route-2',Backend,ProviderProfileId,'account-2',ModelId,MaxDataClass,IsEnabled,Health,CreatedAtUtc,UpdatedAtUtc FROM Routes;
            """);
        var routes = await journal.ListRoutesAsync();
        await Begin(journal, routes.Single(route => route.AccountId == "account-1"));
        await Begin(journal, routes.Single(route => route.AccountId == "account-2"), "request-2", "native-2");
        Assert.Equal("2", await Scalar("SELECT COUNT(*) FROM Executions"));
    }

    [Theory]
    [InlineData("UPDATE Executions SET EndedAtUtc=NULL")]
    [InlineData("UPDATE Executions SET State='Ambiguous'")]
    [InlineData("UPDATE Sessions SET ActiveExecutionId=(SELECT Id FROM Executions)")]
    public async Task IncompletelyReconciledExecution_StillConsumesAccountCapacity(string mutation)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var first = await Begin(journal, route);
        await journal.CompleteAsync(first, Result(first, CursorAcpTurnOutcome.Succeeded));
        await Execute(mutation);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Begin(journal, route, "request-2", "native-2"));
        Assert.Contains("лимит", refusal.Message);
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Executions"));
    }

    [Fact]
    public async Task UnreleasedLock_StillConsumesCapacityAfterTerminalJournalUpdate()
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var first = await Begin(journal, route);
        var locks = new CheckoutLockService(new SqliteProjectLockRepository(_db.Factory), _guard!, TimeProvider.System);
        await using var token = await locks.AcquireWriterLockAsync("project-1", _db.GetWorkspacePath(), first.ExecutionId, 0);
        await journal.CompleteAsync(first, Result(first, CursorAcpTurnOutcome.Succeeded));
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Begin(journal, route, "request-2", "native-2"));
        Assert.Contains("лимит", refusal.Message);
        await token.ReleaseAsync("reconciled");
        await Begin(journal, route, "request-2", "native-2");
    }
}
