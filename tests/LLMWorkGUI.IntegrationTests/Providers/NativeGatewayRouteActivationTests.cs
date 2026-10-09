using System.Text.Json;
using LLMGateway.Core;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Providers;
using LLMWorkGUI.Infrastructure.Security;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Providers;

public sealed partial class NativeGatewayRouteActivationTests : IDisposable
{
    private const string Email = "fixture@example.invalid";
    private const string Model = "grok-4.7-high";
    private readonly TestDatabase _db = new();
    private readonly ProbeAdapter _adapter = new();
    private readonly Guard _guard = new();
    private readonly Clock _clock = new();
    private readonly JsonAccountStore _store = JsonAccountStore.InMemory([new AccountProfile
        { Id = "cursor-owned", Provider = ProviderKind.Cursor, DisplayName = "Owned Cursor", Enabled = true }]);
    private readonly string _provider = GatewayCatalogMapper.ProviderId(ProviderKind.Cursor);
    private readonly string _account = GatewayCatalogMapper.AccountId(ProviderKind.Cursor, "cursor-owned");
    private readonly string _model = GatewayCatalogMapper.ModelId(ProviderKind.Cursor, Model);
    private NativeGatewayRouteActivationService Service => _service ??= new(_db.Factory, () => _store, () => [_adapter], _guard, _clock);
    private NativeGatewayRouteActivationService? _service;
    public void Dispose() => _db.Dispose();

    private async Task Ready()
    {
        await _db.InitializeAsync();
        await _db.SeedRouteChainAsync(providerProfileId: _provider, accountId: _account, modelId: _model);
        await Sql("""
            UPDATE ProviderProfiles SET Backend='NativeGateway',IsEnabled=0,MaxDataClass='PublicSource';
            UPDATE Accounts SET ProviderNativeId='cursor-owned',AuthState='Unknown',IsEnabled=0,Health='ProbeRequired';
            UPDATE Models SET Backend='NativeGateway',ProviderModelId='grok-4.7-high',IsEnabled=0,CapabilityState='Unknown',Health='ProbeRequired',SupportsTools=0,SupportsAttachments=0;
            UPDATE Routes SET Backend='NativeGateway',IsEnabled=0,Health='ProbeRequired',MaxDataClass='PublicSource';
            """);
    }
    private async Task<object?> Sql(string text)
    {
        await using var connection = await _db.Factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand(); command.CommandText = text;
        return await command.ExecuteScalarAsync();
    }
    private async Task<long> AuditCount() => Convert.ToInt64(await Sql("SELECT COUNT(*) FROM HealthEvents"));

