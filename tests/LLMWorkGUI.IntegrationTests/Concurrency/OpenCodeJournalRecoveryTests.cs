using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Backends.Abstractions.OpenCode.Sessions;
using LLMWorkGUI.Backends.OpenCode.Sessions;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Lifecycle;
using LLMWorkGUI.Infrastructure.OpenCode;
using LLMWorkGUI.Infrastructure.Reconciliation;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Concurrency;

public sealed class OpenCodeJournalRecoveryTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private ApplicationInstanceGuard? _guard;
    private readonly FrozenClock _clock = new();
    public void Dispose() { _guard?.Dispose(); _db.Dispose(); }

    private async Task<(SqliteOpenCodeExecutionJournal Journal, OpenCodeJournalEntry Entry)> Ready(bool dispatch = false,
        IActivityCenterService? activity = null)
    {
        await _db.InitializeAsync(); await _db.SeedRouteChainAsync();
        await Execute("UPDATE Models SET ProviderModelId='provider/native-model'");
        _guard = new ApplicationInstanceGuard(Path.Combine(_db.Root, "instance"));
        var journal = new SqliteOpenCodeExecutionJournal(_db.Factory, _clock, _guard, activity);
        var route = Assert.Single(await journal.ListRoutesAsync());
        var entry = await journal.BeginAsync("project-1", _db.GetWorkspacePath(), "synthetic-native-session", route,
            "synthetic-request", "prompt-hash-only", 17);
        if (dispatch)
        {
            await _db.InsertProjectLockAsync("lock-1", _db.GetWorkspacePath(), entry.ExecutionId, applicationInstanceId: _guard.InstanceId, processGeneration: 17);
            await journal.MarkRunningAsync(entry);
        }
        return (journal, entry);
    }
    private SqliteOpenCodeJournalRecoveryService Recovery(IActivityCenterService? activity = null) => new(_db.Factory, _guard!, _clock, activity);
    private ReconciliationService General(IReconciliationProbe probe) => new(new SqliteSessionRepository(_db.Factory),
        new SqliteExecutionRepository(_db.Factory, new SensitiveDataFilter()), new SqliteProjectRepository(_db.Factory),
        new SqliteProjectLockRepository(_db.Factory), probe, new RecoveryMatrix(), _clock,
        openCodeRecovery: Recovery(), connectionFactory: _db.Factory, instanceGuard: _guard!);
    private static TurnResult Result(OpenCodeJournalEntry entry, string status = TurnResult.CompletedStatus, bool uncertain = false, bool timeout = false) =>
        new() { SessionId = entry.NativeSessionId, Status = status, OutputText = "synthetic output excluded from audit",
            ErrorMessage = "synthetic exception excluded from audit", IsDeliveryUncertain = uncertain, WasTimedOut = timeout };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedAdmission_OrDispatch_RetainsOwnershipAndRefusesReplay(bool dispatch)
    {
        var (journal, entry) = await Ready(dispatch);
        var recovery = Recovery();
        var evidence = Assert.Single(await recovery.QuarantineInterruptedAsync());
        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome);
        Assert.False(evidence.ProcessAlive); Assert.False(evidence.BindingMatched);
        Assert.Equal(entry.ExecutionId, evidence.ExecutionId);
        Assert.Equal("Ambiguous", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("Ambiguous", await Scalar("SELECT State FROM Sessions"));
        Assert.Equal(entry.ExecutionId, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Null(await Scalar("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal(dispatch ? "1" : "0", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Null(await Scalar("SELECT ObservedRouteId FROM Executions"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.BeginAsync("project-1", _db.GetWorkspacePath(),
            "other-native", entry.Route, "other-request", "hash", 17));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MarkRunningAsync(entry));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.CompleteAsync(entry, Result(entry)));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ClientRequests"));
        Assert.Single(await recovery.QuarantineInterruptedAsync());
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeInterrupted'"));
    }

    [Fact]
    public async Task OldJournalWithoutEventMarkers_IsStillQuarantined()
    {
        var (_, entry) = await Ready();
        await Execute("DELETE FROM ExecutionEvents");
        Assert.Equal(entry.ExecutionId, Assert.Single(await Recovery().QuarantineInterruptedAsync()).ExecutionId);
        Assert.Equal("Ambiguous", await Scalar("SELECT State FROM Executions"));
    }

    [Fact]
    public async Task GenericAliveProbe_DoesNotPromoteRequestedIdentityOrReattachJournal()
    {
        var (_, entry) = await Ready(true);
        var probe = new CopyRequestedIdentityProbe();
        var evidence = await General(probe).ReconcileSessionAsync(entry.SessionId);
        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome);
        Assert.Equal(0, probe.Calls);
        Assert.Null(await Scalar("SELECT ObservedRouteId FROM Executions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Theory]
    [InlineData(RecoveryAction.CloseSession)]
    [InlineData(RecoveryAction.ResetSession)]
    [InlineData(RecoveryAction.AcknowledgeAmbiguous)]
    public async Task RecoveryActions_CannotTerminalizeUncertainNativeWork(RecoveryAction action)
    {
        var (_, entry) = await Ready(true);
        var service = General(new CopyRequestedIdentityProbe());
        await service.ReconcileSessionAsync(entry.SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyRecoveryActionAsync(entry.SessionId, action));
        Assert.Equal("Ambiguous", await Scalar("SELECT State FROM Executions"));
        Assert.Equal(entry.ExecutionId, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Null(await Scalar("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind IN ('RecoveryAction','OpenCodeRecoveryAction')"));
    }

    [Theory]
    [InlineData(RecoveryAction.CloseSession, "UserClose")]
    [InlineData(RecoveryAction.ResetSession, "UserReset")]
    public async Task RecoveryActions_WithConfirmedTerminal_CloseOnlyLocalSession(RecoveryAction action, string reason)
    {
        var (journal, entry) = await Ready();
        await journal.CompleteAsync(entry, Result(entry));
        await Recovery().TryReconcileAsync(entry.SessionId);
        var result = await General(new CopyRequestedIdentityProbe()).ApplyRecoveryActionAsync(entry.SessionId, action);
        Assert.Null(result.ReleasedLockId);
        Assert.Equal("Closed", await Scalar("SELECT State FROM Sessions"));
        Assert.Equal(reason, await Scalar("SELECT CloseReason FROM Sessions"));
        Assert.Equal("Succeeded", await Scalar("SELECT State FROM Executions"));
        Assert.NotNull(await Scalar("SELECT EndedAtUtc FROM Executions"));
        Assert.Null(await Scalar("SELECT ObservedRouteId FROM Executions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeRecoveryAction'"));
    }

    [Fact]
    public async Task DiagnosticCrashRecovery_DoesNotDropJournalSlotOrLock()
    {
        var (_, entry) = await Ready(true);
        var service = new AppCrashRecoveryService(_db.Factory, new SqliteExecutionRepository(_db.Factory, new SensitiveDataFilter()),
            new SqliteSessionRepository(_db.Factory), new SqliteProjectRepository(_db.Factory), new SqliteWorkflowRunRepository(_db.Factory),
            new SqliteProjectLockRepository(_db.Factory), _guard, timeProvider: _clock, openCodeRecovery: Recovery());
        var report = await service.RecoverAsync();
        Assert.Empty(report.InterruptedExecutionIds); Assert.Empty(report.ReleasedLockIds);
        Assert.Equal("lock-1", Assert.Single(report.RetainedLockIds));
        Assert.Equal(entry.ExecutionId, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Equal("Ambiguous", await Scalar("SELECT State FROM Executions"));
        Assert.Null(await Scalar("SELECT EndedAtUtc FROM Executions"));
    }

    [Fact]
    public async Task ProductionStartup_QuarantinesBeforeProbe_AndReplaysActivityIdempotently()
    {
        var (_, entry) = await Ready(true);
        var probe = new CopyRequestedIdentityProbe();
        using var host = HostBootstrapper.CreateHostBuilder(appDataDirectory: _db.Root)
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<IApplicationInstanceGuard>(_guard!);
                services.AddSingleton<IReconciliationProbe>(probe);
                services.AddSingleton<IActivityCenterService>(new ActivityCenterService(text => text, _clock));
            }).Build();
        await HostBootstrapper.InitializeAsync(host);
        Assert.Equal(0, probe.Calls);
        Assert.Equal(entry.ExecutionId, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        var activity = host.Services.GetRequiredService<IActivityCenterService>();
        Assert.Equal(3, activity.TotalCount);
        Assert.All(activity.Snapshot(), item => { Assert.Equal(entry.SessionId, item.SessionId); Assert.Equal(entry.ExecutionId, item.ExecutionId); Assert.Null(item.RouteId); });
        await HostBootstrapper.InitializeAsync(host);
        Assert.Equal(3, activity.TotalCount);
        Assert.Equal("3", await Scalar("SELECT COUNT(*) FROM ExecutionEvents"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task DispatchReservation_RequiresHeldLock_AndIsSingleUse()
    {
        var (journal, entry) = await Ready();
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MarkRunningAsync(entry));
        Assert.Equal("SessionConfirmed", await Scalar("SELECT State FROM Executions"));
        await _db.InsertProjectLockAsync("lock-1", _db.GetWorkspacePath(), entry.ExecutionId, applicationInstanceId: _guard!.InstanceId, processGeneration: 17);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MarkRunningAsync(entry with { NativeSessionId = "wrong-native" }));
        await journal.MarkRunningAsync(entry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MarkRunningAsync(entry));
        Assert.Equal("Running", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeDispatch'"));
    }

    [Theory]
    [InlineData(false, "TimedOut")]
    [InlineData(true, "Ambiguous")]
    public async Task Timeout_MetadataRetainsDispatchUncertainty(bool uncertain, string state)
    {
        var (journal, entry) = await Ready(uncertain);
        await journal.CompleteAsync(entry, Result(entry, TurnResult.FailedStatus, uncertain, true));
        Assert.Equal(state, await Scalar("SELECT State FROM Executions"));
        Assert.Equal("NetworkTimeout", await Scalar("SELECT FailureReason FROM Executions"));
        Assert.Equal(uncertain, await Scalar("SELECT EndedAtUtc FROM Executions") is null);
        Assert.Equal(uncertain ? entry.ExecutionId : null, await Scalar("SELECT ActiveExecutionId FROM Sessions"));
    }

    [Theory]
    [InlineData(false, "Cancelled")]
    [InlineData(true, "Ambiguous")]
    public async Task Cancellation_MetadataRetainsUnconfirmedNativeOutcome(bool uncertain, string state)
    {
        var (journal, entry) = await Ready(uncertain);
        await journal.CompleteAsync(entry, Result(entry, TurnResult.CancelledStatus, uncertain));
        Assert.Equal(state, await Scalar("SELECT State FROM Executions"));
        Assert.Equal(uncertain, await Scalar("SELECT EndedAtUtc FROM Executions") is null);
        Assert.Equal(uncertain ? "1" : "0", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task TimeoutAfterDispatch_WithConfirmedEnd_DoesNotClaimPromptWasExcluded()
    {
        var (journal, entry) = await Ready(true);
        await Execute("UPDATE ProjectLocks SET ReleasedAtUtc='2026-10-02T00:00:00Z'");
        await journal.CompleteAsync(entry, Result(entry, TurnResult.FailedStatus, timeout: true));
        Assert.Equal("TimedOut", await Scalar("SELECT State FROM Executions"));
        Assert.Contains("SupervisorBudgetExpired", await Scalar("SELECT NormalizedRedactedPayloadJson FROM ExecutionEvents WHERE EventKind='OpenCodeTerminal'"));
        Assert.DoesNotContain("BeforeDispatch", await Scalar("SELECT NormalizedRedactedPayloadJson FROM ExecutionEvents WHERE EventKind='OpenCodeTerminal'"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeDispatch'"));
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("TimedOut")]
    [InlineData("RouteMismatch")]
    public async Task TerminalWithStaleLock_RetainsOwnershipWithoutErasingConfirmedEnd(string state)
    {
        var (journal, entry) = await Ready(); await journal.CompleteAsync(entry, Result(entry));
        await Execute($"UPDATE Executions SET State='{state}'");
        var ended = await Scalar("SELECT EndedAtUtc FROM Executions");
        await _db.InsertProjectLockAsync("lock-1", _db.GetWorkspacePath(), entry.ExecutionId);
        var evidence = Assert.Single(await Recovery().QuarantineInterruptedAsync());
        Assert.Equal(ReconciliationOutcome.Ambiguous, evidence.Outcome); Assert.Null(evidence.ExecutionId);
        Assert.Equal(state, await Scalar("SELECT State FROM Executions"));
        Assert.Equal(ended, await Scalar("SELECT EndedAtUtc FROM Executions"));
        Assert.Null(await Scalar("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal("0", await Scalar("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='OpenCodeInterrupted'"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.BeginAsync("project-1", _db.GetWorkspacePath(), "other-native", entry.Route, "other-request", "hash", 17));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Recovery().TryApplyActionAsync(entry.SessionId, RecoveryAction.ResetSession));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"state\":\"UnknownState\",\"reason\":\"fixture\"}")]
    [InlineData("{\"state\":\"Running\"}")]
    [InlineData("{\"state\":\"Running\",\"reason\":42}")]
    public async Task MalformedAudit_IsPreservedAndWarnedWithoutAbortingProductionStartup(string payload)
    {
        var (journal, entry) = await Ready(); await journal.CompleteAsync(entry, Result(entry));
        await using (var connection = await _db.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE ExecutionEvents SET NormalizedRedactedPayloadJson=$payload WHERE EventKind='OpenCodeAdmission'";
            command.Parameters.AddWithValue("$payload", payload); await command.ExecuteNonQueryAsync();
        }
        var activity = new ActivityCenterService(text => text);
        using var host = HostBootstrapper.CreateHostBuilder(appDataDirectory: _db.Root).ConfigureServices((_, services) =>
        { services.AddSingleton<IApplicationInstanceGuard>(_guard!); services.AddSingleton<IActivityCenterService>(activity); }).Build();
        await HostBootstrapper.InitializeAsync(host);
        Assert.Contains(activity.Snapshot(), item => item.Id == "system:opencode-journal:invalid-events");
        Assert.Contains(activity.Snapshot(), item => item.Title == "OpenCode: локальный статус Succeeded");
        Assert.Equal(payload, await Scalar("SELECT NormalizedRedactedPayloadJson FROM ExecutionEvents WHERE EventKind='OpenCodeAdmission'"));
        Assert.Equal("Succeeded", await Scalar("SELECT State FROM Executions"));
        Assert.Null(await Scalar("SELECT ActiveExecutionId FROM Sessions"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayFailure_WarnsAndKeepsStartupAvailable_ExceptCancellation(bool cancelled)
    {
        var (journal, entry) = await Ready(); await journal.CompleteAsync(entry, Result(entry));
        var activity = new ActivityCenterService(text => text);
        using var host = HostBootstrapper.CreateHostBuilder(appDataDirectory: _db.Root).ConfigureServices((_, services) =>
        { services.AddSingleton<IApplicationInstanceGuard>(_guard!); services.AddSingleton<IActivityCenterService>(activity);
            services.AddSingleton<IOpenCodeJournalRecoveryService>(new FailingReplay(cancelled)); }).Build();
        if (cancelled) await Assert.ThrowsAsync<OperationCanceledException>(() => HostBootstrapper.InitializeAsync(host));
        else
        {
            await HostBootstrapper.InitializeAsync(host);
            var warning = Assert.Single(activity.Snapshot()); Assert.Equal("system:opencode-journal:reload-failed", warning.Id);
            Assert.DoesNotContain("synthetic-private-exception", warning.Description);
        }
        Assert.Equal("Succeeded", await Scalar("SELECT State FROM Executions"));
    }

    [Fact]
    public async Task ContradictoryTimeoutSuccess_IsRefusedBeforeMutation()
    {
        var (journal, entry) = await Ready();
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.CompleteAsync(entry, Result(entry, timeout: true)));
        Assert.Equal("SessionConfirmed", await Scalar("SELECT State FROM Executions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ExecutionEvents"));
    }

    [Fact]
    public async Task DurableAudit_ReplaysInSequenceAtSameTimestamp_WithoutPromptOutputOrObservedIdentity()
    {
        var activity = new ActivityCenterService(text => text, _clock);
        var observed = new List<string>();
        activity.Appended += (_, item) => observed.Add(activity.Snapshot().Single(row => row.Id == item.EventId).Title);
        var (journal, entry) = await Ready(true);
        // No native process in this fixture: removing the synthetic SQL lock represents a confirmed release.
        await Execute("UPDATE ProjectLocks SET ReleasedAtUtc='2026-10-02T00:00:00Z'");
        await journal.CompleteAsync(entry, Result(entry));
        var recovery = Recovery(activity);
        Assert.Equal(3, await recovery.ReplayActivityAsync());
        Assert.Equal(new[] { "OpenCode: локальный статус SessionConfirmed", "OpenCode: локальный статус Running", "OpenCode: локальный статус Succeeded" }, observed);
        Assert.Equal(3, activity.TotalCount);
        Assert.Equal(3, await recovery.ReplayActivityAsync()); Assert.Equal(3, activity.TotalCount);
        Assert.All(activity.Snapshot(), item => { Assert.Null(item.RouteId); Assert.Equal(entry.SessionId, item.SessionId); Assert.Equal(entry.ExecutionId, item.ExecutionId); });
        var payload = await Scalar("SELECT GROUP_CONCAT(NormalizedRedactedPayloadJson) FROM ExecutionEvents");
        Assert.DoesNotContain("prompt-hash-only", payload); Assert.DoesNotContain("synthetic output", payload);
        Assert.DoesNotContain("synthetic exception", payload); Assert.DoesNotContain(entry.NativeSessionId, payload);
    }

    [Fact]
    public async Task ActivityObserverFailure_DoesNotRollBackDurableAdmission()
    {
        var throwing = new ActivityCenterService(_ => throw new InvalidOperationException("observer failure"));
        var (_, entry) = await Ready(activity: throwing);
        Assert.Equal(entry.ExecutionId, await Scalar("SELECT Id FROM Executions"));
        Assert.Equal("1", await Scalar("SELECT COUNT(*) FROM ExecutionEvents"));
        var restored = new ActivityCenterService(text => text);
        Assert.Equal(1, await Recovery(restored).ReplayActivityAsync());
        Assert.Single(restored.Snapshot());
    }

    [Fact]
    public async Task SecondaryInstance_CannotMutateJournalButCanReplayAudit()
    {
        var (_, entry) = await Ready(); using var guard = new ViewOnlyGuard();
        var activity = new ActivityCenterService(text => text);
        var recovery = new SqliteOpenCodeJournalRecoveryService(_db.Factory, guard, _clock, activity);
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => recovery.QuarantineInterruptedAsync());
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => recovery.TryReconcileAsync(entry.SessionId));
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => recovery.TryApplyActionAsync(entry.SessionId, RecoveryAction.CloseSession));
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => new SqliteOpenCodeExecutionJournal(_db.Factory, _clock, guard).MarkRunningAsync(entry));
        Assert.Equal(1, await recovery.ReplayActivityAsync());
        Assert.Equal("SessionConfirmed", await Scalar("SELECT State FROM Executions"));
    }

    [Fact]
    public async Task LegacySessionWithoutClientRequest_RemainsOwnedByExistingRecovery()
    {
        await Ready(); await _db.SeedSessionAsync(sessionId: "legacy-session");
        Assert.Null(await Recovery().TryReconcileAsync("legacy-session"));
        Assert.Null(await Recovery().TryApplyActionAsync("legacy-session", RecoveryAction.CloseSession));
    }

    private sealed class FrozenClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero); }
    private sealed class FailingReplay(bool cancelled) : IOpenCodeJournalRecoveryService
    {
        public Task<IReadOnlyList<ReconciliationEvidence>> QuarantineInterruptedAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ReconciliationEvidence>>([]);
        public Task<ReconciliationEvidence?> TryReconcileAsync(string id, CancellationToken token = default) => Task.FromResult<ReconciliationEvidence?>(null);
        public Task<ReconciliationRecoveryResult?> TryApplyActionAsync(string id, RecoveryAction action, CancellationToken token = default) => Task.FromResult<ReconciliationRecoveryResult?>(null);
        public Task<int> ReplayActivityAsync(CancellationToken token = default) => throw (cancelled ? new OperationCanceledException() : new IOException("synthetic-private-exception"));
    }
    private sealed class CopyRequestedIdentityProbe : IReconciliationProbe
    {
        public int Calls { get; private set; }
        public Task<ReconciliationProbeResult> ProbeAsync(ReconciliationProbeRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ReconciliationProbeResult { BackendAvailable = true, ProcessAlive = true,
            ObservedNativeSessionId = request.Session.NativeSessionId, ObservedBinding = request.Session.Binding, Details = "synthetic PID-only probe" }); }
    }
    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "secondary-fixture"; public bool IsPrimarySupervisor => false; public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new SecondaryInstanceReadOnlyException("View-only fixture");
        public void Dispose() { }
    }
    private async Task Execute(string sql)
    { await using var connection = await _db.Factory.OpenConnectionAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
    private async Task<string?> Scalar(string sql)
    { await using var connection = await _db.Factory.OpenConnectionAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(); return value is null or DBNull ? null : Convert.ToString(value); }
}
