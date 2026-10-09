using System.Runtime.CompilerServices;
using System.Text.Json;
using LLMGateway.Core;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Health;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Reconciliation;
using LLMWorkGUI.Application.Lifecycle;
using LLMWorkGUI.Application.Observability;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.DependencyInjection;
using LLMWorkGUI.Infrastructure.Hosting;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class NativeGatewayTurnTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly Adapter _adapter = new();
    private readonly Store _store = new();
    private readonly List<ICheckoutLockToken> _tokens = [];
    private LlmGateway? _gateway;
    private ApplicationInstanceGuard? _guard;
    private NativeGatewayTurnService? _service;
    private NativeGatewayTurnRequest _request = null!;
    private IHealthCenterService _health = null!;
    private TrackingLocks _locks = null!;
    private int _factoryCalls;
    private readonly ActivityCenterService _activity = new(text => text);
    private readonly GatewayLogCapture<LlmGateway> _gatewayLog = new();

    public void Dispose()
    {
        _adapter.Release.TrySetResult();
        foreach (var token in _tokens)
            try { token.Dispose(); }
            catch (LLMWorkGUI.Domain.Entities.ProjectLockConflictException)
            { /* Fixture cleanup closes the mutex handle; the uncertain SQLite lock remains retained. */ }
        _gateway?.Dispose(); _guard?.Dispose(); _db.Dispose();
    }

    private async Task Ready(bool enable = true)
    {
        await _db.InitializeAsync();
        Directory.CreateDirectory(_db.GetWorkspacePath());
        await _db.SeedRouteChainAsync();
        _guard = new ApplicationInstanceGuard(_db.Root);
        _gateway = new LlmGateway(_store, [_adapter], new GatewayOptions { WorkspaceDirectory = _db.GetWorkspacePath("wrong"), MaxConcurrentRequestsPerAccount = 1 }, _gatewayLog);
        var importer = new SqliteGatewayCatalogImportService(() => _gateway, new(new SensitiveDataFilter()), _db.Factory, _guard, TimeProvider.System);
        var catalog = await importer.ImportAsync();
        var route = Assert.Single(catalog.Routes);
        _request = new("project-1", _db.GetWorkspacePath(), route.Id, Guid.NewGuid().ToString(), "private prompt fixture");
        _health = new HealthCenterService(new SqliteHealthStateRepository(_db.Factory), new SqliteHealthEventRepository(_db.Factory));
        _locks = new TrackingLocks(new CheckoutLockService(new SqliteProjectLockRepository(_db.Factory), _guard, TimeProvider.System), _tokens);
        _service = new NativeGatewayTurnService(() => { Interlocked.Increment(ref _factoryCalls); return _gateway; },
            _db.Factory, _guard, _locks, _health, new SensitiveDataFilter(), TimeProvider.System, _activity);
        await Sql("UPDATE Projects SET DataClassification='PublicSource'");
        if (enable)
            await Sql("""
                UPDATE ProviderProfiles SET IsEnabled=1;
                UPDATE Accounts SET IsEnabled=1,AuthState='Valid',Health='Healthy';
                UPDATE Models SET IsEnabled=1,CapabilityState='Supported',Health='Healthy';
                UPDATE Routes SET IsEnabled=1,Health='Healthy';
                """);
    }

    private Task<ApplicationInstanceGuard> CreateSecondaryGuard()
    {
        // A named mutex is recursive on its owning thread. An async continuation can
        // return to that thread, so model a second instance on a distinct thread.
        var ready = new TaskCompletionSource<ApplicationInstanceGuard>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { ready.TrySetResult(new ApplicationInstanceGuard(_db.Root)); }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true };
        thread.Start();
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task<object?> Sql(string sql)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Success_UsesPinnedWorkspaceAndPersistsOnlyLocalIdentity()
    {
        await Ready();
        var result = await _service!.ExecuteAsync(_request);
        Assert.Equal(ExecutionState.Succeeded, result.State);
        Assert.Equal("answer", result.Content);
        Assert.False(result.RequiresReconciliation);
        Assert.Equal(_request.RootPath, _adapter.Request!.WorkingDirectory);
        Assert.Equal("model", _adapter.Request.Model);
        Assert.Equal("work", _adapter.Account);
        Assert.Equal(1, _adapter.Calls);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal("Closed", await Sql("SELECT State FROM Sessions WHERE Backend='NativeGateway'"));
        Assert.Equal(DBNull.Value, await Sql("SELECT NativeSessionId FROM Sessions WHERE Backend='NativeGateway'"));
        Assert.Equal(DBNull.Value, await Sql("SELECT ObservedRouteId FROM Executions"));
        Assert.Equal(DBNull.Value, await Sql("SELECT ObservedRouteId FROM ClientRequests"));
        Assert.Equal(4L, await Sql("SELECT COUNT(*) FROM ExecutionEvents"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ExecutionEvents WHERE NormalizedRedactedPayloadJson LIKE '%private prompt%' OR NormalizedRedactedPayloadJson LIKE '%answer%'"));
        Assert.Equal(64, ((string)(await Sql("SELECT PromptHash FROM ClientRequests"))!).Length);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM HealthStates"));
    }

    [Fact]
    public async Task Admission_StoresOnlyTheSelectedRouteSnapshot()
    {
        await Ready();
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Executions"));
        var selectedProfile = (string)(await Sql("SELECT ProviderProfileId FROM Routes WHERE Id='" + _request.RouteId + "'"))!;
        var otherProfile = (string)(await Sql("SELECT ProviderProfileId FROM Routes WHERE Backend='OpenCode'"))!;
        var selectedAccount = (string)(await Sql("SELECT AccountId FROM Routes WHERE Id='" + _request.RouteId + "'"))!;
        var selectedNative = (string)(await Sql("SELECT m.ProviderModelId FROM Models m JOIN Routes r ON r.ModelId=m.Id WHERE r.Id='" + _request.RouteId + "'"))!;
        Assert.NotEqual(selectedProfile, otherProfile);
        var journal = new SqliteNativeGatewayJournal(_db.Factory, _guard!, TimeProvider.System);
        var admission = await journal.BeginAsync(_request, CancellationToken.None);
        var id = admission.ExecutionId;
        Assert.Equal(_request.RouteId, await Sql("SELECT RequestedRouteId FROM Executions WHERE Id='" + id + "'"));
        Assert.Equal(selectedProfile, await Sql("SELECT DispatchProviderProfileId FROM Executions WHERE Id='" + id + "'"));
        Assert.Equal(selectedAccount, await Sql("SELECT DispatchAccountId FROM Executions WHERE Id='" + id + "'"));
        Assert.Equal(selectedNative, await Sql("SELECT DispatchNativeModelId FROM Executions WHERE Id='" + id + "'"));
        Assert.NotEqual(otherProfile, await Sql("SELECT DispatchProviderProfileId FROM Executions WHERE Id='" + id + "'"));
        await Sql("UPDATE Models SET ProviderModelId='rewritten-after-admission' WHERE Id=(SELECT ModelId FROM Routes WHERE Id='" + _request.RouteId + "')");
        Assert.Equal(selectedNative, await Sql("SELECT DispatchNativeModelId FROM Executions WHERE Id='" + id + "'"));
        Assert.Equal(DBNull.Value, await Sql("SELECT ObservedRouteId FROM Executions WHERE Id='" + id + "'"));
        var missing = _request with { RouteId = "missing-native-route", ClientRequestId = Guid.NewGuid().ToString() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.BeginAsync(missing, CancellationToken.None));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Executions"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Executions WHERE DispatchProviderProfileId='" + otherProfile + "'"));
    }

    [Theory]
    [InlineData("UPDATE Accounts SET AuthState='Unknown'")]
    [InlineData("UPDATE Accounts SET CooldownUntilUtc='2999-01-01T00:00:00Z'")]
    [InlineData("UPDATE Routes SET IsEnabled=0")]
    [InlineData("UPDATE Routes SET ExecutionMode='agent'")]
    [InlineData("UPDATE Models SET CapabilityState='Unknown'")]
    [InlineData("UPDATE Projects SET DataClassification='Restricted'")]
    [InlineData("UPDATE Projects SET DataClassification='PrivateSource'")]
    [InlineData("UPDATE Accounts SET ProviderNativeId='other' WHERE ProviderNativeId='work'")]
    public async Task Admission_RefusesIneligibleOrChangedBindingWithoutConstructingGateway(string change)
    {
        await Ready(); await Sql(change);
        Assert.Empty(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service!.ExecuteAsync(_request));
        Assert.Equal(0, _factoryCalls); Assert.Equal(0, _adapter.Calls);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Executions"));
    }

    [Fact]
    public async Task ImportedCatalogCannotExecuteWithoutExplicitVerification()
    {
        await Ready(enable: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service!.ExecuteAsync(_request));
        Assert.Equal(0, _factoryCalls);
    }

    [Fact]
    public async Task Dispatch_RechecksRouteAfterLockAcquisition()
    {
        await Ready();
        _locks.AfterAcquire = async () => { await Sql("UPDATE Routes SET IsEnabled=0"); };
        var result = await _service!.ExecuteAsync(_request);
        Assert.Equal(ExecutionState.Failed, result.State); Assert.Equal(0, _factoryCalls);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM HealthEvents"));
    }

    [Fact]
    public async Task DuplicateRequest_IsAtomicAndNeverResends()
    {
        await Ready(); await _service!.ExecuteAsync(_request);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _service.ExecuteAsync(_request));
        Assert.Equal(1, _adapter.Calls);
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Executions"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM Sessions"));
    }

    [Fact]
    public async Task LateJournalFailure_RollsBackWholeAdmission()
    {
        await Ready();
        await Sql("CREATE TRIGGER fail_gateway_event BEFORE INSERT ON ExecutionEvents BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _service!.ExecuteAsync(_request));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Sessions"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Executions"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ClientRequests"));
        Assert.Equal(0, _factoryCalls);
    }

    [Fact]
    public async Task EquivalentRootSpellingIsAcceptedButDifferentRootIsRefused()
    {
        await Ready();
        await Sql("UPDATE Projects SET RootPath=RootPath||'\\'");
        Assert.Equal(ExecutionState.Succeeded, (await _service!.ExecuteAsync(_request)).State);
        var wrong = _db.GetWorkspacePath("another"); Directory.CreateDirectory(wrong);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ExecuteAsync(_request with { ClientRequestId = "wrong-root", RootPath = wrong }));
        Assert.Equal(1, _factoryCalls);
    }

    [Fact]
    public async Task ConcurrentRequest_ReservesAccountBeforeDispatch()
    {
        await Ready(); _adapter.Wait = true;
        var first = _service!.ExecuteAsync(_request);
        await _adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ExecuteAsync(_request with { ClientRequestId = "second" }));
        _adapter.Release.TrySetResult();
        Assert.Equal(ExecutionState.Succeeded, (await first).State);
        Assert.Equal(1, _adapter.Calls);
    }

    [Theory]
    [InlineData(GatewayErrorKind.RateLimited, ExecutionFailureReason.QuotaExceeded, HealthErrorClass.QuotaOrRateLimit)]
    [InlineData(GatewayErrorKind.Timeout, ExecutionFailureReason.NetworkTimeout, HealthErrorClass.NetworkOrTimeout)]
    [InlineData(GatewayErrorKind.ProviderUnavailable, ExecutionFailureReason.StartupFailure, HealthErrorClass.ExecutableMissingOrVersion)]
    [InlineData(GatewayErrorKind.AuthenticationRequired, ExecutionFailureReason.AuthenticationFailure, HealthErrorClass.AuthenticationOrRefresh)]
    [InlineData(GatewayErrorKind.ModelNotFound, ExecutionFailureReason.ModelUnavailable, HealthErrorClass.ModelUnavailableOrMismatch)]
    public async Task Failure_IsClassifiedWithoutFallbackOrFalseTerminal(GatewayErrorKind kind, ExecutionFailureReason reason, HealthErrorClass error)
    {
        await Ready(); _adapter.Error = kind;
        var result = await _service!.ExecuteAsync(_request);
        Assert.Equal(ExecutionState.Ambiguous, result.State); Assert.True(result.RequiresReconciliation);
        Assert.Equal(reason, result.FailureReason); Assert.Null(result.Content);
        Assert.Equal(1, _adapter.Calls);
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(DBNull.Value, await Sql("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal(1L, await Sql($"SELECT COUNT(*) FROM HealthEvents WHERE ErrorClass='{error}'"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM HealthEvents WHERE Reason LIKE '%native secret fixture%'"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ExecuteAsync(_request with { ClientRequestId = "retry" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTimeout_AfterDispatchRetainOwnership(bool timeout)
    {
        await Ready(); _adapter.Wait = true;
        using var source = new CancellationTokenSource();
        var task = _service!.ExecuteAsync(_request with { Timeout = timeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10) }, source.Token);
        await _adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!timeout) source.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ExecutionState.Ambiguous, result.State);
        Assert.Equal(timeout ? ExecutionFailureReason.NetworkTimeout : ExecutionFailureReason.UserCancelled, result.FailureReason);
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task NonCooperativeStream_ReturnsBoundedAmbiguityAndCannotReleaseLate()
    {
        await Ready(); _adapter.Wait = true; _adapter.IgnoreCancellation = true;
        var task = _service!.ExecuteAsync(_request with { Timeout = TimeSpan.FromSeconds(1) });
        var result = await task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(ExecutionState.Ambiguous, result.State);
        _adapter.Release.TrySetResult();
        await _adapter.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Ambiguous", await Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
    }

    [Fact]
    public async Task DisposalFailure_CannotBecomeSuccessAndDoesNotLeakGatewaySlot()
    {
        await Ready(); _adapter.ThrowOnDispose = true;
        var result = await _service!.ExecuteAsync(_request);
        Assert.Equal(ExecutionState.Ambiguous, result.State);
        _adapter.ThrowOnDispose = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var response = await _gateway!.CompleteAsync(CoreRequest(), deadline.Token);
        Assert.Equal("answer", response.Content);
    }

    [Theory]
    [InlineData("auto", "work", ProviderKind.Codex)]
    [InlineData("model", "WORK", ProviderKind.Codex)]
    [InlineData("model", "work", ProviderKind.Claude)]
    public async Task ExactCoreBinding_RejectsFallbackBeforeAdapter(string model, string account, ProviderKind provider)
    {
        await Ready();
        var request = CoreRequest();
        request.ExecutionContext = new(provider, account, model, _request.RootPath);
        await Assert.ThrowsAsync<GatewayException>(() => _gateway!.CompleteAsync(request));
        Assert.Equal(0, _adapter.Calls);
    }

    [Fact]
    public async Task ExactBinding_RejectsProfileArgumentsThatCouldOverrideWorkspaceOrModel()
    {
        await Ready(); _store.Account.ExtraArguments = ["-m", "another"];
        await Assert.ThrowsAsync<GatewayException>(() => _gateway!.CompleteAsync(CoreRequest()));
        Assert.Equal(0, _adapter.Calls);
    }

    [Fact]
    public async Task SynchronousAdapterFailure_DoesNotLeakGatewayAccountSlot()
    {
        await Ready(); _adapter.ThrowBeforeEnumerator = true;
        await Assert.ThrowsAsync<IOException>(() => _gateway!.CompleteAsync(CoreRequest()));
        _adapter.ThrowBeforeEnumerator = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal("answer", (await _gateway!.CompleteAsync(CoreRequest(), timeout.Token)).Content);
    }

    [Fact]
    public async Task HealthBlockAndViewOnly_AreCheckedBeforeNativeFactory()
    {
        await Ready();
        var account = (string)(await Sql("SELECT Id FROM Accounts WHERE ProviderNativeId='work'"))!;
        await _health.ReportFailureAsync(HealthScope.ForAccount(account), HealthErrorClass.AuthenticationOrRefresh);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service!.ExecuteAsync(_request));
        Assert.Equal(0, _factoryCalls);
        using var second = await CreateSecondaryGuard();
        Assert.True(second.IsViewOnly);
        var service = new NativeGatewayTurnService(() => throw new Exception("factory must stay lazy"), _db.Factory, second,
            _locks, _health, new SensitiveDataFilter(), TimeProvider.System);
        await Assert.ThrowsAsync<SecondaryInstanceReadOnlyException>(() => service.ExecuteAsync(_request));
    }

    [Fact]
    public void CompositionIsLazyAndExecutionBindingCannotBeDeserializedFromHttp()
    {
        var services = new ServiceCollection().AddInfrastructure(_db.Root);
        services.AddSingleton<ILlmGateway>(_ => throw new Exception("factory must stay lazy"));
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<INativeGatewayTurnService>());
        var request = JsonSerializer.Deserialize<ChatRequest>("""{"ExecutionContext":{"Provider":0,"AccountId":"work","NativeModel":"model","WorkingDirectory":"D:\\private"}}""");
        Assert.Null(request!.ExecutionContext);
        Assert.DoesNotContain("ExecutionContext", JsonSerializer.Serialize(new ChatRequest { ExecutionContext = new(ProviderKind.Codex, "work", "model", _db.Root) }));
    }

    private ChatRequest CoreRequest() => new() { Model = "codex/work/model", AccountId = "work",
        ExecutionContext = new(ProviderKind.Codex, "work", "model", _request.RootPath), Messages = [ChatMessage.User("fixture")] };

    private SqliteNativeGatewayRouteCatalog Catalog() => new(_db.Factory, new SensitiveDataFilter(), TimeProvider.System);

    [Fact]
    public async Task RouteCatalog_ReadsOnlyEligibleBindingsWithoutConstructingGatewayOrWritingJournal()
    {
        await Ready();
        var option = Assert.Single(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
        Assert.Equal(_request.RouteId, option.Id); Assert.Equal("work", option.Binding.NativeAccountId);
        Assert.Equal("model", option.Binding.NativeModelId); Assert.Equal(0, _factoryCalls);
        Assert.Empty(await Catalog().ListAsync("missing", _request.RootPath));
        Assert.Empty(await Catalog().ListAsync(_request.ProjectId, _db.Root));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Executions"));
        await Sql("UPDATE Models SET Health='ProbeRequired'");
        Assert.Empty(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
    }

    [Fact]
    public async Task ReboundRoute_IsRefusedAgainstUiSnapshotEvenWhenNewBindingIsEligible()
    {
        await Ready();
        var selected = Assert.Single(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
        _adapter.Models.Add(new("model2", "Another model"));
        var importer = new SqliteGatewayCatalogImportService(() => _gateway!, new(new SensitiveDataFilter()), _db.Factory, _guard!, TimeProvider.System);
        await importer.ImportAsync();
        var secondModel = (string)(await Sql("SELECT Id FROM Models WHERE ProviderModelId='model2'"))!;
        await Sql($"UPDATE Models SET IsEnabled=1,CapabilityState='Supported',Health='Healthy'; UPDATE Routes SET ModelId='{secondModel}' WHERE Id='{_request.RouteId}'");
        Assert.Equal("model2", Assert.Single(await Catalog().ListAsync(_request.ProjectId, _request.RootPath)).Binding.NativeModelId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service!.ExecuteAsync(_request with { ExpectedBinding = selected.Binding }));
        Assert.Equal(0, _factoryCalls); Assert.Equal(0, _adapter.Calls);
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM Executions"));
    }

    [Fact]
    public async Task HeldProjectLock_HidesRoutesEvenWhenAccountHasSpareCapacity()
    {
        await Ready(); await Sql("UPDATE Accounts SET MaxConcurrentExecutions=2");
        _adapter.Wait = true;
        var first = _service!.ExecuteAsync(_request);
        try
        {
            await _adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
        }
        finally { _adapter.Release.TrySetResult(); await first; }
        Assert.Single(await Catalog().ListAsync(_request.ProjectId, _request.RootPath));
    }

    [Theory]
    [InlineData(RecoveryAction.CloseSession)]
    [InlineData(RecoveryAction.ResetSession)]
    [InlineData(RecoveryAction.AcknowledgeAmbiguous)]
    public async Task Restart_QuarantinesOnceAndGenericRecoveryCannotClearOwnership(RecoveryAction action)
    {
        await Ready();
        var result = await _service!.ExecuteAsync(_request);
        // Explicit interrupted-process fixture. No live native process was started by this test.
        await Sql($"UPDATE Executions SET State='Running',EndedAtUtc=NULL; UPDATE Sessions SET State='Active',ActiveExecutionId='{result.ExecutionId}'; UPDATE ProjectLocks SET ReleasedAtUtc=NULL,ReleaseReason=NULL");
        var services = new ServiceCollection().AddInfrastructure(_db.Root);
        services.AddSingleton<IApplicationInstanceGuard>(_guard!);
        using var provider = services.BuildServiceProvider();
        var recovery = provider.GetRequiredService<INativeGatewayJournalRecoveryService>();
        await recovery.QuarantineInterruptedAsync(); await recovery.QuarantineInterruptedAsync();
        Assert.Equal("Ambiguous", await Sql("SELECT State FROM Executions"));
        Assert.Equal(result.ExecutionId, await Sql("SELECT ActiveExecutionId FROM Sessions"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM ExecutionEvents WHERE EventKind='NativeGatewayInterrupted'"));
        var general = provider.GetRequiredService<IReconciliationService>();
        var evidence = await general.ReconcileSessionAsync(result.SessionId);
        Assert.Equal(LLMWorkGUI.Application.Reconciliation.ReconciliationOutcome.Ambiguous, evidence.Outcome);
        Assert.Null(evidence.NativeSessionId); Assert.False(evidence.BindingMatched);
        await Assert.ThrowsAsync<InvalidOperationException>(() => general.ApplyRecoveryActionAsync(result.SessionId, action));
        await provider.GetRequiredService<IAppCrashRecoveryService>().RecoverAsync();
        Assert.Equal("Ambiguous", await Sql("SELECT State FROM Executions"));
        Assert.Equal(DBNull.Value, await Sql("SELECT EndedAtUtc FROM Executions"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(1, _adapter.Calls);
    }

    [Fact]
    public async Task Activity_PublishesCommittedLifecycleAndReplaysWithoutDuplicatesOrNativeCalls()
    {
        await Ready();
        var result = await _service!.ExecuteAsync(_request);
        var live = _activity.Snapshot();
        Assert.Equal(3, live.Count);
        Assert.All(live, item =>
        {
            Assert.Equal(result.SessionId, item.SessionId); Assert.Equal(result.ExecutionId, item.ExecutionId);
            Assert.Contains("Нативная идентичность ответа не подтверждена", item.Description);
            Assert.DoesNotContain(_request.Prompt, item.Description); Assert.DoesNotContain("answer", item.Description);
        });
        var restored = new ActivityCenterService(text => text);
        var recovery = new SqliteNativeGatewayRecoveryService(_db.Factory, _guard!, TimeProvider.System, restored);
        Assert.Equal(3, await recovery.ReplayActivityAsync()); Assert.Equal(3, await recovery.ReplayActivityAsync());
        Assert.Equal(live.Select(e => e.Id).Order(), restored.Snapshot().Select(e => e.Id).Order());
        Assert.Equal(1, _factoryCalls); Assert.Equal(1, _adapter.Calls);
        Assert.Equal("Succeeded", await Sql("SELECT State FROM Executions"));
    }

    [Fact]
    public async Task Activity_ObserverFailureDoesNotChangeTerminalStateOrRetainLock()
    {
        await Ready();
        _activity.Appended += (_, _) => throw new InvalidOperationException("observer fixture");
        Assert.Equal(ExecutionState.Succeeded, (await _service!.ExecuteAsync(_request)).State);
        Assert.Equal("Succeeded", await Sql("SELECT State FROM Executions"));
        Assert.Equal(0L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(4L, await Sql("SELECT COUNT(*) FROM ExecutionEvents"));
    }

    [Fact]
    public async Task Activity_RecoveryPublishesOnceAndReadsLegacyInterruptedPayloadInViewOnlyMode()
    {
        await Ready(); var result = await _service!.ExecuteAsync(_request);
        await Sql($"UPDATE Executions SET State='Running',EndedAtUtc=NULL; UPDATE Sessions SET State='Active',ActiveExecutionId='{result.ExecutionId}'");
        var restored = new ActivityCenterService(text => text);
        var recovery = new SqliteNativeGatewayRecoveryService(_db.Factory, _guard!, TimeProvider.System, restored);
        await recovery.QuarantineInterruptedAsync(); await recovery.QuarantineInterruptedAsync();
        Assert.Contains("после перезапуска", Assert.Single(restored.Snapshot()).Description);
        await Sql("UPDATE ExecutionEvents SET NormalizedRedactedPayloadJson='{\"state\":\"Ambiguous\",\"nativeIdentityConfirmed\":false}' WHERE EventKind='NativeGatewayInterrupted'");
        using var secondary = await CreateSecondaryGuard();
        Assert.True(secondary.IsViewOnly);
        var viewer = new SqliteNativeGatewayRecoveryService(_db.Factory, secondary, TimeProvider.System, restored);
        Assert.Equal(4, await viewer.ReplayActivityAsync()); Assert.Equal(4, restored.TotalCount);
        Assert.Equal("Ambiguous", await Sql("SELECT State FROM Executions")); Assert.Equal(1, _adapter.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"state\":\"9999\",\"failure\":\"None\"}")]
    [InlineData("{\"state\":\"Succeeded\",\"failure\":\"private-fixture\"}")]
    public async Task Activity_SkipsCorruptRowsAndKeepsOtherEvents(string payload)
    {
        await Ready(); await _service!.ExecuteAsync(_request);
        await using (var connection = await _db.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE ExecutionEvents SET NormalizedRedactedPayloadJson=$payload WHERE EventKind='NativeGatewayLifecycle' AND json_extract(NormalizedRedactedPayloadJson,'$.state')='Running'";
            command.Parameters.AddWithValue("$payload", payload); await command.ExecuteNonQueryAsync();
        }
        var restored = new ActivityCenterService(text => text);
        var recovery = new SqliteNativeGatewayRecoveryService(_db.Factory, _guard!, TimeProvider.System, restored);
        Assert.Equal(2, await recovery.ReplayActivityAsync()); Assert.Equal(3, restored.TotalCount);
        Assert.Contains(restored.Snapshot(), e => e.Id.EndsWith("native-gateway-journal:invalid-events"));
        Assert.DoesNotContain(restored.Snapshot(), e => e.Description.Contains(payload));
        Assert.Equal(4L, await Sql("SELECT COUNT(*) FROM ExecutionEvents"));
        Assert.Equal("Succeeded", await Sql("SELECT State FROM Executions"));
    }

    [Fact]
    public async Task Activity_ReplayIsBoundedToNewestEvents()
    {
        await Ready(); await _service!.ExecuteAsync(_request);
        var restored = new ActivityCenterService(text => text, capacity: 2);
        var recovery = new SqliteNativeGatewayRecoveryService(_db.Factory, _guard!, TimeProvider.System, restored);
        Assert.Equal(2, await recovery.ReplayActivityAsync());
        Assert.Equal(2, restored.TotalCount);
        Assert.Contains(restored.Snapshot(), e => e.Title.EndsWith("Succeeded"));
        Assert.DoesNotContain(restored.Snapshot(), e => e.Title.EndsWith("Starting"));
    }

    [Fact]
    public async Task Activity_ProductionStartupQuarantinesAndReplaysIdempotentlyWithoutGatewayConstruction()
    {
        await Ready(); var result = await _service!.ExecuteAsync(_request);
        await Sql($"UPDATE Executions SET State='Running',EndedAtUtc=NULL; UPDATE Sessions SET State='Active',ActiveExecutionId='{result.ExecutionId}'; UPDATE ProjectLocks SET ReleasedAtUtc=NULL,ReleaseReason=NULL");
        var activity = new ActivityCenterService(text => text);
        using var host = HostBootstrapper.CreateHostBuilder(appDataDirectory: _db.Root)
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<IApplicationInstanceGuard>(_guard!);
                services.AddSingleton<IActivityCenterService>(activity);
                services.AddSingleton<ILlmGateway>(_ => throw new InvalidOperationException("Gateway must stay lazy"));
            }).Build();
        await HostBootstrapper.InitializeAsync(host); await HostBootstrapper.InitializeAsync(host);
        var events = activity.Snapshot().Where(e => e.Id.StartsWith("native-gateway-journal:")).ToArray();
        Assert.Equal(4, events.Length);
        Assert.All(events, e => { Assert.Equal(result.ExecutionId, e.ExecutionId); Assert.Null(e.RouteId); });
        Assert.Equal("Ambiguous", await Sql("SELECT State FROM Executions"));
        Assert.Equal(1L, await Sql("SELECT COUNT(*) FROM ProjectLocks WHERE ReleasedAtUtc IS NULL"));
        Assert.Equal(5L, await Sql("SELECT COUNT(*) FROM ExecutionEvents")); Assert.Equal(1, _adapter.Calls);
    }

    [Fact]
    public async Task GatewayDiagnosticsExcludeNativeErrorTextAndAccountId()
    {
        await Ready();
        _adapter.Error = GatewayErrorKind.AuthenticationRequired;
        _adapter.EmitError = true;
        await _service!.ExecuteAsync(_request);
        _adapter.FailCatalog = true;
        await _gateway!.GetModelsAsync(refresh: true);
        await _gateway.GetQuotasAsync(refresh: true);
        Assert.Equal(3, _gatewayLog.Entries.Count);
        Assert.All(_gatewayLog.Entries, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("native secret fixture", entry.Text);
            Assert.DoesNotContain(_store.Account.Id, entry.Text);
        });
        Assert.Contains(_gatewayLog.Entries, e => e.Text.Contains("AuthenticationRequired"));
    }

    private sealed class TrackingLocks(ICheckoutLockService inner, List<ICheckoutLockToken> tokens) : ICheckoutLockService
    {
        public Func<Task>? AfterAcquire { get; set; }
        public bool RequiresWriterLock(string? mode) => true;
        public bool RequiresWriterLock(WorkflowRole role, string? mode) => true;
        public async Task<ICheckoutLockToken> AcquireWriterLockAsync(string project, string root, string execution, long generation, CancellationToken token = default)
        {
            var held = await inner.AcquireWriterLockAsync(project, root, execution, generation, token);
            lock (tokens) tokens.Add(held);
            if (AfterAcquire is not null) await AfterAcquire();
            return held;
        }
        public async Task<ICheckoutLockToken?> AcquireLockForExecutionAsync(string project, string root, string execution, long generation,
            string? mode, WorkflowRole role = WorkflowRole.Unknown, CancellationToken token = default) => await AcquireWriterLockAsync(project, root, execution, generation, token);
    }

    private sealed class Store : IAccountStore
    {
        public Action? OnRead { get; set; }
        public AccountProfile Account { get; } = new() { Id = "work", Provider = ProviderKind.Codex, DisplayName = "Fixture", IsActive = true, DefaultModel = "wrong-default" };
        public IReadOnlyList<AccountProfile> GetAll() { OnRead?.Invoke(); return [Account.Clone()]; }
        public AccountProfile? Find(string id) => id == Account.Id ? Account.Clone() : null;
        public Task AddAsync(AccountProfile profile, CancellationToken token = default) => throw new NotSupportedException();
        public Task UpdateAsync(AccountProfile profile, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
        public Task SelectAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
    }

    private sealed class Adapter : IProviderAdapter, INativeDispatchAuthorizationAdapter, IAuthenticatedModelOptionsAdapter
    {
        public bool SupportsNativeDispatchAuthorization => true;
        public Func<Task>? DuringOptionsDiscovery;
        public async Task<AuthenticatedModelOptions> DiscoverModelOptionsAsync(AccountProfile account, string model, CancellationToken token)
        {
            if (DuringOptionsDiscovery is not null) await DuringOptionsDiscovery();
            token.ThrowIfCancellationRequested();
            return new(account.Id, account.Provider, model, ["high", "max"], DateTimeOffset.UtcNow, "Explicit authenticated discovery fixture");
        }
        public bool RefusePreparation;
        public ValueTask<IAsyncDisposable?> PrepareRequestAsync(AccountProfile account, NativeChatRequest request, CancellationToken cancellationToken)
        {
            if (RefusePreparation) throw GatewayException.Invalid("Local provider preparation refused.");
            return ValueTask.FromResult<IAsyncDisposable?>(null);
        }
        public List<NativeModel> Models { get; } = [new("model", "Fixture")];
        public int Calls; public string? Account; public NativeChatRequest? Request;
        public bool Wait, IgnoreCancellation, ThrowOnDispose, ThrowBeforeEnumerator, FailCatalog, EmitError;
        public bool ExecutableMissing;
        public GatewayErrorKind? Error;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderKind Provider => ProviderKind.Codex;
        public string DisplayName => "Fixture";
        public string DefaultExecutable => "fixture";
        public ProviderCapabilities Capabilities { get; } = new(false, "fixture", MultiAccountSupport.Isolated, "fixture", null, null, false, 1_000_000);
        public string? ResolveExecutable(AccountProfile account) => ExecutableMissing ? null : "fixture";
        public Task<AccountStatus> GetStatusAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account, CancellationToken token) =>
            FailCatalog ? throw new IOException("native secret fixture") : Task.FromResult<IReadOnlyList<NativeModel>>(Models);
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account, CancellationToken token) =>
            FailCatalog ? throw new IOException("native secret fixture") : Task.FromResult(QuotaSnapshot.Unsupported(account, AccountAvailability.Unknown, null, "fixture", "fixture"));
        public Task StartInteractiveLoginAsync(AccountProfile account, CancellationToken token) => throw new NotSupportedException();
        public IEnumerable<AccountProfile> DiscoverProfiles() => [];
        public IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account, NativeChatRequest request, CancellationToken token)
        {
            if (ThrowBeforeEnumerator)
            {
                if (request.DispatchAuthorization is { RequiresProcessBinding: true } binding)
                    binding.BindProcessAsync(new FixtureProcessIdentity(), token).GetAwaiter().GetResult();
                request.DispatchAuthorization?.AuthorizeTransportAsync(account, request, request.Prompt, token).GetAwaiter().GetResult();
                throw new IOException("fixture");
            }
            return Run(account, request, token);
        }
        private async IAsyncEnumerable<NativeChatEvent> Run(AccountProfile account, NativeChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            if (request.DispatchAuthorization is { } authorization)
            {
                if (authorization.RequiresProcessBinding)
                    await authorization.BindProcessAsync(new FixtureProcessIdentity(), token);
                await authorization.AuthorizeTransportAsync(account, request, request.Prompt, token);
            }
            Calls++; Account = account.Id; Request = request; Started.TrySetResult();
            try
            {
                if (Wait) await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : token);
                if (Error is { } kind)
                {
                    if (EmitError) { yield return NativeChatEvent.Fail(kind, "native secret fixture"); yield break; }
                    throw new GatewayException(kind, "native secret fixture");
                }
                yield return NativeChatEvent.Delta("answer");
            }
            finally
            {
                Disposed.TrySetResult();
                if (ThrowOnDispose) throw new IOException("fixture disposal failure");
            }
        }
    }

    // Explicit friend-assembly double. These cases exercise journal policy, not OS ownership.
    private sealed class FixtureProcessIdentity : INativeProcessIdentity
    {
        public long ProcessGeneration => 777;
        public bool HasExited => false;
    }
}