    [Fact]
    public async Task Activation_ReprobesAndPersistsOnlyUserDeclaredTextSupportAndForcedAudit()
    {
        await Ready();
        var preview = await Service.PreviewAsync("route-1", Email);
        Assert.Equal(0L, Convert.ToInt64(await Sql("SELECT IsEnabled FROM Routes")));
        await Service.ActivateAsync(preview.ObservationId, true, true, DataClassification.PrivateSource);
        Assert.Equal(4, _adapter.StatusCalls); Assert.Equal(2, _adapter.CatalogCalls);
        Assert.Equal("ForcedEnabled", await Sql("SELECT Health FROM Routes"));
        Assert.Equal("ForcedEnabled", await Sql("SELECT Health FROM Accounts"));
        Assert.Equal("Valid", await Sql("SELECT AuthState FROM Accounts"));
        Assert.Equal(3L, await AuditCount());
        var evidence = JsonSerializer.Deserialize<LLMWorkGUI.Application.Providers.ModelCapabilityEvidence>(
            (string)(await Sql("SELECT CapabilityValue FROM ModelCapabilities"))!);
        Assert.NotNull(evidence); Assert.Equal(ModelProvenance.UserDefined, evidence.Provenance);
        Assert.Equal(ModelCapabilityFlags.Chat, evidence.Flags);
        Assert.Equal(TimeSpan.FromHours(24), evidence.ExpiresAtUtc-evidence.ObservedAtUtc);
        Assert.Equal(0L, Convert.ToInt64(await Sql("SELECT SupportsTools FROM Models")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ActivateAsync(preview.ObservationId, true, true, DataClassification.PrivateSource));
        Assert.Equal(3L, await AuditCount());
    }

    [Fact]
    public async Task AdvancingClockActivationProducesCurrentChatEvidenceThroughProductionReader()
    {
        await Ready();
        _clock.AdvancePerRead = TimeSpan.FromMilliseconds(1);
        var preview = await Service.PreviewAsync("route-1", Email);
        await Service.ActivateAsync(preview.ObservationId, true, true, DataClassification.PublicSource);

        var configuration = await new SqliteModelRouteConfigurationService(
            _db.Factory, _guard, new SensitiveDataFilter(), _clock).ReadAsync();
        var route = Assert.Single(configuration.Routes);
        Assert.True(route.IsEnabled);
        var account = Assert.Single(configuration.Accounts);
        Assert.True(account.IsEnabled);
        Assert.Equal(AuthState.Valid, account.AuthState);
        var model = Assert.Single(configuration.Models);
        Assert.True(model.IsEnabled);
        Assert.Equal(CapabilityState.Supported, model.Capability);
        var evidence = Assert.Single(configuration.Capabilities);
        Assert.Equal(route.ModelId, evidence.ModelId);
        Assert.Equal(route.AccountId, evidence.AccountId);
        Assert.Equal(ModelProvenance.UserDefined, evidence.Provenance);
        Assert.Equal(ModelCapabilityFlags.Chat, evidence.Flags);
        Assert.True(evidence.IsCurrent(_clock.GetUtcNow()));
        Assert.Equal(TimeSpan.FromHours(24), evidence.ExpiresAtUtc - evidence.ObservedAtUtc);
        Assert.Equal(3L, await AuditCount());
    }

    [Theory]
    [InlineData("wrong-identity")]
    [InlineData("missing-model")]
    [InlineData("case-mismatch-model")]
    public async Task Preview_RequiresActualIdentityAndExactFreshCatalog(string failure)
    {
        await Ready();
        if (failure=="wrong-identity") _adapter.Email="other@example.invalid";
        else _adapter.ModelId=failure=="missing-model"?"other-model":"GROK-4.7-HIGH";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.PreviewAsync("route-1", Email));
        Assert.Equal(0L, await AuditCount());
    }

