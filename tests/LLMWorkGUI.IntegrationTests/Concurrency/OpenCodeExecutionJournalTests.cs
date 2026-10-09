using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Data;
using LLMWorkGUI.Infrastructure.OpenCode;
using LLMWorkGUI.Infrastructure.Repositories;
using Xunit;
using LLMWorkGUI.Application.Concurrency;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class OpenCodeExecutionJournalTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private ApplicationInstanceGuard? _guard;
    public void Dispose() { _guard?.Dispose(); _db.Dispose(); }
    private async Task<SqliteOpenCodeExecutionJournal> Ready()
    {
        await _db.InitializeAsync();
        await _db.SeedRouteChainAsync();
        await Execute("UPDATE Models SET ProviderModelId='provider/native-model'");
        _guard = new ApplicationInstanceGuard(Path.Combine(_db.Root, "instance"));
        return new(_db.Factory, TimeProvider.System, _guard);
    }
    private Task<OpenCodeJournalEntry> Begin(SqliteOpenCodeExecutionJournal journal, OpenCodeStoredRoute route,
        string request = "request-1", string native = "native-session") => journal.BeginAsync(
            "project-1", _db.GetWorkspacePath(), native, route, request, "fixture-prompt-hash", 17);
    private static TurnResult Result(OpenCodeJournalEntry entry, string status = TurnResult.CompletedStatus, bool uncertain = false) =>
        new() { SessionId = entry.NativeSessionId, Status = status, OutputText = "", IsDeliveryUncertain = uncertain };

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UnknownProcessGenerationCannotReserveAdmission(long generation)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => journal.BeginAsync("project-1", _db.GetWorkspacePath(),
            "native-session", route, "request-1", "fixture-hash", generation));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM Executions"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ClientRequests"));
        var admitted = await Begin(journal, route);
        Assert.Equal(17, admitted.ProcessGeneration);
        Assert.Equal("17", await Scalar("SELECT json_extract(NormalizedRedactedPayloadJson,'$.processGeneration') FROM ExecutionEvents WHERE EventKind='OpenCodeAdmission'"));
    }

    [Theory]
    [InlineData(18, false)]
    [InlineData(17, true)]
    public async Task DispatchReservationCannotReplaceAdmittedGenerationOrSupervisor(long lockGeneration, bool foreignOwner)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var admitted = await Begin(journal, route);
        await _db.InsertProjectLockAsync("lock-1", _db.GetWorkspacePath(), admitted.ExecutionId,
            applicationInstanceId: foreignOwner ? "foreign-owner" : _guard!.InstanceId, processGeneration: lockGeneration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MarkRunningAsync(admitted with { ProcessGeneration = lockGeneration }));
        Assert.Equal("SessionConfirmed", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeDispatch'"));
    }

    [Fact]
    public async Task Confirmation_PersistsDistinctLocalSession_WithoutExecutionOrAccountReservation()
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var local = await journal.ConfirmSessionAsync("project-1", _db.GetWorkspacePath(), "native-session", route);
        Assert.NotEqual("native-session", local);
        Assert.Equal(local, await Scalar("SELECT Id FROM Sessions"));
        Assert.Equal("Idle", await Scalar("SELECT State FROM Sessions"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM Executions"));
        var entry = await Begin(journal, route);
        Assert.Equal(local, entry.SessionId);
        Assert.Equal("fixture-prompt-hash", await Scalar("SELECT PromptHash FROM ClientRequests"));
        Assert.Equal(route.Id, await Scalar("SELECT RequestedRouteId FROM Executions"));
        Assert.Equal(entry.ExecutionId, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
    }

    [Fact]
    public async Task Admission_RecordsOnlyTheDispatchedRouteDecision()
    {
        var journal = await Ready();
        var dispatched = Assert.Single(await journal.ListRoutesAsync());
        await SeedOtherRouteAsync();
        var other = Assert.Single(await journal.ListRoutesAsync(), route => route.Id == "route-2");
        Assert.Null(await journal.ReadDispatchDecisionAsync("not-admitted"));
        var entry = await Begin(journal, dispatched);
        var decision = await journal.ReadDispatchDecisionAsync(entry.ExecutionId);
        Assert.NotNull(decision);
        Assert.Equal(dispatched.Id, decision.RequestedRouteId);
        Assert.Equal(dispatched.ProviderProfileId, decision.ProviderProfileId);
        Assert.Equal(dispatched.AccountId, decision.AccountId);
        Assert.Equal(dispatched.NativeModelId, decision.NativeModelId);
        Assert.NotEqual(other.Id, decision.RequestedRouteId);
        Assert.NotEqual(other.ProviderProfileId, decision.ProviderProfileId);
        Assert.NotEqual(other.NativeModelId, decision.NativeModelId);
        await Execute("UPDATE Models SET ProviderModelId='rewritten-after-admission'");
        var reread = await journal.ReadDispatchDecisionAsync(entry.ExecutionId);
        Assert.Equal(dispatched.NativeModelId, reread!.NativeModelId);
        Assert.Null(await Scalar("SELECT ObservedRouteId FROM Executions WHERE Id='" + entry.ExecutionId + "'"));
    }

    [Fact]
    public async Task OldSchema_GainsDispatchColumns_AndReadsBackTheDecision()
    {
        var embedded = DatabaseMigrator.LoadEmbeddedMigrations();
        await new DatabaseMigrator(_db.Factory, embedded.Where(m => m.Version < 28).ToArray()).MigrateAsync();
        Assert.Null(await Scalar("SELECT name FROM pragma_table_info('Executions') WHERE name='DispatchNativeModelId'"));
        await new DatabaseMigrator(_db.Factory).MigrateAsync();
        Assert.Equal("DispatchNativeModelId", await Scalar("SELECT name FROM pragma_table_info('Executions') WHERE name='DispatchNativeModelId'"));
        await _db.SeedRouteChainAsync();
        await Execute("UPDATE Models SET ProviderModelId='provider/native-model'");
        _guard = new ApplicationInstanceGuard(Path.Combine(_db.Root, "instance"));
        var journal = new SqliteOpenCodeExecutionJournal(_db.Factory, TimeProvider.System, _guard);
        var route = Assert.Single(await journal.ListRoutesAsync());
        Assert.Null(await journal.ReadDispatchDecisionAsync("not-admitted"));
        var entry = await Begin(journal, route);
        var decision = await journal.ReadDispatchDecisionAsync(entry.ExecutionId);
        Assert.Equal(route.Id, decision!.RequestedRouteId);
        Assert.Equal(route.ProviderProfileId, decision.ProviderProfileId);
        Assert.Equal(route.AccountId, decision.AccountId);
        Assert.Equal(route.NativeModelId, decision.NativeModelId);
    }

    [Theory]
    [InlineData("UPDATE Routes SET IsEnabled=0")]
    [InlineData("UPDATE Accounts SET IsEnabled=0")]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'")]
    [InlineData("UPDATE Accounts SET CooldownUntilUtc='2999-01-01T00:00:00Z'")]
    [InlineData("UPDATE Accounts SET DisabledUntilUtc='2999-01-01T00:00:00Z'")]
    [InlineData("UPDATE Routes SET Health='Quarantined'")]
    [InlineData("UPDATE Routes SET ExecutionMode='plan'")]
    [InlineData("UPDATE Routes SET ReasoningEffort='high'")]
    [InlineData("UPDATE Routes SET SpeedMode='fast'")]
    [InlineData("UPDATE Models SET ProviderModelId='changed'")]
    [InlineData("UPDATE Projects SET DataClassification='Restricted'")]
    [InlineData("UPDATE Routes SET MaxDataClass='PublicSource'")]
    [InlineData("UPDATE ProviderProfiles SET MaxDataClass='PublicSource'")]
    public async Task StaleOrIneligibleRoute_RefusesWithoutPartialAdmission(string mutation)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        await Execute(mutation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Begin(journal, route));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM Executions"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ClientRequests"));
    }

    [Fact]
    public async Task RootMismatch_AndRebindingNativeSession_AreRefused()
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.BeginAsync("project-1",
            _db.GetWorkspacePath("other"), "native-session", route, "request-1", "hash", 17));
        await journal.ConfirmSessionAsync("project-1", _db.GetWorkspacePath(), "native-session", route);
        await Execute("UPDATE Sessions SET ExecutionMode='plan'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Begin(journal, route));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM Executions"));
    }

    [Theory]
    [InlineData(TurnResult.CompletedStatus)]
    [InlineData(TurnResult.FailedStatus)]
    [InlineData(TurnResult.CancelledStatus)]
    public async Task ConfirmedTerminal_ReleasesAccountSlot_AndCannotBeCompletedTwice(string status)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var first = await Begin(journal, route);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Begin(journal, route, "second", "other-native"));
        await journal.CompleteAsync(first, Result(first, status));
        Assert.Equal("Idle", await Scalar("SELECT State FROM Sessions"));
        Assert.Null(await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.NotNull(await Scalar("SELECT EndedAtUtc FROM Executions"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.CompleteAsync(first, Result(first)));
        var second = await Begin(journal, route, "second", "other-native");
        Assert.NotEqual(first.ExecutionId, second.ExecutionId);
    }

    [Fact]
    public async Task ConfirmedTerminal_RecordsObservedRouteOnlyWhenReportedModelMatches()
    {
        var journal = await Ready();
        var route = Assert.Single(await journal.ListRoutesAsync());
        var matched = await Begin(journal, route);
        await journal.CompleteAsync(matched, Result(matched) with { ObservedModelId = "native-model", ObservedProviderId = "provider" });
        Assert.Equal(route.Id, await Scalar("SELECT ObservedRouteId FROM Executions WHERE Id='" + matched.ExecutionId + "'"));

        var different = await Begin(journal, route, "request-different");
        await journal.CompleteAsync(different, Result(different) with { ObservedModelId = "other-model" });
        Assert.Null(await Scalar("SELECT ObservedRouteId FROM Executions WHERE Id='" + different.ExecutionId + "'"));

        var absent = await Begin(journal, route, "request-absent", "native-absent");
        await journal.CompleteAsync(absent, Result(absent));
        Assert.Null(await Scalar("SELECT ObservedRouteId FROM Executions WHERE Id='" + absent.ExecutionId + "'"));
    }

    [Theory]
    [InlineData("provider", "native-model", true)]
    [InlineData("foreign", "native-model", false)]
    [InlineData(null, "native-model", false)]
    [InlineData(null, "provider/native-model", false)]
    [InlineData("foreign", "provider/native-model", false)]
    public async Task QualifiedNativeRouteRequiresExactObservedProviderAndModel(string? provider, string model, bool matches)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var entry = await Begin(journal, route);
        await journal.CompleteAsync(entry, Result(entry) with { ObservedModelId = model, ObservedProviderId = provider });
        Assert.Equal(matches ? route.Id : null, await Scalar("SELECT ObservedRouteId FROM Executions"));
    }

    [Fact]
    public async Task UncertainResult_KeepsWriterLockAndAccountSlot()
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var entry = await Begin(journal, route);
        var locks = new CheckoutLockService(new SqliteProjectLockRepository(_db.Factory), _guard!, TimeProvider.System);
        var token = await locks.AcquireWriterLockAsync("project-1", _db.GetWorkspacePath(), entry.ExecutionId, 0);
        await journal.CompleteAsync(entry, Result(entry, TurnResult.FailedStatus, true));
        Assert.Equal("Ambiguous", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("Ambiguous", await Scalar("SELECT State FROM Sessions"));
        Assert.Equal(entry.ExecutionId, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Null(await Scalar("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Begin(journal, route, "second", "other-native"));
        await Assert.ThrowsAsync<LLMWorkGUI.Domain.Entities.ProjectLockConflictException>(() => token.ReleaseAsync("unsafe release"));
        // Simulate explicit reconciliation only for fixture teardown.
        await Execute("UPDATE Executions SET State='Failed'; UPDATE Sessions SET State='Idle',ActiveExecutionId=NULL");
        await token.ReleaseAsync("fixture cleanup");
    }

    [Fact]
    public async Task ConfirmedTerminal_CommitsBeforeLockRelease_AndKeepsAccountSlotUntilRelease()
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var entry = await Begin(journal, route);
        var locks = new CheckoutLockService(new SqliteProjectLockRepository(_db.Factory), _guard!, TimeProvider.System);
        await using var token = await locks.AcquireWriterLockAsync("project-1", _db.GetWorkspacePath(), entry.ExecutionId, 0);
        await journal.CompleteAsync(entry, Result(entry));
        Assert.Equal("Succeeded", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("Idle", await Scalar("SELECT State FROM Sessions"));
        Assert.True(token.IsHeld);
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Begin(journal, route, "second", "other-native"));
        await token.ReleaseAsync("durable native terminal committed");
        Assert.False(token.IsHeld);
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.NotNull(await Begin(journal, route, "second", "other-native"));
    }

    [Fact]
    public async Task TerminalCommitFailure_RollsBackOutcomeAndPreservesWriterOwnership()
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var entry = await Begin(journal, route);
        var locks = new CheckoutLockService(new SqliteProjectLockRepository(_db.Factory), _guard!, TimeProvider.System);
        await using var token = await locks.AcquireWriterLockAsync("project-1", _db.GetWorkspacePath(), entry.ExecutionId, 17);
        await Execute("CREATE TRIGGER reject_terminal BEFORE INSERT ON ExecutionEvents WHEN NEW.EventKind='OpenCodeTerminal' BEGIN SELECT RAISE(ABORT,'terminal unavailable'); END");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => journal.CompleteAsync(entry, Result(entry)));
        Assert.Equal("SessionConfirmed", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("Active", await Scalar("SELECT State FROM Sessions"));
        Assert.Null(await Scalar("SELECT EndedAtUtc FROM Executions"));
        Assert.True(token.IsHeld);
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        await Execute("DROP TRIGGER reject_terminal");
        await journal.CompleteAsync(entry, Result(entry));
        await token.ReleaseAsync("fixture terminal committed");
    }

    [Fact]
    public async Task DuplicateRequest_AndWrongSessionCompletion_AreRefusedAtomically()
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        var entry = await Begin(journal, route);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.CompleteAsync(entry, Result(entry) with { SessionId = "wrong" }));
        await journal.CompleteAsync(entry, Result(entry));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => Begin(journal, route, native: "second"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Executions"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSameSession_AdmitsOne(bool existing)
    {
        var journal = await Ready(); var route = Assert.Single(await journal.ListRoutesAsync());
        await Execute("UPDATE Accounts SET MaxConcurrentExecutions=2");
        if (existing) await journal.ConfirmSessionAsync("project-1", _db.GetWorkspacePath(), "native-session", route);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
        {
            await start.Task;
            try { return (Entry: await Begin(journal, route, "request-" + i), Error: (Exception?)null); }
            catch (Exception error) { return (Entry: (OpenCodeJournalEntry?)null, Error: error); }
        })).ToArray();
        start.SetResult(true);
        var results = await Task.WhenAll(attempts);
        Assert.Single(results.Where(r => r.Entry is not null));
        Assert.IsType<InvalidOperationException>(Assert.Single(results.Where(r => r.Error is not null)).Error);
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Executions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM Sessions"));
    }

    [Fact]
    public async Task ConcurrentProjectsAndRoutes_ShareAtomicAccountLimit()
    {
        var journal = await Ready();
        await Execute("""
            UPDATE Accounts SET MaxConcurrentExecutions=2;
            INSERT INTO Projects (Id,DisplayName,RootPath,DataClassification,CreatedAtUtc,UpdatedAtUtc)
                SELECT 'project-2','Second',RootPath || '-second',DataClassification,CreatedAtUtc,UpdatedAtUtc FROM Projects;
            INSERT INTO Routes (Id,Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,IsEnabled,Health,CreatedAtUtc,UpdatedAtUtc)
                SELECT 'route-2',Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,IsEnabled,Health,CreatedAtUtc,UpdatedAtUtc FROM Routes;
            """);
        var routes = await journal.ListRoutesAsync(); var root = (await Scalar("SELECT RootPath FROM Projects WHERE Id='project-2'"))!;
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                var second = i % 2 == 1;
                return (Entry: await journal.BeginAsync(second ? "project-2" : "project-1", second ? root : _db.GetWorkspacePath(),
                    "native-" + i, routes[i % 2], "request-" + i, "hash", 17), Error: (Exception?)null);
            }
            catch (Exception error) { return (Entry: (OpenCodeJournalEntry?)null, Error: error); }
        })).ToArray();
        start.SetResult(true);
        var results = await Task.WhenAll(attempts);
        Assert.Equal(2, results.Count(r => r.Entry is not null));
        foreach (var failure in results.Where(r => r.Error is not null)) Assert.Contains("лимит", Assert.IsType<InvalidOperationException>(failure.Error).Message);
        Assert.Equal("2", await Scalar("SELECT COUNT(*) FROM ClientRequests"));
    }

    [Fact]
    public async Task SecondaryInstance_CannotConfirmOrBegin()
    {
        var primary = await Ready(); var route = Assert.Single(await primary.ListRoutesAsync());
        using var secondaryGuard = new ViewOnlyGuard();
        var secondary = new SqliteOpenCodeExecutionJournal(_db.Factory, TimeProvider.System, secondaryGuard);
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => secondary.ConfirmSessionAsync("project-1", _db.GetWorkspacePath(), "native", route));
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => Begin(secondary, route));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM Sessions"));
    }
    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "secondary-fixture";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new SecondaryInstanceReadOnlyException("View-only fixture");
        public void Dispose() { }
    }
    private async Task SeedOtherRouteAsync()
    {
        const string stamp = "2026-01-01T00:00:00.0000000Z";
        await Execute("INSERT INTO ProviderProfiles (Id, DisplayName, Backend, MaxDataClass, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ('provider-2', 'Other', 'OpenCode', 'PrivateSource', 1, '" + stamp + "', '" + stamp + "')");
        await Execute("INSERT INTO Accounts (Id, ProviderProfileId, DisplayName, AuthState, Health, CreatedAtUtc, UpdatedAtUtc) VALUES ('account-2', 'provider-2', 'Other account', 'Valid', 'Healthy', '" + stamp + "', '" + stamp + "')");
        await Execute("INSERT INTO Models (Id, Backend, ProviderProfileId, ProviderModelId, DisplayName, CapabilityState, Provenance, IsEnabled, Health, DiscoveredAtUtc) VALUES ('model-2', 'OpenCode', 'provider-2', 'provider/other-model', 'Other model', 'Supported', 'ProviderReported', 1, 'Healthy', '" + stamp + "')");
        await Execute("INSERT INTO Routes (Id, Backend, ProviderProfileId, AccountId, ModelId, MaxDataClass, IsEnabled, Health, ManualPriority, CreatedAtUtc, UpdatedAtUtc) VALUES ('route-2', 'OpenCode', 'provider-2', 'account-2', 'model-2', 'PrivateSource', 1, 'Healthy', 1, '" + stamp + "', '" + stamp + "')");
    }

    private async Task Execute(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private async Task<string?> Scalar(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(); return result is null or DBNull ? null : Convert.ToString(result);
    }
}
