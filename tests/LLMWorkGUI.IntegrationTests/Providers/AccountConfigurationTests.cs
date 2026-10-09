using LLMWorkGUI.Application.Accounts;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using LLMWorkGUI.Infrastructure.Concurrency;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed class AccountConfigurationTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupervisorLostWhileOpeningWriterConnection_RefusesSaveAndImport(bool import)
    {
        var original = await Ready();
        _bridge.Items = [new("native-account", "Native account", "native-profile", AuthState.Valid)];
        var candidate = Assert.Single(await original.DiscoverAsync("provider-1"));
        var opened = 0;
        var factory = new CallbackConnectionFactory(_db.Factory, () =>
        {
            if (++opened == (import ? 2 : 1)) _guard!.Dispose();
        });
        var service = new SqliteAccountConfigurationService(factory, _guard!, new SensitiveDataFilter(), TimeProvider.System, [_bridge]);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => import
            ? service.ImportAsync("provider-1", candidate) : Add(service));
        Assert.Single((await original.ReadAsync()).Accounts);
    }

    private sealed class CallbackConnectionFactory(ISqliteConnectionFactory inner, Action afterOpen) : ISqliteConnectionFactory
    {
        public string DatabasePath => inner.DatabasePath;
        public string ConnectionString => inner.ConnectionString;
        public SqliteConnection CreateConnection() => inner.CreateConnection();
        public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken token = default)
        {
            var connection = await inner.OpenConnectionAsync(token);
            afterOpen();
            return connection;
        }
    }

    private readonly TestDatabase _db = new();
    private readonly DiscoveryBridge _bridge = new();
    private ApplicationInstanceGuard? _guard;
    public void Dispose() { _guard?.Dispose(); _db.Dispose(); }
    private SqliteAccountConfigurationService Service() => new(_db.Factory, _guard!, new SensitiveDataFilter(), TimeProvider.System, [_bridge]);
    private async Task<SqliteAccountConfigurationService> Ready()
    { await _db.InitializeAsync(); await _db.SeedRouteChainAsync(); _guard = new(_db.Root); return Service(); }
    private Task<string> Add(SqliteAccountConfigurationService service, string name = "New account") =>
        service.SaveAsync(new("provider-1", name, 2, false, 3, 0.2));
    private async Task Execute(string sql)
    { await using var connection = await _db.Factory.OpenConnectionAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
    private async Task<object?> Scalar(string sql)
    { await using var connection = await _db.Factory.OpenConnectionAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }

    [Fact]
    public async Task SessionBindings_ExposeRequestedLocalMetadata_AndKeepAccountAuthUnchanged()
    {
        var service = await Ready();
        await _db.SeedSessionAsync(nativeSessionId: "synthetic-native", activeExecutionId: "synthetic-execution");
        await Execute("UPDATE Sessions SET ReasoningEffort='high',SpeedMode='fast',ExecutionMode='plan'");
        var row = Assert.Single((await service.ReadAsync()).Accounts); var session = Assert.Single(row.Sessions);
        Assert.Equal(1, row.SessionCount); Assert.Equal("session-1", session.LocalSessionId);
        Assert.Equal("project-1", session.ProjectId); Assert.Equal("model-1", session.ModelId);
        Assert.Equal("synthetic-native", session.NativeSessionId); Assert.Equal("high", session.ReasoningEffort);
        Assert.Equal("fast", session.SpeedMode); Assert.Equal("plan", session.ExecutionMode);
        Assert.Equal("synthetic-execution", session.ActiveExecutionId); Assert.Equal(SessionState.Active, session.State);
        Assert.Equal(AuthState.Valid, row.AuthState); // Persisted synthetic fixture, never raised by the binding read.
        await Execute("UPDATE Accounts SET AuthState='Unknown'");
        Assert.Equal(AuthState.Unknown, Assert.Single((await service.ReadAsync()).Accounts).AuthState);
        Assert.Equal(0, _bridge.ProbeOrPinCount);
    }

    [Fact]
    public async Task ClosedSession_VisibleOnlyWhileLocalOwnershipIsRetained()
    {
        var service = await Ready();
        await _db.SeedSessionAsync("closed-clean", state: "Closed");
        await _db.SeedSessionAsync("closed-lock", state: "Closed");
        await _db.SeedExecutionAsync("lock-execution", sessionId: "closed-lock", state: "Succeeded");
        await _db.InsertProjectLockAsync("retained-lock", _db.GetWorkspacePath(), "lock-execution");
        var session = Assert.Single(Assert.Single((await service.ReadAsync()).Accounts).Sessions);
        Assert.Equal("closed-lock", session.LocalSessionId); Assert.Equal(SessionState.Closed, session.State);
    }

    [Fact]
    public async Task SessionBindingSample_IsBoundedWithExactCount_AndScopedToAccountProfile()
    {
        var service = await Ready();
        for (var i = 0; i < 25; i++) await _db.SeedSessionAsync("session-"+i, state: "Idle");
        await Execute("""
            INSERT INTO ProviderProfiles (Id,DisplayName,Backend,MaxDataClass,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('other-profile','Other','OpenCode','PrivateSource',1,'2026-10-02','2026-10-02');
            """);
        await _db.SeedSessionAsync("wrong-profile", providerProfileId: "other-profile");
        var row = Assert.Single((await service.ReadAsync()).Accounts);
        Assert.Equal(25, row.SessionCount); Assert.Equal(20, row.Sessions.Count);
        Assert.DoesNotContain(row.Sessions, s => s.LocalSessionId == "wrong-profile");
        Assert.Equal(25, row.Sessions.Select(s => s.LocalSessionId).Distinct().Count() + 5);
    }

    [Fact]
    public async Task SessionBindingLabels_AreRedactedBeforeTheyLeaveInfrastructure()
    {
        var service = await Ready(); await _db.SeedSessionAsync(nativeSessionId: "Bearer account-binding-fixture-0123456789");
        var session = Assert.Single(Assert.Single((await service.ReadAsync()).Accounts).Sessions);
        Assert.DoesNotContain("account-binding-fixture", session.NativeSessionId);
        Assert.Contains("[", session.NativeSessionId);
    }

    [Fact]
    public async Task ConcurrentImportOfSameContext_AdmitsOnlyOneRow()
    {
        var service = await Ready(); _bridge.Items = [new("native-account", "Native account", "native-profile", AuthState.Valid)];
        var candidate = Assert.Single(await service.DiscoverAsync("provider-1"));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        { await start.Task; return await Record.ExceptionAsync(() => service.ImportAsync("provider-1", candidate)); })).ToArray();
        start.SetResult(); var results = await Task.WhenAll(attempts);
        Assert.Single(results.Where(e => e is null)); Assert.IsType<InvalidOperationException>(Assert.Single(results.Where(e => e is not null)));
        Assert.Single((await service.ReadAsync()).Accounts.Where(a => a.Settings.Id == candidate.Id));
    }

    [Fact]
    public async Task Create_Reopen_UnknownWithoutSecretOrNativeIdentity()
    {
        var service = await Ready(); var id = await Add(service);
        var row = Assert.Single((await Service().ReadAsync()).Accounts.Where(a => a.Settings.Id == id));
        Assert.Equal(AuthState.Unknown, row.AuthState); Assert.False(row.Settings.IsEnabled);
        Assert.Equal(3, row.Settings.MaxConcurrentExecutions); Assert.Equal(0.2, row.Settings.ReserveThreshold);
        Assert.Null(row.Settings.NativeId);
        Assert.Equal(DBNull.Value, await Scalar($"SELECT SecretReference FROM Accounts WHERE Id='{id}'"));
        Assert.Equal(DBNull.Value, await Scalar($"SELECT GatewayNativeId FROM Accounts WHERE Id='{id}'"));
    }

    [Fact]
    public async Task Edit_PreservesConcurrentAuthHealthCooldownSecretAndGatewayIdentity()
    {
        var service = await Ready(); var expected = (await service.ReadAsync()).Accounts.Single().Settings;
        await Execute("UPDATE Accounts SET AuthState='Invalid',Health='QuarantinedAuto',CooldownUntilUtc='2026-10-03',DisabledUntilUtc='2026-10-04',ProviderNativeId=NULL,SecretReference='urn:llmworkgui:secret:fixture',GatewayNativeId='observed-fixture'");
        await service.SaveAsync(new("provider-1", "Renamed", 9, false, 4, null, expected));
        var row = (await service.ReadAsync()).Accounts.Single();
        Assert.Equal(AuthState.Invalid, row.AuthState); Assert.Equal(HealthState.QuarantinedAuto, row.Health);
        Assert.Equal("2026-10-03", row.CooldownUntil); Assert.Equal("2026-10-04", row.DisabledUntil);
        Assert.Equal("urn:llmworkgui:secret:fixture", await Scalar("SELECT SecretReference FROM Accounts"));
        Assert.Equal("observed-fixture", await Scalar("SELECT GatewayNativeId FROM Accounts"));
        Assert.Equal("Renamed", row.Settings.Name);
    }

    [Fact]
    public async Task StaleEditor_CannotOverwriteAnotherUserEdit()
    {
        var service = await Ready(); var expected = (await service.ReadAsync()).Accounts.Single().Settings;
        await service.SaveAsync(new("provider-1", "First", 0, true, 1, null, expected));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(new("provider-1", "Stale", 0, true, 1, null, expected)));
        Assert.Equal("First", (await service.ReadAsync()).Accounts.Single().Settings.Name);
    }

    [Fact]
    public async Task ConcurrentDuplicateAliases_OnlyOneInsertSucceeds()
    {
        var service = await Ready(); var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () => { await start.Task; return await Record.ExceptionAsync(() => Add(service)); })).ToArray();
        start.SetResult(); var results = await Task.WhenAll(attempts);
        Assert.Single(results.Where(e => e is null)); Assert.IsType<InvalidOperationException>(Assert.Single(results.Where(e => e is not null)));
        Assert.Equal(2, (await service.ReadAsync()).Accounts.Count);
    }

    [Theory]
    [InlineData(-1, 1, null)] [InlineData(0, 0, null)] [InlineData(0, 1, -0.1)]
    [InlineData(0, 1, 1.1)] [InlineData(0, 1, double.NaN)] [InlineData(0, 1, double.PositiveInfinity)]
    public async Task InvalidLimits_WriteNothing(int priority, int limit, double? reserve)
    {
        var service = await Ready();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(new("provider-1", "Invalid", priority, true, limit, reserve)));
        Assert.Single((await service.ReadAsync()).Accounts);
    }

    [Theory]
    [InlineData("")] [InlineData("bad\nname")] [InlineData("Bearer account-secret-fixture-0123456789")]
    public async Task InvalidOrSecretAlias_IsRejected(string name)
    {
        var service = await Ready(); await Assert.ThrowsAsync<InvalidOperationException>(() => Add(service, name));
        Assert.Single((await service.ReadAsync()).Accounts);
    }

    [Fact]
    public async Task Import_KeepsAdapterIdentity_ButNeverPromotesDiscoveryAuth()
    {
        var service = await Ready(); _bridge.Items = [new("native-account", "Native account", "native-profile", AuthState.Valid)];
        var candidate = Assert.Single(await service.DiscoverAsync("provider-1"));
        var id = await service.ImportAsync("provider-1", candidate);
        Assert.Equal("native-account", id); Assert.Equal(2, _bridge.DiscoverCount);
        var row = (await service.ReadAsync()).Accounts.Single(a => a.Settings.Id == id);
        Assert.Equal(AuthState.Unknown, row.AuthState); Assert.False(row.Settings.IsEnabled);
        Assert.Equal("native-profile", row.Settings.NativeId); Assert.Equal(0, _bridge.ProbeOrPinCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync("provider-1", candidate));
    }

    [Fact]
    public async Task MissingOrRepointedCandidate_CannotBeImported()
    {
        var service = await Ready(); _bridge.Items = [new("native-account", "Native account", "original-context", AuthState.Valid)];
        var candidate = Assert.Single(await service.DiscoverAsync("provider-1"));
        _bridge.Items = [new("native-account", "Native account", "repointed-context", AuthState.Valid)];
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync("provider-1", candidate));
        _bridge.Items = [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync("provider-1", candidate));
        Assert.Single((await service.ReadAsync()).Accounts);
    }

    [Fact]
    public async Task ProfileChangedDuringDiscovery_RejectsImport()
    {
        var service = await Ready(); _bridge.Items = [new("native-account", "Native account", "native-profile", AuthState.Valid)];
        var candidate = Assert.Single(await service.DiscoverAsync("provider-1"));
        _bridge.OnDiscover = () => Execute("UPDATE ProviderProfiles SET Backend='CursorAcp'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync("provider-1", candidate));
        Assert.Single((await service.ReadAsync()).Accounts);
    }

    [Fact]
    public async Task StarContexts_RequireImportAndKeepCodexHomeAsMetadataOnly()
    {
        var service = await Ready(); await Execute("UPDATE ProviderProfiles SET Backend='StarCliProxy'"); _bridge.BackendId = "star-cliproxy";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Add(service));
        var nativePath = Path.Combine(_db.Root, "native-context");
        _bridge.Items = [new("codex:fixture", "Codex fixture", nativePath, AuthState.Valid)];
        var id = await service.ImportAsync("provider-1", Assert.Single(await service.DiscoverAsync("provider-1")));
        Assert.False(Directory.Exists(nativePath));
        Assert.Equal(nativePath, (await service.ReadAsync()).Accounts.Single(a => a.Settings.Id == id).Settings.NativeId);
    }

    [Fact]
    public async Task SecondaryInstance_CannotCreateOrImport()
    {
        await Ready(); using var secondary = new ViewOnlyGuard();
        var service = new SqliteAccountConfigurationService(_db.Factory, secondary, new SensitiveDataFilter(), TimeProvider.System, [_bridge]);
        Assert.NotNull(await Record.ExceptionAsync(() => Add(service)));
        Assert.NotNull(await Record.ExceptionAsync(() => service.ImportAsync("provider-1", new("native", "Name", null))));
        Assert.Equal(0, _bridge.DiscoverCount); Assert.Single((await service.ReadAsync()).Accounts);
    }

    [Fact]
    public async Task DefaultOpenCodeDiscovery_DoesNotInventNativeAccounts()
    {
        await Ready(); var service = new SqliteAccountConfigurationService(_db.Factory, _guard!, new SensitiveDataFilter(), TimeProvider.System, [new OpenCodePluginAccountBridge()]);
        Assert.Empty(await service.DiscoverAsync("provider-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync("provider-1", new("invented", "Default Account", null)));
        Assert.Single((await service.ReadAsync()).Accounts);
    }

    [Fact]
    public async Task EnabledUnknownAccount_CannotEnterOpenCodeExecution()
    {
        var service = await Ready();
        var id = await service.SaveAsync(new("provider-1", "Unknown enabled", 0, true, 1, null));
        var routes = new SqliteModelRouteConfigurationService(_db.Factory, _guard!, new SensitiveDataFilter(), TimeProvider.System);
        var routeId = await routes.SaveRouteAsync(new("provider-1", id, "model-1", null, DataClassification.PublicSource, true, 0));
        var journal = new LLMWorkGUI.Infrastructure.OpenCode.SqliteOpenCodeExecutionJournal(_db.Factory, TimeProvider.System, _guard!);
        Assert.DoesNotContain(await journal.ListRoutesAsync(), r => r.Id == routeId);
        var route = new LLMWorkGUI.Backends.OpenCode.Sessions.OpenCodeStoredRoute(routeId, "provider-1", id, "model-1", "model-1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.BeginAsync("project-1", _db.GetWorkspacePath(), "native-fixture", route, "request-fixture", "hash-fixture", 17));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM Executions"));
    }

    [Theory]
    [InlineData("native\naccount", "context")]
    [InlineData("native-account", "Bearer native-secret-fixture-0123456789")]
    public async Task UnsafeDiscoveryMetadata_IsRefused(string id, string native)
    {
        var service = await Ready(); _bridge.Items = [new(id, "Native fixture", native, AuthState.Valid)];
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync("provider-1"));
        Assert.Single((await service.ReadAsync()).Accounts);
    }

    [Fact]
    public async Task DuplicateDiscoveryIds_AreAmbiguousAndRefused()
    {
        var service = await Ready(); _bridge.Items = [new("same", "One", "one", AuthState.Valid), new("same", "Two", "two", AuthState.Valid)];
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync("provider-1"));
    }

    [Fact]
    public async Task NativeIdentityChangedSinceRead_CannotBeOverwrittenByEditor()
    {
        var service = await Ready(); var expected = (await service.ReadAsync()).Accounts.Single().Settings;
        await Execute("UPDATE Accounts SET ProviderNativeId='changed-native-context'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(new("provider-1", "Rename", 0, true, 1, null, expected)));
        Assert.Equal("changed-native-context", (await service.ReadAsync()).Accounts.Single().Settings.NativeId);
    }

    private sealed class DiscoveryBridge : IAccountBridge
    {
        public string BackendId { get; set; } = "opencode";
        public bool SupportsPinning => false;
        public bool SupportsObservedRoute => false;
        public IReadOnlyList<DiscoveredAccountInfo> Items { get; set; } = [];
        public Func<Task>? OnDiscover { get; set; }
        public int DiscoverCount { get; private set; }
        public int ProbeOrPinCount { get; private set; }
        public async Task<IReadOnlyList<DiscoveredAccountInfo>> DiscoverAccountsAsync(string profileId, CancellationToken cancellationToken = default)
        { DiscoverCount++; if (OnDiscover is not null) await OnDiscover(); return Items; }
        public Task<AccountPinResult> PinAccountAsync(string p, string a, SessionBinding b, CancellationToken t = default)
        { ProbeOrPinCount++; throw new InvalidOperationException("No native mutation in import"); }
        public Task<AccountAuthProbeResult> ProbeAuthAsync(string p, string a, CancellationToken t = default)
        { ProbeOrPinCount++; throw new InvalidOperationException("Discovery does not authorize"); }
    }

    private sealed class ViewOnlyGuard : IApplicationInstanceGuard
    {
        public string InstanceId => "view-only-fixture";
        public bool IsPrimarySupervisor => false;
        public bool IsViewOnly => true;
        public void EnsureSupervisorPermitted() => throw new InvalidOperationException("View only");
        public void Dispose() { }
    }
}