    [Theory]
    [InlineData("UPDATE Routes SET Health='QuarantinedAuto'",true)]
    [InlineData("UPDATE Accounts SET Health='DisabledManual'",true)]
    [InlineData("UPDATE Accounts SET CooldownUntilUtc='2099-01-01T00:00:00Z'",true)]
    [InlineData("UPDATE Models SET ProviderModelId='changed'",false)]
    [InlineData("UPDATE Accounts SET AuthState='Invalid'",true)]
    public async Task ChangedBindingOrRestrictions_AreNeverCleared(string change, bool beforePreview)
    {
        await Ready();
        if (beforePreview) {
            await Sql(change);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service.PreviewAsync("route-1", Email));
        } else {
            var preview=await Service.PreviewAsync("route-1",Email); await Sql(change);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ActivateAsync(preview.ObservationId,true,true,DataClassification.PublicSource));
        }
        Assert.Equal(0L,await AuditCount());
        Assert.Equal(0L,Convert.ToInt64(await Sql("SELECT IsEnabled FROM Routes")));
    }

    [Fact]
    public async Task NativeIdentityChangedAfterPreview_RejectsWithoutConfigurationMutation()
    {
        await Ready(); var preview=await Service.PreviewAsync("route-1",Email);
        _adapter.Email="other@example.invalid";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.ActivateAsync(preview.ObservationId,true,true,DataClassification.PublicSource));
        Assert.Equal(0L,await AuditCount());
    }

    [Fact]
    public async Task BackendProbeRequiredAndAuthenticationFanoutRemainBlocking()
    {
        await Ready();
        await Sql("INSERT INTO HealthStates(Id,ScopeType,ScopeId,State,UpdatedAtUtc) VALUES('backend-owned','backend','NativeGateway','ProbeRequired','2026-01-01T00:00:00Z')");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal("ProbeRequired",await Sql("SELECT State FROM HealthStates WHERE Id='backend-owned'"));
        Assert.Equal(0L,await AuditCount());
        await Sql("DELETE FROM HealthStates WHERE Id='backend-owned'; INSERT INTO HealthAuthenticationFanout(Id,ScopeType,ScopeId,Reason,ObservedAtUtc) VALUES('fanout-owned','backend','NativeGateway','owned fixture','2026-01-01T00:00:00Z')");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal(1L,Convert.ToInt64(await Sql("SELECT COUNT(*) FROM HealthAuthenticationFanout")));
    }

    [Fact]
    public async Task ActiveExecutionIsNotBypassedBySetup()
    {
        await Ready();
        await _db.SeedSessionAsync(providerProfileId:_provider,accountId:_account,modelId:_model);
        await _db.SeedExecutionAsync("execution-owned");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal(0,_adapter.StatusCalls);
    }

    [Fact]
    public async Task CancellationDuringActualCatalogLeavesNoActivationOrObservation()
    {
        await Ready(); using var stop=new CancellationTokenSource();
        _adapter.OnCatalog=()=>{ stop.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Service.PreviewAsync("route-1",Email,stop.Token));
        Assert.Equal(0L,await AuditCount());
    }

    [Fact]
    public async Task BindingChangedWhileNativeCatalogIsInFlightRejectsPreview()
    {
        await Ready();
        _adapter.OnCatalog=async()=>{ await Sql("UPDATE Models SET ProviderModelId='changed-during-catalog'"); };
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal(0L,await AuditCount());
    }

    [Fact]
    public async Task NativeProfileChangedWhileCatalogIsInFlightRejectsPreview()
    {
        await Ready();
        _adapter.OnCatalog=async()=>{
            var changed=_store.Find("cursor-owned")!; changed.Executable="different-owned-client";
            await _store.UpdateAsync(changed);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal(0L,await AuditCount());
    }

    [Fact]
    public async Task SecondaryGuardAndExpiredObservationCannotActivate()
    {
        await Ready(); _guard.Primary=false;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal(0,_adapter.StatusCalls);
        _guard.Primary=true; var preview=await Service.PreviewAsync("route-1",Email);
        _clock.Now=preview.ExpiresAtUtc;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.ActivateAsync(preview.ObservationId,true,true,DataClassification.PublicSource));
        Assert.Equal(0L,await AuditCount());
    }

    [Theory]
    [InlineData(false,true,DataClassification.PublicSource)]
    [InlineData(true,false,DataClassification.PublicSource)]
    [InlineData(true,true,DataClassification.Restricted)]
    public async Task ExplicitBothConsentsAndSupportedDataClassAreRequired(bool support,bool unverified,DataClassification dataClass)
    {
        await Ready();var preview=await Service.PreviewAsync("route-1",Email);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.ActivateAsync(preview.ObservationId,support,unverified,dataClass));
        Assert.Equal(0L,await AuditCount());
    }

    private Task AddEnabledSibling() => Sql("""
        INSERT INTO Routes(Id,Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,IsEnabled,Health,ManualPriority,CreatedAtUtc,UpdatedAtUtc)
        SELECT 'route-sibling',Backend,ProviderProfileId,AccountId,ModelId,MaxDataClass,1,'Healthy',0,CreatedAtUtc,UpdatedAtUtc
        FROM Routes WHERE Id='route-1';
        """);

    private async Task SaveEvidence(CapabilityState state, DateTimeOffset? observed = null)
    {
        var at=observed ?? _clock.GetUtcNow();
        var evidence=new LLMWorkGUI.Application.Providers.ModelCapabilityEvidence(_model,_account,state,
            ModelProvenance.UserDefined,at,at.AddHours(24),state==CapabilityState.Supported?ModelCapabilityFlags.Chat:ModelCapabilityFlags.None,[],[],[]);
        var store=new SqliteModelCapabilityEvidenceStore(_db.Factory,_guard,new SensitiveDataFilter(),_clock);
        await store.SaveAsync(evidence,await store.CaptureContextAsync(_model,_account));
    }

    private async Task SharedRowsReady()
    {
        await Sql("""
            UPDATE ProviderProfiles SET IsEnabled=1;
            UPDATE Accounts SET IsEnabled=1,AuthState='Valid',Health='Healthy';
            UPDATE Models SET IsEnabled=1,CapabilityState='Supported',Provenance='UserDefined',Health='Healthy';
            """);
        await SaveEvidence(CapabilityState.Supported);
    }

    [Theory]
    [InlineData("UPDATE ProviderProfiles SET IsEnabled=0")]
    [InlineData("UPDATE Accounts SET IsEnabled=0")]
    [InlineData("UPDATE Models SET Health='ProbeRequired'")]
    public async Task SharedGateCannotSilentlyEnableAnotherEnabledRoute(string restriction)
    {
        await Ready();await SharedRowsReady();await AddEnabledSibling();await Sql(restriction);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal(1L,Convert.ToInt64(await Sql("SELECT IsEnabled FROM Routes WHERE Id='route-sibling'")));
        Assert.Equal(0L,Convert.ToInt64(await Sql("SELECT IsEnabled FROM Routes WHERE Id='route-1'")));
        Assert.Equal(0L,await AuditCount());Assert.Equal(0,_adapter.StatusCalls);
    }

    [Fact]
    public async Task ReadySiblingAllowsSelectedRouteWithoutBroadeningAndKeepsVerifiedHealth()
    {
        await Ready();await SharedRowsReady();await AddEnabledSibling();
        await Sql($"INSERT INTO HealthStates(Id,ScopeType,ScopeId,State,UpdatedAtUtc) VALUES('known-account','account','{_account}','Healthy','2026-01-01T00:00:00Z')");
        var preview=await Service.PreviewAsync("route-1",Email);
        await Service.ActivateAsync(preview.ObservationId,true,true,DataClassification.PublicSource);
        Assert.Equal("Healthy",await Sql("SELECT Health FROM Accounts"));
        Assert.Equal("Healthy",await Sql("SELECT Health FROM Models"));
        Assert.Equal("Healthy",await Sql("SELECT State FROM HealthStates WHERE Id='known-account'"));
        Assert.Equal("2026-01-01T00:00:00Z",await Sql("SELECT UpdatedAtUtc FROM HealthStates WHERE Id='known-account'"));
        Assert.Equal("Healthy",await Sql("SELECT Health FROM Routes WHERE Id='route-sibling'"));
        Assert.Equal(3L,await AuditCount());
    }

    [Theory]
    [InlineData("account")]
    [InlineData("model-route")]
    public async Task NormalizedProbeRequiredCannotBeClearedToActivateReadyEntitySibling(string scopeType)
    {
        await Ready();await SharedRowsReady();await AddEnabledSibling();
        var scope=scopeType=="account"?_account:"v1:"+Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(_account))+":"+Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(_model));
        await Sql($"INSERT INTO HealthStates(Id,ScopeType,ScopeId,State,UpdatedAtUtc) VALUES('normalized-owned','{scopeType}','{scope}','ProbeRequired','2026-01-01T00:00:00Z')");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal("ProbeRequired",await Sql("SELECT State FROM HealthStates WHERE Id='normalized-owned'"));
        Assert.Equal("Healthy",await Sql("SELECT Health FROM Accounts"));
        Assert.Equal(1L,Convert.ToInt64(await Sql("SELECT IsEnabled FROM Routes WHERE Id='route-sibling'")));
        Assert.Equal(0L,await AuditCount());
    }

    [Fact]
    public async Task ProfileDataClassWideningWithEnabledSiblingIsRefusedAtomically()
    {
        await Ready();await SharedRowsReady();await AddEnabledSibling();
        var preview=await Service.PreviewAsync("route-1",Email);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.ActivateAsync(preview.ObservationId,true,true,DataClassification.PrivateSource));
        Assert.Equal("PublicSource",await Sql("SELECT MaxDataClass FROM ProviderProfiles"));
        Assert.Equal(0L,await AuditCount());
    }

    [Fact]
    public async Task NewScopedChatGrantCannotActivateAnExistingSiblingWithSameBinding()
    {
        await Ready();await SharedRowsReady();await AddEnabledSibling();await Sql("DELETE FROM ModelCapabilities");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal(0L,await AuditCount());
    }

    [Fact]
    public async Task EnabledSiblingAddedAfterPreviewMakesObservationStale()
    {
        await Ready();var preview=await Service.PreviewAsync("route-1",Email);await AddEnabledSibling();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.ActivateAsync(preview.ObservationId,true,true,DataClassification.PublicSource));
        Assert.Equal(0L,await AuditCount());
    }

    [Fact]
    public async Task ExistingAccountScopedUnsupportedEvidenceIsNotOverwritten()
    {
        await Ready();await SaveEvidence(CapabilityState.Unsupported);
        var before=await Sql("SELECT CapabilityValue FROM ModelCapabilities");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal(before,await Sql("SELECT CapabilityValue FROM ModelCapabilities"));
        Assert.Equal(0L,await AuditCount());
    }

    [Fact]
    public async Task EvidenceChangedAfterPreviewIsNotOverwritten()
    {
        await Ready();var preview=await Service.PreviewAsync("route-1",Email);await SaveEvidence(CapabilityState.Unsupported);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.ActivateAsync(preview.ObservationId,true,true,DataClassification.PublicSource));
        Assert.Equal("Unsupported",await Sql("SELECT State FROM ModelCapabilities"));Assert.Equal(0L,await AuditCount());
    }

    [Fact]
    public async Task FutureScopedEvidenceIsNotReplacedByAnOlderActivation()
    {
        await Ready();await SaveEvidence(CapabilityState.Supported);
        var future=_clock.Now.AddHours(1);
        var value=new LLMWorkGUI.Application.Providers.ModelCapabilityEvidence(_model,_account,CapabilityState.Supported,
            ModelProvenance.UserDefined,future,future.AddHours(24),ModelCapabilityFlags.Chat,[],[],[]);
        await using (var connection=await _db.Factory.OpenConnectionAsync())
        await using (var command=connection.CreateCommand())
        {
            command.CommandText="UPDATE ModelCapabilities SET CapabilityValue=$payload,UpdatedAtUtc=$time";
            command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(value));
            command.Parameters.AddWithValue("$time",future.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service.PreviewAsync("route-1",Email));
        Assert.Equal(0L,await AuditCount());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public TimeSpan AdvancePerRead;
        public override DateTimeOffset GetUtcNow()
        {
            var observed = Now;
            Now += AdvancePerRead;
            return observed;
        }
    }
    private sealed class Guard : IApplicationInstanceGuard {
        public bool Primary=true;public string InstanceId=>"owned";public bool IsPrimarySupervisor=>Primary;public bool IsViewOnly=>!Primary;
        public void EnsureSupervisorPermitted(){if(!Primary)throw new InvalidOperationException("Secondary fixture.");}public void Dispose(){}
    }
    private sealed class ProbeAdapter : IProviderAdapter {
        public string Email=NativeGatewayRouteActivationTests.Email;public string ModelId=Model;
        public int StatusCalls,CatalogCalls;public Func<Task>? OnCatalog;
        public ProviderKind Provider=>ProviderKind.Cursor;public string DisplayName=>"Owned native response fixture";public string DefaultExecutable=>"owned";
        public ProviderCapabilities Capabilities {get;}=new(false,"owned",MultiAccountSupport.Single,"owned",null,null,false,1000);
        public string? ResolveExecutable(AccountProfile account)=>"owned";public IEnumerable<AccountProfile> DiscoverProfiles()=>[];
        public Task<AccountStatus> GetStatusAsync(AccountProfile account,CancellationToken token){token.ThrowIfCancellationRequested();StatusCalls++;return Task.FromResult(new AccountStatus(AccountAvailability.Ready,new AccountIdentity(Email,"owned","42"),"owned",null,DateTimeOffset.UtcNow));}
        public async Task<IReadOnlyList<NativeModel>> ListModelsAsync(AccountProfile account,CancellationToken token){CatalogCalls++;if(OnCatalog is not null)await OnCatalog();token.ThrowIfCancellationRequested();return [new(ModelId,"Owned model")];}
        public Task<QuotaSnapshot> GetQuotaAsync(AccountProfile account,CancellationToken token)=>throw new NotSupportedException();
        public Task StartInteractiveLoginAsync(AccountProfile account,CancellationToken token)=>throw new NotSupportedException();
        public IAsyncEnumerable<NativeChatEvent> RunChatAsync(AccountProfile account,NativeChatRequest request,CancellationToken token)=>throw new InvalidOperationException("Activation must never request a model.");
    }
}
